using DevExpress.Data.Filtering;
using DevExpress.EntityFrameworkCore.Security;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.EFCore.DataLocking;
using DevExpress.ExpressApp.Security;
using Microsoft.EntityFrameworkCore;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using XafStateless.Demo.Module.BusinessObjects;
using XafStateless.Engine;
using XafStateless.Engine.Tests;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Controllers;
using XafStateless.Module.Services;

namespace XafStateless.Module.Tests;

/// <summary>The seeded role matrix (<see cref="WorkflowPermissions"/>) under real XAF security: secured object spaces
/// logged on as an approver and as a designer.</summary>
public class PermissionTests : IClassFixture<TestDb>
{
    const string Approver = "approver", Designer = "designer", Reader = "reader", Outsider = "outsider", Admin = "admin", Others = "Others";
    readonly TestDb db;
    readonly RefundAction refund = new();
    readonly WorkflowRuntime rt;
    readonly DefinitionService svc;

    public PermissionTests(TestDb db)
    {
        this.db = db;
        var actions = new WorkflowActionCatalog([refund]);
        (rt, svc) = (new(new DefinitionStore(), TimeProvider.System, actions), new(new DefinitionStore(), actions));
        using var os = db.NewObjectSpace();
        if (os.FirstOrDefault<PermissionPolicyRole>(r => r.Name == "Approvers") != null) return;   // one seed per fixture
        var approvers = os.CreateObject<PermissionPolicyRole>();
        approvers.Name = "Approvers";
        WorkflowPermissions.GrantParticipant(approvers, (typeof(PurchaseRequest), "PurchaseRequest_ListView"));
        os.CreateObject<PermissionPolicyRole>().Name = Others;   // addressee of tasks the approver must not see
        var designers = os.CreateObject<PermissionPolicyRole>();
        designers.Name = "WorkflowDesigners";
        WorkflowPermissions.GrantDesigner(designers);
        var readers = os.CreateObject<PermissionPolicyRole>();   // Read-only on the subject type
        readers.Name = "Readers";
        readers.AddTypePermission<PurchaseRequest>(SecurityOperations.Read, SecurityPermissionState.Allow);
        var outsiders = os.CreateObject<PermissionPolicyRole>();   // nothing on the subject type
        outsiders.Name = "Outsiders";
        var admins = os.CreateObject<PermissionPolicyRole>();   // administrative by the flag, not by the name
        (admins.Name, admins.IsAdministrative) = ("Superusers", true);
        foreach (var (name, role) in new[] { (Approver, approvers), (Designer, designers), (Reader, readers), (Outsider, outsiders), (Admin, admins) })
        {
            var user = os.CreateObject<ApplicationUser>();
            (user.UserName, user.IsActive) = (name, true);
            user.SetPassword("");
            user.Roles.Add(role);
            os.CommitChanges();
            ((ISecurityUserWithLoginInfo)user).CreateUserLoginInfo(SecurityDefaults.PasswordAuthentication, os.GetKeyValueAsString(user));
        }
        os.CommitChanges();
    }

    /// <summary>A secured provider logged on as <paramref name="userName"/> (built as the demo builds a user's space).</summary>
    SecuredEFCoreObjectSpaceProvider<DemoEFCoreDbContext> LogOn(string userName) => LogOn(userName, out _);

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

    IReadOnlyList<string> Roles()
    {
        using var os = db.NewObjectSpace();
        return WorkflowRoleCatalog.Names(os);
    }

    /// <summary>The engine's purchase-request fixture, published under a unique name through non-secured entities.</summary>
    string PublishPurchaseRequest() => Publish(Fixtures.PurchaseRequest() with { Name = $"pr-{Guid.NewGuid():N}" });

    string Publish(WorkflowDef def)
    {
        using var os = db.NewObjectSpace();
        var d = DefinitionMapper.ToDraft(os, def, typeof(PurchaseRequest).FullName!);
        os.CommitChanges();
        var r = svc.Publish(os, d, "tester", Roles());
        Assert.True(r.Ok, string.Join("; ", r.Messages));
        return def.Name;
    }

    static WorkflowState State(IObjectSpace os, WorkflowDefinition d, string key, StateKind kind, string? role = null)
    {
        var s = os.CreateObject<WorkflowState>();
        (s.Key, s.Caption, s.Kind, s.TaskRole) = (key, key, kind, role);
        d.States.Add(s);
        return s;
    }

