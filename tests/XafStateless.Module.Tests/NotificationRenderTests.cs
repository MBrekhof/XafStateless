using DevExpress.Data.Filtering;
using DevExpress.EntityFrameworkCore.Security;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.EFCore;
using DevExpress.ExpressApp.EFCore.DataLocking;
using DevExpress.ExpressApp.Security;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using XafStateless.Demo.Module.BusinessObjects;
using XafStateless.Engine;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Services;

namespace XafStateless.Module.Tests;

/// <summary>CARD-2597: a notification keeps its template; <see cref="WorkflowNotifications.Render"/> fills it per recipient
/// from that recipient's secured space, masking what they may not read. CARD-2788: the values are those at notify time
/// (a snapshot in a shadow column); the permission checks still run per recipient, on the records the notice read.</summary>
public class NotificationRenderTests : IClassFixture<TestDb>
{
    const string Template = "T={Title} A={Amount} B={Buyer} U={Buyer.UserName}";
    const string M = WorkflowNotifications.Masked;
    readonly TestDb db;
    readonly WorkflowRuntime rt = new(new DefinitionStore(), TimeProvider.System);
    readonly Guid buyer;

    public NotificationRenderTests(TestDb db)
    {
        this.db = db;
        using var os = db.NewObjectSpace();
        if (os.FirstOrDefault<PermissionPolicyRole>(r => r.Name == "NoAmount") == null)
        {
            Role(os, "NoAmount", r =>
            {
                r.AddMemberPermission<PurchaseRequest>(SecurityOperations.Read, nameof(PurchaseRequest.Amount), null, SecurityPermissionState.Deny);
                // reads its list, as GrantParticipant lets participants (without the instance, every placeholder is masked)
                r.AddTypePermission<WorkflowNotification>(SecurityOperations.Read, SecurityPermissionState.Allow);
                r.AddTypePermission<WorkflowInstance>(SecurityOperations.Read, SecurityPermissionState.Allow);
            });
            Role(os, "UserReaders", r => r.AddTypePermission<ApplicationUser>(SecurityOperations.Read, SecurityPermissionState.Allow));
            Role(os, "NoUserName", r =>
            {
                r.AddTypePermission<ApplicationUser>(SecurityOperations.Read, SecurityPermissionState.Allow);
                r.AddMemberPermission<ApplicationUser>(SecurityOperations.Read, nameof(ApplicationUser.UserName), null, SecurityPermissionState.Deny);
            });
            Role(os, "OneUser",r => r.AddObjectPermission<ApplicationUser>(SecurityOperations.Read, "[UserName] = 'someone-else'", SecurityPermissionState.Allow));
            Role(os, "OtherTitles", null, "[Title] = 'Lamp'");
            Role(os, "Nothing", null, subject: false);
            var b = os.CreateObject<ApplicationUser>();
            (b.UserName, b.IsActive) = ("mail-buyer", true);
            os.CommitChanges();
        }
        buyer = os.FirstOrDefault<ApplicationUser>(u => u.UserName == "mail-buyer")!.ID;
    }

    /// <summary>A role and its one user (named as the role): Read on PurchaseRequest (all, by <paramref name="criteria"/>, or
    /// none), plus <paramref name="grant"/>.</summary>
    static void Role(IObjectSpace os, string name, Action<PermissionPolicyRole>? grant, string? criteria = null, bool subject = true)
    {
        var role = os.CreateObject<PermissionPolicyRole>();
        role.Name = name;
        if (subject && criteria == null) role.AddTypePermission<PurchaseRequest>(SecurityOperations.Read, SecurityPermissionState.Allow);
        if (criteria != null) role.AddObjectPermission<PurchaseRequest>(SecurityOperations.Read, criteria, SecurityPermissionState.Allow);
        grant?.Invoke(role);
        var user = os.CreateObject<ApplicationUser>();
        (user.UserName, user.IsActive) = (name, true);
        user.SetPassword("");
        user.Roles.Add(role);
        os.CommitChanges();
        ((ISecurityUserWithLoginInfo)user).CreateUserLoginInfo(SecurityDefaults.PasswordAuthentication, os.GetKeyValueAsString(user));
        os.CommitChanges();
    }

