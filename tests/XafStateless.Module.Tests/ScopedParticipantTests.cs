using DevExpress.Data.Filtering;
using DevExpress.EntityFrameworkCore.Security;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.EFCore.DataLocking;
using DevExpress.ExpressApp.Security;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using Microsoft.EntityFrameworkCore;
using XafStateless.Demo.Module.BusinessObjects;
using XafStateless.Engine;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Services;

namespace XafStateless.Module.Tests;

/// <summary>CARD-2144: a participant role scoped by a criteria on the subject reads only the instances, tasks and
/// notifications of subjects inside the scope, under real XAF security.</summary>
public class ScopedParticipantTests(TestDb db) : IClassFixture<TestDb>
{
    const string Amount = "[Amount] > 100";
    static readonly string Nav = nameof(PurchaseRequest) + "_ListView";

    sealed record World(string Role, string Definition, Guid Small, Guid Large);

    SecuredEFCoreObjectSpaceProvider<DemoEFCoreDbContext> LogOn(string userName, out SecurityStrategyComplex security)
    {
        var auth = new AuthenticationStandard();
        security = new SecurityStrategyComplex(typeof(ApplicationUser), typeof(PermissionPolicyRole), auth);
        var provider = new SecuredEFCoreObjectSpaceProvider<DemoEFCoreDbContext>(security, XafTypesInfo.Instance, db.ConnectionString,
            (builder, cs) => { builder.UseConnectionString(cs); builder.AddInterceptors(new EFCoreOptimisticLockInterceptor()); });
        auth.SetLogonParameters(new AuthenticationStandardLogonParameters(userName, ""));
        var s = security;
        using (var logon = ((INonsecuredObjectSpaceProvider)provider).CreateNonsecuredObjectSpace()) s.Logon(logon);
        return provider;
    }

    /// <summary>A user holding <paramref name="roles"/>; returns its id.</summary>
    Guid User(string name, params string[] roles)
    {
        using var os = db.NewObjectSpace();
        var u = os.CreateObject<ApplicationUser>();
        (u.UserName, u.IsActive) = (name, true);
        u.SetPassword("");
        foreach (var role in roles) u.Roles.Add(os.FirstOrDefault<PermissionPolicyRole>(r => r.Name == role));
        os.CommitChanges();
        ((ISecurityUserWithLoginInfo)u).CreateUserLoginInfo(SecurityDefaults.PasswordAuthentication, os.GetKeyValueAsString(u));
        os.CommitChanges();
        return u.ID;
    }

    void Role(string name, Action<PermissionPolicyRole> grant)
    {
        using var os = db.NewObjectSpace();
        var r = os.CreateObject<PermissionPolicyRole>();
        r.Name = name;
        grant(r);
        os.CommitChanges();
    }

    /// <summary>A published definition for <paramref name="role"/>: draft (tells the requester) -Submit-> review (task and
    /// notification to the role) -Approve-> done (tells the requester).</summary>
    string Publish(string role)
    {
        var def = new WorkflowDef($"sc-{Guid.NewGuid():N}", 1, "draft",
            [
                new("draft", "Draft", StateKind.Manual, OnEntry: [new(EffectKind.Notify, EffectDef.Requester, "Started {Title}")]),
                new("review", "Review", StateKind.Task, TaskRole: role, OnEntry: [new(EffectKind.Notify, role, "Review {Title}")]),
                new("done", "Done", StateKind.Final, OnEntry: [new(EffectKind.Notify, EffectDef.Requester, "Done {Title}")]),
            ],
            [new("draft", "Submit", "review"), new("review", "Approve", "done")]);
        var actions = new WorkflowActionCatalog([]);
        using var os = db.NewObjectSpace();
        var d = DefinitionMapper.ToDraft(os, def, typeof(PurchaseRequest).FullName!);
        os.CommitChanges();
        var r = new DefinitionService(new DefinitionStore(), actions).Publish(os, d, "tester", WorkflowRoleCatalog.Names(os));
        Assert.True(r.Ok, string.Join("; ", r.Messages));
        return def.Name;
    }

    static WorkflowRuntime Runtime() => new(new DefinitionStore(), TimeProvider.System, new WorkflowActionCatalog([]));

    Guid Request(string title, decimal amount)
    {
        using var os = db.NewObjectSpace();
        var pr = os.CreateObject<PurchaseRequest>();
        (pr.Title, pr.Amount) = (title, amount);
        os.CommitChanges();
        return pr.ID;
    }