    static WorkflowTransition Transition(IObjectSpace os, WorkflowDefinition d, string from, string trigger, string to)
    {
        var t = os.CreateObject<WorkflowTransition>();
        (t.FromKey, t.Trigger, t.ToKey) = (from, trigger, to);
        d.Transitions.Add(t);
        return t;
    }

    WorkflowCaller Caller(IObjectSpace secured, IRequestSecurityStrategy security, string name, params string[] roles) =>
        new(name, roles.Contains, secured, db.NewObjectSpace, security);

    static void Ok(RunResult r) => Assert.True(r.Ok, r.Message);

    /// <summary>A small request started and submitted by the approver; returns (request id, open task id).</summary>
    (Guid Request, Guid Task) Submitted(IObjectSpace approverSpace, IRequestSecurityStrategy security)
    {
        var name = PublishPurchaseRequest();
        var pr = approverSpace.CreateObject<PurchaseRequest>();
        (pr.Title, pr.Amount) = ("Chair", 50);
        approverSpace.CommitChanges();
        var c = Caller(approverSpace, security, Approver, "Approvers");
        Ok(rt.Start(c, typeof(PurchaseRequest), pr.ID, name));
        Ok(rt.Fire(c, typeof(PurchaseRequest), pr.ID, "Submit"));
        using var check = db.NewObjectSpace();
        var task = check.GetObjectsQuery<WorkflowTask>().Single(t => t.Instance!.SubjectId == pr.ID && t.State == TaskState.Open);
        return (pr.ID, task.ID);
    }

    [Fact] public void Approver_can_start_fire_and_decide()
    {
        using var provider = LogOn(Approver, out var security);
        using var user = provider.CreateObjectSpace();
        Assert.IsType<SecuredEFCoreObjectSpace>(user);
        var (request, task) = Submitted(user, security);
        var c = Caller(user, security, Approver, "Approvers");
        Assert.Equal(["Approve", "Reject"], rt.Outcomes(c, task));

        Ok(rt.Decide(c, task, "Approve"));

        using var check = db.NewObjectSpace();
        var pr = check.GetObjectByKey<PurchaseRequest>(request);
        Assert.Equal("ordered", pr.Workflow!.StateKey);
        Assert.True(pr.Ordered);
        Assert.Equal((TaskState.Decided, Approver), (check.GetObjectByKey<WorkflowTask>(task).State, check.GetObjectByKey<WorkflowTask>(task).DecidedBy));
    }

    [Fact] public void Action_writes_a_related_record_the_caller_could_not_write()   // XSM-032
    {
        // Readers have Read only on PurchaseRequest. A reader cannot Start (no Write), so the initial state's action never runs;
        // a reader who is in the task's role may decide it, and the ordered state's action then writes a related request through
        // the command space: the host's capability, not the caller's permission.
        var def = Fixtures.PurchaseRequest() with { Name = $"pr-{Guid.NewGuid():N}" };
        def = def with { States = def.States.Select(s => s.Key switch
        {
            "draft" => s with { OnEntry = [new(EffectKind.RunAction, "refund", "Draft {Title}")] },
            "ordered" => s with { OnEntry = [.. s.Effects, new(EffectKind.RunAction, "refund", RefundAction.Title + "{Title}")] },
            _ => s,
        }).ToList() };
        Publish(def);
        var title = $"Chair {Guid.NewGuid():N}";
        using var approverProvider = LogOn(Approver, out var approverSecurity);
        using var approver = approverProvider.CreateObjectSpace();
        var pr = approver.CreateObject<PurchaseRequest>();
        (pr.Title, pr.Amount) = (title, 50);
        approver.CommitChanges();

        using var readerProvider = LogOn(Reader, out var readerSecurity);
        using var reader = readerProvider.CreateObjectSpace();
        Assert.False(readerSecurity.IsGranted(new PermissionRequest(reader, typeof(PurchaseRequest), SecurityOperations.Create)));
        var noWrite = rt.Start(Caller(reader, readerSecurity, Reader, "Approvers"), typeof(PurchaseRequest), pr.ID, def.Name);
        Assert.Equal((false, 0), (noWrite.Ok, refund.Calls));   // refused before the initial state's action
        Assert.Contains("requires write access", noWrite.Message);

        var a = Caller(approver, approverSecurity, Approver, "Approvers");
        Ok(rt.Start(a, typeof(PurchaseRequest), pr.ID, def.Name));
        Assert.Equal(1, refund.Calls);
        Ok(rt.Fire(a, typeof(PurchaseRequest), pr.ID, "Submit"));
        Guid task;
        using (var check = db.NewObjectSpace()) task = check.GetObjectsQuery<WorkflowTask>().Single(t => t.Instance!.SubjectId == pr.ID && t.State == TaskState.Open).ID;

        var refused = rt.Decide(Caller(reader, readerSecurity, Reader), task, "Approve");   // not in Approvers: refused before any effect
        Assert.Equal((false, 1), (refused.Ok, refund.Calls));

        Ok(rt.Decide(Caller(reader, readerSecurity, Reader, "Approvers"), task, "Approve"));

        using var after = db.NewObjectSpace();
        Assert.Equal(2, refund.Calls);
        Assert.Single(after.GetObjectsQuery<PurchaseRequest>().Where(p => p.Title == RefundAction.Title + title));
        Assert.Equal("ordered", after.GetObjectByKey<PurchaseRequest>(pr.ID).Workflow!.StateKey);
    }

