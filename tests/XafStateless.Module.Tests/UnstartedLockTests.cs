using DevExpress.ExpressApp;
using DevExpress.ExpressApp.EFCore;
using DevExpress.Persistent.BaseImpl.EF;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using Microsoft.EntityFrameworkCore;
using XafStateless.Demo.Module.BusinessObjects;
using XafStateless.Engine;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Controllers;
using XafStateless.Module.Services;

namespace XafStateless.Module.Tests;

/// <summary>CARD-2713: a subject without a workflow link is locked as if in the initial state of every Published definition
/// for its type. In this class's database: "ul-a" locks Ordered in its initial state and Revisions only in a later one (and
/// sets Ordered on entry); "ul-b" locks Title in its initial state. Requests here are created without a Title.</summary>
public class UnstartedLockTests : IClassFixture<TestDb>
{
    const string A = "ul-a", B = "ul-b";
    readonly TestDb db;
    readonly RefundAction refund = new();
    readonly WorkflowRuntime rt;
    readonly DefinitionService svc;

    public UnstartedLockTests(TestDb db)
    {
        this.db = db;
        var actions = new WorkflowActionCatalog([refund]);
        (rt, svc) = (new WorkflowRuntime(new DefinitionStore(), TimeProvider.System, actions), new DefinitionService(new DefinitionStore(), actions));
        using var os = db.NewObjectSpace();
        if (os.FirstOrDefault<PermissionPolicyRole>(r => r.Name == "Approvers") != null) return;   // one seed per fixture
        os.CreateObject<PermissionPolicyRole>().Name = "Approvers";
        os.CommitChanges();
        Publish(new(A, 1, "draft",
            [
                new("draft", "Draft", StateKind.Manual, OnEntry: [new(EffectKind.SetField, "Ordered", "true")], Locked: ["Ordered"]),
                new("approval", "Approval", StateKind.Manual, Locked: ["Revisions"]),
                new("done", "Done", StateKind.Final, OnEntry: [new(EffectKind.RunAction, "refund", "")]),
            ],
            [new("draft", "Submit", "approval"), new("approval", "Go", "done")]));
        Publish(new(B, 1, "start", [new("start", "Start", StateKind.Manual, Locked: ["Title"]), new("end", "End", StateKind.Final)],
            [new("start", "Finish", "end")]));
    }

    void Publish(WorkflowDef def)
    {
        using var os = db.NewObjectSpace();
        var d = DefinitionMapper.ToDraft(os, def, typeof(PurchaseRequest).FullName!);
        os.CommitChanges();
        var r = svc.Publish(os, d, "tester", WorkflowRoleCatalog.Names(os));
        Assert.True(r.Ok, string.Join("; ", r.Messages));
    }

    static void Ok(RunResult r) => Assert.True(r.Ok, r.Message);

    static void AssertUnstartedLock(string member, Exception? ex) =>
        Assert.Contains($"'{member}' is locked until its workflow starts", Assert.IsType<UserFriendlyException>(ex).Message);

    Guid NewRequest(decimal amount = 0)
    {
        using var os = db.NewObjectSpace();
        var pr = os.CreateObject<PurchaseRequest>();
        pr.Amount = amount;
        os.CommitChanges();
        return pr.ID;
    }

    void Change(Guid id, Action<PurchaseRequest> change)
    {
        using var os = db.NewObjectSpace();
        change(os.GetObjectByKey<PurchaseRequest>(id));
        os.CommitChanges();
    }

    PurchaseRequest Saved(Guid id) => db.NewObjectSpace().GetObjectByKey<PurchaseRequest>(id);

    [Fact] public void A_new_request_may_not_set_an_initially_locked_member()
    {
        using (var os = db.NewObjectSpace())
        {
            os.CreateObject<PurchaseRequest>().Ordered = true;
            AssertUnstartedLock("Ordered", Record.Exception(() => os.CommitChanges()));
        }
        using (var os = db.NewObjectSpace())
        {
            os.CreateObject<PurchaseRequest>().Title = "Chair";   // locked by the second definition: the union
            AssertUnstartedLock("Title", Record.Exception(() => os.CommitChanges()));
        }
        using (var os = db.NewObjectSpace())
        {
            var pr = os.CreateObject<PurchaseRequest>();
            (pr.Amount, pr.Revisions) = (40, 3);   // Revisions is locked only once submitted (the TimeEntry.Hours case)
            os.CommitChanges();
        }
    }

