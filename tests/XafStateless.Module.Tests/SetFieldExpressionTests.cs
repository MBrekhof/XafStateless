using DevExpress.ExpressApp;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using Microsoft.EntityFrameworkCore;
using XafStateless.Demo.Module.BusinessObjects;
using XafStateless.Engine;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Services;

namespace XafStateless.Module.Tests;

/// <summary>CARD-2895: a Set field whose Value starts with '=' evaluates the rest on the subject when the state is entered
/// (BPG's TimeEntry.Approver from [Project.Manager]; here PurchaseRequest.Approver from [Buyer]).</summary>
public class SetFieldExpressionTests : IClassFixture<TestDb>
{
    readonly TestDb db;
    readonly WorkflowRuntime rt = new(new DefinitionStore(), TimeProvider.System);
    readonly Guid buyer, other;

    public SetFieldExpressionTests(TestDb db)
    {
        this.db = db;
        using var os = db.NewObjectSpace();
        if (os.FirstOrDefault<PermissionPolicyRole>(r => r.Name == "Approvers") == null)
        {
            os.CreateObject<PermissionPolicyRole>().Name = "Approvers";
            foreach (var name in new[] { "buyer", "other" })
            {
                var u = os.CreateObject<ApplicationUser>();
                (u.UserName, u.IsActive) = (name, true);
            }
            os.CommitChanges();
        }
        Guid Id(string name) => os.FirstOrDefault<ApplicationUser>(u => u.UserName == name)!.ID;
        (buyer, other) = (Id("buyer"), Id("other"));
    }

    static string? Check(string target, string value) =>
        new XafWorkflowEnvironment(["Approvers"], XafTypesInfo.Instance.FindTypeInfo(typeof(PurchaseRequest)))
            .CheckEffect(new(EffectKind.SetField, target, value));

    [Theory]
    [InlineData("Approver", "=[Buyer]")]
    [InlineData("Approver", "=Iif([Amount] > 1, [Buyer], null)")]
    [InlineData("Title", "=[Buyer.UserName]")]
    [InlineData("Title", "=ToStr([Amount])")]
    [InlineData("Title", "='=x'")]   // a literal '=' text
    [InlineData("Amount", "=[Amount] * 2")]
    [InlineData("Amount", "=[Revisions] + 1")]   // int? into decimal
    [InlineData("Revisions", "=[Revisions] + 1")]
    [InlineData("Ordered", "=[Amount] > 100")]
    [InlineData("Title", "Plain {Title}")]   // a constant, as before
    [InlineData("Amount", "=Iif([Ordered], [Amount], 0m)")]
    public void Validation_accepts(string target, string value) => Assert.Null(Check(target, value));