    [Fact] public void Approver_cannot_edit_task_role()
    {
        using var provider = LogOn(Approver, out var security);
        Guid task;
        using (var user = provider.CreateObjectSpace()) (_, task) = Submitted(user, security);
        using (var user = provider.CreateObjectSpace())
        {
            var t = user.GetObjectByKey<WorkflowTask>(task);
            var pr = user.GetObjectByKey<PurchaseRequest>(t.Instance!.SubjectId);   // control: the same check grants a subject edit
            Assert.True(security.IsGranted(new PermissionRequest(user, typeof(PurchaseRequest), SecurityOperations.Write, pr, nameof(PurchaseRequest.Title))));
            Assert.False(security.IsGranted(new PermissionRequest(user, typeof(WorkflowTask), SecurityOperations.Write, t, nameof(WorkflowTask.Role))));
            t.Role = "WorkflowDesigners";
            Assert.ThrowsAny<Exception>(() => user.CommitChanges());   // the task guard refuses first; security would too
        }
        using var check = db.NewObjectSpace();
        Assert.Equal("Approvers", check.GetObjectByKey<WorkflowTask>(task).Role);
    }

    [Fact] public void Approver_cannot_change_workflow_link()
    {
        using var provider = LogOn(Approver, out var security);
        Guid request;
        using (var user = provider.CreateObjectSpace()) (request, _) = Submitted(user, security);
        using (var user = provider.CreateObjectSpace())
        {
            var pr = user.GetObjectByKey<PurchaseRequest>(request);
            Assert.NotNull(pr.Workflow);   // load the link first: nulling a never-loaded lazy reference is no change at all
            // The permission denies the write in its own right (control: Title is granted) ...
            Assert.True(security.IsGranted(new PermissionRequest(user, typeof(PurchaseRequest), SecurityOperations.Write, pr, nameof(PurchaseRequest.Title))));
            Assert.False(security.IsGranted(new PermissionRequest(user, typeof(PurchaseRequest), SecurityOperations.Write, pr, nameof(PurchaseRequest.Workflow))));
            pr.Workflow = null;
            // ... and StateLockGuard refuses the save first, for every role (Task 7b).
            Assert.Contains(StateLockGuard.LinkManaged, Assert.ThrowsAny<Exception>(() => user.CommitChanges()).Message);
        }
        using var check = db.NewObjectSpace();
        Assert.NotNull(check.GetObjectByKey<PurchaseRequest>(request).Workflow);
    }

    [Fact] public void Approver_cannot_create_definitions()
    {
        using var provider = LogOn(Approver);
        using var user = provider.CreateObjectSpace();
        var d = user.CreateObject<WorkflowDefinition>();
        (d.Name, d.TargetTypeName, d.Version) = ($"x-{Guid.NewGuid():N}", typeof(PurchaseRequest).FullName!, 1);
        Assert.Contains("prohibited by security rules", Assert.ThrowsAny<Exception>(() => user.CommitChanges()).Message);
        using var check = db.NewObjectSpace();
        Assert.Null(check.FirstOrDefault<WorkflowDefinition>(x => x.Name == d.Name));
    }

    [Fact] public void Designer_can_create_draft()
    {
        using var provider = LogOn(Designer);
        using var user = provider.CreateObjectSpace();
        var name = $"d-{Guid.NewGuid():N}";
        var d = svc.NewDraft(user, name);
        d.TargetTypeName = typeof(PurchaseRequest).FullName!;
        user.CommitChanges();
        using var check = db.NewObjectSpace();
        Assert.Equal(DefinitionState.Draft, check.FirstOrDefault<WorkflowDefinition>(x => x.Name == name)!.State);
    }