    [Fact] public void An_existing_unstarted_request_may_not_change_an_initially_locked_member()
    {
        var id = NewRequest(10);
        AssertUnstartedLock("Ordered", Record.Exception(() => Change(id, pr => pr.Ordered = true)));
        AssertUnstartedLock("Title", Record.Exception(() => Change(id, pr => pr.Title = "Desk")));
        Change(id, pr => (pr.Amount, pr.Revisions) = (20, 1));
        Assert.Equal((20m, 1, false, ""), (Saved(id).Amount, Saved(id).Revisions, Saved(id).Ordered, Saved(id).Title));
    }

    [Fact] public void A_disconnected_update_of_an_unstarted_request_is_judged_against_the_database()
    {
        var id = NewRequest(10);
        using (var ctx = db.NewDbContext())   // ctx.Update marks every property Modified with original == current
        {
            var pr = ctx.PurchaseRequests.AsNoTracking().Single(x => x.ID == id);
            pr.Ordered = true;
            ctx.Update(pr);
            AssertUnstartedLock("Ordered", Record.Exception(() => ctx.SaveChanges()));
        }
        using (var os = db.NewObjectSpace())   // a forged original value
        {
            var pr = os.GetObjectByKey<PurchaseRequest>(id);
            pr.Ordered = true;
            var ordered = ((EFCoreObjectSpace)os).DbContext.Entry(pr).Property(nameof(PurchaseRequest.Ordered));
            (ordered.OriginalValue, ordered.IsModified) = (true, true);
            AssertUnstartedLock("Ordered", Record.Exception(() => os.CommitChanges()));
        }
        Assert.False(Saved(id).Ordered);
    }

    [Fact] public void Start_works_and_then_the_current_state_rules()
    {
        var id = NewRequest(10);
        Ok(rt.Start(db.Caller("alice"), typeof(PurchaseRequest), id, A));   // draft's SetField of the locked Ordered is the runtime's
        Assert.True(Saved(id).Ordered);
        Assert.Contains("'Ordered' is locked in state 'Draft'", Assert.IsType<UserFriendlyException>(Record.Exception(() => Change(id, pr => pr.Ordered = false))).Message);
        Change(id, pr => pr.Title = "Desk");   // B's initial lock no longer applies: the request has its own workflow
        Ok(rt.Fire(db.Caller("alice"), typeof(PurchaseRequest), id, "Submit"));
        Change(id, pr => pr.Ordered = false);
        Assert.Contains("'Revisions' is locked in state 'Approval'", Assert.IsType<UserFriendlyException>(Record.Exception(() => Change(id, pr => pr.Revisions = 9))).Message);
    }

    [Fact] public void An_action_may_not_change_a_locked_member_of_another_unstarted_request()
    {
        var (id, bystander) = (NewRequest(10), NewRequest(5));
        Ok(rt.Start(db.Caller("alice"), typeof(PurchaseRequest), id, A));
        Ok(rt.Fire(db.Caller("alice"), typeof(PurchaseRequest), id, "Submit"));
        refund.Also = c => c.ObjectSpace.GetObjectByKey<PurchaseRequest>(bystander).Ordered = true;
        try { AssertUnstartedLock("Ordered", Record.Exception(() => rt.Fire(db.Caller("alice"), typeof(PurchaseRequest), id, "Go"))); }
        finally { refund.Also = null; }
        Assert.False(Saved(bystander).Ordered);
        Ok(rt.Fire(db.Caller("alice"), typeof(PurchaseRequest), id, "Go"));   // the refund request itself starts with defaults
    }

