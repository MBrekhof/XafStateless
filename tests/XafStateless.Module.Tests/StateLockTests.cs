using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DataLocking;
using DevExpress.ExpressApp.EFCore;
using DevExpress.ExpressApp.Localization;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using XafStateless.Demo.Module.BusinessObjects;
using XafStateless.Engine;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Controllers;
using XafStateless.Module.Services;

namespace XafStateless.Module.Tests;

/// <summary>Per-state field locks at persistence (<see cref="StateLockGuard"/>): refused saves, the runtime's member-precise
/// exemption, the runtime-owned Workflow link, and a user save racing a transition into a locking state.</summary>
public class StateLockTests : IClassFixture<TestDb>
{
    readonly TestDb db;
    readonly WorkflowRuntime rt = new(new DefinitionStore(), TimeProvider.System);

    public StateLockTests(TestDb db)
    {
        this.db = db;
        using var os = db.NewObjectSpace();
        if (os.FirstOrDefault<PermissionPolicyRole>(r => r.Name == "Approvers") == null) os.CreateObject<PermissionPolicyRole>().Name = "Approvers";
        os.CommitChanges();
    }

    /// <summary>The purchase-request fixture with <paramref name="approvalLocks"/> on approval; ordered SetFields Ordered = true.</summary>
    static WorkflowDef Fixture(string name, params string[] approvalLocks) => new(name, 1, "draft",
        [
            new("draft", "Draft", StateKind.Manual),
            new("approval", "Approval", StateKind.Task, TaskRole: "Approvers", Locked: approvalLocks),
            new("decide-amount", "Amount check", StateKind.Decision),
            new("wait-cooling", "Cooling-off", StateKind.Wait, WaitMinutes: 1),
            new("ordered", "Ordered", StateKind.Final, OnEntry: [new(EffectKind.SetField, "Ordered", "true")]),
            new("rejected", "Rejected", StateKind.Final),
        ],
        [
            new("draft", "Submit", "approval"),
            new("approval", "Approve", "decide-amount"),
            new("approval", "Reject", "rejected"),
            new("decide-amount", "Large", "wait-cooling", Criteria: "Amount > 1000", Order: 0),
            new("decide-amount", "Small", "ordered", Order: 1),
            new("wait-cooling", "Cooled", "ordered"),
        ]);

    WorkflowCaller Alice() => db.Caller("alice", "Approvers");

    static void Ok(RunResult r) => Assert.True(r.Ok, r.Message);

    /// <summary>A published fixture and a started request (amount 50), optionally submitted into approval.</summary>
    Guid Started(bool submit, params string[] approvalLocks)
    {
        var def = Fixture($"lock-{Guid.NewGuid():N}", approvalLocks);
        using (var os = db.NewObjectSpace())
        {
            var d = DefinitionMapper.ToDraft(os, def, typeof(PurchaseRequest).FullName!);
            os.CommitChanges();
            var r = new DefinitionService(new DefinitionStore()).Publish(os, d, "tester", WorkflowRoleCatalog.Names(os));
            Assert.True(r.Ok, string.Join("; ", r.Messages));
        }
        Guid id;
        using (var os = db.NewObjectSpace())
        {
            var pr = os.CreateObject<PurchaseRequest>();
            (pr.Title, pr.Amount) = ("Chair", 50);
            os.CommitChanges();
            id = pr.ID;
        }
        Ok(rt.Start(Alice(), typeof(PurchaseRequest), id, def.Name));
        if (submit) Ok(rt.Fire(Alice(), typeof(PurchaseRequest), id, "Submit"));
        return id;
    }

    PurchaseRequest Saved(Guid id) => db.NewObjectSpace().GetObjectByKey<PurchaseRequest>(id);

    Guid OpenTask(Guid id)
    {
        using var os = db.NewObjectSpace();
        return os.GetObjectsQuery<WorkflowTask>().Single(t => t.Instance!.SubjectId == id && t.State == TaskState.Open).ID;
    }

    static void AssertLocked(Exception? ex)
    {
        var ufe = Assert.IsType<UserFriendlyException>(ex);
        Assert.Contains("'Amount' is locked", ufe.Message);
    }

    [Fact] public void Locks_round_trip_and_unknown_member_blocks_publish()
    {
        using var os = db.NewObjectSpace();
        var d = DefinitionMapper.ToDraft(os, Fixture($"lock-{Guid.NewGuid():N}", "Amount", "Nope"), typeof(PurchaseRequest).FullName!);
        var approval = d.States.Single(s => s.Key == "approval");
        Assert.Equal("Amount;Nope", approval.LockedMembers);
        Assert.Equal(["Amount", "Nope"], DefinitionMapper.ToDef(d).State("approval").LockedMembers);
        Assert.Equal(["Amount", "Ordered", "Title"], ((IStringLookupSource)approval).LookupValues(nameof(WorkflowState.LockedMembers), () => []).Order());
        os.CommitChanges();
        var r = new DefinitionService(new DefinitionStore()).Publish(os, d, "tester", WorkflowRoleCatalog.Names(os));
        Assert.False(r.Ok);
        Assert.Contains(r.Messages, m => m.StartsWith("lock on 'Nope': "));
        Assert.DoesNotContain(r.Messages, m => m.Contains("lock on 'Amount'"));
    }

