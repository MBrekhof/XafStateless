using DevExpress.ExpressApp;
using DevExpress.ExpressApp.EFCore;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using Microsoft.EntityFrameworkCore;
using XafStateless.Demo.Module.BusinessObjects;
using XafStateless.Engine;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Controllers;
using XafStateless.Module.Services;

namespace XafStateless.Module.Tests;

/// <summary>CARD-2735: a state locks a reference member (PurchaseRequest.Buyer) as it locks a plain one; the reference
/// changes when its key does.</summary>
public class ReferenceLockTests : IClassFixture<TestDb>
{
    readonly TestDb db;
    readonly WorkflowRuntime rt = new(new DefinitionStore(), TimeProvider.System);
    readonly Guid first, second;

    public ReferenceLockTests(TestDb db)
    {
        this.db = db;
        using var os = db.NewObjectSpace();
        if (os.FirstOrDefault<PermissionPolicyRole>(r => r.Name == "Approvers") == null)
        {
            os.CreateObject<PermissionPolicyRole>().Name = "Approvers";
            foreach (var name in new[] { "first", "second" })
            {
                var u = os.CreateObject<ApplicationUser>();
                (u.UserName, u.IsActive) = (name, true);
            }
            os.CommitChanges();
        }
        (first, second) = (os.FirstOrDefault<ApplicationUser>(u => u.UserName == "first")!.ID, os.FirstOrDefault<ApplicationUser>(u => u.UserName == "second")!.ID);
    }

    /// <summary>draft (locks <paramref name="draftLocks"/>) -Submit-> submitted (locks Buyer) -Finish-> done.</summary>
    static WorkflowDef Flow(string name, params string[] draftLocks) => new(name, 1, "draft",
        [
            new("draft", "Draft", StateKind.Manual, Locked: draftLocks),
            new("submitted", "Submitted", StateKind.Manual, Locked: ["Buyer"]),
            new("done", "Done", StateKind.Final),
        ],
        [new("draft", "Submit", "submitted"), new("submitted", "Finish", "done")]);

    string Publish(WorkflowDef def)
    {
        using var os = db.NewObjectSpace();
        var d = DefinitionMapper.ToDraft(os, def, typeof(PurchaseRequest).FullName!);
        os.CommitChanges();
        var r = new DefinitionService(new DefinitionStore()).Publish(os, d, "tester", WorkflowRoleCatalog.Names(os));
        Assert.True(r.Ok, string.Join("; ", r.Messages));
        return def.Name;
    }

    WorkflowCaller Caller() => db.Caller("alice", "Approvers");

    static void Ok(RunResult r) => Assert.True(r.Ok, r.Message);

    /// <summary>A request created without a buyer (so no initial lock on Buyer can refuse it), started, given the first
    /// buyer in draft (unlocked there), and submitted: Buyer is now locked.</summary>
    Guid Submitted()
    {
        var name = Publish(Flow($"rl-{Guid.NewGuid():N}"));
        Guid id;
        using (var os = db.NewObjectSpace())
        {
            var pr = os.CreateObject<PurchaseRequest>();
            pr.Title = "Desk";
            os.CommitChanges();
            id = pr.ID;
        }
        Ok(rt.Start(Caller(), typeof(PurchaseRequest), id, name));
        using (var os = db.NewObjectSpace())
        {
            os.GetObjectByKey<PurchaseRequest>(id).Buyer = os.GetObjectByKey<ApplicationUser>(first);
            os.CommitChanges();   // draft does not lock Buyer
        }
        Ok(rt.Fire(Caller(), typeof(PurchaseRequest), id, "Submit"));
        return id;
    }

    Guid? BuyerOf(Guid id) => db.NewObjectSpace().GetObjectByKey<PurchaseRequest>(id).Buyer?.ID;

    static void AssertLocked(Exception? ex, string message = "'Buyer' is locked in state 'Submitted'") =>
        Assert.Equal(message, Assert.IsType<UserFriendlyException>(ex).Message);

