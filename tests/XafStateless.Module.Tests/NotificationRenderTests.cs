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
            foreach (var name in new[] { "mail-buyer", "someone-else" })
            {
                var b = os.CreateObject<ApplicationUser>();
                (b.UserName, b.IsActive) = (name, true);
            }
            os.CommitChanges();
        }
        buyer = os.FirstOrDefault<ApplicationUser>(u => u.UserName == "mail-buyer")!.ID;
        someoneElse = os.FirstOrDefault<ApplicationUser>(u => u.UserName == "someone-else")!.ID;
    }

    readonly Guid someoneElse;   // the one user OneUser may read

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

    /// <summary>Sets the snapshot column outside EF (the guard refuses it through EF): a 0.4.x row (null), or a damaged one.</summary>
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

    [Theory, InlineData("{"), InlineData(""), InlineData("[]"), InlineData("{}"),   // {}: no entry for any placeholder
     InlineData("""{"Title":null}"""), InlineData("""{"Title":{}}"""), InlineData("""{"Title":{"Text":"x","Reads":[null]}}"""),
     InlineData("""{"Title":{"Text":"x","Reads":[{"Type":"XafStateless.Demo.Module.BusinessObjects.PurchaseRequest","Key":null}]}}"""),
     // one damaged entry masks the sound ones too
     InlineData("""{"Title":{"Text":"x","Reads":[{"Type":null,"Key":"1"}]},"Amount":{"Text":"12.00","Reads":[]},"Buyer":{"Text":"b","Reads":[]},"Buyer.UserName":{"Text":"b","Reads":[]}}""")]
    public void A_damaged_snapshot_masks_every_placeholder(string json)
    {
        var id = Notified();
        SetSnapshot(id, json);
        Assert.Equal($"T={M} A={M} B={M} U={M}", Unsecured(id));
        Assert.Equal($"T={M} A={M} B={M} U={M}", RenderFor(id, "UserReaders"));
    }

    /// <summary>The snapshot holds unmasked values: a shadow column, no XAF member, so no view, criteria, permission or Web API
    /// reaches it through a participant's secured space (Middle Tier would; integrating.md says so).</summary>
    [Fact] public void The_snapshot_is_no_member_a_participant_can_query()
    {
        var id = Notified();
        Assert.Null(XafTypesInfo.Instance.FindTypeInfo(typeof(WorkflowNotification)).FindMember(WorkflowNotifications.SnapshotColumn));
        using var provider = LogOn("NoAmount", out _);
        using var secured = provider.CreateObjectSpace();
        Assert.NotNull(Record.Exception(() => secured.GetObjects<WorkflowNotification>(
            CriteriaOperator.Parse($"Contains([{WorkflowNotifications.SnapshotColumn}], '12.00')")).Count));
        Assert.NotNull(secured.GetObjectByKey<WorkflowNotification>(id));   // the row itself is readable
    }

    /// <summary>The check runs on the record the notice named, not the subject's current reference.</summary>
    [Theory, InlineData(false), InlineData(true)]
    public void A_hidden_reference_reassigned_to_a_readable_one_stays_masked(bool prefetch)
    {
        var id = Notified();   // mail-buyer: hidden from OneUser
        Change(id, pr => pr.Buyer = ((IObjectSpaceLink)pr).ObjectSpace.GetObjectByKey<ApplicationUser>(someoneElse));
        Assert.Equal($"T=Desk A=12.00 B={M} U={M}", RenderFor(id, "OneUser", prefetch));
    }

    /// <summary>A readable reference reassigned to a hidden one: shown without prefetch; with prefetch XAF judges the
    /// subject's member on its current (hidden) target and masks. Fails closed either way; documented in integrating.md.</summary>
    [Theory, InlineData(false, "someone-else"), InlineData(true, M)]
    public void A_readable_reference_reassigned_to_a_hidden_one(bool prefetch, string shown)
    {
        var id = Notified(buyerId: someoneElse);
        Change(id, pr => pr.Buyer = ((IObjectSpaceLink)pr).ObjectSpace.GetObjectByKey<ApplicationUser>(buyer));
        Assert.Equal($"T=Desk A=12.00 B={shown} U={shown}", RenderFor(id, "OneUser", prefetch));
    }

    /// <summary>A calculated member's getter reads members no permission check names, so it is not snapshotted: it is filled
    /// at render time from what the viewer's space holds (its denied members already blanked).</summary>
    [Fact] public void A_calculated_member_is_filled_at_render_time()
    {
        var id = Notified("S={Summary} T={Title}");
        Change(id, pr => pr.Title = "Chair");
        Assert.Equal("S=Chair (12.00) T=Desk", Unsecured(id));
        Assert.Equal("S=Chair (12.00) T=Desk", RenderFor(id, "UserReaders"));
        Assert.Equal("S=Chair (0) T=Desk", RenderFor(id, "NoAmount"));
    }

    /// <summary>A dispatcher holding a row another process deleted meanwhile: no snapshot row to read, so nothing renders
    /// (not the subject's values now).</summary>
    [Fact] public void A_note_deleted_after_it_was_loaded_masks_every_placeholder()
    {
        var id = Notified();
        using var outbox = db.NewObjectSpace();
        var note = outbox.GetObjectByKey<WorkflowNotification>(id);
        using (var os = db.NewObjectSpace())
        {
            os.Delete(os.GetObjectByKey<WorkflowNotification>(id));
            os.CommitChanges();
        }
        Assert.Equal($"T={M} A={M} B={M} U={M}", WorkflowNotifications.Render(note, outbox, null));
    }

    [Fact] public void A_space_that_is_not_an_EF_Core_space_masks_every_placeholder()
    {
        var id = Notified();
        using var outbox = db.NewObjectSpace();
        using var other = new NonPersistentObjectSpace(XafTypesInfo.Instance);
        other.AdditionalObjectSpaces.Add(outbox);   // shaped like an entry screen's composite space
        Assert.Equal($"T={M} A={M} B={M} U={M}", WorkflowNotifications.Render(outbox.GetObjectByKey<WorkflowNotification>(id), other, null));
    }

    /// <summary>A strategy with a non-secured space (a dispatcher passing its outbox space by mistake): that space does not
    /// blank what the strategy denies, so nothing renders.</summary>
    [Fact] public void A_strategy_with_a_non_secured_space_masks_every_placeholder()
    {
        var id = Notified();
        using var provider = LogOn("UserReaders", out var security);
        using var outbox = db.NewObjectSpace();
        Assert.Equal($"T={M} A={M} B={M} U={M}", WorkflowNotifications.Render(outbox.GetObjectByKey<WorkflowNotification>(id), outbox, security));
    }

    /// <summary>A count over a collection includes rows the recipient's criteria hide: not snapshotted, counted at render time.</summary>
    [Fact] public void A_collection_count_is_filled_at_render_time()
    {
        Guid owner;
        using (var os = db.NewObjectSpace())
        {
            var u = os.CreateObject<ApplicationUser>();
            (u.UserName, u.IsActive) = ($"roles-{Guid.NewGuid():N}", true);
            os.CommitChanges();
            owner = u.ID;
        }
        var id = Notified("R={Buyer.Roles.Count}", buyerId: owner);
        Assert.Equal("R=0", Unsecured(id));
        using (var os = db.NewObjectSpace())
        {
            os.GetObjectByKey<ApplicationUser>(owner).Roles.Add(os.FirstOrDefault<PermissionPolicyRole>(r => r.Name == "UserReaders")!);
            os.CommitChanges();
        }
        Assert.Equal("R=1", Unsecured(id));
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

    /// <summary>CARD-2871: a user whose role reads the subject and has only the requester rows (no role rows) reads the
    /// instance of a notification addressed to them, so the in-app list renders it; not the instance of another user's.
    /// With a scope, only the instances of subjects in the scope.</summary>
    [Theory, InlineData(null, false), InlineData(null, true), InlineData("[Title] = 'Desk'", false), InlineData("[Title] = 'Desk'", true)]
    public void A_user_addressed_notification_renders_for_a_requester_rows_role(string? scope, bool prefetch)
    {
        var name = $"req-{Guid.NewGuid():N}";
        using (var os = db.NewObjectSpace())
            Role(os, name, r => WorkflowPermissions.GrantRequesterRows(r, (typeof(PurchaseRequest), scope)));
        Guid me;
        using (var os = db.NewObjectSpace()) me = os.FirstOrDefault<ApplicationUser>(u => u.UserName == name)!.ID;
        var (mine, theirs) = (Notified(buyerId: me), Notified());
        var outOfScope = Notified(buyerId: me);
        Change(outOfScope, pr => pr.Title = "Lamp");   // the subject leaves the scope (unscoped: still readable)

        using var provider = LogOn(name, out var security, prefetch);
        using var secured = provider.CreateObjectSpace();
        var listed = secured.GetObjects<WorkflowNotification>().ToDictionary(x => x.ID);   // the list's query
        Assert.NotNull(listed[mine].Instance);
        Assert.DoesNotContain(theirs, listed.Keys);
        var note = secured.GetObjectByKey<WorkflowNotification>(mine);
        Assert.NotNull(note.Instance);
        Assert.Equal($"T=Desk A=12.00 B={M} U={M}", WorkflowNotifications.Render(note, secured, security));   // no read on users
        Assert.Null(secured.GetObjectByKey<WorkflowNotification>(theirs));
        using var os2 = db.NewObjectSpace();
        Assert.Null(secured.GetObjectByKey<WorkflowInstance>(os2.GetObjectByKey<WorkflowNotification>(theirs).Instance!.ID));
        var outside = secured.GetObjectByKey<WorkflowInstance>(os2.GetObjectByKey<WorkflowNotification>(outOfScope).Instance!.ID);
        Assert.Equal(scope == null, outside != null);
    }

    /// <summary>CARD-2871: the assignee rows read the instance of a task assigned to the user (<c>@Buyer</c>), not another's.</summary>
    [Theory, InlineData(null, false), InlineData(null, true), InlineData("[Title] = 'Desk'", false), InlineData("[Title] = 'Desk'", true)]
    public void A_user_assigned_task_shows_its_instance_for_an_assignee_rows_role(string? scope, bool prefetch)
    {
        var name = $"asg-{Guid.NewGuid():N}";
        using (var os = db.NewObjectSpace())
            Role(os, name, r => WorkflowPermissions.GrantAssigneeRows(r, (typeof(PurchaseRequest), scope)));
        var def = new WorkflowDef($"nt-{Guid.NewGuid():N}", 1, "review",
            [new("review", "Review", StateKind.Task, TaskRole: "@Buyer"), new("done", "Done", StateKind.Final)],
            [new("review", "Approve", "done")]);
        using (var os = db.NewObjectSpace())
        {
            var d = DefinitionMapper.ToDraft(os, def, typeof(PurchaseRequest).FullName!);
            os.CommitChanges();
            var published = new DefinitionService(new DefinitionStore()).Publish(os, d, "tester", WorkflowRoleCatalog.Names(os));
            Assert.True(published.Ok, string.Join("; ", published.Messages));
        }
        Guid Task(string userName, string title = "Desk")
        {
            using var os = db.NewObjectSpace();
            var pr = os.CreateObject<PurchaseRequest>();
            (pr.Title, pr.Buyer) = (title, os.FirstOrDefault<ApplicationUser>(u => u.UserName == userName));
            os.CommitChanges();
            Assert.True(rt.Start(new WorkflowCaller("starter", _ => true, db.NewObjectSpace(), db.NewObjectSpace), typeof(PurchaseRequest), pr.ID, def.Name).Ok);
            using var check = db.NewObjectSpace();
            return check.GetObjectsQuery<WorkflowTask>().Single(t => t.Instance!.SubjectId == pr.ID).ID;
        }
        var (mine, theirs, outOfScope) = (Task(name), Task("mail-buyer"), Task(name, "Lamp"));

        using var provider = LogOn(name, out _, prefetch);
        using var secured = provider.CreateObjectSpace();
        var listed = secured.GetObjects<WorkflowTask>().ToDictionary(x => x.ID);   // the list's query
        Assert.NotNull(listed[mine].Instance);
        Assert.DoesNotContain(theirs, listed.Keys);
        Assert.NotNull(secured.GetObjectByKey<WorkflowTask>(mine).Instance);
        using var os2 = db.NewObjectSpace();
        Assert.Null(secured.GetObjectByKey<WorkflowInstance>(os2.GetObjectByKey<WorkflowTask>(theirs).Instance!.ID));
        var outside = secured.GetObjectByKey<WorkflowInstance>(os2.GetObjectByKey<WorkflowTask>(outOfScope).Instance!.ID);
        Assert.Equal(scope == null, outside != null);   // a Lamp request is outside the scope
    }
}
