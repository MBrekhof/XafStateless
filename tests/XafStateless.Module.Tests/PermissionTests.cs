using DevExpress.EntityFrameworkCore.Security;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.EFCore.DataLocking;
using DevExpress.ExpressApp.Security;
using Microsoft.EntityFrameworkCore;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using XafStateless.Demo.Module.BusinessObjects;
using XafStateless.Engine;
using XafStateless.Engine.Tests;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Services;

namespace XafStateless.Module.Tests;

/// <summary>The seeded role matrix (<see cref="WorkflowPermissions"/>) under real XAF security: secured object spaces
/// logged on as an approver and as a designer.</summary>
public class PermissionTests : IClassFixture<TestDb>
{
    const string Approver = "approver", Designer = "designer", Others = "Others";
    readonly TestDb db;
    readonly WorkflowRuntime rt = new(new DefinitionStore(), TimeProvider.System);
    readonly DefinitionService svc = new(new DefinitionStore());

    public PermissionTests(TestDb db)
    {
        this.db = db;
        using var os = db.NewObjectSpace();
        if (os.FirstOrDefault<PermissionPolicyRole>(r => r.Name == "Approvers") != null) return;   // one seed per fixture
        var approvers = os.CreateObject<PermissionPolicyRole>();
        approvers.Name = "Approvers";
        WorkflowPermissions.GrantParticipant(approvers, (typeof(PurchaseRequest), "PurchaseRequest_ListView"));
        os.CreateObject<PermissionPolicyRole>().Name = Others;   // addressee of tasks the approver must not see
        var designers = os.CreateObject<PermissionPolicyRole>();
        designers.Name = "WorkflowDesigners";
        WorkflowPermissions.GrantDesigner(designers);
        foreach (var (name, role) in new[] { (Approver, approvers), (Designer, designers) })
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

    WorkflowCaller Caller(IObjectSpace secured, string name, params string[] roles) => new(name, roles.Contains, secured, db.NewObjectSpace);

    static void Ok(RunResult r) => Assert.True(r.Ok, r.Message);

    /// <summary>A small request started and submitted by the approver; returns (request id, open task id).</summary>
    (Guid Request, Guid Task) Submitted(IObjectSpace approverSpace)
    {
        var name = PublishPurchaseRequest();
        var pr = approverSpace.CreateObject<PurchaseRequest>();
        (pr.Title, pr.Amount) = ("Chair", 50);
        approverSpace.CommitChanges();
        var c = Caller(approverSpace, Approver, "Approvers");
        Ok(rt.Start(c, typeof(PurchaseRequest), pr.ID, name));
        Ok(rt.Fire(c, typeof(PurchaseRequest), pr.ID, "Submit"));
        using var check = db.NewObjectSpace();
        var task = check.GetObjectsQuery<WorkflowTask>().Single(t => t.Instance!.SubjectId == pr.ID && t.State == TaskState.Open);
        return (pr.ID, task.ID);
    }

    [Fact] public void Approver_can_start_fire_and_decide()
    {
        using var provider = LogOn(Approver);
        using var user = provider.CreateObjectSpace();
        Assert.IsType<SecuredEFCoreObjectSpace>(user);
        var (request, task) = Submitted(user);
        var c = Caller(user, Approver, "Approvers");
        Assert.Equal(["Approve", "Reject"], rt.Outcomes(c, task));

        Ok(rt.Decide(c, task, "Approve"));

        using var check = db.NewObjectSpace();
        var pr = check.GetObjectByKey<PurchaseRequest>(request);
        Assert.Equal("ordered", pr.Workflow!.StateKey);
        Assert.True(pr.Ordered);
        Assert.Equal((TaskState.Decided, Approver), (check.GetObjectByKey<WorkflowTask>(task).State, check.GetObjectByKey<WorkflowTask>(task).DecidedBy));
    }

    [Fact] public void Approver_cannot_edit_task_role()
    {
        using var provider = LogOn(Approver, out var security);
        Guid task;
        using (var user = provider.CreateObjectSpace()) (_, task) = Submitted(user);
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
        using var provider = LogOn(Approver);
        Guid request;
        using (var user = provider.CreateObjectSpace()) (request, _) = Submitted(user);
        using (var user = provider.CreateObjectSpace())
        {
            var pr = user.GetObjectByKey<PurchaseRequest>(request);
            Assert.NotNull(pr.Workflow);   // load the link first: nulling a never-loaded lazy reference is no change at all
            pr.Workflow = null;
            Assert.Contains("prohibited by security rules", Assert.ThrowsAny<Exception>(() => user.CommitChanges()).Message);
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

        using var provider = LogOn(Approver);
        Guid ownRequest, ownTask;
        using (var user = provider.CreateObjectSpace()) (ownRequest, ownTask) = Submitted(user);
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
            Ok(rt.Decide(Caller(user, Approver, "Approvers"), ownTask, "Approve"));
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
        using var provider = LogOn(Approver);
        Guid ownRequest;
        using (var user = provider.CreateObjectSpace()) (ownRequest, _) = Submitted(user);
        using (var user = provider.CreateObjectSpace())
        {
            Assert.Null(user.GetObjectByKey<WorkflowInstance>(foreignInstance));
            Assert.Empty(user.GetObjectsQuery<WorkflowInstance>().Where(i => i.SubjectType == "Some.Other.Subject").ToList());
            Assert.NotNull(user.GetObjectByKey<PurchaseRequest>(ownRequest).Workflow);             // its own subject's instance
        }
    }
}