    [Fact] public void Validation_accepts_a_reference_lock_and_offers_it()
    {
        var target = XafTypesInfo.Instance.FindTypeInfo(typeof(PurchaseRequest));
        var env = new XafWorkflowEnvironment(["Approvers"], target);
        Assert.Null(env.CheckMember("Buyer"));
        Assert.NotNull(env.CheckMember(nameof(PurchaseRequest.Workflow)));
        Assert.Empty(WorkflowValidator.Validate(Flow("v"), env));
        Assert.Contains("Buyer", WorkflowMembers.Lockable(target).Select(m => m.Name));
        Assert.DoesNotContain("Buyer", WorkflowMembers.Supported(target).Select(m => m.Name));   // SetField still cannot write it
    }

    [Fact] public void Changing_a_locked_reference_is_refused()
    {
        var id = Submitted();
        using (var os = db.NewObjectSpace())
        {
            os.GetObjectByKey<PurchaseRequest>(id).Buyer = os.GetObjectByKey<ApplicationUser>(second);
            AssertLocked(Record.Exception(() => os.CommitChanges()));
        }
        using (var os = db.NewObjectSpace())
        {
            var pr = os.GetObjectByKey<PurchaseRequest>(id);
            Assert.NotNull(pr.Buyer);   // loaded: clearing an unloaded lazy reference changes nothing
            pr.Buyer = null;
            AssertLocked(Record.Exception(() => os.CommitChanges()));
        }
        Assert.Equal(first, BuyerOf(id));
    }

    [Fact] public void Changing_a_locked_reference_by_its_key_is_refused()
    {
        var id = Submitted();
        using (var ctx = db.NewDbContext())
        {
            var pr = ctx.PurchaseRequests.Single(x => x.ID == id);
            var fk = ctx.Entry(pr).Navigation(nameof(PurchaseRequest.Buyer)).Metadata is Microsoft.EntityFrameworkCore.Metadata.INavigation n
                ? n.ForeignKey.Properties.Single().Name : throw new InvalidOperationException("Buyer is not a navigation");
            ctx.Entry(pr).Property(fk).CurrentValue = second;
            AssertLocked(Record.Exception(() => ctx.SaveChanges()));
        }
        Assert.Equal(first, BuyerOf(id));
    }

    [Fact] public void A_detached_update_cannot_change_a_locked_reference()
    {
        var id = Submitted();
        using (var ctx = db.NewDbContext())
        {
            var pr = ctx.PurchaseRequests.AsNoTracking().Include(x => x.Workflow).Single(x => x.ID == id);
            pr.Buyer = ctx.Set<ApplicationUser>().AsNoTracking().Single(u => u.ID == second);
            ctx.Update(pr);
            foreach (var other in ctx.ChangeTracker.Entries().Where(e => e.Entity != pr)) other.State = EntityState.Unchanged;
            AssertLocked(Record.Exception(() => ctx.SaveChanges()));
        }
        Assert.Equal(first, BuyerOf(id));
    }

    [Fact] public void A_forged_original_key_cannot_change_a_locked_reference()
    {
        var id = Submitted();
        using (var os = db.NewObjectSpace())
        {
            var pr = os.GetObjectByKey<PurchaseRequest>(id);
            pr.Buyer = os.GetObjectByKey<ApplicationUser>(second);
            var ctx = ((EFCoreObjectSpace)os).DbContext;
            ctx.ChangeTracker.DetectChanges();
            var key = ctx.Entry(pr).Properties.Single(p => p.Metadata.IsForeignKey() && p.Metadata.GetContainingForeignKeys().Any(f => f.DependentToPrincipal?.Name == "Buyer"));
            (key.OriginalValue, key.IsModified) = (second, true);
            AssertLocked(Record.Exception(() => os.CommitChanges()));
        }
        Assert.Equal(first, BuyerOf(id));
    }