    [Fact] public void New_definition_numbers_past_versions_the_caller_cannot_read()   // XSM-018 (Codex)
    {
        var name = PublishPurchaseRequest();   // published v1
        var user = $"drafts-{Guid.NewGuid():N}";
        using (var os = db.NewObjectSpace())
        {
            var role = os.CreateObject<PermissionPolicyRole>();
            role.Name = user;   // may create and edit drafts, reads drafts only
            role.AddTypePermission<WorkflowDefinition>(SecurityOperations.Create + ";" + SecurityOperations.Write, SecurityPermissionState.Allow);
            role.AddObjectPermission<WorkflowDefinition>(SecurityOperations.Read, new BinaryOperator(nameof(WorkflowDefinition.State), DefinitionState.Draft).ToString(), SecurityPermissionState.Allow);
            var u = os.CreateObject<ApplicationUser>();
            (u.UserName, u.IsActive) = (user, true);
            u.SetPassword("");
            u.Roles.Add(role);
            os.CommitChanges();
            ((ISecurityUserWithLoginInfo)u).CreateUserLoginInfo(SecurityDefaults.PasswordAuthentication, os.GetKeyValueAsString(u));
            os.CommitChanges();
        }
        using var secured = LogOn(user).CreateObjectSpace();
        Assert.False(secured.GetObjectsQuery<WorkflowDefinition>().Any(d => d.Name == name));   // v1 is hidden from this caller
        var draft = secured.CreateObject<WorkflowDefinition>();
        (draft.Name, draft.TargetTypeName) = (name, typeof(PurchaseRequest).FullName!);
        secured.CommitChanges();
        Assert.Equal(2, draft.Version);
    }

    [Fact] public void Participants_see_their_own_requester_notifications_only()   // XSM-014
    {
        var def = Fixtures.PurchaseRequest() with { Name = $"pr-{Guid.NewGuid():N}" };
        def = def with { States = def.States.Select(s => s.Key == "draft" ? s with { OnEntry = [new(EffectKind.Notify, EffectDef.Requester, "Started: {Title}")] } : s).ToList() };
        var name = Publish(def);
        var other = $"approver2-{Guid.NewGuid():N}";   // shares the Approvers role with 'approver'
        using (var os = db.NewObjectSpace())
        {
            var u = os.CreateObject<ApplicationUser>();
            (u.UserName, u.IsActive) = (other, true);
            u.SetPassword("");
            u.Roles.Add(os.FirstOrDefault<PermissionPolicyRole>(r => r.Name == "Approvers"));
            os.CommitChanges();
            ((ISecurityUserWithLoginInfo)u).CreateUserLoginInfo(SecurityDefaults.PasswordAuthentication, os.GetKeyValueAsString(u));
            os.CommitChanges();
        }
        using var approverSpace = LogOn(Approver, out var security).CreateObjectSpace();
        var pr = approverSpace.CreateObject<PurchaseRequest>();
        (pr.Title, pr.Amount) = ("Mine", 5);
        approverSpace.CommitChanges();
        Ok(rt.Start(new WorkflowCaller(Approver, "Approvers".Equals, approverSpace, db.NewObjectSpace, security, (Guid)security.UserId), typeof(PurchaseRequest), pr.ID, name));
        Guid noteId;
        using (var check = db.NewObjectSpace())
            noteId = check.GetObjectsQuery<WorkflowNotification>().Single(n => n.Instance!.SubjectId == pr.ID).ID;

        using (var mine = LogOn(Approver).CreateObjectSpace())
        {
            Assert.NotNull(mine.GetObjectByKey<WorkflowNotification>(noteId));
            Assert.True(mine.GetObjectsQuery<WorkflowNotification>().Any(n => n.ID == noteId));
        }
        using var theirs = LogOn(other).CreateObjectSpace();
        Assert.Null(theirs.GetObjectByKey<WorkflowNotification>(noteId));
        Assert.False(theirs.GetObjectsQuery<WorkflowNotification>().Any(n => n.ID == noteId));
    }

