using DevExpress.EntityFrameworkCore.Security;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using DevExpress.ExpressApp.EFCore.DataLocking;
using DevExpress.ExpressApp.Security;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Transactions;
using XafStateless.Demo.Module.BusinessObjects;
using XafStateless.Engine;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Services;

namespace XafStateless.Module.Tests;

public class DefinitionServiceTests : IClassFixture<TestDb>
{
    readonly TestDb db;
    readonly DefinitionService svc = new(new DefinitionStore());

    public DefinitionServiceTests(TestDb db)
    {
        this.db = db;
        using var os = db.NewObjectSpace();
        foreach (var role in new[] { "Approvers", "Administrators" })
            if (os.FirstOrDefault<PermissionPolicyRole>(r => r.Name == role) == null)
                os.CreateObject<PermissionPolicyRole>().Name = role;
        os.CommitChanges();
    }

    IReadOnlyList<string> Roles()
    {
        using var os = db.NewObjectSpace();   // TestDb's plain object space is non-secured
        return WorkflowRoleCatalog.Names(os);
    }

    /// <summary>Refused by DefinitionGuard (its message prefix), not by some other UserFriendlyException such as a lost race.</summary>
    static void Guarded(Action commit) =>
        Assert.StartsWith("Workflow definition:", Assert.Throws<UserFriendlyException>(commit).Message);

    static string Unique() => $"pr-{Guid.NewGuid():N}";

    static WorkflowState S(IObjectSpace os, WorkflowDefinition d, string key, StateKind kind, string? role = null)
    {
        var s = os.CreateObject<WorkflowState>();
        (s.Key, s.Caption, s.Kind, s.TaskRole) = (key, key, kind, role);
        d.States.Add(s);
        return s;
    }

    static WorkflowTransition T(IObjectSpace os, WorkflowDefinition d, string from, string trigger, string to, string? criteria = null)
    {
        var t = os.CreateObject<WorkflowTransition>();
        (t.FromKey, t.Trigger, t.ToKey, t.Criteria) = (from, trigger, to, criteria);
        d.Transitions.Add(t);
        return t;
    }

    static WorkflowEffect E(IObjectSpace os, WorkflowState s, EffectKind kind, string target, string value)
    {
        var e = os.CreateObject<WorkflowEffect>();
        (e.Kind, e.Target, e.Value, e.Order) = (kind, target, value, s.Effects.Count);
        s.Effects.Add(e);
        return e;
    }

    /// <summary>draft -submit-> review (task, Approvers, locks Amount) -approve-> ordered (final, 2 effects) / -reject-> rejected (final).</summary>
    static WorkflowDefinition Draft(IObjectSpace os, string name)
    {
        var d = os.CreateObject<WorkflowDefinition>();
        (d.Name, d.TargetTypeName, d.Version, d.State, d.InitialStateKey) = (name, typeof(PurchaseRequest).FullName!, 1, DefinitionState.Draft, "draft");
        S(os, d, "draft", StateKind.Manual);
        S(os, d, "review", StateKind.Task, "Approvers").LockedMembers = "Amount";   // a lock, so the deep copy covers locks too
        var ordered = S(os, d, "ordered", StateKind.Final);
        E(os, ordered, EffectKind.SetField, "Ordered", "true");
        E(os, ordered, EffectKind.Notify, "Approvers", "{Title} was ordered");
        S(os, d, "rejected", StateKind.Final);
        T(os, d, "draft", "submit", "review");
        T(os, d, "review", "approve", "ordered");
        T(os, d, "review", "reject", "rejected");
        return d;
    }

    Guid PublishNew(string name)
    {
        using var os = db.NewObjectSpace();
        var d = Draft(os, name);
        os.CommitChanges();
        var r = svc.Publish(os, d, "tester", Roles());
        Assert.True(r.Ok, string.Join("; ", r.Messages));
        return d.ID;
    }

    Guid PublishNext(string name)
    {
        using var os = db.NewObjectSpace();
        var d = svc.NewDraft(os, name);
        os.CommitChanges();
        var r = svc.Publish(os, d, "tester", Roles());
        Assert.True(r.Ok, string.Join("; ", r.Messages));
        return d.ID;
    }

    DefinitionState StateOf(Guid id)
    {
        using var os = db.NewObjectSpace();
        return os.GetObjectByKey<WorkflowDefinition>(id).State;
    }

    int LockVersion(Guid id)
    {
        using var os = db.NewObjectSpace();
        return os.GetObjectByKey<WorkflowDefinition>(id).OptimisticLockField;
    }

    void Refused(Guid id, Action<IObjectSpace, WorkflowDefinition> change, bool publishing = false)
    {
        using var os = db.NewObjectSpace();
        change(os, os.GetObjectByKey<WorkflowDefinition>(id));
        using (publishing ? DefinitionGuard.AllowPublishing(id, id) : null)
            Guarded(() => os.CommitChanges());
    }

    [Fact] public void Publish_valid_draft_retires_previous()
    {
        var name = Unique();
        var v1 = PublishNew(name);
        var v2 = PublishNext(name);
        Assert.Equal(DefinitionState.Retired, StateOf(v1));
        Assert.Equal(DefinitionState.Published, StateOf(v2));
        using var os = db.NewObjectSpace();
        var d2 = os.GetObjectByKey<WorkflowDefinition>(v2);
        Assert.Equal((2, "tester"), (d2.Version, d2.PublishedBy));
        Assert.NotNull(d2.PublishedUtc);
    }