    /// <summary>A secured provider logged on as <paramref name="userName"/>, as PermissionTests builds one; <paramref name="prefetch"/>
    /// as the demo's and BPG's hosts set it (<c>PreFetchReferenceProperties()</c>).</summary>
    SecuredEFCoreObjectSpaceProvider<DemoEFCoreDbContext> LogOn(string userName, out SecurityStrategyComplex security, bool prefetch = false)
    {
        var auth = new AuthenticationStandard();
        security = new SecurityStrategyComplex(typeof(ApplicationUser), typeof(PermissionPolicyRole), auth);
        var provider = new SecuredEFCoreObjectSpaceProvider<DemoEFCoreDbContext>(security, XafTypesInfo.Instance, db.ConnectionString,
            (builder, cs) => { builder.UseConnectionString(cs); builder.AddInterceptors(new EFCoreOptimisticLockInterceptor()); })
            { PreFetchReferenceProperties = prefetch };
        auth.SetLogonParameters(new AuthenticationStandardLogonParameters(userName, ""));
        var s = security;
        using (var logon = ((INonsecuredObjectSpaceProvider)provider).CreateNonsecuredObjectSpace()) s.Logon(logon);
        return provider;
    }

    /// <summary>A "Desk" request for 12 bought by <paramref name="buyerId"/> (mail-buyer by default, none when <paramref name="noBuyer"/>),
    /// started on a workflow whose initial state notifies with <paramref name="template"/> (@Buyer, or the NoAmount role
    /// without a buyer), then runs <paramref name="after"/>; returns the notification's id.</summary>
    Guid Notified(string template = Template, bool noBuyer = false, Guid? buyerId = null, params EffectDef[] after)
    {
        var def = new WorkflowDef($"nr-{Guid.NewGuid():N}", 1, "draft",
            [new("draft", "Draft", StateKind.Manual, OnEntry: [new(EffectKind.Notify, noBuyer ? "NoAmount" : "@Buyer", template), .. after]),
             new("done", "Done", StateKind.Final)],
            [new("draft", "Finish", "done")]);
        using var os = db.NewObjectSpace();
        var d = DefinitionMapper.ToDraft(os, def, typeof(PurchaseRequest).FullName!);
        os.CommitChanges();
        var r = new DefinitionService(new DefinitionStore()).Publish(os, d, "tester", WorkflowRoleCatalog.Names(os));
        Assert.True(r.Ok, string.Join("; ", r.Messages));
        var pr = os.CreateObject<PurchaseRequest>();
        (pr.Title, pr.Amount, pr.Buyer) = ("Desk", 12, noBuyer ? null : os.GetObjectByKey<ApplicationUser>(buyerId ?? buyer));
        os.CommitChanges();
        var started = rt.Start(new WorkflowCaller("starter", _ => true, db.NewObjectSpace(), db.NewObjectSpace), typeof(PurchaseRequest), pr.ID, def.Name);
        Assert.True(started.Ok, started.Message);
        using var check = db.NewObjectSpace();
        return check.GetObjectsQuery<WorkflowNotification>().Single(n => n.Instance!.SubjectId == pr.ID).ID;
    }

    /// <summary>The notes rendered for <paramref name="userName"/>, in order, in one secured space (as the in-app list
    /// does): the notes read non-secured (as a dispatcher reads its outbox), the subjects from the user's secured space.</summary>
    string[] RenderFor(string userName, bool prefetch, params Guid[] noteIds)
    {
        using var provider = LogOn(userName, out var security, prefetch);
        using var secured = provider.CreateObjectSpace();
        using var outbox = db.NewObjectSpace();
        return noteIds.Select(id => WorkflowNotifications.Render(outbox.GetObjectByKey<WorkflowNotification>(id), secured, security)).ToArray();
    }

