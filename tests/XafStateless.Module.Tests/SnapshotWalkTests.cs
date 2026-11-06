using System.Text.Json;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.EFCore;
using Microsoft.EntityFrameworkCore;
using XafStateless.Demo.Module.BusinessObjects;
using XafStateless.Module.Services;

namespace XafStateless.Module.Tests;

/// <summary>CARD-2811: which placeholders the notification snapshot judges at notify time and which it leaves Live (filled
/// and checked at render time, as 0.4 did), for references whose default member is calculated, a reference, or missing.
/// Rendering a Live entry is covered by <c>NotificationRenderTests.A_calculated_member_is_filled_at_render_time</c>.</summary>
public class SnapshotWalkTests(TestDb db) : IClassFixture<TestDb>
{
    [Fact] public void A_default_member_that_is_calculated_or_missing_is_live_a_reference_chain_is_judged()
    {
        using var provider = new EFCoreObjectSpaceProvider<TestSubjectsDbContext>((b, _) => b.UseConnectionString(db.ConnectionString));
        Guid subjectId, userId, deskId;
        using (var os = provider.CreateObjectSpace())
        {
            var user = os.CreateObject<ApplicationUser>();
            (user.UserName, user.IsActive) = ($"owner-{Guid.NewGuid():N}", true);
            var contact = os.CreateObject<Contact>();
            (contact.First, contact.Last) = ("Ada", "Lovelace");
            var desk = os.CreateObject<Desk>();
            (desk.Code, desk.Owner) = ("D1", user);
            var bare = os.CreateObject<Bare>();
            bare.Number = 7;
            var node = os.CreateObject<Node>();
            node.Next = node;
            var s = os.CreateObject<OtherSubject>();
            (s.Name, s.Contact, s.Desk, s.Bare, s.Node) = ("S", contact, desk, bare, node);
            os.CommitChanges();
            (subjectId, userId, deskId) = (s.ID, user.ID, desk.ID);
        }

        using var space = provider.CreateObjectSpace();
        var type = space.TypesInfo.FindTypeInfo(typeof(OtherSubject));
        Assert.True(space.TypesInfo.FindTypeInfo(typeof(Contact)).IsPersistent);   // the walk only judges persistent records
        var json = WorkflowNotifications.Snapshot(space, type, space.GetObjectByKey<OtherSubject>(subjectId), "{Name} {Contact} {Desk} {Bare} {Node} {Desk.Code}");
        var entries = JsonSerializer.Deserialize<Dictionary<string, WorkflowNotifications.Entry>>(json)!;

        Assert.True(entries["Contact"].Live);   // FullName's getter reads First and Last, which no check names
        Assert.True(entries["Node"].Live);      // the default member leads back to the same record: nothing to show or judge
        var subject = space.GetObjectByKey<OtherSubject>(subjectId);
        Assert.Equal("Ada Lovelace/", WorkflowNotifications.Fill(space, type, subject, "{Contact}/{Node}"));   // a Live text; no stack overflow
        Assert.False(entries["Name"].Live);
        Assert.False(entries["Desk.Code"].Live);
        Assert.False(entries["Bare"].Live);     // XAF's fallback default member is the key: judged as a read of ID
        Assert.Equal("ID", entries["Bare"].Reads[^1].Member);
        Assert.Equal(subject.Bare!.ID.ToString(), entries["Bare"].Text);

        // A type without any default member (a host that cleared it): Live, and shown as its ToString. Only this test uses Bare.
        var bareType = (DevExpress.ExpressApp.DC.TypeInfo)space.TypesInfo.FindTypeInfo(typeof(Bare));
        var fallback = bareType.DefaultMember;
        try
        {
            bareType.DefaultMember = null;
            var none = JsonSerializer.Deserialize<Dictionary<string, WorkflowNotifications.Entry>>(WorkflowNotifications.Snapshot(space, type, subject, "{Bare}"))!;
            Assert.True(none["Bare"].Live);
            Assert.Equal(subject.Bare!.ToString(), WorkflowNotifications.Fill(space, type, subject, "{Bare}"));
        }
        finally { bareType.DefaultMember = fallback; }

        var desk2 = entries["Desk"];   // Desk shows Owner, a reference that shows UserName: every record on the way is read
        Assert.False(desk2.Live);
        Assert.StartsWith("owner-", desk2.Text);
        Assert.Equal(
            [(typeof(OtherSubject).FullName!, subjectId.ToString(), "Desk"), (typeof(Desk).FullName!, deskId.ToString(), "Owner"),
             (typeof(ApplicationUser).FullName!, userId.ToString(), "UserName")],
            desk2.Reads.Select(r => (r.Type, r.Key, r.Member)));
    }
}