    [Fact] public void Publish_invalid_draft_lists_errors_and_stays_draft()
    {
        using var os = db.NewObjectSpace();
        var d = Draft(os, Unique());
        S(os, d, "orphan", StateKind.Final);
        os.CommitChanges();
        var r = svc.Publish(os, d, "tester", Roles());
        Assert.False(r.Ok);
        Assert.Contains(r.Messages, m => m.Contains("'orphan' is unreachable"));
        Assert.Equal(DefinitionState.Draft, StateOf(d.ID));
    }

    [Fact] public void Editing_published_rule_is_refused()
    {
        var id = PublishNew(Unique());
        using var os = db.NewObjectSpace();
        var t = os.GetObjectByKey<WorkflowDefinition>(id).Transitions[0];
        var before = t.Trigger;
        t.Trigger = "hacked";
        Guarded(() => os.CommitChanges());
        os.Rollback();
        using var check = db.NewObjectSpace();
        Assert.Equal(before, check.GetObjectByKey<WorkflowTransition>(t.ID).Trigger);
    }

    [Fact] public void Publish_refuses_unconvertible_setfield()
    {
        using var os = db.NewObjectSpace();
        var d = Draft(os, Unique());
        E(os, d.States.Single(s => s.Key == "ordered"), EffectKind.SetField, "Amount", "abc");
        os.CommitChanges();
        var r = svc.Publish(os, d, "tester", Roles());
        Assert.False(r.Ok);
        Assert.Contains(r.Messages, m => m.Contains("effect on 'ordered':"));
        Assert.Equal(DefinitionState.Draft, StateOf(d.ID));
    }

    [Fact] public void Publish_refuses_increment_on_text_member()   // XSM-031
    {
        using var os = db.NewObjectSpace();
        var d = Draft(os, Unique());
        E(os, d.States.Single(s => s.Key == "ordered"), EffectKind.Increment, "Title", "1");
        os.CommitChanges();
        var r = svc.Publish(os, d, "tester", Roles());
        Assert.False(r.Ok);
        Assert.Contains(r.Messages, m => m.Contains("effect on 'ordered':") && m.Contains("'Title'"));
    }

    [Fact] public void Increment_validation()   // XSM-031: counters are integral or decimal members; the step is a number that fits
    {
        using var os = db.NewObjectSpace();
        string? Check(Type type, string target, string value) =>
            new XafWorkflowEnvironment([], os.TypesInfo.FindTypeInfo(type)).CheckEffect(new(EffectKind.Increment, target, value));

        Assert.Null(Check(typeof(PurchaseRequest), "Revisions", ""));       // step 1
        Assert.Null(Check(typeof(PurchaseRequest), "Revisions", "-1"));
        Assert.Null(Check(typeof(PurchaseRequest), "Amount", "0.5"));
        Assert.Null(Check(typeof(WorkflowDefinition), "Version", "2"));
        Assert.Contains("'Title'", Check(typeof(PurchaseRequest), "Title", "1"));
        Assert.Contains("'Ordered'", Check(typeof(PurchaseRequest), "Ordered", "1"));
        Assert.Contains("'State'", Check(typeof(WorkflowDefinition), "State", "1"));   // an enum: its TypeCode is Int32, still not a counter
        Assert.Contains("'abc'", Check(typeof(PurchaseRequest), "Revisions", "abc"));
        Assert.Contains("'1.5'", Check(typeof(WorkflowDefinition), "Version", "1.5"));   // a whole-number member takes whole steps
    }

    [Fact] public void Increment_target_lookup_lists_counters_only()   // XSM-031
    {
        using var os = db.NewObjectSpace();
        var d = Draft(os, Unique());
        var e = E(os, d.States.Single(s => s.Key == "ordered"), EffectKind.Increment, "", "");
        Assert.Equal(["Amount", "Revisions"], ((IStringLookupSource)e).LookupValues(nameof(WorkflowEffect.Target), () => []).Order());
        e.Kind = EffectKind.SetField;
        Assert.Equal(["Amount", "Ordered", "Revisions", "Title"], ((IStringLookupSource)e).LookupValues(nameof(WorkflowEffect.Target), () => []).Order());
    }

    [Fact] public void Criteria_with_unknown_member_is_refused()
    {
        using var os = db.NewObjectSpace();
        var d = Draft(os, Unique());
        d.Transitions.Single(t => t.Trigger == "submit").Criteria = "Nope > 1";
        var r = svc.Validate(os, d, Roles());
        Assert.False(r.Ok);
        Assert.Contains(r.Messages, m => m.Contains("criteria on"));

        d.Transitions.Single(t => t.Trigger == "submit").Criteria = "Amount > 1 And Workflow.StateKey = 'draft'";   // a path resolves
        Assert.True(svc.Validate(os, d, Roles()).Ok);
    }

    [Fact] public void Definition_created_with_New_gets_the_next_version()   // XSM-018: the list view's New left Version at 0
    {
        var (fresh, used) = (Unique(), Unique());
        PublishNew(used);
        using var os = db.NewObjectSpace();
        WorkflowDefinition New(string name)
        {
            var d = os.CreateObject<WorkflowDefinition>();   // what the New action does: no NewDraft, Version untouched
            (d.Name, d.TargetTypeName) = (name, typeof(PurchaseRequest).FullName!);
            return d;
        }
        var (a, b) = (New(fresh), New(used));
        os.CommitChanges();
        Assert.Equal((1, 2), (a.Version, b.Version));
    }

    [Fact] public void Target_type_list_is_the_workflow_subjects()   // XSM-022: IWorkflowSubject is the marker
    {
        using var _ = db.NewObjectSpace();   // types info is set up with the fixture
        var names = WorkflowDefinition.SubjectTypeNames().ToList();
        Assert.Contains(typeof(PurchaseRequest).FullName!, names);
        Assert.DoesNotContain(typeof(WorkflowDefinition).FullName!, names);
        Assert.All(names, n => Assert.True(typeof(IWorkflowSubject).IsAssignableFrom(XafTypesInfo.Instance.FindTypeInfo(n).Type)));
    }