    [Fact] public void A_reference_with_a_clr_key_is_locked_too()
    {
        // An OtherSubject on the submitted request's instance (the runtime scope lets the link be set): 'Buyer' is locked there.
        var instanceId = db.NewObjectSpace().GetObjectByKey<PurchaseRequest>(Submitted()).Workflow!.ID;
        Guid id;
        using (var ctx = db.NewSubjectsContext())
        using (RuntimeScope.Open(ctx))
        {
            var s = ctx.CreateProxy<OtherSubject>();
            (s.Name, s.BuyerId, s.Workflow) = ("Twin", first, ctx.WorkflowInstances.Single(i => i.ID == instanceId));
            ctx.OtherSubjects.Add(s);
            ctx.SaveChanges();
            id = s.ID;
        }
        using (var ctx = db.NewSubjectsContext())
        {
            ctx.OtherSubjects.Single(x => x.ID == id).BuyerId = second;
            AssertLocked(Record.Exception(() => ctx.SaveChanges()));
        }
        using (var ctx = db.NewSubjectsContext())
        {
            ctx.OtherSubjects.Single(x => x.ID == id).Buyer = ctx.Set<ApplicationUser>().Single(u => u.ID == second);
            AssertLocked(Record.Exception(() => ctx.SaveChanges()));
        }
        using (var ctx = db.NewSubjectsContext()) Assert.Equal(first, ctx.OtherSubjects.Single(x => x.ID == id).BuyerId);
    }

    /// <summary>CARD-2895 (Gemini): a lock on 'Buyer' covers the declared key 'BuyerId'; a Set field of either name is the
    /// runtime's write of that column, so the runtime exempts every name of it.</summary>
    [Theory, InlineData("BuyerId"), InlineData("Buyer")]
    public void The_runtime_exemption_covers_every_name_of_the_column(string written)
    {
        var instanceId = db.NewObjectSpace().GetObjectByKey<PurchaseRequest>(Submitted()).Workflow!.ID;   // locks 'Buyer'
        Guid id;
        using (var ctx = db.NewSubjectsContext())
        using (RuntimeScope.Open(ctx))
        {
            var s = ctx.CreateProxy<OtherSubject>();
            (s.Name, s.BuyerId, s.Workflow) = ("Twin", first, ctx.WorkflowInstances.Single(i => i.ID == instanceId));
            ctx.OtherSubjects.Add(s);
            ctx.SaveChanges();
            id = s.ID;
        }
        using (var ctx = db.NewSubjectsContext())
        using (var scope = RuntimeScope.Open(ctx))
        {
            var s = ctx.OtherSubjects.Single(x => x.ID == id);
            var entity = ctx.Entry(s).Metadata;
            Assert.Equal(["Buyer", "BuyerId"], StateLockGuard.Aliases(entity, written).Order());
            scope.AllowColumn(entity, id, written);   // what WorkflowRuntime.Write calls for every Set field, Increment and rule output
            if (written == "Buyer") s.Buyer = ctx.Set<ApplicationUser>().Single(u => u.ID == second);
            else s.BuyerId = second;
            ctx.SaveChanges();
        }
        using (var ctx = db.NewSubjectsContext()) Assert.Equal(second, ctx.OtherSubjects.Single(x => x.ID == id).BuyerId);
    }

    [Fact] public void Other_members_save_while_the_reference_is_locked()
    {
        var id = Submitted();
        using var os = db.NewObjectSpace();
        os.GetObjectByKey<PurchaseRequest>(id).Title = "Big desk";
        os.CommitChanges();
        Assert.Equal(first, BuyerOf(id));
    }

    [Fact] public void The_editor_of_a_locked_reference_is_disabled()
    {
        var id = Submitted();
        using var os = db.NewObjectSpace();
        var (lockable, locked) = StateLockAppearanceController.Locks(new DefinitionStore(), os, os.GetObjectByKey<PurchaseRequest>(id));
        Assert.Contains("Buyer", lockable);
        Assert.Equal(["Buyer"], locked);
    }

    [Fact] public void An_unstarted_new_record_keeps_a_reference_locked_in_the_initial_state_empty()
    {
        Publish(Flow($"rl-{Guid.NewGuid():N}", "Buyer"));   // every unstarted request in this class's database is now locked on Buyer
        using (var os = db.NewObjectSpace())
        {
            var pr = os.CreateObject<PurchaseRequest>();
            (pr.Title, pr.Buyer) = ("Lamp", os.GetObjectByKey<ApplicationUser>(first));
            AssertLocked(Record.Exception(() => os.CommitChanges()), "'Buyer' is locked until its workflow starts");
        }
        using (var os = db.NewObjectSpace())
        {
            os.CreateObject<PurchaseRequest>().Title = "Lamp";
            os.CommitChanges();
        }
    }
}
