using DevExpress.ExpressApp;
using Microsoft.EntityFrameworkCore;
using XafStateless.Demo.Module.BusinessObjects;
using XafStateless.Engine;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Services;

namespace XafStateless.Module.Tests;

/// <summary>CARD-2812 (Gemini): the guards judge a reference's foreign key as EF will write it. Host code that detaches the
/// principal and sets the foreign-key property leaves the navigation on the old record (change-tracking proxies); a guard
/// reading the navigation first compared the old key and let the save change a fixed link.</summary>
public class GuardForeignKeyTests(TestDb db) : IClassFixture<TestDb>
{
    readonly WorkflowRuntime rt = new(new DefinitionStore(), TimeProvider.System);

    /// <summary>Two requests started on a workflow whose first state is a task for @Buyer and notifies @Buyer: their ids.</summary>
    (Guid A, Guid B) TwoStarted()
    {
        var def = new WorkflowDef($"fk-{Guid.NewGuid():N}", 1, "review",
            [new("review", "Review", StateKind.Task, TaskRole: "@Buyer", OnEntry: [new(EffectKind.Notify, "@Buyer", "Review {Title}")]),
             new("done", "Done", StateKind.Final)],
            [new("review", "Approve", "done")]);
        using var os = db.NewObjectSpace();
        var d = DefinitionMapper.ToDraft(os, def, typeof(PurchaseRequest).FullName!);
        os.CommitChanges();
        var published = new DefinitionService(new DefinitionStore()).Publish(os, d, "tester", WorkflowRoleCatalog.Names(os));
        Assert.True(published.Ok, string.Join("; ", published.Messages));
        var user = os.CreateObject<ApplicationUser>();
        (user.UserName, user.IsActive) = ($"fk-{Guid.NewGuid():N}", true);
        var ids = new List<Guid>();
        for (var i = 0; i < 2; i++)
        {
            var pr = os.CreateObject<PurchaseRequest>();
            (pr.Title, pr.Buyer) = ($"R{i}", user);
            os.CommitChanges();
            var started = rt.Start(new WorkflowCaller("starter", _ => true, db.NewObjectSpace(), db.NewObjectSpace), typeof(PurchaseRequest), pr.ID, def.Name);
            Assert.True(started.Ok, started.Message);
            ids.Add(pr.ID);
        }
        return (ids[0], ids[1]);
    }

    /// <summary>Loads <typeparamref name="T"/> by <paramref name="pick"/>, detaches its Instance and sets the foreign key to
    /// <paramref name="to"/>; the save must be refused and the stored key unchanged.</summary>
    void DetachedRelink<T>(Func<DbContext, T> pick, string navigation, Guid? to) where T : class
    {
        Guid? before;
        using (var ctx = db.NewDbContext())
        {
            var row = pick(ctx);
            var fk = RawSql.ForeignKey<T>(ctx, navigation);
            before = (Guid?)ctx.Entry(row).Property(fk).CurrentValue;
            var principal = ctx.Entry(row).Reference(navigation);
            principal.Load();
            Assert.NotNull(principal.CurrentValue);
            ctx.Entry(principal.CurrentValue!).State = EntityState.Detached;
            ctx.Entry(row).Property(fk).CurrentValue = to;
            Assert.NotNull(principal.CurrentValue);   // the navigation still points at the detached record
            Assert.Throws<UserFriendlyException>(() => ctx.SaveChanges());   // the guard's refusal, not a database error
        }
        using (var check = db.NewDbContext())
        {
            var row = pick(check);
            Assert.Equal(before, (Guid?)check.Entry(row).Property(RawSql.ForeignKey<T>(check, navigation)).CurrentValue);
        }
    }

    Guid InstanceOf(DbContext ctx, Guid request) => ctx.Set<WorkflowInstance>().Single(i => i.SubjectId == request).ID;

    [Fact] public void A_task_cannot_be_moved_to_another_instance_through_its_foreign_key()
    {
        var (a, b) = TwoStarted();
        Guid other;
        using (var ctx = db.NewDbContext()) other = InstanceOf(ctx, b);
        DetachedRelink(ctx => ctx.Set<WorkflowTask>().Single(t => t.Instance!.SubjectId == a), nameof(WorkflowTask.Instance), other);
        DetachedRelink(ctx => ctx.Set<WorkflowTask>().Single(t => t.Instance!.SubjectId == a), nameof(WorkflowTask.Instance), null);
    }

    [Fact] public void A_notification_cannot_be_moved_to_another_instance_through_its_foreign_key()
    {
        var (a, b) = TwoStarted();
        Guid other;
        using (var ctx = db.NewDbContext()) other = InstanceOf(ctx, b);
        DetachedRelink(ctx => ctx.Set<WorkflowNotification>().Single(n => n.Instance!.SubjectId == a), nameof(WorkflowNotification.Instance), other);
        DetachedRelink(ctx => ctx.Set<WorkflowNotification>().Single(n => n.Instance!.SubjectId == a), nameof(WorkflowNotification.Instance), null);
    }

    [Fact] public void A_subjects_workflow_link_cannot_be_changed_through_its_foreign_key()
    {
        var (a, b) = TwoStarted();
        Guid other;
        using (var ctx = db.NewDbContext()) other = InstanceOf(ctx, b);
        DetachedRelink(ctx => ctx.Set<PurchaseRequest>().Single(p => p.ID == a), nameof(PurchaseRequest.Workflow), other);
        DetachedRelink(ctx => ctx.Set<PurchaseRequest>().Single(p => p.ID == a), nameof(PurchaseRequest.Workflow), null);
    }
}