    [Fact] public void Target_type_shows_as_its_caption_and_unknown_names_as_stored()   // XSM-025
    {
        using var _ = db.NewObjectSpace();
        var d = new WorkflowDefinition();
        Assert.Equal("Purchase Request", d.LookupText(nameof(WorkflowDefinition.TargetTypeName), typeof(PurchaseRequest).FullName!));
        Assert.Equal("No.Such.Type", d.LookupText(nameof(WorkflowDefinition.TargetTypeName), "No.Such.Type"));
        Assert.Equal("draft", d.LookupText(nameof(WorkflowDefinition.InitialStateKey), "draft"));
    }

    [Fact] public void Target_type_captions_never_collide()   // XSM-025
    {
        string[] subjects = ["A.Models.Request", "B.Models.Request", "C.Models.Order", "C.Models.Orders.Order"];
        static string Caption(string n) => n[(n.LastIndexOf('.') + 1)..];
        var shown = subjects.Select(s => WorkflowDefinition.SubjectCaption(s, subjects, Caption)).ToList();
        Assert.Equal(["Request (A.Models)", "Request (B.Models)", "Order (C.Models)", "Order (C.Models.Orders)"], shown);
        string[] same = ["X.Request", "X.Request2"];   // same namespace, same caption: full names
        Assert.Equal(same, same.Select(s => WorkflowDefinition.SubjectCaption(s, same, _ => "Request")));
        Assert.Equal("X.NotASubject", WorkflowDefinition.SubjectCaption("X.NotASubject", same, Caption));
    }

    [Fact] public void Validate_returns_findings_tagged_with_their_state()   // XSM-027
    {
        using var os = db.NewObjectSpace();
        var d = Draft(os, Unique());
        d.Transitions.Remove(d.Transitions.Single(t => t.Trigger == "reject"));
        var r = svc.Validate(os, d, Roles());
        Assert.False(r.Ok);
        Assert.Contains(r.Findings!, f => f.State == "rejected" && f.Message.StartsWith("'rejected' is unreachable"));
        Assert.Equal(r.Messages, r.Findings!.Select(f => f.Message));
    }

    [Fact] public void A_cleared_initial_state_is_a_finding_not_an_exception()   // XSM-027
    {
        using var os = db.NewObjectSpace();
        var d = Draft(os, Unique());
        d.InitialStateKey = null!;   // what the lookup's clear button leaves
        Assert.Contains(svc.Validate(os, d, Roles()).Messages, m => m == "initial state '' does not exist");
        Assert.DoesNotContain("[*] -->", WorkflowMermaid.Format(DefinitionMapper.ToDef(d, os), new DiagramStyle(ByKind: true)));
    }

    [Fact] public void Dry_run_refuses_an_invalid_definition()   // XSM-027: as Publish does
    {
        using var os = db.NewObjectSpace();
        var d = Draft(os, Unique());
        d.InitialStateKey = "nope";
        var (steps, error) = svc.DryRun(os, d, Roles(), ["submit"], new HashSet<string>());
        Assert.Empty(steps);
        Assert.StartsWith("the definition is not valid: initial state 'nope' does not exist", error);
    }

    [Fact] public void Children_marked_for_deletion_are_left_out_of_validation()   // XSM-027: an unsaved delete counts
    {
        using var os = db.NewObjectSpace();
        var d = Draft(os, Unique());
        os.CommitChanges();
        Assert.True(svc.Validate(os, d, Roles()).Ok);
        var victim = d.Transitions.Single(t => t.Trigger == "reject");
        os.Delete(victim);
        Assert.Contains(svc.Validate(os, d, Roles()).Findings!, f => f.State == "rejected");
    }

    [Fact] public void Failed_New_renamed_and_retried_is_renumbered()   // XSM-018 (Codex): a refused save must not pin the number
    {
        var (taken, fresh) = (Unique(), Unique());
        using (var seed = db.NewObjectSpace()) { Draft(seed, taken); seed.CommitChanges(); }   // 'taken' has draft v1
        using var os = db.NewObjectSpace();
        var d = os.CreateObject<WorkflowDefinition>();
        (d.Name, d.TargetTypeName) = (taken, typeof(PurchaseRequest).FullName!);
        Assert.ThrowsAny<Exception>(() => os.CommitChanges());   // one draft per name; v2 was assigned on the way
        d.Name = fresh;
        os.CommitChanges();
        Assert.Equal(1, d.Version);
    }

    [Fact] public void NewDraft_copies_published_deeply()
    {
        var name = Unique();
        var v1 = PublishNew(name);
        using var os = db.NewObjectSpace();
        var draft = svc.NewDraft(os, name);
        os.CommitChanges();
        var src = os.GetObjectByKey<WorkflowDefinition>(v1);
        Assert.Equal((DefinitionState.Draft, 2, src.InitialStateKey, src.TargetTypeName), (draft.State, draft.Version, draft.InitialStateKey, draft.TargetTypeName));
        Assert.Equal(src.States.Count, draft.States.Count);
        Assert.Equal(src.States.Sum(s => s.Effects.Count), draft.States.Sum(s => s.Effects.Count));
        Assert.Equal(src.Transitions.Count, draft.Transitions.Count);
        var oldIds = src.States.Select(s => s.ID).Concat(src.States.SelectMany(s => s.Effects).Select(e => e.ID)).Concat(src.Transitions.Select(t => t.ID)).Append(src.ID);
        var newIds = draft.States.Select(s => s.ID).Concat(draft.States.SelectMany(s => s.Effects).Select(e => e.ID)).Concat(draft.Transitions.Select(t => t.ID)).Append(draft.ID);
        Assert.Empty(oldIds.Intersect(newIds));
        Assert.Equal(DefinitionMapper.ToDef(src) with { Version = 2 }, DefinitionMapper.ToDef(draft), new DefComparer());
    }

