using System.Data.Common;
using System.Text.RegularExpressions;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.EFCore;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using XafStateless.Demo.Module.BusinessObjects;
using XafStateless.Engine;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Services;

namespace XafStateless.Module.Tests;

public class WorkflowRuntimeTests : IClassFixture<TestDb>
{
    readonly TestDb db;
    readonly FakeClock clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    readonly WorkflowRuntime rt;

    public WorkflowRuntimeTests(TestDb db)
    {
        this.db = db;
        rt = new WorkflowRuntime(new DefinitionStore(), clock);
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
            new("ordered", "Ordered", StateKind.Final, OnEntry: [new(EffectKind.SetField, "Ordered", "true"), new(EffectKind.Notify, "Approvers", "Ordered: {Title}")]),
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
        var d = os.CreateObject<WorkflowDefinition>();
        (d.Name, d.TargetTypeName, d.Version, d.State, d.InitialStateKey) = (def.Name, typeof(PurchaseRequest).FullName!, 1, DefinitionState.Draft, def.InitialState);
        foreach (var s in def.States)
        {
            var state = os.CreateObject<WorkflowState>();
            (state.Key, state.Caption, state.Kind, state.TaskRole, state.WaitMinutes) = (s.Key, s.Caption, s.Kind, s.TaskRole, s.WaitMinutes);
            foreach (var e in s.Effects)
            {
                var effect = os.CreateObject<WorkflowEffect>();
                (effect.Order, effect.Kind, effect.Target, effect.Value) = (state.Effects.Count, e.Kind, e.Target, e.Value);
                state.Effects.Add(effect);
            }
            d.States.Add(state);
        }
        foreach (var t in def.Transitions)
        {
            var tr = os.CreateObject<WorkflowTransition>();
            (tr.FromKey, tr.Trigger, tr.ToKey, tr.Criteria, tr.RequiredRole, tr.Order) = (t.From, t.Trigger, t.To, t.Criteria, t.RequiredRole, t.Order);
            d.Transitions.Add(tr);
        }
        os.CommitChanges();
        var r = new DefinitionService(new DefinitionStore()).Publish(os, d, "tester", Roles());
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

        var svc = new DefinitionService(new DefinitionStore());
        using (var os = db.NewObjectSpace())
        {
            var draft = svc.NewDraft(os, name);
            draft.Transitions.Single(t => t.Trigger == "Submit").Trigger = "Send";
            os.CommitChanges();
            Assert.True(svc.Publish(os, draft, "tester", Roles()).Ok);
        }
        Assert.Equal(DefinitionState.Retired, db.NewObjectSpace().GetObjectByKey<WorkflowDefinition>(v1).State);

        var cold = new WorkflowRuntime(new DefinitionStore(), clock);   // a restart: empty definition cache
        Ok(cold.Fire(Alice(), typeof(PurchaseRequest), id, "Submit"));
        var inst = Instance(id);
        Assert.Equal(("approval", v1), (inst.StateKey, inst.Definition!.ID));
    }
}
