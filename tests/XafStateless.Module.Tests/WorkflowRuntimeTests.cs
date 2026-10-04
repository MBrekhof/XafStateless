using System.Data.Common;
using System.Text.RegularExpressions;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.EFCore;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using XafStateless.Demo.Module.BusinessObjects;
using XafStateless.Engine;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Services;

namespace XafStateless.Module.Tests;

public class WorkflowRuntimeTests : IClassFixture<TestDb>
{
    readonly TestDb db;
    readonly FakeClock clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    readonly RefundAction refund = new();   // the fixture's ordered state runs it (XSM-032)
    readonly WorkflowActionCatalog actions;
    readonly WorkflowRuntime rt;

    public WorkflowRuntimeTests(TestDb db)
    {
        this.db = db;
        actions = new([refund]);
        rt = new WorkflowRuntime(new DefinitionStore(), clock, actions);
        using var os = db.NewObjectSpace();
        foreach (var role in new[] { "Approvers", "Administrators" })
            if (os.FirstOrDefault<PermissionPolicyRole>(r => r.Name == role) == null)
                os.CreateObject<PermissionPolicyRole>().Name = role;
        os.CommitChanges();
    }

    sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    sealed class SaveCounter : SaveChangesInterceptor
    {
        public int Count;
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        { Count++; return result; }
    }

    /// <summary>Lets the first DML command execute, then throws before the next one (only while armed).</summary>
    sealed class FailSecondWrite : DbCommandInterceptor
    {
        public volatile bool Armed;
        public readonly List<string> Executed = [];
        public bool Injected;