    [Theory]
    [InlineData("Approver", "Desk", "is a reference")]
    [InlineData("Title", "=", "needs an expression")]
    [InlineData("Title", "=   ", "needs an expression")]
    [InlineData("Title", "=[Title", "not a valid expression")]
    [InlineData("Title", "=[Nope]", "Nope")]
    [InlineData("Title", "=[Buyer.Nope]", "Nope")]
    [InlineData("Revisions", "=[<PurchaseRequest>][].Count()", "a join")]
    [InlineData("Title", "=[^.Title]", "'^.' and type casts are not supported")]
    [InlineData("Title", "=Iif(True, 'a', [^.Title])", "'^.' and type casts are not supported")]   // not left to the converter
    [InlineData("Title", "=NoSuchFunction()", "not a valid expression")]
    [InlineData("Title", "=[Buyer.StoredPassword]", "'StoredPassword'")]
    [InlineData("Revisions", "=[Buyer.<ApplicationUser>AccessFailedCount]", "type casts are not supported")]
    [InlineData("Title", "=Iif(True, 'a', [Buyer.StoredPassword])", "'StoredPassword'")]   // a branch the converter folds away
    [InlineData("Revisions", "=Iif(True, 1, [<ApplicationUser>][].Count())", "a join")]
    [InlineData("Title", "=/* nothing */", "needs an expression")]
    [InlineData("Amount", "=Iif([Ordered], [Amount], 0)", "not a valid expression")]   // the converter is stricter than the evaluator (documented)
    [InlineData("Approver", "=[buyer]", "'buyer' is not a member")]   // XAF's evaluator is case-sensitive
    [InlineData("Title", "=ToStr([ObjectSpace])", "'ObjectSpace' is not a member")]   // a CLR property XAF does not know
    [InlineData("Title", "=[Summary]", "'Summary' of PurchaseRequest cannot be read")]   // hidden (non-browsable)
    [InlineData("Title", "=[Buyer.ChangePasswordOnFirstLogon]", "'ChangePasswordOnFirstLogon'")]
    [InlineData("Revisions", "=[Buyer.Roles][].Count()", "'Roles'")]
    [InlineData("Revisions", "=[Buyer.UserLogins][].Count()", "'UserLogins'")]
    [InlineData("Amount", "=[Title]", "String")]
    [InlineData("Approver", "=[Workflow]", "WorkflowInstance")]
    [InlineData("Approver", "=null", "Object")]
    [InlineData("Workflow", "=[Workflow]", "cannot be set")]
    [InlineData("ID", "=[ID]", "cannot be set")]
    public void Validation_refuses(string target, string value, string expected) =>
        Assert.Contains(expected, Check(target, value) ?? "(accepted)");

    [Theory]
    [InlineData("=1", null)]
    [InlineData("='Task'", null)]   // an enum by name
    [InlineData("=Iif([WaitMinutes] > 0, 'Wait', 'Manual')", null)]
    [InlineData("=[Definition.State]", "does not fit")]   // another enum
    [InlineData("=1.5", "does not fit")]
    public void An_enum_takes_a_whole_number_or_a_name(string value, string? expected)
    {
        var finding = new XafWorkflowEnvironment([], XafTypesInfo.Instance.FindTypeInfo(typeof(WorkflowState)))
            .CheckEffect(new(EffectKind.SetField, nameof(WorkflowState.Kind), value));
        if (expected is null) Assert.Null(finding);
        else Assert.Contains(expected, finding ?? "(accepted)");
    }

    [Theory]
    [InlineData(typeof(DevExpress.Persistent.BaseImpl.EF.PermissionPolicy.PermissionPolicyUser), true)]
    [InlineData(typeof(DevExpress.ExpressApp.Security.ISecurityUserLockout), true)]   // these interfaces do not extend ISecurityUser
    [InlineData(typeof(DevExpress.ExpressApp.Security.ISecurityUserWithRoles), true)]
    [InlineData(typeof(DevExpress.Persistent.Base.Security.IAuthenticationStandardUser), true)]
    [InlineData(typeof(DevExpress.Persistent.Base.IPermissionPolicyUser), true)]
    [InlineData(typeof(PurchaseRequest), false)]
    public void User_secrets_apply_to_every_user_type(Type type, bool isUser) => Assert.Equal(isUser, WorkflowMembers.IsUserType(type));

    [Fact] public void The_target_lookup_offers_references()
    {
        using var os = db.NewObjectSpace();
        var d = DefinitionMapper.ToDraft(os, Flow("lk"), typeof(PurchaseRequest).FullName!);
        var e = d.States.Single(s => s.Key == "submitted").Effects.First();
        Assert.Equal(["Amount", "Approver", "Buyer", "Ordered", "Revisions", "Title"],
            ((IStringLookupSource)e).LookupValues(nameof(WorkflowEffect.Target), () => []).Order());
    }

