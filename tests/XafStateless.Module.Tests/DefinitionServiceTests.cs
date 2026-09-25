using DevExpress.ExpressApp;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>draft -submit-> review (task, Approvers) -approve-> ordered (final, 2 effects) / -reject-> rejected (final).</summary>
    static WorkflowDefinition Draft(IObjectSpace os, string name)
    {
        var d = os.CreateObject<WorkflowDefinition>();
        (d.Name, d.TargetTypeName, d.Version, d.State, d.InitialStateKey) = (name, typeof(PurchaseRequest).FullName!, 1, DefinitionState.Draft, "draft");
        S(os, d, "draft", StateKind.Manual);
        S(os, d, "review", StateKind.Task, "Approvers");
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
        using (publishing ? DefinitionGuard.AllowPublishing() : null)
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
            os.CommitChanges();
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
            os.GetObjectByKey<WorkflowInstance>(instanceId).StateKey = "review";   // runtime-owned, not frozen here
            os.CommitChanges();
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
            using (publishing ? DefinitionGuard.AllowPublishing() : null)
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

    sealed class FakeClock : TimeProvider;

    /// <summary>Records hold lists; compare them by content.</summary>
    sealed class DefComparer : IEqualityComparer<WorkflowDef>
    {
        public bool Equals(WorkflowDef? a, WorkflowDef? b) =>
            a!.Name == b!.Name && a.Version == b.Version && a.InitialState == b.InitialState
            && a.States.OrderBy(s => s.Key).Select(s => (s.Key, s.Caption, s.Kind, s.TaskRole, s.WaitMinutes, string.Join("|", s.Effects)))
               .SequenceEqual(b.States.OrderBy(s => s.Key).Select(s => (s.Key, s.Caption, s.Kind, s.TaskRole, s.WaitMinutes, string.Join("|", s.Effects))))
            && a.Transitions.OrderBy(t => t.Trigger).SequenceEqual(b.Transitions.OrderBy(t => t.Trigger));
        public int GetHashCode(WorkflowDef d) => d.Name.GetHashCode();
    }
}