    [Fact] public void Changing_instance_definition_is_refused()
    {
        Guid instanceId, otherId;
        using (var os = db.NewObjectSpace())
        {
            var d1 = Draft(os, Unique()); var d2 = Draft(os, Unique());
            var i = os.CreateObject<WorkflowInstance>();
            (i.Definition, i.SubjectType, i.SubjectId, i.StateKey) = (d1, typeof(PurchaseRequest).FullName!, Guid.NewGuid(), "draft");
            using (RuntimeScope.Open(((DevExpress.ExpressApp.EFCore.EFCoreObjectSpace)os).DbContext)) os.CommitChanges();   // instances are runtime-created
            (instanceId, otherId) = (i.ID, d2.ID);
        }
        using (var os = db.NewObjectSpace())
        {
            var i = os.GetObjectByKey<WorkflowInstance>(instanceId);
            i.Definition = os.GetObjectByKey<WorkflowDefinition>(otherId);
            Guarded(() => os.CommitChanges());
        }
        using (var os = db.NewObjectSpace())
        {
            os.GetObjectByKey<WorkflowInstance>(instanceId).SubjectId = Guid.NewGuid();
            Guarded(() => os.CommitChanges());
        }
        using (var os = db.NewObjectSpace())
        {
            os.GetObjectByKey<WorkflowInstance>(instanceId).StateKey = "review";   // runtime-owned: only inside the runtime scope
            Assert.StartsWith("Workflow instance:", Assert.Throws<UserFriendlyException>(() => os.CommitChanges()).Message);
            using (RuntimeScope.Open(((DevExpress.ExpressApp.EFCore.EFCoreObjectSpace)os).DbContext)) os.CommitChanges();
        }
    }

    [Fact] public void Published_header_is_frozen()
    {
        var id = PublishNew(Unique());
        Refused(id, (_, d) => d.InitialStateKey = "review");
        Refused(id, (_, d) => d.Name = Unique());
        Refused(id, (_, d) => d.TargetTypeName = "Other.Type");
        Refused(id, (_, d) => d.State = DefinitionState.Draft);
        Refused(id, (_, d) => d.State = DefinitionState.Draft, publishing: true);
        Assert.Equal(DefinitionState.Published, StateOf(id));
    }

    [Fact] public void Inserting_published_definition_is_refused()
    {
        foreach (var publishing in new[] { false, true })
        {
            using var os = db.NewObjectSpace();
            var d = Draft(os, Unique());
            d.State = DefinitionState.Published;
            using (publishing ? DefinitionGuard.AllowPublishing(d.ID, d.ID) : null)
                Guarded(() => os.CommitChanges());
        }
    }

    [Fact] public void Retired_is_frozen()
    {
        var name = Unique();
        var v1 = PublishNew(name);
        PublishNext(name);
        Assert.Equal(DefinitionState.Retired, StateOf(v1));
        Refused(v1, (_, d) => d.Name = Unique());
        Refused(v1, (_, d) => d.InitialStateKey = "review");
        Refused(v1, (_, d) => d.State = DefinitionState.Published, publishing: true);
        Refused(v1, (_, d) => d.State = DefinitionState.Draft, publishing: true);
        Refused(v1, (os, d) => os.Delete(d));
        Refused(v1, (_, d) => d.Transitions[0].Trigger = "hacked");
        Refused(v1, (_, d) => { var s = d.States.Single(s => s.Key == "ordered"); s.Effects.Remove(s.Effects[0]); });
        Refused(v1, (_, d) => d.States.Single(s => s.Key == "ordered").Effects[0].Value = "false");
        Refused(v1, (os, d) => os.Delete(d.Transitions[0]));
        Refused(v1, (os, d) => S(os, d, "extra", StateKind.Final));
        Refused(v1, (os, d) => E(os, d.States.Single(s => s.Key == "ordered"), EffectKind.Notify, "Approvers", "x"));
        Assert.Equal(DefinitionState.Retired, StateOf(v1));
    }

    [Fact] public void SetField_on_ID_is_refused()
    {
        using var os = db.NewObjectSpace();
        var d = Draft(os, Unique());
        E(os, d.States.Single(s => s.Key == "ordered"), EffectKind.SetField, "ID", Guid.NewGuid().ToString());
        var r = svc.Validate(os, d, Roles());
        Assert.False(r.Ok);
        Assert.Contains(r.Messages, m => m.Contains("cannot be set by a workflow"));
    }

    [Fact] public void Draft_children_edit_and_delete_are_allowed()
    {
        Guid id;
        using (var os = db.NewObjectSpace()) { var d = Draft(os, Unique()); os.CommitChanges(); id = d.ID; }
        using (var os = db.NewObjectSpace())
        {
            var d = os.GetObjectByKey<WorkflowDefinition>(id);
            d.Transitions.Single(t => t.Trigger == "reject").Trigger = "decline";
            S(os, d, "extra", StateKind.Final);
            os.CommitChanges();
        }
        Assert.Equal(1, LockVersion(id));   // child writes Touch the definition: +1
        using (var os = db.NewObjectSpace())
        {
            var d = os.GetObjectByKey<WorkflowDefinition>(id);
            d.InitialStateKey = "review";                                      // the definition itself changes too...
            d.Transitions.Single(t => t.Trigger == "decline").Trigger = "reject";
            os.CommitChanges();
        }
        Assert.Equal(2, LockVersion(id));   // ...still exactly +1: the guard's Touch and XAF's interceptor agree on original+1
        using (var os = db.NewObjectSpace())
        {
            os.Delete(os.GetObjectByKey<WorkflowDefinition>(id));
            os.CommitChanges();
        }
    }