    string RenderFor(Guid noteId, string userName, bool prefetch = false) => RenderFor(userName, prefetch, noteId)[0];

    string Unsecured(Guid noteId)
    {
        using var os = db.NewObjectSpace();
        return WorkflowNotifications.Render(os.GetObjectByKey<WorkflowNotification>(noteId), os, null);
    }

    void Change(Guid noteId, Action<PurchaseRequest> change)
    {
        using var os = db.NewObjectSpace();
        var note = os.GetObjectByKey<WorkflowNotification>(noteId);
        change(os.GetObjectByKey<PurchaseRequest>(note.Instance!.SubjectId));
        os.CommitChanges();   // the initial state locks nothing
    }

    /// <summary>Sets the snapshot column outside EF (the guard refuses it through EF): a 0.4.0 row, or a damaged one.</summary>
    void SetSnapshot(Guid noteId, string? json)
    {
        using var ctx = db.NewDbContext();
        var table = ctx.Model.FindEntityType(typeof(WorkflowNotification))!.GetTableName();
        using var conn = new SqlConnection(db.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE [{table}] SET [{WorkflowNotifications.SnapshotColumn}] = @j WHERE [ID] = @id";
        cmd.Parameters.AddWithValue("@j", (object?)json ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", noteId);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    [Fact] public void The_notification_keeps_the_template()
    {
        var id = Notified();
        Assert.Equal(Template, db.NewObjectSpace().GetObjectByKey<WorkflowNotification>(id).Body);
    }

    [Fact] public void Without_security_everything_renders()
    {
        Assert.Equal("T=Desk A=12.00 B=mail-buyer U=mail-buyer", Unsecured(Notified()));
    }

    /// <summary>The Workflow Notification list shows <see cref="WorkflowNotification.Text"/>: rendered in the space that loaded
    /// the row with that space's security strategy (from its ServiceProvider, as in the app: the E2E checks that path), so a
    /// participant's list never shows the template, nor what they may not read. A secured space whose strategy cannot be
    /// found (this test's provider has no ServiceProvider) masks every placeholder: the snapshot is unmasked.</summary>
    [Fact] public void The_list_text_is_rendered_in_the_viewers_space()
    {
        var id = Notified();
        using (var os = db.NewObjectSpace())
            Assert.Equal(Unsecured(id), os.GetObjectByKey<WorkflowNotification>(id).Text);
        using var provider = LogOn("NoAmount", out _);
        using var secured = provider.CreateObjectSpace();
        Assert.Null(secured.ServiceProvider);
        Assert.Equal($"T={M} A={M} B={M} U={M}", secured.GetObjectByKey<WorkflowNotification>(id).Text);
    }

    [Theory, InlineData(false), InlineData(true)]
    public void A_recipient_who_may_read_everything_gets_the_unsecured_text(bool prefetch)
    {
        var id = Notified();
        Assert.Equal(Unsecured(id), RenderFor(id, "UserReaders", prefetch));
    }

    [Theory, InlineData(false), InlineData(true)]
    public void A_denied_member_and_an_unreadable_reference_type_are_masked(bool prefetch) =>
        Assert.Equal($"T=Desk A={M} B={M} U={M}", RenderFor(Notified(), "NoAmount", prefetch));

    /// <summary>{Buyer} shows the user's default member (UserName): a recipient who may read the user but not that member
    /// gets the mask, not the record's text.</summary>
    [Theory, InlineData(false), InlineData(true)]
    public void A_reference_whose_default_member_is_denied_is_masked(bool prefetch) =>
        Assert.Equal($"T=Desk A=12.00 B={M} U={M}", RenderFor(Notified(), "NoUserName", prefetch));

    /// <summary>CARD-2783: a reference to a record the recipient's criteria hide is masked, with or without prefetch (0.4.0
    /// rendered it empty without prefetch and masked with it).</summary>
    [Theory, InlineData(false), InlineData(true)]
    public void A_reference_the_criteria_do_not_allow_is_masked(bool prefetch) =>
        Assert.Equal($"T=Desk A=12.00 B={M} U={M}", RenderFor(Notified(), "OneUser", prefetch));

    /// <summary>CARD-2788: a later change (a cleared rejection reason, a resubmit) does not rewrite an earlier notice; the
    /// recipient's permissions still mask what they may not read.</summary>
    [Theory, InlineData(false), InlineData(true)]
    public void Values_are_those_at_notify_time(bool prefetch)
    {
        var id = Notified();
        Change(id, pr =>
        {
            Assert.NotNull(pr.Buyer);   // loaded: clearing an unloaded lazy reference changes nothing
            (pr.Title, pr.Amount, pr.Buyer) = ("Chair", 99, null);
        });
        Assert.Equal("T=Desk A=12.00 B=mail-buyer U=mail-buyer", Unsecured(id));
        Assert.Equal("T=Desk A=12.00 B=mail-buyer U=mail-buyer", RenderFor(id, "UserReaders", prefetch));
        Assert.Equal($"T=Desk A={M} B={M} U={M}", RenderFor(id, "NoAmount", prefetch));
        Assert.Equal($"T=Desk A=12.00 B={M} U={M}", RenderFor(id, "OneUser", prefetch));
    }

    /// <summary>Gemini (CARD-2788 plan review): a record another row prefetched into the recipient's space must not pass as
    /// readable. The first note's subject still references mail-buyer (hidden from OneUser); the second's did at notify time.</summary>
    [Fact] public void A_hidden_record_tracked_by_an_earlier_render_stays_masked()
    {
        var (first, second) = (Notified(), Notified());
        Change(second, pr => { Assert.NotNull(pr.Buyer); pr.Buyer = null; });
        var texts = RenderFor("OneUser", prefetch: true, first, second);
        Assert.All(texts, t => Assert.Equal($"T=Desk A=12.00 B={M} U={M}", t));
    }

    [Theory, InlineData(false), InlineData(true)]
    public void A_deleted_referenced_record_masks(bool prefetch)
    {
        Guid gone;
        using (var os = db.NewObjectSpace())
        {
            var u = os.CreateObject<ApplicationUser>();
            (u.UserName, u.IsActive) = ($"gone-{Guid.NewGuid():N}", true);
            os.CommitChanges();
            gone = u.ID;
        }
        var id = Notified(buyerId: gone);
        Change(id, pr => { Assert.NotNull(pr.Buyer); pr.Buyer = null; });
        using (var os = db.NewObjectSpace())
        {
            os.Delete(os.GetObjectByKey<ApplicationUser>(gone));
            os.CommitChanges();
        }
        Assert.Equal($"T=Desk A=12.00 B={M} U={M}", RenderFor(id, "UserReaders", prefetch));
    }

    [Fact] public void A_reference_empty_at_notify_time_renders_empty()
    {
        var id = Notified(noBuyer: true);
        Change(id, pr => pr.Buyer = ((IObjectSpaceLink)pr).ObjectSpace.GetObjectByKey<ApplicationUser>(buyer));
        Assert.Equal("T=Desk A=12.00 B= U=", Unsecured(id));
        Assert.Equal("T=Desk A=12.00 B= U=", RenderFor(id, "UserReaders"));
    }

    [Fact] public void A_path_over_several_records_and_a_repeated_placeholder()
    {
        var id = Notified("D={Workflow.Definition.Name} B={Buyer}/{Buyer} L={Title.Length}");
        var text = Unsecured(id);
        Assert.Matches(@"^D=nr-[0-9a-f]{32} B=mail-buyer/mail-buyer L=4$", text);
        Assert.Equal($"D={M} B=mail-buyer/mail-buyer L=4", RenderFor(id, "UserReaders"));   // may not read WorkflowInstance
    }

    /// <summary>A Notify takes the values of its own point in the state's effects: a SetField after it is not in the text.</summary>
    [Fact] public void A_later_effect_of_the_same_state_is_not_in_the_text()
    {
        var id = Notified(after: new EffectDef(EffectKind.SetField, nameof(PurchaseRequest.Title), "Changed"));
        Assert.StartsWith("T=Desk ", Unsecured(id));
    }

    [Fact] public void A_row_without_snapshot_renders_live()
    {
        var id = Notified();
        SetSnapshot(id, null);   // as 0.4.0 wrote it
        Change(id, pr => pr.Title = "Chair");
        Assert.StartsWith("T=Chair ", Unsecured(id));
        Assert.Equal($"T=Chair A={M} B={M} U={M}", RenderFor(id, "NoAmount"));
    }

    [Theory, InlineData("{"), InlineData(""), InlineData("[]"), InlineData("{}")]   // {}: no entry for any placeholder
    public void A_damaged_snapshot_masks_every_placeholder(string json)
    {
        var id = Notified();
        SetSnapshot(id, json);
        Assert.Equal($"T={M} A={M} B={M} U={M}", Unsecured(id));
        Assert.Equal($"T={M} A={M} B={M} U={M}", RenderFor(id, "UserReaders"));
    }

    /// <summary>The snapshot holds unmasked values: it is a shadow column, no XAF member, so no view, criteria, Web API or
    /// generic tool reaches it through a participant's secured space.</summary>
    [Fact] public void The_snapshot_is_no_member_a_participant_can_query()
    {
        var id = Notified();
        Assert.Null(XafTypesInfo.Instance.FindTypeInfo(typeof(WorkflowNotification)).FindMember(WorkflowNotifications.SnapshotColumn));
        using var provider = LogOn("NoAmount", out _);
        using var secured = provider.CreateObjectSpace();
        var probe = Record.Exception(() => secured.GetObjects<WorkflowNotification>(
            CriteriaOperator.Parse($"Contains([{WorkflowNotifications.SnapshotColumn}], '12.00')")).Count);
        Assert.True(probe != null || secured.GetObjects<WorkflowNotification>(
            CriteriaOperator.Parse($"Contains([{WorkflowNotifications.SnapshotColumn}], '12.00')")).Count == 0);
        Assert.NotNull(secured.GetObjectByKey<WorkflowNotification>(id));   // the row itself is readable
    }

    [Fact] public void An_unknown_subject_type_masks_every_placeholder()
    {
        var id = Notified();
        using var provider = LogOn("UserReaders", out var security);
        using var secured = provider.CreateObjectSpace();
        using var os = db.NewObjectSpace();
        var note = os.GetObjectByKey<WorkflowNotification>(id);
        note.Instance!.SubjectType = "No.Such.Type";   // not saved: a type the application no longer has
        Assert.Equal($"T={M} A={M} B={M} U={M}", WorkflowNotifications.Render(note, secured, security));
    }

    [Fact] public void A_row_from_before_templates_is_sent_as_stored()
    {
        var id = Notified();
        using var os = db.NewObjectSpace();
        var note = os.GetObjectByKey<WorkflowNotification>(id);
        note.IsTemplate = false;   // not saved: as a 0.3 row reads ({Title} was then filled text the subject held)
        Assert.Equal(Template, WorkflowNotifications.Render(note, os, null));
    }

    [Theory, InlineData("OtherTitles"), InlineData("Nothing")]
    public void A_subject_the_recipient_cannot_read_masks_every_placeholder(string user) =>
        Assert.Equal($"T={M} A={M} B={M} U={M}", RenderFor(Notified(), user));
}
