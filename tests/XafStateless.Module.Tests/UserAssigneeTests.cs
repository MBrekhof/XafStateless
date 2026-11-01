using DevExpress.ExpressApp;
using DevExpress.ExpressApp.EFCore;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using XafStateless.Demo.Module.BusinessObjects;
using XafStateless.Engine;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Controllers;
using XafStateless.Module.Services;

namespace XafStateless.Module.Tests;

/// <summary>CARD-2615 (1): a Task state assigned to a user member of the subject (<c>@Buyer</c>), and a Notify to it.</summary>
public class UserAssigneeTests : IClassFixture<TestDb>
{
    readonly TestDb db;
    readonly Clock clock = new(new DateTimeOffset(2018, 1, 1, 0, 0, 0, TimeSpan.Zero));
    readonly WorkflowRuntime rt;
    readonly Guid buyer, other, inactive;

    public UserAssigneeTests(TestDb db)
    {
        this.db = db;
        rt = new WorkflowRuntime(new DefinitionStore(), clock);
        using var os = db.NewObjectSpace();
        if (os.FirstOrDefault<PermissionPolicyRole>(r => r.Name == "Approvers") == null)
        {
            os.CreateObject<PermissionPolicyRole>().Name = "Approvers";
            foreach (var (name, active) in new[] { ("buyer", true), ("other", true), ("inactive", false) })
            {
                var u = os.CreateObject<ApplicationUser>();
                (u.UserName, u.IsActive) = (name, active);
            }
            os.CommitChanges();
        }
        Guid Id(string name) => os.FirstOrDefault<ApplicationUser>(u => u.UserName == name)!.ID;
        (buyer, other, inactive) = (Id("buyer"), Id("other"), Id("inactive"));
    }

    sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>draft (tells @Buyer) -Submit-> [wait 1 min -Cooled->] review (task for @Buyer) -Approve-> done / -Reject-> rejected.</summary>
    static WorkflowDef Flow(string name, bool wait = false) => new(name, 1, "draft",
        [
            new("draft", "Draft", StateKind.Manual, OnEntry: [new(EffectKind.Notify, "@Buyer", "Started: {Title}")]),
            .. wait ? new StateDef[] { new("cooling", "Cooling", StateKind.Wait, WaitMinutes: 1) } : [],
            new("review", "Review", StateKind.Task, TaskRole: "@Buyer"),
            new("done", "Done", StateKind.Final),
            new("rejected", "Rejected", StateKind.Final),
        ],
        [
            new("draft", "Submit", wait ? "cooling" : "review"),
            .. wait ? new TransitionDef[] { new("cooling", "Cooled", "review") } : [],
            new("review", "Approve", "done"), new("review", "Reject", "rejected"),
        ]);

    static IReadOnlyList<string> Roles(params string[] extra) => ["Approvers", .. extra];

    static IReadOnlyList<string> Validate(WorkflowDef def, params string[] extraRoles) =>
        WorkflowValidator.Validate(def, new XafWorkflowEnvironment(Roles(extraRoles), XafTypesInfo.Instance.FindTypeInfo(typeof(PurchaseRequest))));

    string Published(bool wait = false)
    {
        var def = Flow($"ua-{Guid.NewGuid():N}", wait);
        using var os = db.NewObjectSpace();
        var d = DefinitionMapper.ToDraft(os, def, typeof(PurchaseRequest).FullName!);
        os.CommitChanges();
        var r = new DefinitionService(new DefinitionStore()).Publish(os, d, "tester", WorkflowRoleCatalog.Names(os));
        Assert.True(r.Ok, string.Join("; ", r.Messages));
        return def.Name;
    }

    Guid NewRequest(string title, Guid? buyerId)
    {
        using var os = db.NewObjectSpace();
        var pr = os.CreateObject<PurchaseRequest>();
        (pr.Title, pr.Buyer) = (title, buyerId is { } id ? os.GetObjectByKey<ApplicationUser>(id) : null);
        os.CommitChanges();
        return pr.ID;
    }

    /// <summary>A caller in every role, keyed by <paramref name="userId"/>.</summary>
    WorkflowCaller Caller(string name, Guid? userId) => new(name, _ => true, db.NewObjectSpace(), db.NewObjectSpace, null, userId);

    static void Ok(RunResult r) => Assert.True(r.Ok, r.Message);