    [Fact] public void Designer_can_publish_under_security()
    {
        using var provider = LogOn(Designer);
        using var user = provider.CreateObjectSpace();
        // The designer's secured space cannot read roles: the reason for the role catalogue.
        Assert.Empty(user.GetObjectsQuery<PermissionPolicyRole>().ToList());

        var name = $"d-{Guid.NewGuid():N}";
        var d = svc.NewDraft(user, name);
        (d.TargetTypeName, d.InitialStateKey) = (typeof(PurchaseRequest).FullName!, "draft");
        State(user, d, "draft", StateKind.Manual);
        var review = State(user, d, "review", StateKind.Task, "Approvers");
        State(user, d, "done", StateKind.Final);
        Transition(user, d, "draft", "Submit", "review");
        Transition(user, d, "review", "Approve", "done");
        user.CommitChanges();

        // The editor's lookup still lists the role, through the non-secured catalogue.
        Assert.Contains("Approvers", ((IStringLookupSource)review).LookupValues(nameof(WorkflowState.TaskRole), Roles));
        Assert.True(svc.Validate(user, d, Roles()).Ok);
        var r = svc.Publish(user, d, Designer, Roles());
        Assert.True(r.Ok, string.Join("; ", r.Messages));
        using var check = db.NewObjectSpace();
        Assert.Equal(DefinitionState.Published, check.FirstOrDefault<WorkflowDefinition>(x => x.Name == name)!.State);
    }

    [Fact] public void Approver_reads_only_own_role_tasks_and_notifications()
    {
        // Another role's task + notification: started and submitted by a caller in that role.
        var name = Publish(new WorkflowDef($"o-{Guid.NewGuid():N}", 1, "draft",
            [
                new("draft", "Draft", StateKind.Manual),
                new("review", "Review", StateKind.Task, TaskRole: Others, OnEntry: [new(EffectKind.Notify, Others, "Review {Title}")]),
                new("done", "Done", StateKind.Final),
            ],
            [new("draft", "Submit", "review"), new("review", "Approve", "done")]));
        Guid foreignRequest;
        using (var os = db.NewObjectSpace())
        {
            var pr = os.CreateObject<PurchaseRequest>();
            pr.Title = "Foreign";
            os.CommitChanges();
            foreignRequest = pr.ID;
        }
        var other = db.Caller("other", Others);
        Ok(rt.Start(other, typeof(PurchaseRequest), foreignRequest, name));
        Ok(rt.Fire(other, typeof(PurchaseRequest), foreignRequest, "Submit"));
        Guid foreignTask, foreignNote;
        using (var os = db.NewObjectSpace())
        {
            foreignTask = os.GetObjectsQuery<WorkflowTask>().Single(t => t.Instance!.SubjectId == foreignRequest).ID;
            foreignNote = os.GetObjectsQuery<WorkflowNotification>().Single(n => n.Instance!.SubjectId == foreignRequest).ID;
        }

        using var provider = LogOn(Approver, out var security);
        Guid ownRequest, ownTask;
        using (var user = provider.CreateObjectSpace()) (ownRequest, ownTask) = Submitted(user, security);
        using (var user = provider.CreateObjectSpace())
        {
            Assert.Null(user.GetObjectByKey<WorkflowTask>(foreignTask));
            Assert.Empty(user.GetObjectsQuery<WorkflowTask>().Where(t => t.ID == foreignTask).ToList());
            Assert.Null(user.GetObjectByKey<WorkflowNotification>(foreignNote));
            Assert.Empty(user.GetObjectsQuery<WorkflowNotification>().Where(n => n.Role == Others).ToList());

            Assert.NotNull(user.GetObjectByKey<WorkflowTask>(ownTask));                       // its own role's rows stay readable
            Assert.All(user.GetObjectsQuery<WorkflowTask>().ToList(), t => Assert.Equal("Approvers", t.Role));
            Assert.Contains(user.GetObjectsQuery<WorkflowNotification>().ToList(), n => n.Instance!.SubjectId == ownRequest);
        }
        // The runtime still decides for the approver through its secured read proof.
        using (var user = provider.CreateObjectSpace())
            Ok(rt.Decide(Caller(user, security, Approver, "Approvers"), ownTask, "Approve"));
    }