    [Fact] public void Locked_member_edit_is_refused_on_save()
    {
        var id = Started(true, "Amount");
        using var os = db.NewObjectSpace();
        os.GetObjectByKey<PurchaseRequest>(id).Amount = 999;
        var ex = Record.Exception(() => os.CommitChanges());
        AssertLocked(ex);
        Assert.Equal("'Amount' is locked in state 'Approval'", ex!.Message);
        Assert.Equal(50m, Saved(id).Amount);
    }

    [Fact] public void Unlocked_member_edit_is_allowed()
    {
        var id = Started(true, "Amount");
        using (var os = db.NewObjectSpace())
        {
            os.GetObjectByKey<PurchaseRequest>(id).Title = "Desk";
            os.CommitChanges();
        }
        Assert.Equal("Desk", Saved(id).Title);
    }

    [Fact] public void SetField_may_write_member_locked_in_source_state()
    {
        var id = Started(true, "Amount", "Ordered");
        // Approve -> decide-amount -> Small -> ordered, whose SetField writes Ordered while the persisted state is approval.
        Ok(rt.Decide(Alice(), OpenTask(id), "Approve"));
        var pr = Saved(id);
        Assert.True(pr.Ordered);
        Assert.Equal("ordered", pr.Workflow!.StateKey);
    }

    [Fact] public void Lock_ends_when_state_changes()
    {
        var id = Started(true, "Amount");
        Ok(rt.Decide(Alice(), OpenTask(id), "Reject"));
        using (var os = db.NewObjectSpace())
        {
            os.GetObjectByKey<PurchaseRequest>(id).Amount = 999;
            os.CommitChanges();
        }
        Assert.Equal(999m, Saved(id).Amount);
    }

    [Fact] public void Pending_locked_edit_does_not_ride_along()
    {
        var id = Started(true, "Amount");
        var task = OpenTask(id);

        // The controller command path: save the view first, then run the command. The save is refused; Decide never runs.
        using (var user = db.NewObjectSpace())
        {
            user.GetObjectByKey<PurchaseRequest>(id).Amount = 999;
            var (ran, errors) = (false, new List<string>());
            WorkflowCommand.Run(user, () => Alice(), c => { ran = true; return rt.Decide(c, task, "Approve"); }, errors.Add);
            Assert.False(ran);
            Assert.Contains(errors, m => m.Contains("'Amount' is locked"));
        }
        var pr = Saved(id);
        Assert.Equal((50m, "approval", false), (pr.Amount, pr.Workflow!.StateKey, pr.Ordered));

        // Directly: a runtime scope that allows only Ordered does not exempt a modified Amount in the same save.
        using (var os = db.NewObjectSpace())
        {
            var subject = os.GetObjectByKey<PurchaseRequest>(id);
            (subject.Ordered, subject.Amount) = (true, 999);
            using var scope = RuntimeScope.Open(((EFCoreObjectSpace)os).DbContext);
            scope.Allow(id, nameof(PurchaseRequest.Ordered));
            AssertLocked(Record.Exception(() => os.CommitChanges()));
        }
        Assert.Equal((50m, false), (Saved(id).Amount, Saved(id).Ordered));
    }

    [Fact] public void Clearing_workflow_link_does_not_dodge_lock()
    {
        var id = Started(true, "Amount");
        using (var os = db.NewObjectSpace())
        {
            var pr = os.GetObjectByKey<PurchaseRequest>(id);
            Assert.NotNull(pr.Workflow);   // load the link: nulling a never-loaded lazy reference is no change
            (pr.Workflow, pr.Amount) = (null, 999);
            var ex = Assert.IsType<UserFriendlyException>(Record.Exception(() => os.CommitChanges()));
            Assert.Contains("workflow link", ex.Message);
        }
        var saved = Saved(id);
        Assert.Equal(50m, saved.Amount);
        Assert.NotNull(saved.Workflow);
    }

    [Fact] public void Workflow_link_is_runtime_owned()
    {
        var id = Started(false);   // no locks anywhere: the link rule stands on its own
        using var os = db.NewObjectSpace();
        var pr = os.GetObjectByKey<PurchaseRequest>(id);
        Assert.NotNull(pr.Workflow);
        pr.Workflow = null;
        Assert.Contains("workflow link is managed by the workflow", Record.Exception(() => os.CommitChanges())!.Message);
        Assert.NotNull(Saved(id).Workflow);
    }