    /// <summary>draft (locks Approver) -Submit-> submitted: Approver = [Buyer], Notify @Approver, Title gets a literal
    /// '{Title}' prefix, then a Task for @Approver (or a Manual state) -Return-> draft, -Approve-> done.</summary>
    static WorkflowDef Flow(string name, bool task = true, params EffectDef[] extra) => new(name, 1, "draft",
        [
            new("draft", "Draft", StateKind.Manual, Locked: ["Approver"]),
            new("submitted", "Submitted", task ? StateKind.Task : StateKind.Manual, TaskRole: task ? "@Approver" : null,
                OnEntry: [new(EffectKind.SetField, "Approver", "=[Buyer]"), new(EffectKind.Notify, "@Approver", "For {Title}"),
                    new(EffectKind.SetField, "Title", "='{Title}' + [Title]"), .. extra]),
            new("done", "Done", StateKind.Final),
        ],
        [new("draft", "Submit", "submitted"), new("submitted", "Return", "draft"), new("submitted", "Approve", "done")]);

    string Published(bool task = true, params EffectDef[] extra)
    {
        var def = Flow($"sfx-{Guid.NewGuid():N}", task, extra);
        using var os = db.NewObjectSpace();
        var d = DefinitionMapper.ToDraft(os, def, typeof(PurchaseRequest).FullName!);
        os.CommitChanges();
        var r = new DefinitionService(new DefinitionStore()).Publish(os, d, "tester", WorkflowRoleCatalog.Names(os));
        Assert.True(r.Ok, string.Join("; ", r.Messages));
        return def.Name;
    }

    Guid Started(string name, Guid? buyerId)
    {
        Guid id;
        using (var os = db.NewObjectSpace())
        {
            var pr = os.CreateObject<PurchaseRequest>();
            (pr.Title, pr.Buyer) = ("Desk", buyerId is { } b ? os.GetObjectByKey<ApplicationUser>(b) : null);
            os.CommitChanges();
            id = pr.ID;
        }
        Ok(rt.Start(Caller(), typeof(PurchaseRequest), id, name));
        return id;
    }

    WorkflowCaller Caller() => new("alice", _ => true, db.NewObjectSpace(), db.NewObjectSpace, null, other);

    static void Ok(RunResult r) => Assert.True(r.Ok, r.Message);

    PurchaseRequest Load(Guid id) => db.NewObjectSpace().GetObjectByKey<PurchaseRequest>(id);

    void SetBuyer(Guid id, Guid? user)
    {
        using var os = db.NewObjectSpace();
        var pr = os.GetObjectByKey<PurchaseRequest>(id);
        _ = pr.Buyer;   // loaded: clearing an unloaded lazy reference changes nothing
        pr.Buyer = user is { } u ? os.GetObjectByKey<ApplicationUser>(u) : null;
        os.CommitChanges();
        Assert.Equal(user, Load(id).Buyer?.ID);
    }

    [Fact] public void Submit_sets_the_approver_and_the_notify_and_task_after_it_use_it()
    {
        var id = Started(Published(), buyer);
        Ok(rt.Fire(Caller(), typeof(PurchaseRequest), id, "Submit"));   // draft locks Approver: the runtime's write is exempt

        var pr = Load(id);
        Assert.Equal(buyer, pr.Approver?.ID);
        Assert.Equal("{Title}Desk", pr.Title);   // evaluated, and never filled as a notification template
        using var os = db.NewObjectSpace();
        var task = os.GetObjectsQuery<WorkflowTask>().Single(t => t.Instance!.SubjectId == id);
        Assert.Equal(buyer, task.UserId);
        var note = os.GetObjectsQuery<WorkflowNotification>().Single(n => n.Instance!.SubjectId == id);
        Assert.Equal((buyer, "For Desk"), (note.UserId!.Value, WorkflowNotifications.Render(note, db.NewObjectSpace(), null)));
    }