    [Fact] public void Approver_reads_only_instances_of_granted_subject_types()
    {
        var name = PublishPurchaseRequest();
        Guid foreignInstance;
        using (var os = db.NewObjectSpace())
        {
            var i = os.CreateObject<WorkflowInstance>();
            (i.Definition, i.SubjectType, i.SubjectId, i.StateKey) =
                (os.FirstOrDefault<WorkflowDefinition>(d => d.Name == name), "Some.Other.Subject", Guid.NewGuid(), "draft");
            using (RuntimeScope.Open(((DevExpress.ExpressApp.EFCore.EFCoreObjectSpace)os).DbContext)) os.CommitChanges();   // instances are runtime-created
            foreignInstance = i.ID;
        }
        using var provider = LogOn(Approver, out var security);
        Guid ownRequest;
        using (var user = provider.CreateObjectSpace()) (ownRequest, _) = Submitted(user, security);
        using (var user = provider.CreateObjectSpace())
        {
            Assert.Null(user.GetObjectByKey<WorkflowInstance>(foreignInstance));
            Assert.Empty(user.GetObjectsQuery<WorkflowInstance>().Where(i => i.SubjectType == "Some.Other.Subject").ToList());
            Assert.NotNull(user.GetObjectByKey<PurchaseRequest>(ownRequest).Workflow);             // its own subject's instance
        }
    }

    const string NeedsWrite = "changing the workflow of this PurchaseRequest requires write access to it";
    const string CannotRead = "the PurchaseRequest does not exist or cannot be read";

    /// <summary>A new request (amount 50) under a freshly published definition, optionally started by an admin-style caller.</summary>
    (Guid Request, string Definition) NewRequest(bool start)
    {
        var name = PublishPurchaseRequest();
        Guid id;
        using (var os = db.NewObjectSpace())
        {
            var pr = os.CreateObject<PurchaseRequest>();
            (pr.Title, pr.Amount) = ("Chair", 50);
            os.CommitChanges();
            id = pr.ID;
        }
        if (start) Ok(rt.Start(db.Caller("admin", "Approvers"), typeof(PurchaseRequest), id, name));
        return (id, name);
    }

    string? StateOf(Guid request)
    {
        using var check = db.NewObjectSpace();
        return check.GetObjectByKey<PurchaseRequest>(request).Workflow?.StateKey;
    }

    [Fact] public void Read_only_caller_cannot_start_or_fire()
    {
        using var provider = LogOn(Reader, out var security);
        var (fresh, name) = NewRequest(false);
        var (started, _) = NewRequest(true);   // in draft; Submit has no RequiredRole
        using (var user = provider.CreateObjectSpace())
        {
            Assert.NotNull(user.GetObjectByKey<PurchaseRequest>(fresh));   // control: the subject is readable
            var c = Caller(user, security, Reader, "Readers");
            var start = rt.Start(c, typeof(PurchaseRequest), fresh, name);
            var fire = rt.Fire(c, typeof(PurchaseRequest), started, "Submit");
            Assert.Equal((false, NeedsWrite), (start.Ok, start.Message));
            Assert.Equal((false, NeedsWrite), (fire.Ok, fire.Message));
            Assert.Empty(rt.Available(c, typeof(PurchaseRequest), started));
        }
        Assert.Null(StateOf(fresh));
        Assert.Equal("draft", StateOf(started));
    }

    [Fact] public void Caller_who_cannot_read_subject_cannot_start_or_fire()
    {
        using var provider = LogOn(Outsider, out var security);
        var (fresh, name) = NewRequest(false);
        var (started, _) = NewRequest(true);
        using (var user = provider.CreateObjectSpace())
        {
            Assert.Null(user.GetObjectByKey<PurchaseRequest>(fresh));
            var c = Caller(user, security, Outsider, "Outsiders");
            var start = rt.Start(c, typeof(PurchaseRequest), fresh, name);
            var fire = rt.Fire(c, typeof(PurchaseRequest), started, "Submit");
            Assert.Equal((false, CannotRead), (start.Ok, start.Message));
            Assert.Equal((false, CannotRead), (fire.Ok, fire.Message));
        }
        Assert.Null(StateOf(fresh));
        Assert.Equal("draft", StateOf(started));
    }

    [Fact] public void Secured_caller_without_security_strategy_is_refused()
    {
        // Fail closed: a secured space without the strategy that secures it cannot prove write access.
        using var provider = LogOn(Approver);
        var (fresh, name) = NewRequest(false);
        using (var user = provider.CreateObjectSpace())
        {
            var r = rt.Start(new WorkflowCaller(Approver, "Approvers".Equals, user, db.NewObjectSpace), typeof(PurchaseRequest), fresh, name);
            Assert.Equal((false, NeedsWrite), (r.Ok, r.Message));
        }
        Assert.Null(StateOf(fresh));
    }