    [Fact] public void Publish_racing_draft_child_edit_loses()
    {
        Guid id, tid;
        using (var os = db.NewObjectSpace())
        {
            var d = Draft(os, Unique()); os.CommitChanges();
            (id, tid) = (d.ID, d.Transitions.Single(t => t.Trigger == "reject").ID);
        }
        using var publisher = db.NewObjectSpace();
        var draft = publisher.GetObjectByKey<WorkflowDefinition>(id);
        _ = DefinitionMapper.ToDef(draft);            // the publisher has read (and would validate) this snapshot

        using (var editor = db.NewObjectSpace())     // a designer edits a rule meanwhile
        {
            editor.GetObjectByKey<WorkflowTransition>(tid).ToKey = "ordered";
            editor.CommitChanges();
        }

        var r = svc.Publish(publisher, draft, "tester", Roles());
        Assert.False(r.Ok);
        Assert.Contains(r.Messages, m => m.Contains("changed meanwhile"));
        Assert.Equal(DefinitionState.Draft, StateOf(id));
    }

    [Fact] public void AddXafStateless_resolves_services()
    {
        var sp = new ServiceCollection().AddXafStateless().BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<DefinitionStore>());
        Assert.NotNull(sp.GetRequiredService<DefinitionService>());
        Assert.Same(TimeProvider.System, sp.GetRequiredService<TimeProvider>());
        Assert.Same(sp.GetRequiredService<DefinitionService>(), sp.GetRequiredService<DefinitionService>());