    /// <summary>A role granted by <paramref name="grant"/>, a definition for it, and two requests (50 and 500) started by
    /// the user <paramref name="users"/> creates (after the role, which it may hold) and submitted: each has an instance, an
    /// open task, a role and a requester notification.</summary>
    World Build(string role, Action<PermissionPolicyRole> grant, Func<Guid> users)
    {
        Role(role, grant);
        var requester = users();
        var def = Publish(role);
        var (small, large) = (Request("small", 50), Request("large", 500));
        var rt = Runtime();
        foreach (var id in new[] { small, large })
        {
            var c = new WorkflowCaller("starter", role.Equals, db.NewObjectSpace(), db.NewObjectSpace, null, requester);
            Assert.True(rt.Start(c, typeof(PurchaseRequest), id, def).Ok);
            Assert.True(rt.Fire(c, typeof(PurchaseRequest), id, "Submit").Ok);
        }
        return new World(role, def, small, large);
    }

    sealed record Ids(Guid Instance, Guid Task, Guid RoleNote, Guid RequesterNote);

    Ids RowsOf(Guid subject)
    {
        using var os = db.NewObjectSpace();
        var inst = os.GetObjectByKey<PurchaseRequest>(subject).Workflow!;
        var notes = os.GetObjectsQuery<WorkflowNotification>().Where(n => n.Instance!.ID == inst.ID).ToList();
        return new Ids(inst.ID, os.GetObjectsQuery<WorkflowTask>().Single(t => t.Instance!.ID == inst.ID).ID,
            notes.Single(n => n.UserId == null).ID, notes.Single(n => n.UserId != null).ID);
    }

    /// <summary>What <paramref name="user"/> reads of <paramref name="subject"/>'s rows, by each read path.</summary>
    void AssertReads(string user, Guid subject, bool visible)
    {
        var ids = RowsOf(subject);
        using var provider = LogOn(user, out _);
        using (var os = provider.CreateObjectSpace())
        {
            Assert.Equal(visible, os.GetObjectsQuery<WorkflowTask>().Any(t => t.ID == ids.Task));
            Assert.Equal(visible, os.GetObjectsQuery<WorkflowNotification>().Any(n => n.ID == ids.RoleNote));
            Assert.Equal(visible, os.GetObjectsQuery<WorkflowNotification>().Any(n => n.ID == ids.RequesterNote));
            Assert.Equal(visible, os.GetObjectsQuery<WorkflowInstance>().Any(i => i.ID == ids.Instance));
            Assert.Equal(visible, os.GetObjects<WorkflowTask>(CriteriaOperator.Parse("[State] = 'Open'")).Any(t => t.ID == ids.Task));
            Assert.Equal(visible, os.GetObjects<WorkflowNotification>(CriteriaOperator.Parse("[Body] Like 'Started%'")).Any(n => n.ID == ids.RequesterNote));
        }
        using (var os = provider.CreateObjectSpace())   // by key: XAF's in-memory check
        {
            Assert.Equal(visible, os.GetObjectByKey<WorkflowTask>(ids.Task) != null);
            Assert.Equal(visible, os.GetObjectByKey<WorkflowNotification>(ids.RoleNote) != null);
            Assert.Equal(visible, os.GetObjectByKey<WorkflowNotification>(ids.RequesterNote) != null);
            Assert.Equal(visible, os.GetObjectByKey<WorkflowInstance>(ids.Instance) != null);
            Assert.Equal(visible, os.GetObjectByKey<PurchaseRequest>(subject)?.Workflow != null);   // null: the subject is unreadable
        }
    }

    [Fact] public void A_scope_on_the_subject_limits_every_read_path()
    {
        var role = $"Amount-{Guid.NewGuid():N}";
        var user = $"u-{Guid.NewGuid():N}";
        var w = Build(role, r => WorkflowPermissions.GrantParticipant(r, new ParticipantSubject(typeof(PurchaseRequest), Nav, Amount)), () => User(user, role));
        AssertReads(user, w.Large, visible: true);
        AssertReads(user, w.Small, visible: false);
    }

    [Fact] public void A_membership_scope_through_a_nested_join_and_a_collection_limits_by_user()
    {
        var role = $"Member-{Guid.NewGuid():N}";
        var members = $"Members-{Guid.NewGuid():N}";
        Role(members, _ => { });
        var scope = $"{Amount} And [<{typeof(ApplicationUser).FullName}>][[ID] = CurrentUserId() And [Roles][[Name] = '{members}']]";
        var (member, outsider) = ($"m-{Guid.NewGuid():N}", $"o-{Guid.NewGuid():N}");
        var w = Build(role, r => WorkflowPermissions.GrantParticipant(r, new ParticipantSubject(typeof(PurchaseRequest), Nav, scope)),
            () => { User(outsider, role); return User(member, role, members); });
        AssertReads(member, w.Large, visible: true);
        AssertReads(member, w.Small, visible: false);
        AssertReads(outsider, w.Large, visible: false);   // same role, not a member
    }