    WorkflowInstance Instance(Guid subject) => db.NewObjectSpace().GetObjectByKey<PurchaseRequest>(subject).Workflow!;

    List<WorkflowTask> Tasks(Guid subject) =>
        db.NewObjectSpace().GetObjectsQuery<WorkflowTask>().Where(t => t.Instance!.SubjectId == subject).ToList();

    List<WorkflowNotification> Notes(Guid subject) =>
        db.NewObjectSpace().GetObjectsQuery<WorkflowNotification>().Where(n => n.Instance!.SubjectId == subject).ToList();

    (Guid Request, Guid Task) Submitted(Guid? buyerId)
    {
        var name = Published();
        var id = NewRequest("Desk", buyerId);
        Ok(rt.Start(Caller("starter", other), typeof(PurchaseRequest), id, name));
        Ok(rt.Fire(Caller("starter", other), typeof(PurchaseRequest), id, "Submit"));
        return (id, Tasks(id).Single().ID);
    }

    [Fact] public void The_task_goes_to_the_buyer_and_only_the_buyer_decides_it()
    {
        var (id, taskId) = Submitted(buyer);
        var task = Tasks(id).Single();
        Assert.Equal((buyer, "buyer", "", TaskState.Open), (task.UserId!.Value, task.UserName, task.Role, task.State));

        var someoneElse = Caller("other", other);   // in every role, but not the buyer
        Assert.Empty(rt.Outcomes(someoneElse, taskId));
        var refused = rt.Decide(someoneElse, taskId, "Approve");
        Assert.False(refused.Ok);
        Assert.Contains("assigned to 'buyer'", refused.Message);
        Assert.Empty(rt.Outcomes(Caller("nobody", null), taskId));

        Assert.Equal(["Approve", "Reject"], rt.Outcomes(Caller("buyer", buyer), taskId).Order());
        Ok(rt.Decide(Caller("buyer", buyer), taskId, "Approve"));
        Assert.Equal("done", Instance(id).StateKey);
    }

    [Theory, InlineData(false), InlineData(true)]
    public void An_empty_or_inactive_buyer_refuses_entering_the_task(bool inactiveBuyer)
    {
        var name = Published();
        var id = NewRequest("Desk", inactiveBuyer ? inactive : null);
        Ok(rt.Start(Caller("starter", other), typeof(PurchaseRequest), id, name));
        var ex = Assert.IsType<UserFriendlyException>(Record.Exception(() => rt.Fire(Caller("starter", other), typeof(PurchaseRequest), id, "Submit")));
        Assert.Contains("'review' is assigned to 'Buyer'", ex.Message);
        Assert.Equal("draft", Instance(id).StateKey);
        Assert.Empty(Tasks(id));
    }

    [Fact] public void A_changed_buyer_does_not_move_the_open_task()
    {
        var (id, taskId) = Submitted(buyer);
        using (var os = db.NewObjectSpace())
        {
            os.GetObjectByKey<PurchaseRequest>(id).Buyer = os.GetObjectByKey<ApplicationUser>(other);
            os.CommitChanges();
        }
        Assert.Equal(buyer, Tasks(id).Single().UserId);
        Assert.False(rt.Decide(Caller("other", other), taskId, "Approve").Ok);
        Ok(rt.Decide(Caller("buyer", buyer), taskId, "Reject"));
    }

    [Fact] public void Notify_goes_to_the_buyer_and_an_empty_buyer_is_skipped_with_a_note()
    {
        var name = Published();
        var id = NewRequest("Desk", buyer);
        Ok(rt.Start(Caller("starter", other), typeof(PurchaseRequest), id, name));
        var note = Notes(id).Single();
        Assert.Equal((buyer, "buyer", "", "Started: Desk"), (note.UserId!.Value, note.UserName, note.Role, WorkflowNotifications.Render(note, db.NewObjectSpace(), null)));

        var empty = NewRequest("Lamp", null);
        var r = rt.Start(Caller("starter", other), typeof(PurchaseRequest), empty, name);
        Ok(r);
        Assert.Contains("notification to 'Buyer' was skipped", r.Message);
        Assert.Empty(Notes(empty));
    }