    [Fact] public void A_resubmit_takes_the_buyer_of_that_moment()
    {
        var id = Started(Published(task: false), buyer);
        Ok(rt.Fire(Caller(), typeof(PurchaseRequest), id, "Submit"));
        Ok(rt.Fire(Caller(), typeof(PurchaseRequest), id, "Return"));
        SetBuyer(id, other);
        Assert.Equal(buyer, Load(id).Approver?.ID);   // stale until the state is entered again
        Ok(rt.Fire(Caller(), typeof(PurchaseRequest), id, "Submit"));
        Assert.Equal(other, Load(id).Approver?.ID);
    }

    [Fact] public void An_empty_source_clears_the_reference()
    {
        var id = Started(Published(task: false), buyer);
        Ok(rt.Fire(Caller(), typeof(PurchaseRequest), id, "Submit"));
        Ok(rt.Fire(Caller(), typeof(PurchaseRequest), id, "Return"));
        SetBuyer(id, null);
        var r = rt.Fire(Caller(), typeof(PurchaseRequest), id, "Submit");
        Ok(r);
        Assert.Contains("notification to 'Approver' was skipped", r.Message);
        Assert.Null(Load(id).Approver);
    }

    [Fact] public void A_null_into_a_non_nullable_member_refuses_the_command()
    {
        var id = Started(Published(task: false, new EffectDef(EffectKind.SetField, "Amount", "=[Revisions]")), buyer);
        var ex = Assert.IsType<UserFriendlyException>(Record.Exception(() => rt.Fire(Caller(), typeof(PurchaseRequest), id, "Submit")));
        Assert.Contains("'Amount' cannot be set from '[Revisions]'", ex.Message);
        var pr = Load(id);
        Assert.Equal(("draft", (Guid?)null, "Desk"), (pr.Workflow!.StateKey, pr.Approver?.ID, pr.Title));   // nothing committed
    }

    [Fact] public void An_empty_value_into_a_required_member_refuses_the_command()
    {
        var id = Started(Published(task: false, new EffectDef(EffectKind.SetField, "Title", "=[Buyer.UserName]")), null);
        var ex = Assert.IsType<UserFriendlyException>(Record.Exception(() => rt.Fire(Caller(), typeof(PurchaseRequest), id, "Submit")));
        Assert.Contains("'Title' cannot be set from '[Buyer.UserName]': it is empty, and the member is required", ex.Message);
        Assert.Equal("Desk", Load(id).Title);
    }

    [Fact] public void A_definition_published_before_with_an_equals_constant_refuses_instead_of_failing()
    {
        // Published as 0.4 did (no expression check): Publish validates, so the effect is changed in the database afterwards.
        var name = Published(task: false, new EffectDef(EffectKind.SetField, "Title", "Done"));
        using (var os = db.NewObjectSpace())
        {
            var effect = os.GetObjectsQuery<WorkflowEffect>().Single(e => e.State!.Definition!.Name == name && e.Target == "Title" && e.Value == "Done");
            using var ctx = db.NewDbContext();
            ctx.Database.ExecuteSqlRaw("UPDATE WorkflowEffects SET Value = {0} WHERE ID = {1}", "=Done [", effect.ID);
        }
        var id = Started(name, buyer);
        var ex = Assert.IsType<UserFriendlyException>(Record.Exception(() => rt.Fire(Caller(), typeof(PurchaseRequest), id, "Submit")));
        Assert.Contains("'Title' cannot be set from 'Done ['", ex.Message);
    }

    /// <summary>A value edited past Publish (or published before 0.5.0) is checked again when it runs: a user secret refuses.</summary>
    [Fact] public void An_unchecked_expression_is_checked_when_it_runs()
    {
        var name = Published(task: false, new EffectDef(EffectKind.SetField, "Title", "Done"));
        using (var os = db.NewObjectSpace())
        {
            var effect = os.GetObjectsQuery<WorkflowEffect>().Single(e => e.State!.Definition!.Name == name && e.Target == "Title" && e.Value == "Done");
            using var ctx = db.NewDbContext();
            ctx.Database.ExecuteSqlRaw("UPDATE WorkflowEffects SET Value = {0} WHERE ID = {1}", "=[Buyer.StoredPassword]", effect.ID);
        }
        var id = Started(name, buyer);
        var ex = Assert.IsType<UserFriendlyException>(Record.Exception(() => rt.Fire(Caller(), typeof(PurchaseRequest), id, "Submit")));
        Assert.Contains("'StoredPassword' of ApplicationUser cannot be read", ex.Message);
        Assert.Equal("Desk", Load(id).Title);
    }