        static bool Dml(DbCommand c) => Regex.IsMatch(c.CommandText, @"\b(INSERT|UPDATE|DELETE)\b", RegexOptions.IgnoreCase);
        void Before(DbCommand c)
        {
            if (!Armed || !Dml(c) || Executed.Count == 0 || Injected) return;
            Injected = true;
            throw new InvalidOperationException("injected failure");
        }
        void After(DbCommand c) { if (Armed && Dml(c)) Executed.Add(c.CommandText); }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result) { Before(command); return result; }
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result) { Before(command); return result; }
        public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result) { After(command); return result; }
        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result) { After(command); return result; }
    }

    static string Unique() => $"pr-{Guid.NewGuid():N}";

    /// <summary>The engine's Fixtures.PurchaseRequest, plus a Notify on ordered so the small-amount path writes an outbox row.</summary>
    static WorkflowDef Fixture(string name, string? submitRole = null, string submit = "Submit") => new(name, 1, "draft",
        [
            new("draft", "Draft", StateKind.Manual),
            new("approval", "Approval", StateKind.Task, TaskRole: "Approvers",
                OnEntry: [new(EffectKind.Notify, "Approvers", "Please approve {Title}")]),
            new("decide-amount", "Amount check", StateKind.Decision),
            new("wait-cooling", "Cooling-off", StateKind.Wait, WaitMinutes: 1),
            // RunAction refund writes a related request (XSM-032); Increment Revisions (null -> 1) after it: a retried attempt must
            // leave exactly one of each (XSM-031), and an Increment that overflows must take the refund down with it.
            new("ordered", "Ordered", StateKind.Final, OnEntry: [new(EffectKind.SetField, "Ordered", "true"), new(EffectKind.Notify, "Approvers", "Ordered: {Title}"),
                new(EffectKind.RunAction, "refund", RefundAction.Title + "{Title}"), new(EffectKind.Increment, "Revisions", "")]),
            new("rejected", "Rejected", StateKind.Final),
        ],
        [
            new("draft", submit, "approval", RequiredRole: submitRole),
            new("approval", "Approve", "decide-amount"),
            new("approval", "Reject", "rejected"),
            new("decide-amount", "Large", "wait-cooling", Criteria: "Amount > 1000", Order: 0),
            new("decide-amount", "Small", "ordered", Order: 1),
            new("wait-cooling", "Cooled", "ordered"),
        ]);

    IReadOnlyList<string> Roles()
    {
        using var os = db.NewObjectSpace();
        return WorkflowRoleCatalog.Names(os);
    }

    /// <summary>Seeds <paramref name="def"/> as a draft through entities, then publishes it through DefinitionService.</summary>
    Guid Publish(WorkflowDef def)
    {
        using var os = db.NewObjectSpace();
        var d = DefinitionMapper.ToDraft(os, def, typeof(PurchaseRequest).FullName!);
        os.CommitChanges();
        var r = new DefinitionService(new DefinitionStore(), actions).Publish(os, d, "tester", Roles());
        Assert.True(r.Ok, string.Join("; ", r.Messages));
        return d.ID;
    }

    Guid NewRequest(decimal amount, string? title = null)
    {
        using var os = db.NewObjectSpace();
        var pr = os.CreateObject<PurchaseRequest>();
        (pr.Title, pr.Amount) = (title ?? $"Req {Guid.NewGuid():N}", amount);
        os.CommitChanges();
        return pr.ID;
    }

    WorkflowCaller Alice() => db.Caller("alice", "Approvers");

    /// <summary>A published fixture, a started subject, optionally submitted. Returns (subject, instance).</summary>
    (Guid Subject, Guid Instance) Started(decimal amount, bool submit = true, string? title = null, WorkflowDef? def = null)
    {
        var name = Unique();
        Publish(def ?? Fixture(name));
        var id = NewRequest(amount, title);
        var r = rt.Start(Alice(), typeof(PurchaseRequest), id, def?.Name ?? name);
        Assert.True(r.Ok, r.Message);
        if (submit) Ok(rt.Fire(Alice(), typeof(PurchaseRequest), id, "Submit"));
        return (id, Instance(id).ID);
    }

    static void Ok(RunResult r) => Assert.True(r.Ok, r.Message);

    WorkflowInstance Instance(Guid subjectId)
    {
        var os = db.NewObjectSpace();
        return os.GetObjectByKey<PurchaseRequest>(subjectId).Workflow!;
    }

    PurchaseRequest Request(Guid id) => db.NewObjectSpace().GetObjectByKey<PurchaseRequest>(id);

    List<WorkflowTask> Tasks(Guid instanceId) =>
        db.NewObjectSpace().GetObjectsQuery<WorkflowTask>().Where(t => t.Instance!.ID == instanceId).ToList();

    List<WorkflowNotification> Notifications(Guid instanceId) =>
        db.NewObjectSpace().GetObjectsQuery<WorkflowNotification>().Where(n => n.Instance!.ID == instanceId).ToList();

    Guid OpenTask(Guid instanceId) => Tasks(instanceId).Single(t => t.State == TaskState.Open).ID;

    /// <summary>The refund requests the fixture's ordered action wrote for request <paramref name="id"/>.</summary>
    int Refunds(Guid id)
    {
        var title = RefundAction.Title + Request(id).Title;
        return db.NewObjectSpace().GetObjectsQuery<PurchaseRequest>().Count(p => p.Title == title);
    }

    [Fact] public void Start_pins_published_version_and_enters_initial()
    {
        var name = Unique();
        var defId = Publish(Fixture(name));
        var id = NewRequest(10);

        Ok(rt.Start(Alice(), typeof(PurchaseRequest), id, name));

        var inst = Instance(id);
        Assert.Equal(defId, inst.Definition!.ID);
        Assert.Equal(("draft", "Draft", typeof(PurchaseRequest).FullName, id), (inst.StateKey, inst.StateCaption, inst.SubjectType, inst.SubjectId));
        Assert.Equal(clock.Now.UtcDateTime, inst.EnteredUtc);
        var again = rt.Start(Alice(), typeof(PurchaseRequest), id, name);
        Assert.Equal((false, "the workflow has already started"), (again.Ok, again.Message));
        var missing = Unique();
        var none = rt.Start(Alice(), typeof(PurchaseRequest), NewRequest(1), missing);
        Assert.Equal((false, $"'{missing}' has no published version"), (none.Ok, none.Message));
    }

    [Fact] public void Submit_creates_task_and_notification()
    {
        var title = $"Laptop {Guid.NewGuid():N}";
        var (_, inst) = Started(10, title: title);

        var task = Assert.Single(Tasks(inst), t => t.State == TaskState.Open);
        Assert.Equal(("Approvers", "approval", title), (task.Role, task.StateKey, task.Subject));
        Assert.Equal(["Approve", "Reject"], rt.Outcomes(Alice(), task.ID));
        var note = Assert.Single(Notifications(inst), n => n.State == NotificationState.Pending);
        Assert.Contains(title, note.Body);
        Assert.Equal("Approvers", note.Role);
    }

    [Fact] public void Decide_small_amount_reaches_ordered_and_sets_field()
    {
        var (id, inst) = Started(10);
        var task = OpenTask(inst);
        var before = Instance(id).OptimisticLockField;
        var saves = new SaveCounter();
        using var provider = db.NewProvider(extra: saves);
        var caller = new WorkflowCaller("alice", r => r == "Approvers", db.NewObjectSpace(), provider.CreateObjectSpace);

        var r = rt.Decide(caller, task, "Approve");

        Ok(r);
        Assert.Equal(["decide-amount", "ordered"], r.Steps.Select(s => s.To));
        Assert.Equal(1, saves.Count);
        Assert.True(Request(id).Ordered);
        var after = Instance(id);
        Assert.Equal("ordered", after.StateKey);
        Assert.Equal(before + 1, after.OptimisticLockField);
        Assert.Single(Notifications(inst), n => n.Body.StartsWith("Ordered:"));
        var decided = Tasks(inst).Single(t => t.ID == task);
        Assert.Equal((TaskState.Decided, "alice", "Approve", clock.Now.UtcDateTime), (decided.State, decided.DecidedBy, decided.Outcome, decided.DecidedUtc));
    }

    [Fact] public void Fire_outside_manual_state_is_refused()
    {
        var (id, inst) = Started(5000);
        var r = rt.Fire(Alice(), typeof(PurchaseRequest), id, "Approve");
        Assert.False(r.Ok);
        Assert.Contains("can only be decided through its task", r.Message);
        Assert.Single(Tasks(inst), t => t.State == TaskState.Open);

        Ok(rt.Decide(Alice(), OpenTask(inst), "Approve"));
        Assert.Equal("wait-cooling", Instance(id).StateKey);
        var w = rt.Fire(Alice(), typeof(PurchaseRequest), id, "Cooled");
        Assert.False(w.Ok);
        Assert.Contains("leaves by its timer", w.Message);
        Assert.Equal("wait-cooling", Instance(id).StateKey);
    }

    [Fact] public void Start_with_wrong_target_type_is_refused()
    {
        var name = Unique();
        var defId = Publish(Fixture(name));
        using (var ctx = db.NewDbContext())   // ExecuteUpdate bypasses SaveChanges interceptors (the definition guard)
            ctx.WorkflowDefinitions.Where(d => d.ID == defId).ExecuteUpdate(s => s.SetProperty(d => d.TargetTypeName, typeof(ApplicationUser).FullName!));
        var id = NewRequest(1);

        var r = rt.Start(Alice(), typeof(PurchaseRequest), id, name);

        Assert.False(r.Ok);
        Assert.Contains($"definition '{name}' is for {typeof(ApplicationUser).FullName}", r.Message);
        Assert.Null(Request(id).Workflow);
        Assert.False(db.NewObjectSpace().GetObjectsQuery<WorkflowInstance>().Any(i => i.SubjectId == id));
    }

    [Fact] public void Link_mismatch_is_refused()
    {
        var (a, instA) = Started(10);
        var (_, instB) = Started(10);
        var taskA = OpenTask(instA);
        using (var ctx = db.NewDbContext())
        {
            var fk = ctx.Model.FindEntityType(typeof(PurchaseRequest))!.FindNavigation(nameof(PurchaseRequest.Workflow))!.ForeignKey.Properties[0].Name;
            ctx.PurchaseRequests.Where(p => p.ID == a).ExecuteUpdate(s => s.SetProperty(p => EF.Property<Guid?>(p, fk), instB));
        }

        var r = rt.Decide(Alice(), taskA, "Approve");

        Assert.False(r.Ok);
        Assert.Equal(WorkflowRuntime.LinkMismatch, r.Message);
        Assert.Equal(TaskState.Open, Tasks(instA).Single(t => t.ID == taskA).State);
    }

    [Fact] public void Task_instance_binding_is_immutable()
    {
        var (_, inst) = Started(10);
        var (_, other) = Started(10);
        var task = OpenTask(inst);

        void Refused(Action<IObjectSpace, WorkflowTask> change, bool inScope = false)
        {
            using var os = db.NewObjectSpace();
            change(os, os.GetObjectByKey<WorkflowTask>(task));
            using (inScope ? RuntimeScope.Open(Ctx(os)) : null)
                Assert.StartsWith("Workflow task:", Assert.Throws<UserFriendlyException>(() => os.CommitChanges()).Message);
        }

        Refused((os, t) => t.Instance = os.GetObjectByKey<WorkflowInstance>(other), inScope: true);
        Refused((_, t) => t.StateKey = "rejected", inScope: true);
        Refused((_, t) => t.Role = "Administrators", inScope: true);
        Refused((_, t) => t.Subject = "x", inScope: true);
        Refused((_, t) => t.State = TaskState.Decided);                  // runtime-written only
        Refused((_, t) => t.Outcome = "Approve");
        Refused((os, _) => { var n = os.CreateObject<WorkflowTask>(); n.Instance = os.GetObjectByKey<WorkflowInstance>(inst); });
        Refused((os, t) => os.Delete(t));

        var saved = Tasks(inst).Single(t => t.ID == task);
        Assert.Equal((inst, "approval", "Approvers", TaskState.Open, (string?)null), (saved.Instance!.ID, saved.StateKey, saved.Role, saved.State, saved.Outcome));
    }

    static DbContext Ctx(IObjectSpace os) => ((EFCoreObjectSpace)os).DbContext;

    [Fact] public async Task Scope_is_bound_to_its_context()
    {
        var (_, inst) = Started(10);
        var task = OpenTask(inst);
        using var command = db.NewObjectSpace();

        using (RuntimeScope.Open(Ctx(command)))
        {
            // The AsyncLocal flows into Task.Run; a different context there is still outside the scope.
            var ex = await Record.ExceptionAsync(() => Task.Run(() =>
            {
                using var other = db.NewObjectSpace();
                other.GetObjectByKey<WorkflowTask>(task).State = TaskState.Decided;
                other.CommitChanges();
            }));
            Assert.StartsWith("Workflow task:", Assert.IsType<UserFriendlyException>(ex).Message);

            using var sameThread = db.NewObjectSpace();
            sameThread.GetObjectByKey<WorkflowTask>(task).State = TaskState.Decided;
            Assert.StartsWith("Workflow task:", Assert.Throws<UserFriendlyException>(() => sameThread.CommitChanges()).Message);
        }
        Assert.Equal(TaskState.Open, Tasks(inst).Single(t => t.ID == task).State);
    }

    [Fact] public void Instance_state_and_outbox_are_guarded_at_persistence()
    {
        var (id, inst) = Started(10);
        static void Refused(IObjectSpace os, string prefix) =>
            Assert.StartsWith(prefix, Assert.Throws<UserFriendlyException>(() => os.CommitChanges()).Message);

        // A plain (non-secured, admin-equivalent) object space cannot move the state ...
        using (var os = db.NewObjectSpace()) { os.GetObjectByKey<WorkflowInstance>(inst).StateKey = "ordered"; Refused(os, "Workflow instance:"); }
        using (var os = db.NewObjectSpace()) { os.GetObjectByKey<WorkflowInstance>(inst).DueUtc = DateTime.UtcNow; Refused(os, "Workflow instance:"); }
        using (var os = db.NewObjectSpace()) { os.GetObjectByKey<WorkflowInstance>(inst).StateCaption = "x"; Refused(os, "Workflow instance:"); }
        // ... nor create an instance or an outbox row.
        using (var os = db.NewObjectSpace())
        {
            var i = os.CreateObject<WorkflowInstance>();
            (i.SubjectType, i.SubjectId) = (typeof(PurchaseRequest).FullName!, Guid.NewGuid());
            Refused(os, "Workflow instance:");
        }
        using (var os = db.NewObjectSpace())
        {
            var n = os.CreateObject<WorkflowNotification>();
            (n.Role, n.Body, n.Instance) = ("Approvers", "fake", os.GetObjectByKey<WorkflowInstance>(inst));
            Refused(os, "Workflow notification:");
        }
        Assert.Equal("approval", Instance(id).StateKey);
        Assert.DoesNotContain(Notifications(inst), n => n.Body == "fake");

        // The dispatcher's delivery update stays allowed outside the scope.
        using (var os = db.NewObjectSpace())
        {
            var n = os.GetObjectsQuery<WorkflowNotification>().Single(x => x.Instance!.ID == inst);
            (n.State, n.Attempts, n.LastError) = (NotificationState.Failed, n.Attempts + 1, "smtp down");
            os.CommitChanges();
        }
        Assert.Equal((NotificationState.Failed, 1, "smtp down"), Notifications(inst).Select(n => (n.State, n.Attempts, n.LastError)).Single());
    }

    [Fact] public void Outcomes_only_for_open_current_task_the_caller_may_decide()
    {
        var (_, inst) = Started(10);
        var task = OpenTask(inst);
        Assert.Equal(["Approve", "Reject"], rt.Outcomes(Alice(), task));
        Assert.Empty(rt.Outcomes(db.Caller("bob", "Administrators"), task));   // not in the task's role
        Ok(rt.Decide(Alice(), task, "Reject"));
        Assert.Empty(rt.Outcomes(Alice(), task));                              // no longer Open
    }

    [Fact] public void Failure_after_effects_rolls_back_everything()
    {
        var (id, inst) = Started(10);
        var task = OpenTask(inst);
        var before = Instance(id).OptimisticLockField;
        var fail = new FailSecondWrite();
        using var provider = db.NewProvider(maxBatchSize1: true, fail);
        var caller = new WorkflowCaller("alice", r => r == "Approvers", db.NewObjectSpace(), provider.CreateObjectSpace);

        fail.Armed = true;
        RunResult? result = null;
        var ex = Record.Exception(() => result = rt.Decide(caller, task, "Approve"));
        fail.Armed = false;

        Assert.True(ex != null || result is { Ok: false }, "the decide must fail");
        Assert.NotEmpty(fail.Executed);   // a write had already executed ...
        Assert.True(fail.Injected);       // ... when the next one failed
        Assert.False(Request(id).Ordered);
        var after = Instance(id);
        Assert.Equal(("approval", before), (after.StateKey, after.OptimisticLockField));
        Assert.Equal(TaskState.Open, Tasks(inst).Single(t => t.ID == task).State);
        Assert.DoesNotContain(Notifications(inst), n => n.Body.StartsWith("Ordered:"));
        Assert.Equal(0, Refunds(id));   // the action's related record went down with the rest (XSM-032)
    }

    [Fact] public void Retry_reloads_after_lost_race()
    {
        var (id, inst) = Started(10);
        var task = OpenTask(inst);
        var attempts = new List<int>();
        rt.TestHook = (point, attempt) =>
        {
            if (point != "before-commit") return;
            attempts.Add(attempt);
            if (attempt != 1) return;
            using var other = db.NewObjectSpace();
            Concurrency.Touch(other, other.GetObjectByKey<WorkflowInstance>(inst));
            other.CommitChanges();
        };

        Ok(rt.Decide(Alice(), task, "Approve"));

        Assert.Equal([1, 2], attempts);
        Assert.Equal("ordered", Instance(id).StateKey);
        Assert.Single(Notifications(inst), n => n.Body.StartsWith("Ordered:"));
        Assert.Equal(1, Request(id).Revisions);   // the first attempt's increment was rolled back with its object space
        Assert.Equal((2, 1), (refund.Calls, Refunds(id)));   // the action ran once per attempt; only the last attempt's row persists (XSM-032)
    }

    [Fact] public void RunAction_writes_through_the_command_space_and_fills_the_argument()   // XSM-032
    {
        var title = $"Desk {Guid.NewGuid():N}";
        var (id, inst) = Started(10, title: title);

        Ok(rt.Decide(Alice(), OpenTask(inst), "Approve"));

        Assert.Equal(1, Refunds(id));
        Assert.Equal([(id, "ordered")], refund.Seen);   // the subject, its instance already in the entered state
        Assert.Equal("ordered", Instance(id).StateKey);
    }

    [Fact] public void RunAction_that_throws_commits_nothing()
    {
        refund.Also = _ => throw new UserFriendlyException("no slots");   // after it staged its refund row
        var (id, inst) = Started(10);
        var task = OpenTask(inst);

        var ex = Record.Exception(() => rt.Decide(Alice(), task, "Approve"));

        Assert.Equal("no slots", Assert.IsType<UserFriendlyException>(ex).Message);
        Assert.Equal(("approval", false, 0), (Instance(id).StateKey, Request(id).Ordered, Refunds(id)));
        Assert.Equal(TaskState.Open, Tasks(inst).Single(t => t.ID == task).State);
    }

    [Fact] public void Failing_effect_after_action_commits_nothing()
    {
        var (id, inst) = Started(10);
        using (var os = db.NewObjectSpace())
        {
            os.GetObjectByKey<PurchaseRequest>(id).Revisions = int.MaxValue;   // the fixture's Increment, after the action, overflows
            os.CommitChanges();
        }

        var ex = Record.Exception(() => rt.Decide(Alice(), OpenTask(inst), "Approve"));

        Assert.Equal("'Revisions' cannot be incremented past its range", Assert.IsType<UserFriendlyException>(ex).Message);
        Assert.Equal((1, "approval", 0), (refund.Calls, Instance(id).StateKey, Refunds(id)));
    }

    [Fact] public void DI_resolved_runtime_runs_the_registered_action()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddXafStateless();
        services.AddSingleton<IWorkflowAction>(refund);
        using var provider = services.BuildServiceProvider();
        var (id, inst) = Started(10);

        Ok(provider.GetRequiredService<WorkflowRuntime>().Decide(Alice(), OpenTask(inst), "Approve"));

        Assert.Equal((1, 1), (refund.Calls, Refunds(id)));
    }

    [Fact] public void Unregistered_action_at_runtime_commits_nothing()
    {
        var (id, inst) = Started(10);
        var bare = new WorkflowRuntime(new DefinitionStore(), clock);   // a host that lost the registration after publishing

        var ex = Record.Exception(() => bare.Decide(Alice(), OpenTask(inst), "Approve"));

        Assert.Contains("'refund'", Assert.IsType<InvalidOperationException>(ex).Message);
        Assert.Equal(("approval", false), (Instance(id).StateKey, Request(id).Ordered));
    }

    [Fact] public void RunAction_cannot_change_a_locked_member()
    {
        var name = Unique();
        var def = Fixture(name) with { States = Fixture(name).States.Select(s => s.Key == "approval" ? s with { Locked = ["Amount"] } : s).ToList() };
        refund.Also = c => ((PurchaseRequest)c.Subject).Amount = 999;   // no effect of ordered writes Amount, so no exemption
        var (id, inst) = Started(10, def: def);

        var ex = Record.Exception(() => rt.Decide(Alice(), OpenTask(inst), "Approve"));

        Assert.Equal("'Amount' is locked in state 'Approval'", Assert.IsType<UserFriendlyException>(ex).Message);
        Assert.Equal(("approval", 10m, 0), (Instance(id).StateKey, Request(id).Amount, Refunds(id)));
    }

    /// <summary>theory -Fail-> count (Decision; Increment <paramref name="member"/> by <paramref name="step"/>) -Limit [criteria]-> failed / -Retry-> theory; theory -Pass-> passed.
    /// theory locks the counter: the runtime's own increment is exempt (XSM-031).</summary>
    static WorkflowDef Counting(string name, string member, string step, string limit) => new(name, 1, "theory",
        [
            new("theory", "Theory", StateKind.Manual, Locked: [member]),
            new("count", "Count", StateKind.Decision, OnEntry: [new(EffectKind.Increment, member, step)]),
            new("failed", "Failed", StateKind.Final),
            new("passed", "Passed", StateKind.Final),
        ],
        [
            new("theory", "Fail", "count"),
            new("theory", "Pass", "passed"),
            new("count", "Limit", "failed", Criteria: limit, Order: 0),
            new("count", "Retry", "theory", Order: 1),
        ]);

    Guid StartCounting(WorkflowDef def, decimal amount = 0, int? revisions = null)
    {
        Publish(def);
        var id = NewRequest(amount);
        if (revisions != null)
        {
            using var os = db.NewObjectSpace();
            os.GetObjectByKey<PurchaseRequest>(id).Revisions = revisions;
            os.CommitChanges();
        }
        Ok(rt.Start(Alice(), typeof(PurchaseRequest), id, def.Name));
        return id;
    }

    [Fact] public void Increment_counts_and_decision_sees_it()   // XSM-031: an attempt limit through the workflow itself
    {
        var id = StartCounting(Counting(Unique(), "Revisions", "", "Revisions >= 2"));
        Assert.Null(Request(id).Revisions);

        Ok(rt.Fire(Alice(), typeof(PurchaseRequest), id, "Fail"));
        Assert.Equal(("theory", 1), (Instance(id).StateKey, Request(id).Revisions));   // null + 1; written while theory locks Revisions

        Ok(rt.Fire(Alice(), typeof(PurchaseRequest), id, "Fail"));
        Assert.Equal(("failed", 2), (Instance(id).StateKey, Request(id).Revisions));
    }

    [Fact] public void Increment_steps()
    {
        var byHalf = StartCounting(Counting(Unique(), "Amount", "0.5", "Amount >= 100"), amount: 10);
        Ok(rt.Fire(Alice(), typeof(PurchaseRequest), byHalf, "Fail"));
        Assert.Equal(10.5m, Request(byHalf).Amount);

        var down = StartCounting(Counting(Unique(), "Revisions", "-1", "Revisions <= 0"), revisions: 3);
        Ok(rt.Fire(Alice(), typeof(PurchaseRequest), down, "Fail"));
        Assert.Equal(("theory", 2), (Instance(down).StateKey, Request(down).Revisions));
    }

    [Theory]
    [InlineData("1", int.MaxValue)]                               // the conversion back to int overflows
    [InlineData("79228162514264337593543950335", 1)]              // decimal.MaxValue: the decimal sum itself overflows
    public void Increment_past_range_is_refused_and_commits_nothing(string step, int revisions)
    {
        var id = StartCounting(Counting(Unique(), "Revisions", step, "Revisions < 0"), revisions: revisions);

        var ex = Assert.IsType<UserFriendlyException>(Record.Exception(() => rt.Fire(Alice(), typeof(PurchaseRequest), id, "Fail")));

        Assert.Equal("'Revisions' cannot be incremented past its range", ex.Message);
        Assert.Equal(("theory", revisions), (Instance(id).StateKey, Request(id).Revisions));
    }

    [Fact] public void Large_amount_waits_until_timer()
    {
        // Other tests in this database leave Wait instances due at 2026-01-01 00:01; this one lives in 2020 so only it is due.
        clock.Now = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var (id, inst) = Started(5000);
        Ok(rt.Decide(Alice(), OpenTask(inst), "Approve"));
        Assert.Equal(clock.Now.UtcDateTime.AddMinutes(1), Instance(id).DueUtc);

        Assert.Equal(0, rt.RunDueTimers(db.NewObjectSpace));
        clock.Now = clock.Now.AddSeconds(61);
        Assert.Equal(1, rt.RunDueTimers(db.NewObjectSpace));

        var after = Instance(id);
        Assert.Equal("ordered", after.StateKey);
        Assert.Null(after.DueUtc);
        Assert.True(Request(id).Ordered);
    }

    [Fact] public void Fire_without_role_is_refused()
    {
        var name = Unique();
        var (id, _) = Started(10, submit: false, def: Fixture(name, submitRole: "Administrators"));

        Assert.DoesNotContain(rt.Available(Alice(), typeof(PurchaseRequest), id), t => t.Trigger == "Submit");
        var r = rt.Fire(Alice(), typeof(PurchaseRequest), id, "Submit");
        Assert.False(r.Ok);
        Assert.Equal("draft", Instance(id).StateKey);

        var admin = db.Caller("root", "Administrators");
        Assert.Contains(rt.Available(admin, typeof(PurchaseRequest), id), t => t.Trigger == "Submit");
        Ok(rt.Fire(admin, typeof(PurchaseRequest), id, "Submit"));
    }

    [Fact] public async Task Concurrent_decide_one_wins()
    {
        var (id, inst) = Started(10);
        var task = OpenTask(inst);
        using var barrier = new Barrier(2);
        var timedOut = false;
        rt.TestHook = (point, attempt) =>
        {
            if (point == "after-load" && attempt == 1 && !barrier.SignalAndWait(TimeSpan.FromSeconds(10))) timedOut = true;
        };

        var results = await Task.WhenAll(
            Task.Run(() => rt.Decide(db.Caller("alice", "Approvers"), task, "Approve")),
            Task.Run(() => rt.Decide(db.Caller("bob", "Approvers"), task, "Approve")));

        Assert.False(timedOut, "both commands must have loaded the open task before either committed");
        Assert.Single(results, r => r.Ok);
        Assert.Equal(WorkflowRuntime.AlreadyDecided, Assert.Single(results, r => !r.Ok).Message);
        Assert.Equal(TaskState.Decided, Tasks(inst).Single(t => t.ID == task).State);
        Assert.Equal("ordered", Instance(id).StateKey);
        Assert.Single(Notifications(inst), n => n.Body.StartsWith("Ordered:"));
    }

    [Fact] public void Instance_on_retired_version_still_fires()
    {
        var name = Unique();
        var v1 = Publish(Fixture(name));
        var id = NewRequest(10);
        Ok(rt.Start(Alice(), typeof(PurchaseRequest), id, name));

        var svc = new DefinitionService(new DefinitionStore(), actions);
        using (var os = db.NewObjectSpace())
        {
            var draft = svc.NewDraft(os, name);
            draft.Transitions.Single(t => t.Trigger == "Submit").Trigger = "Send";
            os.CommitChanges();
            Assert.True(svc.Publish(os, draft, "tester", Roles()).Ok);
        }
        Assert.Equal(DefinitionState.Retired, db.NewObjectSpace().GetObjectByKey<WorkflowDefinition>(v1).State);

        var cold = new WorkflowRuntime(new DefinitionStore(), clock, actions);   // a restart: empty definition cache
        Ok(cold.Fire(Alice(), typeof(PurchaseRequest), id, "Submit"));
        var inst = Instance(id);
        Assert.Equal(("approval", v1), (inst.StateKey, inst.Definition!.ID));
    }
}
