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

    [Fact] public void NewDraft_keeps_locks()
    {
        var def = Fixture($"lock-{Guid.NewGuid():N}", "Amount");
        using var os = db.NewObjectSpace();
        var published = DefinitionMapper.ToDraft(os, def, typeof(PurchaseRequest).FullName!);
        os.CommitChanges();
        var service = new DefinitionService(new DefinitionStore());
        Assert.True(service.Publish(os, published, "tester", WorkflowRoleCatalog.Names(os)).Ok);

        var draft = service.NewDraft(os, def.Name);
        os.CommitChanges();
        Assert.Equal("Amount", draft.States.Single(s => s.Key == "approval").LockedMembers);
        Assert.Equal(["Amount"], DefinitionMapper.ToDef(draft).State("approval").LockedMembers);
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

    [Fact] public void Detached_update_cannot_write_locked_member()
    {
        // The disconnected-update pattern: ctx.Update marks every property Modified with original == current.
        var id = Started(true, "Amount");
        using (var ctx = db.NewDbContext())
        {
            var pr = ctx.PurchaseRequests.AsNoTracking().Include(x => x.Workflow).Single(x => x.ID == id);   // the link keeps its FK
            pr.Amount = 999;
            ctx.Update(pr);
            ctx.Entry(pr.Workflow!).State = EntityState.Unchanged;   // Update walks the graph; only the subject is being updated
            Assert.Equal(999m, ctx.Entry(pr).Property(x => x.Amount).OriginalValue);
            AssertLocked(Record.Exception(() => ctx.SaveChanges()));
        }
        Assert.Equal(50m, Saved(id).Amount);
    }

    [Fact] public void Forged_original_value_cannot_write_locked_member()
    {
        var id = Started(true, "Amount");
        using (var os = db.NewObjectSpace())
        {
            var pr = os.GetObjectByKey<PurchaseRequest>(id);
            pr.Amount = 999;
            var amount = ((EFCoreObjectSpace)os).DbContext.Entry(pr).Property(nameof(PurchaseRequest.Amount));
            (amount.OriginalValue, amount.IsModified) = (999m, true);
            AssertLocked(Record.Exception(() => os.CommitChanges()));
        }
        Assert.Equal(50m, Saved(id).Amount);
    }

    [Fact] public void Instance_delete_is_refused_outside_runtime()
    {
        // Deleting the instance would drop the subject's locks. Plain (admin-style, non-secured) spaces, soft and hard delete.
        var id = Started(true, "Amount");
        var instanceId = Saved(id).Workflow!.ID;
        using (var os = db.NewObjectSpace())
        {
            os.Delete(os.GetObjectByKey<WorkflowInstance>(instanceId));
            Assert.Contains("deleted by the workflow runtime only", Record.Exception(() => os.CommitChanges())!.Message);
        }
        using (var ctx = db.NewDbContext())
        {
            ctx.Remove(ctx.WorkflowInstances.Single(i => i.ID == instanceId));
            Assert.Contains("deleted by the workflow runtime only", Record.Exception(() => ctx.SaveChanges())!.Message);
        }
        using (var check = db.NewObjectSpace()) Assert.NotNull(check.GetObjectByKey<WorkflowInstance>(instanceId));
        Assert.Equal("approval", Saved(id).Workflow!.StateKey);
    }

    static void AssertInFlight(Exception? ex) =>
        Assert.Equal("'Chair' cannot be deleted while its workflow is in 'Approval'", Assert.IsType<UserFriendlyException>(ex).Message);

    [Fact] public void Subject_hard_delete_is_refused_mid_workflow()
    {
        var id = Started(true);
        using (var ctx = db.NewDbContext())
        {
            ctx.Remove(ctx.PurchaseRequests.Single(x => x.ID == id));
            AssertInFlight(Record.Exception(() => ctx.SaveChanges()));
        }
        Assert.NotNull(Saved(id));
    }

    [Fact] public void Subject_soft_delete_is_refused_mid_workflow()
    {
        var id = Started(true);
        using (var os = db.NewObjectSpace())
        {
            os.Delete(os.GetObjectByKey<PurchaseRequest>(id));
            AssertInFlight(Record.Exception(() => os.CommitChanges()));
        }
        Assert.NotNull(Saved(id));
    }

    [Fact] public void Forged_original_gcrecord_cannot_soft_delete_mid_workflow()
    {
        // Original == current == nonzero: judged against the persisted (live) row, this is still a soft delete.
        var id = Started(true);
        using (var ctx = db.NewDbContext())
        {
            var gc = ctx.Entry(ctx.PurchaseRequests.Single(x => x.ID == id)).Property(x => x.GCRecord);
            (gc.CurrentValue, gc.OriginalValue, gc.IsModified) = (7, 7, true);
            AssertInFlight(Record.Exception(() => ctx.SaveChanges()));
        }
        using (var ctx = db.NewDbContext())
            Assert.Equal(0, ctx.PurchaseRequests.IgnoreQueryFilters().Single(x => x.ID == id).GCRecord);
    }

    /// <summary>A <see cref="ConvertedSubject"/> linked to an in-flight request's instance (approval locks Ordered). The link is
    /// runtime-owned, so it is set inside a runtime scope; the guard only reads link -> instance -> state.</summary>
    Guid ConvertedInApproval()
    {
        var instanceId = Saved(Started(true, "Ordered")).Workflow!.ID;
        using var ctx = db.NewSubjectsContext();
        using var scope = RuntimeScope.Open(ctx);
        var s = ctx.CreateProxy<ConvertedSubject>();
        (s.Name, s.Workflow) = ("Gadget", ctx.WorkflowInstances.Single(i => i.ID == instanceId));
        ctx.ConvertedSubjects.Add(s);
        ctx.SaveChanges();
        return s.ID;
    }

    [Fact] public void Converted_locked_member_change_is_refused()
    {
        var id = ConvertedInApproval();
        using (var ctx = db.NewSubjectsContext())
        {
            ctx.ConvertedSubjects.Single(x => x.ID == id).Ordered = true;   // stored inverted: the column goes 1 -> 0
            Assert.Equal("'Ordered' is locked in state 'Approval'", Assert.IsType<UserFriendlyException>(Record.Exception(() => ctx.SaveChanges())).Message);
        }
        using (var ctx = db.NewSubjectsContext()) Assert.False(ctx.ConvertedSubjects.Single(x => x.ID == id).Ordered);
    }

    [Fact] public void Converted_locked_member_unchanged_is_allowed()
    {
        var id = ConvertedInApproval();
        using (var ctx = db.NewSubjectsContext())
        {
            var s = ctx.ConvertedSubjects.Single(x => x.ID == id);
            ctx.Entry(s).Property(x => x.Ordered).IsModified = true;   // same value, written again
            s.Name = "Gizmo";
            ctx.SaveChanges();
        }
        using (var ctx = db.NewSubjectsContext()) Assert.Equal(("Gizmo", false), (ctx.ConvertedSubjects.Single(x => x.ID == id).Name, ctx.ConvertedSubjects.Single(x => x.ID == id).Ordered));
    }

    [Fact] public void Colliding_ids_across_subject_types_do_not_share_links()
    {
        // A workflow-free OtherSubject with the in-flight request's ID, both hard-deleted in one save (request tracked first).
        var id = Started(true);
        using (var ctx = db.NewSubjectsContext())
        {
            var other = ctx.CreateProxy<OtherSubject>();
            (other.ID, other.Name) = (id, "Twin");
            ctx.OtherSubjects.Add(other);
            ctx.SaveChanges();
        }
        using (var ctx = db.NewSubjectsContext())
        {
            ctx.Remove(ctx.PurchaseRequests.Single(x => x.ID == id));
            ctx.Remove(ctx.OtherSubjects.Single(x => x.ID == id));
            AssertInFlight(Record.Exception(() => ctx.SaveChanges()));
        }
        Assert.NotNull(Saved(id));
        using (var ctx = db.NewSubjectsContext()) Assert.NotNull(ctx.OtherSubjects.SingleOrDefault(x => x.ID == id));
    }

    [Fact] public void Subject_delete_is_allowed_in_final_state()
    {
        var id = Started(true);
        Ok(rt.Decide(Alice(), OpenTask(id), "Reject"));
        using (var os = db.NewObjectSpace())
        {
            os.Delete(os.GetObjectByKey<PurchaseRequest>(id));
            os.CommitChanges();
        }
        Assert.Null(Saved(id));
    }

    [Fact] public void Subject_delete_is_allowed_without_workflow()
    {
        Guid id;
        using (var os = db.NewObjectSpace())
        {
            var pr = os.CreateObject<PurchaseRequest>();
            pr.Title = "Lamp";
            os.CommitChanges();
            id = pr.ID;
        }
        using (var os = db.NewObjectSpace())
        {
            os.Delete(os.GetObjectByKey<PurchaseRequest>(id));
            os.CommitChanges();
        }
        Assert.Null(Saved(id));
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
            // Exactly 1, not the brief's ">= 2": the UI path merges BEFORE committing (ProcessDataLockingInfoController checks
            // GetDataLockingInfo first, as reproduced here), so no failed first pass exists. The one pass is the merged save, and
            // the locked half below proves the guard judges it against the new persisted state.
            Assert.Equal(1, saves.Count);
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