    [Fact] public void Narrowing_an_existing_role_takes_effect_under_security()
    {
        // The upgrade path: a role seeded unscoped, regranted scoped by a later updater run. The removed rows are soft-deleted
        // (the demo context uses deferred deletion); security must not read them.
        var role = $"Up-{Guid.NewGuid():N}";
        var user = $"u-{Guid.NewGuid():N}";
        var w = Build(role, r => WorkflowPermissions.GrantParticipant(r, (typeof(PurchaseRequest), Nav)), () => User(user, role));
        AssertReads(user, w.Small, visible: true);   // control: unscoped
        using (var os = db.NewObjectSpace())
        {
            WorkflowPermissions.GrantParticipant(os.FirstOrDefault<PermissionPolicyRole>(r => r.Name == role),
                new ParticipantSubject(typeof(PurchaseRequest), Nav, Amount));
            os.CommitChanges();
        }
        AssertReads(user, w.Large, visible: true);
        AssertReads(user, w.Small, visible: false);
    }

    [Fact] public void An_Or_scope_stays_grouped_inside_the_join()
    {
        // Ungrouped, "[ID] = x And [Amount] > 1000 Or [Title] = 'large'" would match every row while a 'large' request exists.
        var role = $"Or-{Guid.NewGuid():N}";
        var user = $"u-{Guid.NewGuid():N}";
        var w = Build(role, r => WorkflowPermissions.GrantParticipant(r,
            new ParticipantSubject(typeof(PurchaseRequest), Nav, "[Amount] > 1000 Or [Title] = 'large'")), () => User(user, role));
        AssertReads(user, w.Large, visible: true);
        AssertReads(user, w.Small, visible: false);
    }

    [Fact] public void Plain_per_type_rows_read_every_subject_of_the_type()
    {
        var role = $"Plain-{Guid.NewGuid():N}";
        var user = $"u-{Guid.NewGuid():N}";
        var w = Build(role, r =>
        {
            WorkflowPermissions.GrantParticipant(r, new ParticipantSubject(typeof(PurchaseRequest), Nav, Amount));
            WorkflowPermissions.GrantParticipant(r, (typeof(PurchaseRequest), Nav));   // unscoped again: per-type plain rows
        }, () => User(user, role));
        AssertReads(user, w.Large, visible: true);
        AssertReads(user, w.Small, visible: true);
    }

    [Fact] public void A_requester_moved_out_of_scope_reads_neither_old_nor_new_requester_notifications()
    {
        var role = $"Req-{Guid.NewGuid():N}";
        var user = $"u-{Guid.NewGuid():N}";
        var id = Guid.Empty;
        var w = Build(role, r => WorkflowPermissions.GrantParticipant(r, new ParticipantSubject(typeof(PurchaseRequest), Nav, Amount)),
            () => id = User(user, role));
        var stays = Request("stays", 600);
        var rt = Runtime();
        var c = new WorkflowCaller("starter", role.Equals, db.NewObjectSpace(), db.NewObjectSpace, null, id);
        Assert.True(rt.Start(c, typeof(PurchaseRequest), stays, w.Definition).Ok);
        Assert.True(rt.Fire(c, typeof(PurchaseRequest), stays, "Submit").Ok);
        using (var os = db.NewObjectSpace())
        {
            os.GetObjectByKey<PurchaseRequest>(w.Large).Amount = 50;   // stands in for lost membership
            os.CommitChanges();
        }
        foreach (var subject in new[] { w.Large, stays })
            Assert.True(rt.Decide(db.Caller("approver", role), RowsOf(subject).Task, "Approve").Ok);   // "Done" to the requester

        Guid[] Requester(Guid subject)
        {
            using var os = db.NewObjectSpace();
            var inst = os.GetObjectByKey<PurchaseRequest>(subject).Workflow!.ID;
            return os.GetObjectsQuery<WorkflowNotification>().Where(n => n.Instance!.ID == inst && n.UserId == id).Select(n => n.ID).ToArray();
        }
        var (moved, kept) = (Requester(w.Large), Requester(stays));
        Assert.Equal((2, 2), (moved.Length, kept.Length));
        using var provider = LogOn(user, out _);
        using var secured = provider.CreateObjectSpace();
        Assert.All(moved, n => Assert.Null(secured.GetObjectByKey<WorkflowNotification>(n)));
        Assert.All(kept, n => Assert.NotNull(secured.GetObjectByKey<WorkflowNotification>(n)));
        Assert.Equal(2, secured.GetObjectsQuery<WorkflowNotification>().Count(n => n.UserId == id));
    }