    [Fact] public void A_timer_into_the_task_with_an_empty_buyer_is_skipped_and_stays_due()
    {
        var name = Published(wait: true);
        var id = NewRequest("Desk", null);
        Ok(rt.Start(Caller("starter", other), typeof(PurchaseRequest), id, name));
        Ok(rt.Fire(Caller("starter", other), typeof(PurchaseRequest), id, "Submit"));
        clock.Now = clock.Now.AddMinutes(2);
        Assert.Equal(0, rt.RunDueTimers(db.NewObjectSpace));
        var instance = Instance(id);
        Assert.Equal("cooling", instance.StateKey);
        Assert.NotNull(instance.DueUtc);
        Assert.Empty(Tasks(id));
    }

    [Fact] public void Validation_accepts_user_members_and_refuses_other_at_targets()
    {
        var def = Flow("v");
        Assert.Empty(Validate(def));
        Assert.Empty(Validate(def, "@Other"));   // an @ role that no target uses collides with nothing

        static WorkflowDef Retarget(WorkflowDef d, string task, string notify) => d with
        {
            States = d.States.Select(s => s.Key switch
            {
                "review" => s with { TaskRole = task },
                "draft" => s with { OnEntry = [new(EffectKind.Notify, notify, "x")] },
                _ => s,
            }).ToList(),
        };
        foreach (var bad in new[] { "@Title", "@Nope" })
        {
            Assert.Contains(Validate(Retarget(def, bad, "@Buyer")), m => m.Contains($"'{bad[1..]}' is not a reference"));
            Assert.Contains(Validate(Retarget(def, "@Buyer", bad)), m => m.Contains($"'{bad[1..]}' is not a reference"));
        }
        Assert.Contains(Validate(Retarget(def, "@Buyer", "Approvers"), "@Buyer"), m => m.Contains("a role named '@Buyer' conflicts"));
        Assert.Contains(Validate(Retarget(def, "Approvers", "@Buyer"), "@Buyer"), m => m.Contains("a role named '@Buyer' conflicts"));
    }

    [Fact] public void Lookups_offer_the_user_members()
    {
        using var os = db.NewObjectSpace();
        var d = DefinitionMapper.ToDraft(os, Flow("lk"), typeof(PurchaseRequest).FullName!);
        var review = d.States.Single(s => s.Key == "review");
        Assert.Equal(["@Buyer", "Approvers"], ((IStringLookupSource)review).LookupValues(nameof(WorkflowState.TaskRole), () => ["Approvers"]).Order());
        var notify = d.States.Single(s => s.Key == "draft").Effects.Single();
        Assert.Equal(["@Buyer", "@requester", "Approvers"], ((IStringLookupSource)notify).LookupValues(nameof(WorkflowEffect.Target), () => ["Approvers"]).Order());
    }

    [Theory, InlineData(false), InlineData(true)]
    public void A_tasks_user_is_fixed_inside_and_outside_the_runtime(bool inScope)
    {
        var (_, taskId) = Submitted(buyer);
        using var os = db.NewObjectSpace();
        var task = os.GetObjectByKey<WorkflowTask>(taskId);
        (task.UserId, task.UserName) = (other, "other");
        using var scope = inScope ? RuntimeScope.Open(((EFCoreObjectSpace)os).DbContext) : null;
        Assert.Contains("fixed", Assert.IsType<UserFriendlyException>(Record.Exception(() => os.CommitChanges())).Message);
    }

    /// <summary>XAF picks a default member by name ("Name" first, contains-match), so UserName would caption every task, empty
    /// for a role's task: the task keeps its subject as caption.</summary>
    [Fact] public void A_task_is_captioned_by_its_subject_not_its_user()
    {
        using var os = db.NewObjectSpace();
        Assert.Equal(nameof(WorkflowTask.Subject), os.TypesInfo.FindTypeInfo(typeof(WorkflowTask)).DefaultMember?.Name);
    }

    [Fact] public void The_task_list_shows_the_task_to_its_user_only()
    {
        var (_, taskId) = Submitted(buyer);
        using var os = db.NewObjectSpace();
        Assert.Contains(taskId, os.GetObjects<WorkflowTask>(MyTasksController.Criteria([], false, buyer)).Select(t => t.ID));
        Assert.DoesNotContain(taskId, os.GetObjects<WorkflowTask>(MyTasksController.Criteria(["Approvers"], false, other)).Select(t => t.ID));
        Assert.Contains(taskId, os.GetObjects<WorkflowTask>(MyTasksController.Criteria([], true, null)).Select(t => t.ID));
    }
}
