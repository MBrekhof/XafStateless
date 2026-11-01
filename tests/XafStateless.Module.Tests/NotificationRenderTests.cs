using DevExpress.EntityFrameworkCore.Security;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.EFCore.DataLocking;
using DevExpress.ExpressApp.Security;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using Microsoft.EntityFrameworkCore;
using XafStateless.Demo.Module.BusinessObjects;
using XafStateless.Engine;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Services;

namespace XafStateless.Module.Tests;

/// <summary>CARD-2597: a notification keeps its template; <see cref="WorkflowNotifications.Render"/> fills it per recipient
/// from that recipient's secured space, masking what they may not read.</summary>
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

    /// <summary>A secured provider logged on as <paramref name="userName"/>, as PermissionTests builds one.</summary>
    SecuredEFCoreObjectSpaceProvider<DemoEFCoreDbContext> LogOn(string userName, out SecurityStrategyComplex security)
    {
        var auth = new AuthenticationStandard();
        security = new SecurityStrategyComplex(typeof(ApplicationUser), typeof(PermissionPolicyRole), auth);
        var provider = new SecuredEFCoreObjectSpaceProvider<DemoEFCoreDbContext>(security, XafTypesInfo.Instance, db.ConnectionString,
            (builder, cs) => { builder.UseConnectionString(cs); builder.AddInterceptors(new EFCoreOptimisticLockInterceptor()); });
        auth.SetLogonParameters(new AuthenticationStandardLogonParameters(userName, ""));
        var s = security;
        using (var logon = ((INonsecuredObjectSpaceProvider)provider).CreateNonsecuredObjectSpace()) s.Logon(logon);
        return provider;
    }

    /// <summary>A "Desk" request for 12 bought by mail-buyer, started on a workflow whose initial state notifies @Buyer with
    /// <see cref="Template"/>; returns the notification's id.</summary>
    Guid Notified()
    {
        var def = new WorkflowDef($"nr-{Guid.NewGuid():N}", 1, "draft",
            [new("draft", "Draft", StateKind.Manual, OnEntry: [new(EffectKind.Notify, "@Buyer", Template)]), new("done", "Done", StateKind.Final)],
            [new("draft", "Finish", "done")]);
        using var os = db.NewObjectSpace();
        var d = DefinitionMapper.ToDraft(os, def, typeof(PurchaseRequest).FullName!);
        os.CommitChanges();
        var r = new DefinitionService(new DefinitionStore()).Publish(os, d, "tester", WorkflowRoleCatalog.Names(os));
        Assert.True(r.Ok, string.Join("; ", r.Messages));
        var pr = os.CreateObject<PurchaseRequest>();
        (pr.Title, pr.Amount, pr.Buyer) = ("Desk", 12, os.GetObjectByKey<ApplicationUser>(buyer));
        os.CommitChanges();
        var started = rt.Start(new WorkflowCaller("starter", _ => true, db.NewObjectSpace(), db.NewObjectSpace), typeof(PurchaseRequest), pr.ID, def.Name);
        Assert.True(started.Ok, started.Message);
        using var check = db.NewObjectSpace();
        return check.GetObjectsQuery<WorkflowNotification>().Single(n => n.Instance!.SubjectId == pr.ID).ID;
    }

    /// <summary>The note rendered for <paramref name="userName"/>: the note read non-secured (as a dispatcher reads its outbox),
    /// the subject from the user's secured space.</summary>
    string RenderFor(Guid noteId, string userName)
    {
        using var provider = LogOn(userName, out var security);
        using var secured = provider.CreateObjectSpace();
        using var outbox = db.NewObjectSpace();
        return WorkflowNotifications.Render(outbox.GetObjectByKey<WorkflowNotification>(noteId), secured, security);
    }

    string Unsecured(Guid noteId)
    {
        using var os = db.NewObjectSpace();
        return WorkflowNotifications.Render(os.GetObjectByKey<WorkflowNotification>(noteId), os, null);
    }

    [Fact] public void The_notification_keeps_the_template()
    {
        var id = Notified();
        Assert.Equal(Template, db.NewObjectSpace().GetObjectByKey<WorkflowNotification>(id).Body);
    }

    [Fact] public void Without_security_everything_renders()
    {
        var text = Unsecured(Notified());
        Assert.StartsWith("T=Desk A=12", text);
        Assert.EndsWith(" U=mail-buyer", text);
        Assert.DoesNotContain(M, text);
    }

    /// <summary>The Workflow Notification list shows <see cref="WorkflowNotification.Text"/>: rendered in the space that loaded
    /// the row, so a participant's list never shows the template, nor what they may not read.</summary>
    [Fact] public void The_list_text_is_rendered_in_the_viewers_space()
    {
        var id = Notified();
        using (var os = db.NewObjectSpace())
            Assert.Equal(Unsecured(id), os.GetObjectByKey<WorkflowNotification>(id).Text);
        using var provider = LogOn("NoAmount", out _);
        using var secured = provider.CreateObjectSpace();
        var text = secured.GetObjectByKey<WorkflowNotification>(id).Text;
        Assert.StartsWith("T=Desk A=", text);
        Assert.DoesNotContain("12", text);
    }

    [Fact] public void A_recipient_who_may_read_everything_gets_the_unsecured_text()
    {
        var id = Notified();
        Assert.Equal(Unsecured(id), RenderFor(id, "UserReaders"));
    }

    [Fact] public void A_denied_member_and_an_unreadable_reference_type_are_masked()
    {
        var text = RenderFor(Notified(), "NoAmount");
        Assert.Equal($"T=Desk A={M} B={M} U={M}", text);
    }

    /// <summary>{Buyer} shows the user's default member (UserName): a recipient who may read the user but not that member
    /// gets the mask, not the record's text.</summary>
    [Fact] public void A_reference_whose_default_member_is_denied_is_masked()
    {
        var id = Notified();
        Assert.Equal($"T=Desk A=12.00 B={M} U={M}", RenderFor(id, "NoUserName"));
    }

    /// <summary>The secured space returns a reference to an object the criteria hide as null, as the app's views show it to
    /// that user: empty, never the name.</summary>
    [Fact] public void A_reference_the_criteria_do_not_allow_renders_as_the_recipient_sees_it()
    {
        var id = Notified();
        Assert.Equal("T=Desk A=12.00 B= U=", RenderFor(id, "OneUser"));
    }

    [Fact] public void Values_are_read_at_render_time_and_an_empty_hop_renders_empty()
    {
        var id = Notified();
        using (var os = db.NewObjectSpace())
        {
            var note = os.GetObjectByKey<WorkflowNotification>(id);
            var pr = os.GetObjectByKey<PurchaseRequest>(note.Instance!.SubjectId);
            Assert.NotNull(pr.Buyer);   // loaded: clearing an unloaded lazy reference changes nothing
            (pr.Title, pr.Buyer) = ("Chair", null);
            os.CommitChanges();   // the initial state locks nothing
        }
        Assert.Equal("T=Chair A=12.00 B= U=", Unsecured(id));
        Assert.Equal("T=Chair A=12.00 B= U=", RenderFor(id, "UserReaders"));
        Assert.Equal($"T=Chair A={M} B={M} U={M}", RenderFor(id, "NoAmount"));   // a denied member masks even when empty
    }

    [Fact] public void An_unknown_subject_type_masks_every_placeholder()
    {
        var id = Notified();
        using var os = db.NewObjectSpace();
        var note = os.GetObjectByKey<WorkflowNotification>(id);
        note.Instance!.SubjectType = "No.Such.Type";   // not saved: a type the application no longer has
        Assert.Equal($"T={M} A={M} B={M} U={M}", WorkflowNotifications.Render(note, os, null));
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