    // ---- XSM-013: admins see all open tasks; Open task on the request ----

    /// <summary>An open task of role <see cref="Others"/> on a fresh request; returns (request id, task id).</summary>
    (Guid Request, Guid Task) ForeignOpenTask()
    {
        var name = Publish(new WorkflowDef($"o-{Guid.NewGuid():N}", 1, "draft",
            [new("draft", "Draft", StateKind.Manual), new("review", "Review", StateKind.Task, TaskRole: Others), new("done", "Done", StateKind.Final)],
            [new("draft", "Submit", "review"), new("review", "Approve", "done")]));
        Guid request;
        using (var os = db.NewObjectSpace())
        {
            var pr = os.CreateObject<PurchaseRequest>();
            pr.Title = "Foreign";
            os.CommitChanges();
            request = pr.ID;
        }
        var other = db.Caller("other", Others);
        Ok(rt.Start(other, typeof(PurchaseRequest), request, name));
        Ok(rt.Fire(other, typeof(PurchaseRequest), request, "Submit"));
        using var check = db.NewObjectSpace();
        return (request, check.GetObjectsQuery<WorkflowTask>().Single(t => t.Instance!.SubjectId == request && t.State == TaskState.Open).ID);
    }

    /// <summary>A task written directly (runtime-owned rows need a runtime scope) on the request's instance.</summary>
    Guid AddTask(Guid request, string stateKey, string role, TaskState state = TaskState.Open)
    {
        using var os = db.NewObjectSpace();
        var t = os.CreateObject<WorkflowTask>();
        (t.Instance, t.StateKey, t.Role, t.Subject, t.State) = (os.GetObjectByKey<PurchaseRequest>(request).Workflow, stateKey, role, "x", state);
        using (RuntimeScope.Open(((DevExpress.ExpressApp.EFCore.EFCoreObjectSpace)os).DbContext)) os.CommitChanges();
        return t.ID;
    }

    /// <summary>The Workflow Task list's own criteria for <paramref name="userName"/>, run in that user's secured space. The
    /// user row comes from a live non-secured space: LogOn disposes its logon space, so security.User cannot lazy-load roles.</summary>
    List<Guid> Listed(IObjectSpace user, string userName)
    {
        using var os = db.NewObjectSpace();
        var u = os.FirstOrDefault<ApplicationUser>(x => x.UserName == userName);
        return user.GetObjects<WorkflowTask>(MyTasksController.Criteria(WorkflowCommand.RoleNames(u), WorkflowCommand.IsAdministrator(u)))
            .Select(t => t.ID).ToList();
    }

    [Fact] public void Administrator_is_the_IsAdministrative_role_not_a_role_name()
    {
        using var os = db.NewObjectSpace();
        Assert.True(WorkflowCommand.IsAdministrator(os.FirstOrDefault<ApplicationUser>(u => u.UserName == Admin)));   // role "Superusers"
        Assert.False(WorkflowCommand.IsAdministrator(os.FirstOrDefault<ApplicationUser>(u => u.UserName == Approver)));
        Assert.False(WorkflowCommand.IsAdministrator((object?)null));
    }

    [Fact] public void Task_list_admin_sees_every_open_task_participant_only_own_role()
    {
        var (_, foreign) = ForeignOpenTask();
        using var approverProvider = LogOn(Approver, out var approverSecurity);
        Guid decided, own, withSuperseded;
        using (var user = approverProvider.CreateObjectSpace())
        {
            (_, decided) = Submitted(user, approverSecurity);
            Ok(rt.Decide(Caller(user, approverSecurity, Approver, "Approvers"), decided, "Reject"));
            (withSuperseded, own) = Submitted(user, approverSecurity);
        }
        var superseded = AddTask(withSuperseded, "approval", "Approvers", TaskState.Superseded);

        using (var provider = LogOn(Admin))
        using (var user = provider.CreateObjectSpace())
        {
            var listed = Listed(user, Admin);
            Assert.Contains(foreign, listed);
            Assert.Contains(own, listed);
            Assert.DoesNotContain(decided, listed);
            Assert.DoesNotContain(superseded, listed);
        }
        using (var user = approverProvider.CreateObjectSpace())
        {
            var listed = Listed(user, Approver);
            Assert.Contains(own, listed);
            Assert.DoesNotContain(foreign, listed);
            Assert.DoesNotContain(decided, listed);
            Assert.DoesNotContain(superseded, listed);
        }
        using (var provider = LogOn(Outsider))
        using (var user = provider.CreateObjectSpace())
            Assert.Empty(Listed(user, Outsider));   // no task role: nothing listed
    }