        var fake = new FakeClock();
        var withFake = new ServiceCollection().AddSingleton<TimeProvider>(fake).AddXafStateless().BuildServiceProvider();
        Assert.Same(fake, withFake.GetRequiredService<TimeProvider>());
    }

    // ---- Fix round 1 ----

    Guid CommittedDraft()
    {
        using var os = db.NewObjectSpace();
        var d = Draft(os, Unique());
        os.CommitChanges();
        return d.ID;
    }

    [Fact] public void Publish_refuses_pending_changes_to_other_definitions()
    {
        var (d, x) = (CommittedDraft(), CommittedDraft());
        var y = PublishNew(Unique());
        using (var os = db.NewObjectSpace())
        {
            os.GetObjectByKey<WorkflowDefinition>(x).State = DefinitionState.Published;    // unvalidated X rides along?
            var r = svc.Publish(os, os.GetObjectByKey<WorkflowDefinition>(d), "tester", Roles());
            Assert.False(r.Ok);
            Assert.Contains(r.Messages, m => m.Contains("Save or cancel"));
        }
        using (var os = db.NewObjectSpace())
        {
            os.GetObjectByKey<WorkflowDefinition>(y).State = DefinitionState.Retired;      // any published Y retired?
            Assert.False(svc.Publish(os, os.GetObjectByKey<WorkflowDefinition>(d), "tester", Roles()).Ok);
        }
        Assert.Equal((DefinitionState.Draft, DefinitionState.Draft, DefinitionState.Published), (StateOf(d), StateOf(x), StateOf(y)));
    }

    [Fact] public void Publishing_scope_covers_only_its_own_ids()
    {
        var (d, x) = (CommittedDraft(), CommittedDraft());
        var y = PublishNew(Unique());
        var retire = PublishNew(Unique());
        using (var os = db.NewObjectSpace())
        {
            os.GetObjectByKey<WorkflowDefinition>(x).State = DefinitionState.Published;
            using (DefinitionGuard.AllowPublishing(d, retire)) Guarded(() => os.CommitChanges());
        }
        using (var os = db.NewObjectSpace())
        {
            os.GetObjectByKey<WorkflowDefinition>(y).State = DefinitionState.Retired;
            using (DefinitionGuard.AllowPublishing(d, retire)) Guarded(() => os.CommitChanges());
        }
        using (var os = db.NewObjectSpace())
        {
            os.GetObjectByKey<WorkflowDefinition>(y).State = DefinitionState.Retired;     // publish id is not a retire permit
            using (DefinitionGuard.AllowPublishing(y, null)) Guarded(() => os.CommitChanges());
        }
        Assert.Equal((DefinitionState.Draft, DefinitionState.Published), (StateOf(x), StateOf(y)));
    }

    [Fact] public void Publishing_scope_captured_after_dispose_is_refused()
    {
        var d = CommittedDraft();
        using var os = db.NewObjectSpace();
        os.GetObjectByKey<WorkflowDefinition>(d).State = DefinitionState.Published;
        ExecutionContext captured;
        using (DefinitionGuard.AllowPublishing(d, null)) captured = ExecutionContext.Capture()!;   // e.g. a task started inside the scope
        ExecutionContext.Run(captured, _ => Guarded(() => os.CommitChanges()), null);
        Assert.Equal(DefinitionState.Draft, StateOf(d));
    }

    /// <summary>An untracked definition attached with fake original values (State = Draft) over a Published row.</summary>
    static WorkflowDefinition AttachFakeDraft(DemoEFCoreDbContext ctx, Guid id, int version)
    {
        var fake = ctx.CreateProxy<WorkflowDefinition>();
        (fake.ID, fake.Name, fake.Version, fake.State, fake.OptimisticLockField) = (id, "fake", 1, DefinitionState.Draft, version);
        ctx.Attach(fake);
        return fake;
    }

    [Fact] public void Attached_definition_with_fake_draft_original_is_refused()
    {
        var id = PublishNew(Unique());
        using (var ctx = db.NewDbContext())
        {
            AttachFakeDraft(ctx, id, LockVersion(id)).InitialStateKey = "review";
            Guarded(() => ctx.SaveChanges());
        }
        using (var ctx = db.NewDbContext())
        {
            AttachFakeDraft(ctx, id, LockVersion(id));
            ctx.WorkflowTransitions.First(t => EF.Property<Guid?>(t, "DefinitionID") == id).Trigger = "hacked";
            Guarded(() => ctx.SaveChanges());
        }
        using var check = db.NewObjectSpace();
        var d = check.GetObjectByKey<WorkflowDefinition>(id);
        Assert.Equal(("draft", DefinitionState.Published), (d.InitialStateKey, d.State));
        Assert.DoesNotContain(d.Transitions, t => t.Trigger == "hacked");
    }

    [Fact] public void Attached_instance_with_fake_original_is_refused()
    {
        var (d1, d2) = (CommittedDraft(), CommittedDraft());
        Guid instanceId;
        using (var os = db.NewObjectSpace())
        {
            var i = os.CreateObject<WorkflowInstance>();
            (i.Definition, i.SubjectType, i.SubjectId) = (os.GetObjectByKey<WorkflowDefinition>(d1), "T", Guid.NewGuid());
            using (RuntimeScope.Open(((DevExpress.ExpressApp.EFCore.EFCoreObjectSpace)os).DbContext)) os.CommitChanges();
            instanceId = i.ID;
        }
        using var ctx = db.NewDbContext();
        var fake = ctx.CreateProxy<WorkflowInstance>();
        fake.ID = instanceId;
        ctx.Attach(fake);                                    // originals: no definition, no subject ("unset")
        ctx.Entry(fake).Property("DefinitionID").CurrentValue = d2;
        Guarded(() => ctx.SaveChanges());
    }

    [Fact] public void GCRecord_marked_but_unchanged_is_not_a_deletion()
    {
        var d = CommittedDraft();
        using var os = db.NewObjectSpace();
        var def = os.GetObjectByKey<WorkflowDefinition>(d);
        def.State = DefinitionState.Published;
        ((DevExpress.ExpressApp.EFCore.EFCoreObjectSpace)os).DbContext.Entry(def).Property("GCRecord").IsModified = true;
        Guarded(() => os.CommitChanges());
        Assert.Equal(DefinitionState.Draft, StateOf(d));
    }

    [Fact] public void Store_does_not_cache_inside_a_transaction()
    {
        var store = new DefinitionStore();
        var d = CommittedDraft();
        using (var os = db.NewObjectSpace())
        {
            var ef = (DevExpress.ExpressApp.EFCore.EFCoreObjectSpace)os;
            using var tx = ef.DbContext.Database.BeginTransaction();
            using (DefinitionGuard.AllowPublishing(d, null))
            {
                os.GetObjectByKey<WorkflowDefinition>(d).State = DefinitionState.Published;
                os.CommitChanges();
            }
            Assert.Contains(store.Get(os, d).Transitions, t => t.Trigger == "reject");
            tx.Rollback();                                   // never published after all
        }
        using (var os = db.NewObjectSpace())               // still a draft: edit it
        {
            os.GetObjectByKey<WorkflowDefinition>(d).Transitions.Single(t => t.Trigger == "reject").Trigger = "decline";
            os.CommitChanges();
        }
        using (var os = db.NewObjectSpace())
            Assert.Contains(store.Get(os, d).Transitions, t => t.Trigger == "decline");
    }

    [Fact] public void Allow_list_pins()
    {
        var draft = CommittedDraft();
        var published = PublishNew(Unique());
        Refused(draft, (_, d) => d.State = DefinitionState.Published);                    // Draft->Published outside the scope
        Refused(draft, (_, d) => d.PublishedBy = "someone");                              // PublishedBy on a draft
        Refused(published, (_, d) => d.State = DefinitionState.Retired);                  // Published->Retired outside the scope
        Refused(published, (_, d) => (d.State, d.InitialStateKey) = (DefinitionState.Retired, "review"), publishing: true);   // retire + header change
        Refused(published, (os, d) => os.Delete(d));                                      // delete a Published definition
        Assert.Equal((DefinitionState.Draft, DefinitionState.Published), (StateOf(draft), StateOf(published)));
    }

    [Fact] public void Null_effect_value_is_a_message_not_an_exception()
    {
        using var os = db.NewObjectSpace();
        var d = Draft(os, Unique());
        var ordered = d.States.Single(s => s.Key == "ordered");
        foreach (var e in ordered.Effects) e.Value = null!;
        var r = svc.Validate(os, d, Roles());
        Assert.False(r.Ok);
        Assert.Equal(2, r.Messages.Count(m => m.StartsWith("effect on 'ordered':")));
    }

    // ---- Fix round 2 ----

    [Fact] public void Store_snapshot_ignores_rolled_back_graph()
    {
        var store = new DefinitionStore();
        var d = CommittedDraft();
        using var os = db.NewObjectSpace();
        var def = os.GetObjectByKey<WorkflowDefinition>(d);
        using (var tx = ((DevExpress.ExpressApp.EFCore.EFCoreObjectSpace)os).DbContext.Database.BeginTransaction())
        {
            def.Transitions.Single(t => t.Trigger == "reject").Trigger = "rolledback";
            os.CommitChanges();
            using (DefinitionGuard.AllowPublishing(d, null)) { def.State = DefinitionState.Published; os.CommitChanges(); }
            tx.Rollback();
        }
        // The object space still holds the accepted Published graph in memory, and no transaction is active any more.
        Assert.Equal(DefinitionState.Published, def.State);
        Assert.False(os.IsModified);
        var snapshot = store.Get(os, d);
        Assert.Contains(snapshot.Transitions, t => t.Trigger == "reject");            // the committed row, not the tracked graph
        Assert.DoesNotContain(snapshot.Transitions, t => t.Trigger == "rolledback");

        using (var edit = db.NewObjectSpace())                                        // still a draft in the database: edit it
        {
            edit.GetObjectByKey<WorkflowDefinition>(d).Transitions.Single(t => t.Trigger == "reject").Trigger = "decline";
            edit.CommitChanges();
        }
        using var fresh = db.NewObjectSpace();
        Assert.Contains(store.Get(fresh, d).Transitions, t => t.Trigger == "decline");   // nothing was cached
    }

    [Fact] public void Guard_reads_are_not_masked_by_security()
    {
        var published = PublishNew(Unique());
        var draft = CommittedDraft();
        using var provider = StateMaskedDesigner();

        using (var user = provider.CreateObjectSpace())
        {
            Assert.IsType<SecuredEFCoreObjectSpace>(user);
            user.GetObjectByKey<WorkflowDefinition>(draft).Transitions.Single(t => t.Trigger == "reject").Trigger = "decline";
            user.CommitChanges();                                                     // a draft child edit goes through
        }
        using (var user = provider.CreateObjectSpace())
        {
            var d = user.GetObjectByKey<WorkflowDefinition>(published);
            Assert.Equal(DefinitionState.Draft, d.State);                             // masked: the scenario is real
            d.InitialStateKey = "review";
            Guarded(() => user.CommitChanges());                                      // the guard read the real State
        }
        using var check = db.NewObjectSpace();
        Assert.Equal("draft", check.GetObjectByKey<WorkflowDefinition>(published).InitialStateKey);
        Assert.Contains(check.GetObjectByKey<WorkflowDefinition>(draft).Transitions, t => t.Trigger == "decline");
    }

    /// <summary>A secured provider logged on as a designer role whose State member is read-denied: a secured LINQ read
    /// sees default(State) = Draft. Built as the Task 4b spike builds a user's space; the demo DbContext adds the guard itself.</summary>
    SecuredEFCoreObjectSpaceProvider<DemoEFCoreDbContext> StateMaskedDesigner()
    {
        var userName = $"u{Guid.NewGuid():N}";
        using (var os = db.NewObjectSpace())
        {
            var role = os.CreateObject<PermissionPolicyRole>();
            role.Name = userName;
            foreach (var t in new[] { typeof(WorkflowDefinition), typeof(WorkflowState), typeof(WorkflowTransition), typeof(WorkflowEffect) })
                role.AddTypePermission(t, SecurityOperations.CRUDAccess, SecurityPermissionState.Allow);
            role.AddMemberPermission<WorkflowDefinition>(SecurityOperations.Read, nameof(WorkflowDefinition.State), null, SecurityPermissionState.Deny);
            var user = os.CreateObject<ApplicationUser>(); user.UserName = userName; user.SetPassword(""); user.Roles.Add(role);
            os.CommitChanges();
            ((ISecurityUserWithLoginInfo)user).CreateUserLoginInfo(SecurityDefaults.PasswordAuthentication, os.GetKeyValueAsString(user));
            os.CommitChanges();
        }
        var auth = new AuthenticationStandard();
        var security = new SecurityStrategyComplex(typeof(ApplicationUser), typeof(PermissionPolicyRole), auth);
        var provider = new SecuredEFCoreObjectSpaceProvider<DemoEFCoreDbContext>(security, XafTypesInfo.Instance, db.ConnectionString,
            (builder, cs) => { builder.UseConnectionString(cs); builder.AddInterceptors(new EFCoreOptimisticLockInterceptor()); });
        auth.SetLogonParameters(new AuthenticationStandardLogonParameters(userName, ""));
        using (var logon = ((INonsecuredObjectSpaceProvider)provider).CreateNonsecuredObjectSpace()) security.Logon(logon);
        return provider;
    }

    // ---- Fix round 3 ----

    [Fact] public void Secured_child_edit_on_published_is_refused()
    {
        var published = PublishNew(Unique());
        using var provider = StateMaskedDesigner();
        using (var user = provider.CreateObjectSpace())
        {
            var d = user.GetObjectByKey<WorkflowDefinition>(published);
            Assert.Equal(DefinitionState.Draft, d.State);                             // masked
            d.Transitions.Single(t => t.Trigger == "reject").Trigger = "hacked";
            Guarded(() => user.CommitChanges());
        }
        using (var user = provider.CreateObjectSpace())
        {
            var effect = user.GetObjectByKey<WorkflowDefinition>(published).States.Single(s => s.Key == "ordered").Effects[0];
            effect.Value = "false";
            Guarded(() => user.CommitChanges());
        }
        using (var user = provider.CreateObjectSpace())
        {
            S(user, user.GetObjectByKey<WorkflowDefinition>(published), "extra", StateKind.Final);
            Guarded(() => user.CommitChanges());
        }
        using var check = db.NewObjectSpace();
        var saved = check.GetObjectByKey<WorkflowDefinition>(published);
        Assert.DoesNotContain(saved.Transitions, t => t.Trigger == "hacked");
        Assert.DoesNotContain(saved.States, s => s.Key == "extra");
        Assert.Equal("true", saved.States.Single(s => s.Key == "ordered").Effects.Single(e => e.Kind == EffectKind.SetField).Value);
    }

    [Fact] public void Stale_definition_edit_loses_before_any_sql()
    {
        var d = CommittedDraft();
        using var stale = db.NewObjectSpace();
        var def = stale.GetObjectByKey<WorkflowDefinition>(d);
        using (var other = db.NewObjectSpace())
        {
            other.GetObjectByKey<WorkflowDefinition>(d).InitialStateKey = "review";
            other.CommitChanges();
        }
        def.Version = 7;                                                              // a header edit from a stale load
        var ex = Assert.Throws<UserFriendlyException>(() => stale.CommitChanges());
        Assert.True(LostRace.Is(ex), ex.ToString());
        // The guard's version check fired (its message), not EF's UPDATE affecting 0 rows afterwards.
        Assert.Equal(DefinitionGuard.ChangedMeanwhile, Assert.IsType<DbUpdateConcurrencyException>(ex.InnerException).Message);
    }

    [Fact] public void Store_does_not_cache_inside_an_enlisted_transaction()
    {
        var store = new DefinitionStore();
        var d = CommittedDraft();
        using (var os = db.NewObjectSpace())
        using (var committable = new System.Transactions.CommittableTransaction())
        {
            var database = ((DevExpress.ExpressApp.EFCore.EFCoreObjectSpace)os).DbContext.Database;
            database.OpenConnection();                                                // enlisting needs an open connection
            database.EnlistTransaction(committable);
            Assert.Null(System.Transactions.Transaction.Current);                    // explicit enlistment, no ambient scope
            using (DefinitionGuard.AllowPublishing(d, null))
            {
                os.GetObjectByKey<WorkflowDefinition>(d).State = DefinitionState.Published;
                os.CommitChanges();
            }
            Assert.Contains(store.Get(os, d).Transitions, t => t.Trigger == "reject");
            committable.Rollback();                                                   // never published after all
            database.CloseConnection();
        }
        using (var edit = db.NewObjectSpace())
        {
            edit.GetObjectByKey<WorkflowDefinition>(d).Transitions.Single(t => t.Trigger == "reject").Trigger = "decline";
            edit.CommitChanges();
        }
        using var fresh = db.NewObjectSpace();
        Assert.Contains(store.Get(fresh, d).Transitions, t => t.Trigger == "decline");
    }

    // ---- Fix round 4 (XSM-008: nested "+New" must link the child to its definition) ----

    /// <summary>Mimics XAF Blazor's nested "+New" action for an aggregated collection item: for EF Core,
    /// IObjectSpace.CreateNestedObjectSpace() returns a plain object space over its own separate DbContext (no
    /// merge-on-parent-commit; see EFCoreObjectSpace.CreateNestedObjectSpace in the installed source), so the new
    /// child is created and added through the parent collection resolved in that SAME nested space, which then
    /// commits on its own - exactly the sequence NewObjectViewController.AddNewObjectToCollectionSource runs when
    /// objectSpace != currentCollectionSource.ObjectSpace.</summary>
    [Fact] public void NestedObjectSpace_New_links_transition_to_its_definition()
    {
        Guid id;
        using (var os = db.NewObjectSpace()) { var d = Draft(os, Unique()); os.CommitChanges(); id = d.ID; }

        using (var os = db.NewObjectSpace())
        {
            var nested = os.CreateNestedObjectSpace();
            var definition = nested.GetObjectByKey<WorkflowDefinition>(id);
            var t = nested.CreateObject<WorkflowTransition>();
            (t.FromKey, t.Trigger, t.ToKey) = ("draft", "Send back", "review");
            definition.Transitions.Add(t);
            nested.CommitChanges();
        }

        using var check = db.NewObjectSpace();
        var reloaded = check.GetObjectByKey<WorkflowDefinition>(id);
        Assert.Contains(reloaded.Transitions, t => t.Trigger == "Send back");
    }

    /// <summary>Same nested-object-space path for States, and for an Effect nested two levels deep (State, then Effect).</summary>
    [Fact] public void NestedObjectSpace_New_links_state_and_effect_to_their_owners()
    {
        Guid id;
        using (var os = db.NewObjectSpace()) { var d = Draft(os, Unique()); os.CommitChanges(); id = d.ID; }

        using (var os = db.NewObjectSpace())
        {
            var nested = os.CreateNestedObjectSpace();
            var definition = nested.GetObjectByKey<WorkflowDefinition>(id);
            var s = nested.CreateObject<WorkflowState>();
            (s.Key, s.Caption, s.Kind) = ("extra", "Extra", StateKind.Final);
            definition.States.Add(s);
            var e = nested.CreateObject<WorkflowEffect>();
            (e.Kind, e.Target, e.Value) = (EffectKind.SetField, "Ordered", "true");
            s.Effects.Add(e);
            nested.CommitChanges();
        }

        using var check = db.NewObjectSpace();
        var reloaded = check.GetObjectByKey<WorkflowDefinition>(id);
        var extra = Assert.Single(reloaded.States, s => s.Key == "extra");
        Assert.Single(extra.Effects);
    }

    sealed class FakeClock : TimeProvider;

    /// <summary>Records hold lists; compare them by content.</summary>
    sealed class DefComparer : IEqualityComparer<WorkflowDef>
    {
        public bool Equals(WorkflowDef? a, WorkflowDef? b) =>
            a!.Name == b!.Name && a.Version == b.Version && a.InitialState == b.InitialState
            && a.States.OrderBy(s => s.Key).Select(s => (s.Key, s.Caption, s.Kind, s.TaskRole, s.WaitMinutes, string.Join("|", s.Effects), string.Join("|", s.LockedMembers)))
               .SequenceEqual(b.States.OrderBy(s => s.Key).Select(s => (s.Key, s.Caption, s.Kind, s.TaskRole, s.WaitMinutes, string.Join("|", s.Effects), string.Join("|", s.LockedMembers))))
            && a.Transitions.OrderBy(t => t.Trigger).SequenceEqual(b.Transitions.OrderBy(t => t.Trigger));
        public int GetHashCode(WorkflowDef d) => d.Name.GetHashCode();
    }
}