    /// <summary>The recorded lost-race shape (Task 4b).</summary>
    static void AssertLostRace(Exception? ex)
    {
        var ufe = Assert.IsType<UserFriendlyException>(ex);
        Assert.Equal(UserVisibleExceptionLocalizer.GetExceptionMessage(UserVisibleExceptionId.ObjectToSaveWasChanged), ufe.Message);
        Assert.IsType<DbUpdateConcurrencyException>(ufe.InnerException);
    }

    [Fact] public void Subject_save_and_transition_race_one_loses()
    {
        // OS-A loads the request in the unlocked draft and edits Amount; a real Submit moves it into approval (Amount locked)
        // and commits; then OS-A commits. Either refusal is correct; what matters is that Amount did not land.
        var id = Started(false, "Amount");
        using (var a = db.NewObjectSpace())
        {
            a.GetObjectByKey<PurchaseRequest>(id).Amount = 999;
            Ok(rt.Fire(Alice(), typeof(PurchaseRequest), id, "Submit"));
            var ex = Record.Exception(() => a.CommitChanges());
            Assert.NotNull(ex);
            if (!(ex is UserFriendlyException && ex.Message.Contains("'Amount' is locked"))) AssertLostRace(ex);

            // (b) OS-A reloads and re-applies the edit: now the guard refuses it against the persisted approval state.
            a.Refresh();
            a.GetObjectByKey<PurchaseRequest>(id).Amount = 999;
            AssertLocked(Record.Exception(() => a.CommitChanges()));
        }
        Assert.Equal((50m, "approval"), (Saved(id).Amount, Saved(id).Workflow!.StateKey));
    }

    [Fact] public void Subject_save_and_transition_race_without_lock_is_seen_by_guard()
    {
        // (a) No lock configured, the same interleaving: a bare object space fails with the lost-race shape (4b case i).
        var id = Started(false);
        using (var a = db.NewObjectSpace())
        {
            a.GetObjectByKey<PurchaseRequest>(id).Amount = 999;
            Ok(rt.Fire(Alice(), typeof(PurchaseRequest), id, "Submit"));
            AssertLostRace(Record.Exception(() => a.CommitChanges()));
        }
        Assert.Equal(50m, Saved(id).Amount);
    }

    [Fact] public void Stale_ui_merge_after_real_transition_reruns_guard()
    {
        // The UI Save path (ProcessDataLockingInfoController: GetDataLockingInfo -> MergeData -> CommitChanges), reproduced
        // without a frame, against a REAL transition (Submit changes the instance and Touches the subject), not a Touch alone.
        var saves = new SaveCounter();
        using var provider = db.NewProvider(false, saves);

        // Without a lock the merged save lands, through SaveChanges passes the guard sees.
        var free = Started(false);
        using (var a = provider.CreateObjectSpace())
        {
            a.GetObjectByKey<PurchaseRequest>(free).Amount = 777;
            Ok(rt.Fire(Alice(), typeof(PurchaseRequest), free, "Submit"));
            var mgr = (IDataLockingManager)a;
            var info = mgr.GetDataLockingInfo();
            Assert.True(info.IsLocked);
            Assert.True(info.CanAutoMerge);   // Submit changed no field of the subject but its lock version
            mgr.MergeData(info);
            saves.Count = 0;
            a.CommitChanges();
            Assert.True(saves.Count >= 1);
        }
        Assert.Equal(777m, Saved(free).Amount);

        // With Amount locked in approval, the same merged save is refused by the guard against the new persisted state.
        var locked = Started(false, "Amount");
        using (var a = provider.CreateObjectSpace())
        {
            a.GetObjectByKey<PurchaseRequest>(locked).Amount = 777;
            Ok(rt.Fire(Alice(), typeof(PurchaseRequest), locked, "Submit"));
            var mgr = (IDataLockingManager)a;
            var info = mgr.GetDataLockingInfo();
            Assert.True(info.CanAutoMerge);
            mgr.MergeData(info);
            AssertLocked(Record.Exception(() => a.CommitChanges()));
        }
        Assert.Equal(50m, Saved(locked).Amount);
    }

    [Fact] public void Pre_lock_edit_committed_first_wins_and_command_retries()
    {
        // Mirror: OS-A commits first (still draft, unlocked); the command, loaded before that, loses, retries and succeeds.
        var id = Started(false, "Amount");
        var runtime = new WorkflowRuntime(new DefinitionStore(), TimeProvider.System);
        runtime.TestHook = (point, attempt) =>
        {
            if (point != "after-load" || attempt != 1) return;
            using var a = db.NewObjectSpace();
            a.GetObjectByKey<PurchaseRequest>(id).Amount = 999;
            a.CommitChanges();
        };
        Ok(runtime.Fire(Alice(), typeof(PurchaseRequest), id, "Submit"));
        Assert.Equal((999m, "approval"), (Saved(id).Amount, Saved(id).Workflow!.StateKey));
    }

    sealed class SaveCounter : SaveChangesInterceptor
    {
        public int Count;
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        { Count++; return result; }
    }
}
