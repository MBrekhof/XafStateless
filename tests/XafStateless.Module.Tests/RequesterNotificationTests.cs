using DevExpress.ExpressApp;
using DevExpress.ExpressApp.EFCore;
using Microsoft.EntityFrameworkCore;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using XafStateless.Demo.Module.BusinessObjects;
using XafStateless.Engine;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Services;

namespace XafStateless.Module.Tests;

/// <summary>XSM-014: a Notify effect can address the requester, the user who started the workflow.</summary>
public class RequesterNotificationTests : IClassFixture<TestDb>
{
    readonly TestDb db;
    readonly Clock clock = new(new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero));   // before other tests' timers
    readonly WorkflowRuntime rt;
    static readonly Guid AliceId = Guid.NewGuid(), BobId = Guid.NewGuid();

    public RequesterNotificationTests(TestDb db)
    {
        this.db = db;
        rt = new WorkflowRuntime(new DefinitionStore(), clock);
        using var os = db.NewObjectSpace();
        if (os.FirstOrDefault<PermissionPolicyRole>(r => r.Name == "Approvers") == null)
            os.CreateObject<PermissionPolicyRole>().Name = "Approvers";
        os.CommitChanges();
    }

    sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    const string Requester = EffectDef.Requester;

    /// <summary>draft (tells the requester) -Submit-> approval (Approvers) -Approve-> cooling (1 min) -> ordered (tells the
    /// requester and Approvers).</summary>
    static WorkflowDef Flow(string name) => new(name, 1, "draft",
        [
            new("draft", "Draft", StateKind.Manual, OnEntry: [new(EffectKind.Notify, Requester, "Started: {Title}")]),
            new("approval", "Approval", StateKind.Task, TaskRole: "Approvers"),
            new("cooling", "Cooling", StateKind.Wait, WaitMinutes: 1),
            new("ordered", "Ordered", StateKind.Final,
                OnEntry: [new(EffectKind.Notify, Requester, "Ordered: {Title}"), new(EffectKind.Notify, "Approvers", "Ordered: {Title}")]),
        ],
        [new("draft", "Submit", "approval"), new("approval", "Approve", "cooling"), new("cooling", "Cooled", "ordered")]);

    WorkflowCaller Caller(string name, Guid? userId) => new(name, "Approvers".Equals, db.NewObjectSpace(), db.NewObjectSpace, null, userId);

    string Published()
    {
        var name = $"rq-{Guid.NewGuid():N}";
        using var os = db.NewObjectSpace();
        var d = DefinitionMapper.ToDraft(os, Flow(name), typeof(PurchaseRequest).FullName!);
        os.CommitChanges();
        var r = new DefinitionService(new DefinitionStore()).Publish(os, d, "tester", WorkflowRoleCatalog.Names(os));
        Assert.True(r.Ok, string.Join("; ", r.Messages));
        return name;
    }

    Guid NewRequest(string title)
    {
        using var os = db.NewObjectSpace();
        var pr = os.CreateObject<PurchaseRequest>();
        (pr.Title, pr.Amount) = (title, 10);
        os.CommitChanges();
        return pr.ID;
    }

    static void Ok(RunResult r) => Assert.True(r.Ok, r.Message);

    WorkflowInstance Instance(Guid subject) => db.NewObjectSpace().GetObjectByKey<PurchaseRequest>(subject).Workflow!;

    List<WorkflowNotification> Notes(Guid instance) =>
        db.NewObjectSpace().GetObjectsQuery<WorkflowNotification>().Where(n => n.Instance!.ID == instance).OrderBy(n => n.Body).ToList();

    [Fact] public void Requester_is_told_on_start_and_after_someone_else_and_the_timer_move_it()
    {
        var name = Published();
        var id = NewRequest("Desk");
        Ok(rt.Start(Caller("alice", AliceId), typeof(PurchaseRequest), id, name));
        Ok(rt.Fire(Caller("alice", AliceId), typeof(PurchaseRequest), id, "Submit"));
        var task = db.NewObjectSpace().GetObjectsQuery<WorkflowTask>().Single(t => t.Instance!.ID == Instance(id).ID && t.State == TaskState.Open).ID;
        Ok(rt.Decide(Caller("bob", BobId), task, "Approve"));
        clock.Now = clock.Now.AddMinutes(2);
        Assert.Equal(1, rt.RunDueTimers(db.NewObjectSpace));

        var inst = Instance(id);
        Assert.Equal(("ordered", AliceId, "alice"), (inst.StateKey, inst.StartedById, inst.StartedBy));
        var notes = Notes(inst.ID);
        Assert.Equal(3, notes.Count);
        using var os = db.NewObjectSpace();
        var toAlice = notes.Where(n => n.UserId == AliceId).Select(n => (WorkflowNotifications.Render(n, os, null), n.Role, n.UserName)).ToList();
        Assert.Equal([("Ordered: Desk", "", "alice"), ("Started: Desk", "", "alice")], toAlice);
        Assert.Single(notes, n => n.Role == "Approvers" && n.UserId == null);   // role addressing is unchanged
    }

    [Fact] public void Without_a_user_the_requester_notification_is_skipped_and_reported()
    {
        var name = Published();
        var id = NewRequest("Chair");
        var r = rt.Start(Caller("system", null), typeof(PurchaseRequest), id, name);
        Ok(r);
        Assert.Contains("no requester", r.Message);
        var inst = Instance(id);
        Assert.Equal((null, null), (inst.StartedById, inst.StartedBy));
        Assert.Empty(Notes(inst.ID));
    }

    [Fact] public void The_requester_is_fixed_once_the_instance_exists()
    {
        var name = Published();
        var (withUser, withoutUser) = (NewRequest("Lamp"), NewRequest("Rug"));
        Ok(rt.Start(Caller("alice", AliceId), typeof(PurchaseRequest), withUser, name));
        Ok(rt.Start(Caller("system", null), typeof(PurchaseRequest), withoutUser, name));
        foreach (var (subject, change) in new (Guid, Action<WorkflowInstance>)[]
                 {
                     (withUser, i => i.StartedById = BobId),
                     (withUser, i => i.StartedBy = "bob"),
                     (withoutUser, i => (i.StartedById, i.StartedBy) = (BobId, "bob")),   // null -> a user: still refused
                 })
        {
            using var os = db.NewObjectSpace();
            change(os.GetObjectByKey<PurchaseRequest>(subject).Workflow!);
            Assert.StartsWith("Workflow instance:", Assert.Throws<UserFriendlyException>(() => os.CommitChanges()).Message);
        }
    }

    [Fact] public void A_notification_keeps_its_recipient_and_body_but_delivery_may_update_it()
    {
        var name = Published();
        var id = NewRequest("Shelf");
        Ok(rt.Start(Caller("alice", AliceId), typeof(PurchaseRequest), id, name));
        var noteId = Notes(Instance(id).ID).Single().ID;
        foreach (var change in new Action<WorkflowNotification>[]
                 {
                     n => n.UserId = BobId, n => n.UserName = "bob", n => n.Role = "Approvers", n => n.Body = "changed",
                     n => n.IsTemplate = false,   // CARD-2597: a cleared flag would mail the raw template
                     n => ((EFCoreObjectSpace)((IObjectSpaceLink)n).ObjectSpace).DbContext.Entry(n)   // CARD-2788: the values of the notice
                         .Property(WorkflowNotifications.SnapshotColumn).CurrentValue = "{}",
                 })
        {
            using var os = db.NewObjectSpace();
            change(os.GetObjectByKey<WorkflowNotification>(noteId));
            Assert.StartsWith("Workflow notification:", Assert.Throws<UserFriendlyException>(() => os.CommitChanges()).Message);
        }
        using (var os = db.NewObjectSpace())
        {
            var n = os.GetObjectByKey<WorkflowNotification>(noteId);
            (n.State, n.Attempts, n.LastError) = (NotificationState.Failed, 1, "smtp down");   // what a dispatcher does
            os.CommitChanges();
        }
        Assert.Equal(NotificationState.Failed, db.NewObjectSpace().GetObjectByKey<WorkflowNotification>(noteId).State);

        // A dispatcher that attaches an untracked row for its delivery update: the row carries no shadow values (the snapshot,
        // the instance key: null), and neither Attach nor Update writes them, so the update is accepted and they stay.
        string? Snapshot()
        {
            using var ctx = db.NewDbContext();
            return ctx.Set<WorkflowNotification>().Where(x => x.ID == noteId)
                .Select(x => EF.Property<string?>(x, WorkflowNotifications.SnapshotColumn)).Single();
        }
        var stored = Snapshot();
        Assert.NotNull(stored);
        foreach (var (attempts, attach) in new[] { (2, true), (3, false) })
        {
            using var ctx = db.NewDbContext();
            var n = ctx.Set<WorkflowNotification>().AsNoTracking().Single(x => x.ID == noteId);
            if (attach) ctx.Attach(n); else ctx.Update(n);
            n.Attempts = attempts;
            ctx.SaveChanges();
        }
        var reloaded = db.NewObjectSpace().GetObjectByKey<WorkflowNotification>(noteId);
        Assert.Equal(3, reloaded.Attempts);
        Assert.NotNull(reloaded.Instance);
        Assert.Equal(stored, Snapshot());
        using (var ctx = db.NewDbContext())   // Update writes every CLR column: a changed text is still refused
        {
            var n = ctx.Set<WorkflowNotification>().AsNoTracking().Single(x => x.ID == noteId);
            ctx.Update(n);
            n.Body = "changed";
            Assert.StartsWith("Workflow notification:", Assert.ThrowsAny<Exception>(() => ctx.SaveChanges()).Message);
        }
    }
}