    [Fact] public void A_False_scope_hides_every_row_from_a_role_that_cannot_read_the_subject()
    {
        // CARD-2284: a host grants a notified role its workflow rows without Read on the subject (BPG). A notification's text
        // carries the subject's fields, so such a role's rows are scoped to nothing; unscoped, it read them.
        var role = $"Notified-{Guid.NewGuid():N}";
        var user = $"u-{Guid.NewGuid():N}";
        var w = Build(role, r =>
        {
            foreach (var t in new[] { typeof(WorkflowDefinition), typeof(WorkflowState), typeof(WorkflowTransition), typeof(WorkflowEffect) })
                r.EnsureTypePermission(t).ReadState = SecurityPermissionState.Allow;
            WorkflowPermissions.GrantRoleRows(r, (typeof(PurchaseRequest), "False"));
        }, () => User(user, role));
        using (var provider = LogOn(user, out _))
        using (var os = provider.CreateObjectSpace())
            Assert.Null(os.GetObjectByKey<PurchaseRequest>(w.Large));   // nothing on the subject
        AssertReads(user, w.Large, visible: false);
        AssertReads(user, w.Small, visible: false);
    }

    [Fact] public void A_writer_reads_the_definition_and_its_scoped_instances_but_cannot_change_the_link()
    {
        // CARD-2308: what a host (BPG) grants a role with Write on the subject that no workflow names. Start lists the published
        // definitions, Transition and the state locks need subject.Workflow.Definition; the link stays the runtime's.
        var role = $"Writer-{Guid.NewGuid():N}";
        var user = $"u-{Guid.NewGuid():N}";
        var id = Guid.Empty;
        var w = Build(role, r =>
        {
            r.AddObjectPermission<PurchaseRequest>(SecurityOperations.Read + ";" + SecurityOperations.Write, Amount, SecurityPermissionState.Allow);
            r.EnsureTypePermission(typeof(WorkflowDefinition)).ReadState = SecurityPermissionState.Allow;
            WorkflowPermissions.GrantRoleRows(r, (typeof(PurchaseRequest), Amount));
            var link = r.EnsureTypePermission(typeof(PurchaseRequest)).CreateMemberPermission();
            (link.Members, link.WriteState) = (nameof(IWorkflowSubject.Workflow), SecurityPermissionState.Deny);
        }, () => id = User(user, role));
        var fresh = Request("fresh", 500);
        using var provider = LogOn(user, out var security);
        using var os = provider.CreateObjectSpace();
        Assert.Contains(w.Definition, os.GetObjectsQuery<WorkflowDefinition>().Where(d => d.State == DefinitionState.Published).Select(d => d.Name).ToList());
        var c = new WorkflowCaller(user, role.Equals, os, db.NewObjectSpace, security, id);
        Assert.True(Runtime().Start(c, typeof(PurchaseRequest), fresh, w.Definition).Ok);   // as the user, through security
        Assert.Contains(Runtime().Available(c, typeof(PurchaseRequest), fresh), t => t.Trigger == "Submit");
        var large = os.GetObjectByKey<PurchaseRequest>(w.Large);
        Assert.NotNull(large.Workflow?.Definition);
        Assert.Null(os.GetObjectByKey<PurchaseRequest>(w.Small));   // outside the scope
        bool May(string member) => security.IsGranted(new PermissionRequest(os, typeof(PurchaseRequest), SecurityOperations.Write, large, member));
        Assert.True(May(nameof(PurchaseRequest.Title)));
        Assert.False(May(nameof(IWorkflowSubject.Workflow)));   // the editor is read-only; the guard below refuses the save anyway
        large.Workflow = null;
        Assert.Equal(StateLockGuard.LinkManaged, Assert.ThrowsAny<Exception>(() => os.CommitChanges()).Message);
    }

    [Fact] public void Decide_is_refused_when_the_subject_read_is_scoped_the_same_way()
    {
        var role = $"Dec-{Guid.NewGuid():N}";
        var user = $"u-{Guid.NewGuid():N}";
        var id = Guid.Empty;
        var w = Build(role, r =>
        {
            WorkflowPermissions.GrantParticipant(r, new ParticipantSubject(typeof(PurchaseRequest), Nav, Amount));
            // The host scopes the subject itself, as BPG's boundary does: no type-wide read or write, an object permission instead.
            var subject = r.TypePermissions.Single(t => t.TargetType == typeof(PurchaseRequest));
            (subject.ReadState, subject.WriteState) = (null, null);
            r.AddObjectPermission<PurchaseRequest>(SecurityOperations.Read + ";" + SecurityOperations.Write, Amount, SecurityPermissionState.Allow);
        }, () => id = User(user, role));
        var (small, large) = (RowsOf(w.Small).Task, RowsOf(w.Large).Task);
        using var provider = LogOn(user, out var security);
        using var secured = provider.CreateObjectSpace();
        var c = new WorkflowCaller(user, role.Equals, secured, db.NewObjectSpace, security, id);
        var refused = Runtime().Decide(c, small, "Approve");
        Assert.False(refused.Ok);
        Assert.Contains("does not exist or cannot be read", refused.Message);
        Assert.True(Runtime().Decide(c, large, "Approve").Ok);
    }
}