    [Fact] public void A_mixed_save_checks_only_the_live_added_and_modified_requests()
    {
        var (modified, deleted) = (NewRequest(10), NewRequest(10));
        void Save(bool violate)
        {
            using var os = db.NewObjectSpace();
            os.CreateObject<PurchaseRequest>().Amount = 1;
            os.CreateObject<PurchaseRequest>().Ordered = violate;
            os.GetObjectByKey<PurchaseRequest>(modified).Amount = 11;
            os.Delete(os.GetObjectByKey<PurchaseRequest>(deleted));   // soft (deferred deletion)
            os.CommitChanges();
        }
        AssertUnstartedLock("Ordered", Record.Exception(() => Save(violate: true)));
        Assert.Equal(10m, Saved(modified).Amount);
        Save(violate: false);
        Assert.Equal(11m, Saved(modified).Amount);
        Assert.Null(Saved(deleted));
    }

    [Fact] public void Draft_and_retired_definitions_lock_nothing_and_the_appearance_follows_the_guard()
    {
        var name = $"ul-c-{Guid.NewGuid():N}";
        WorkflowDef C(params string[] locks) => new(name, 1, "s", [new("s", "S", StateKind.Manual, Locked: locks), new("e", "E", StateKind.Final)], [new("s", "End", "e")]);
        using (var os = db.NewObjectSpace())   // a Draft only
        {
            DefinitionMapper.ToDraft(os, C("Amount"), typeof(PurchaseRequest).FullName!);
            os.CommitChanges();
        }
        var id = NewRequest(10);
        Change(id, pr => pr.Amount = 11);
        var store = new DefinitionStore();
        using (var os = db.NewObjectSpace())
        {
            var (lockable, locked) = StateLockAppearanceController.Locks(store, os, os.GetObjectByKey<PurchaseRequest>(id));
            Assert.Equal(["Ordered", "Title"], locked.Order());
            Assert.Equal(["Ordered", "Revisions", "Title"], lockable.Order());
        }

        using (var os = db.NewObjectSpace())   // publish the draft: Amount is locked
        {
            var r = svc.Publish(os, os.FirstOrDefault<WorkflowDefinition>(d => d.Name == name)!, "tester", WorkflowRoleCatalog.Names(os));
            Assert.True(r.Ok, string.Join("; ", r.Messages));
        }
        AssertUnstartedLock("Amount", Record.Exception(() => Change(id, pr => pr.Amount = 12)));
        using (var os = db.NewObjectSpace())
            Assert.Contains("Amount", StateLockAppearanceController.Locks(store, os, os.GetObjectByKey<PurchaseRequest>(id)).Locked);

        using (var os = db.NewObjectSpace())   // v2 without the lock retires v1
        {
            var draft = svc.NewDraft(os, name);
            draft.States.Single(s => s.Key == "s").LockedMembers = null;
            os.CommitChanges();
            var r = svc.Publish(os, draft, "tester", WorkflowRoleCatalog.Names(os));
            Assert.True(r.Ok, string.Join("; ", r.Messages));
        }
        Change(id, pr => pr.Amount = 13);
        using (var os = db.NewObjectSpace())
            Assert.DoesNotContain("Amount", StateLockAppearanceController.Locks(store, os, os.GetObjectByKey<PurchaseRequest>(id)).Locked);
    }

    public class WithDefault : BaseObject
    {
        public virtual string Title { get; set; } = "init";
        public virtual int Count { get; set; }
        public override void OnCreated() => Count = 7;
    }

    public class ThrowingDefault : BaseObject
    {
        public virtual int Count { get; set; }
        public override void OnCreated()
        {
            Count = 7;
            throw new InvalidOperationException("needs the object space");
        }
    }

    [Fact] public void A_new_subjects_baseline_is_a_fresh_instance_after_OnCreated_or_the_constructor_alone()
    {
        var withDefault = (WithDefault)StateLockGuard.Baseline(typeof(WithDefault));
        Assert.Equal(("init", 7), (withDefault.Title, withDefault.Count));
        Assert.Equal(0, ((ThrowingDefault)StateLockGuard.Baseline(typeof(ThrowingDefault))).Count);
    }
}