    static Guid? OpenTask(IObjectSpace user, Guid request) =>
        SubjectWorkflowController.OpenTaskId(user, user.GetObjectByKey<PurchaseRequest>(request));

    [Fact] public void Open_task_available_to_approver_and_admin_for_the_current_open_task()
    {
        using var provider = LogOn(Approver, out var security);
        Guid request, task;
        using (var user = provider.CreateObjectSpace()) (request, task) = Submitted(user, security);
        using (var user = provider.CreateObjectSpace()) Assert.Equal(task, OpenTask(user, request));
        using var adminProvider = LogOn(Admin);
        using (var user = adminProvider.CreateObjectSpace()) Assert.Equal(task, OpenTask(user, request));
    }

    [Fact] public void Open_task_not_available_without_workflow()
    {
        var (request, _) = NewRequest(false);
        using var provider = LogOn(Admin);
        using var user = provider.CreateObjectSpace();
        Assert.Null(OpenTask(user, request));
    }

    [Fact] public void Open_task_not_available_for_a_closed_task()
    {
        using var provider = LogOn(Approver, out var security);
        Guid request, task;
        using (var user = provider.CreateObjectSpace()) (request, task) = Submitted(user, security);
        using (var user = provider.CreateObjectSpace()) Ok(rt.Decide(Caller(user, security, Approver, "Approvers"), task, "Reject"));
        using (var user = provider.CreateObjectSpace()) Assert.Null(OpenTask(user, request));
    }

    [Fact] public void Open_task_not_available_for_another_state_or_another_instance()
    {
        var (a, _) = NewRequest(true);   // both in draft
        var (b, _) = NewRequest(true);
        AddTask(a, "approval", "Approvers");            // open, but for a state its instance is not in
        var onB = AddTask(b, "draft", "Approvers");     // open and current on b: another instance than a's
        using var provider = LogOn(Approver);
        using var user = provider.CreateObjectSpace();
        Assert.Null(OpenTask(user, a));
        Assert.Equal(onB, OpenTask(user, b));           // control
    }

    [Fact] public void Open_task_not_available_without_read_on_the_task()
    {
        using var approverProvider = LogOn(Approver, out var security);
        Guid request, task;
        using (var user = approverProvider.CreateObjectSpace()) (request, task) = Submitted(user, security);
        using var readerProvider = LogOn(Reader);
        using (var reader = readerProvider.CreateObjectSpace())
        {
            Assert.NotNull(reader.GetObjectByKey<PurchaseRequest>(request));   // control: the request itself is readable
            Assert.Null(OpenTask(reader, request));
        }
        using var approver = approverProvider.CreateObjectSpace();
        Assert.Equal(task, OpenTask(approver, request));
    }

    [Fact] public void Open_task_rechecked_on_execute_after_it_closed()
    {
        using var provider = LogOn(Approver, out var security);
        Guid request, task;
        using (var user = provider.CreateObjectSpace()) (request, task) = Submitted(user, security);
        using var view = provider.CreateObjectSpace();   // the request view's space: available, then the task closes elsewhere
        var subject = view.GetObjectByKey<PurchaseRequest>(request);
        Assert.Equal(task, SubjectWorkflowController.OpenTaskId(view, subject));
        var created = new List<IObjectSpace>();
        IObjectSpace NewSpace() { var s = provider.CreateObjectSpace(); created.Add(s); return s; }

        var open = SubjectWorkflowController.PrepareOpen(NewSpace, subject);   // still open: the task, in the new space, left open
        Assert.NotNull(open);
        Assert.Same(created[0], open.Value.Space);
        Assert.Equal(task, open.Value.Task.ID);
        Assert.False(created[0].IsDisposed);
        created[0].Dispose();   // the root view's job in the app

        using (var other = provider.CreateObjectSpace()) Ok(rt.Decide(Caller(other, security, Approver, "Approvers"), task, "Reject"));
        Assert.Null(SubjectWorkflowController.PrepareOpen(NewSpace, subject));   // closed meanwhile: nothing to open ...
        Assert.Equal(2, created.Count);
        Assert.True(created[1].IsDisposed);                                    // ... and the space it made is disposed
    }
}
