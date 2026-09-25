using DevExpress.EntityFrameworkCore.Security;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.EFCore.DataLocking;
using DevExpress.ExpressApp.Security;
using Microsoft.EntityFrameworkCore;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using XafStateless.Demo.Module.BusinessObjects;
using XafStateless.Engine;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Services;

namespace XafStateless.Module.Tests;

/// <summary>The seeded role matrix (<see cref="WorkflowPermissions"/>) under real XAF security: secured object spaces
/// logged on as an approver and as a designer.</summary>
public class PermissionTests : IClassFixture<TestDb>
{
    const string Approver = "approver", Designer = "designer";
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
    string PublishPurchaseRequest()
    {
        var name = $"pr-{Guid.NewGuid():N}";
        using var os = db.NewObjectSpace();
        var d = os.CreateObject<WorkflowDefinition>();
        (d.Name, d.TargetTypeName, d.Version, d.State, d.InitialStateKey) = (name, typeof(PurchaseRequest).FullName!, 1, DefinitionState.Draft, "draft");
        State(os, d, "draft", StateKind.Manual);
        State(os, d, "approval", StateKind.Task, "Approvers");
        State(os, d, "decide-amount", StateKind.Decision);
        State(os, d, "wait-cooling", StateKind.Wait).WaitMinutes = 1;
        var ordered = State(os, d, "ordered", StateKind.Final);
        var set = os.CreateObject<WorkflowEffect>();
        (set.Kind, set.Target, set.Value) = (EffectKind.SetField, "Ordered", "true");
        ordered.Effects.Add(set);
        State(os, d, "rejected", StateKind.Final);
        Transition(os, d, "draft", "Submit", "approval");
        Transition(os, d, "approval", "Approve", "decide-amount");
        Transition(os, d, "approval", "Reject", "rejected");
        Transition(os, d, "decide-amount", "Large", "wait-cooling").Criteria = "Amount > 1000";
        Transition(os, d, "decide-amount", "Small", "ordered").Order = 1;
        Transition(os, d, "wait-cooling", "Cooled", "ordered");
        os.CommitChanges();
        var r = svc.Publish(os, d, "tester", Roles());
        Assert.True(r.Ok, string.Join("; ", r.Messages));
        return name;
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
        Assert.ThrowsAny<Exception>(() => user.CommitChanges());
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
}