    [Fact] public void A_decision_after_it_sees_the_new_value()
    {
        var def = new WorkflowDef($"sfx-{Guid.NewGuid():N}", 1, "draft",
            [
                new("draft", "Draft", StateKind.Manual),
                new("check", "Check", StateKind.Decision, OnEntry: [new(EffectKind.SetField, "Approver", "=[Buyer]")]),
                new("assigned", "Assigned", StateKind.Final),
                new("nobody", "Nobody", StateKind.Final),
            ],
            [new("draft", "Submit", "check"), new("check", "Has", "assigned", "Not IsNull([Approver])", Order: 1), new("check", "None", "nobody", Order: 2)]);
        string name;
        using (var os = db.NewObjectSpace())
        {
            var d = DefinitionMapper.ToDraft(os, def, typeof(PurchaseRequest).FullName!);
            os.CommitChanges();
            var r = new DefinitionService(new DefinitionStore()).Publish(os, d, "tester", WorkflowRoleCatalog.Names(os));
            Assert.True(r.Ok, string.Join("; ", r.Messages));
            name = def.Name;
        }
        var id = Started(name, buyer);
        Ok(rt.Fire(Caller(), typeof(PurchaseRequest), id, "Submit"));
        Assert.Equal("assigned", Load(id).Workflow!.StateKey);
    }

    [Fact] public void A_timer_whose_expression_is_refused_is_skipped_and_stays_due()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var timed = new WorkflowRuntime(new DefinitionStore(), clock);
        var def = new WorkflowDef($"sfx-{Guid.NewGuid():N}", 1, "draft",
            [
                new("draft", "Draft", StateKind.Manual),
                new("cooling", "Cooling", StateKind.Wait, WaitMinutes: 1),
                new("done", "Done", StateKind.Final, OnEntry: [new(EffectKind.SetField, "Amount", "=[Revisions]")]),
            ],
            [new("draft", "Submit", "cooling"), new("cooling", "Cooled", "done")]);
        using (var os = db.NewObjectSpace())
        {
            var d = DefinitionMapper.ToDraft(os, def, typeof(PurchaseRequest).FullName!);
            os.CommitChanges();
            Assert.True(new DefinitionService(new DefinitionStore()).Publish(os, d, "tester", WorkflowRoleCatalog.Names(os)).Ok);
        }
        var id = Started(def.Name, buyer);
        Ok(timed.Fire(Caller(), typeof(PurchaseRequest), id, "Submit"));
        clock.Now = clock.Now.AddMinutes(2);
        Assert.Equal(0, timed.RunDueTimers(db.NewObjectSpace));   // Revisions is null: refused, logged, not thrown
        var instance = Load(id).Workflow!;
        Assert.Equal(("cooling", true), (instance.StateKey, instance.DueUtc != null));
    }

    sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact] public void A_scalar_expression_is_evaluated_and_converted()
    {
        var id = Started(Published(task: false, new EffectDef(EffectKind.SetField, "Revisions", "=Iif(IsNull([Revisions]), 0, [Revisions]) + 1"),
            new EffectDef(EffectKind.SetField, "Amount", "=[Revisions] * 2.5")), buyer);
        Ok(rt.Fire(Caller(), typeof(PurchaseRequest), id, "Submit"));
        var pr = Load(id);
        Assert.Equal((1, 2.5m), (pr.Revisions, pr.Amount));   // effects run in order: Amount sees the new Revisions
    }
}
