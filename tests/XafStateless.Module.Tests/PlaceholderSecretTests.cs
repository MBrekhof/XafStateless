using System.Text.Json;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.EFCore;
using Microsoft.EntityFrameworkCore;
using XafStateless.Demo.Module.BusinessObjects;
using XafStateless.Engine;
using XafStateless.Module.Services;

namespace XafStateless.Module.Tests;

/// <summary>CARD-2909: a Notify or Run action placeholder may not read a hidden ([Browsable(false)]) member or a user's
/// secret (<see cref="WorkflowMembers.UserSecrets"/>), the rule a Set field expression follows (CARD-2895). Refused at
/// publish; a definition published before renders them empty and stores nothing of them.</summary>
public class PlaceholderSecretTests(TestDb db) : IClassFixture<TestDb>
{
    static XafWorkflowEnvironment Env() =>
        new([], XafTypesInfo.Instance.FindTypeInfo(typeof(PurchaseRequest)), ["log"]);

    static WorkflowNotifications.Entry Entry(string json, string name) =>
        JsonSerializer.Deserialize<Dictionary<string, WorkflowNotifications.Entry>>(json)![name];

    [Theory]
    [InlineData("{Buyer.StoredPassword}", "Buyer.StoredPassword", "StoredPassword"), InlineData("x {Buyer.Roles.Count} y", "Buyer.Roles.Count", "Roles"),
     InlineData("{Buyer.AccessFailedCount}", "Buyer.AccessFailedCount", "AccessFailedCount"), InlineData("{Buyer.LockoutEnd}", "Buyer.LockoutEnd", "LockoutEnd"),
     InlineData("{Buyer.ChangePasswordOnFirstLogon}", "Buyer.ChangePasswordOnFirstLogon", "ChangePasswordOnFirstLogon"),
     InlineData("{Buyer.UserLogins.Count}", "Buyer.UserLogins.Count", "UserLogins")]
    public void A_user_secret_is_refused_in_a_notify_and_a_run_action(string template, string placeholder, string secret)
    {
        foreach (var effect in new EffectDef[] { new(EffectKind.Notify, "@Buyer", template), new(EffectKind.RunAction, "log", template) })
            Assert.StartsWith($"placeholder '{{{placeholder}}}': '{secret}' of ApplicationUser cannot be read", Env().CheckEffect(effect));
    }

    [Theory, InlineData("{Buyer} {Buyer.UserName} {Title} {Amount} {Summary}"), InlineData("{Workflow.StateCaption}")]
    public void Other_placeholders_still_pass(string template)
    {
        Assert.Null(Env().CheckEffect(new(EffectKind.Notify, "@Buyer", template)));
        Assert.Null(Env().CheckEffect(new(EffectKind.RunAction, "log", template)));
    }

    [Fact] public void A_hidden_member_of_the_subject_is_refused() =>
        Assert.StartsWith("placeholder '{SortKey}': 'SortKey' of PurchaseRequest cannot be read", Env().CheckEffect(new(EffectKind.Notify, "@Buyer", "{SortKey}")));

    [Fact] public void An_unknown_member_is_still_named_as_such() =>
        Assert.Contains("is not a member of", Env().CheckEffect(new(EffectKind.Notify, "@Buyer", "{Buyer.Nope}")));

    /// <summary>A template published before this check: the runtime fills a secret as empty (a Run action's argument) and
    /// stores nothing of it in the snapshot (Live: judged again, and found empty again, at render time).</summary>
    [Fact] public void At_runtime_a_secret_fills_empty_and_is_not_stored()
    {
        using var os = db.NewObjectSpace();
        var user = os.CreateObject<ApplicationUser>();
        (user.UserName, user.IsActive) = ($"sec-{Guid.NewGuid():N}", true);
        user.SetPassword("hunter2");
        var pr = os.CreateObject<PurchaseRequest>();
        (pr.Title, pr.Buyer) = ("Desk", user);
        os.CommitChanges();
        Assert.False(string.IsNullOrEmpty(user.StoredPassword));

        var type = os.TypesInfo.FindTypeInfo(typeof(PurchaseRequest));
        Assert.Equal($"[] [] [{user.UserName}]", WorkflowNotifications.Fill(os, type, pr, "[{Buyer.StoredPassword}] [{Buyer.Roles.Count}] [{Buyer.UserName}]"));
        var json = WorkflowNotifications.Snapshot(os, type, pr, "{Buyer.StoredPassword}");
        Assert.DoesNotContain(user.StoredPassword, json);
        var entry = Entry(json, "Buyer.StoredPassword");
        Assert.Equal((true, "", 0), (entry.Live, entry.Text, entry.Reads.Count));
    }

    /// <summary>On the test-only types: a hidden member is refused and fills empty; a user's secret declared on a base class
    /// that is no user (XAF names that base as the owner) is caught on the user type it is read on, in a path and as a
    /// default member.</summary>
    [Fact] public void A_hidden_member_and_a_secret_declared_on_a_non_user_base()
    {
        using var provider = new EFCoreObjectSpaceProvider<TestSubjectsDbContext>((b, _) => b.UseConnectionString(db.ConnectionString));
        using var os = provider.CreateObjectSpace();
        var agent = os.CreateObject<Agent>();
        (agent.UserName, agent.StoredPassword) = ("a", "hash-123");
        var desk = os.CreateObject<Desk>();
        (desk.Code, desk.ApiKey) = ("D1", "key-456");
        var s = os.CreateObject<OtherSubject>();
        (s.Name, s.Agent, s.Desk) = ("S", agent, desk);
        os.CommitChanges();
        var type = os.TypesInfo.FindTypeInfo(typeof(OtherSubject));
        var env = new XafWorkflowEnvironment([], type);

        Assert.StartsWith("placeholder '{Desk.ApiKey}': 'ApiKey' of Desk cannot be read", env.CheckEffect(new(EffectKind.Notify, "Approvers", "{Desk.ApiKey}")));
        Assert.StartsWith("placeholder '{Agent.StoredPassword}': 'StoredPassword' of Agent cannot be read",
            env.CheckEffect(new(EffectKind.Notify, "Approvers", "{Agent.StoredPassword}")));
        Assert.Null(env.CheckEffect(new(EffectKind.Notify, "Approvers", "{Agent} {Agent.UserName} {Desk.Code}")));   // no secret on the path

        Assert.Equal("[] [] [] [a] [D1]", WorkflowNotifications.Fill(os, type, s, "[{Desk.ApiKey}] [{Agent.StoredPassword}] [{Agent}] [{Agent.UserName}] [{Desk.Code}]"));
        var vault = os.CreateObject<Vault>();
        vault.Secret = "vault-789";
        var empty = os.CreateObject<OtherSubject>();   // no Agent: the path is refused before anything is read
        (empty.Name, s.Vault) = ("E", vault);
        os.CommitChanges();
        // XAF does not take a hidden member as the default member (it falls back to the key): {Vault} shows the ID, never the secret
        Assert.Equal($"[{vault.ID}]", WorkflowNotifications.Fill(os, type, s, "[{Vault}]"));
        Assert.DoesNotContain("vault-789", WorkflowNotifications.Snapshot(os, type, s, "{Vault}"));
        foreach (var (subject, name) in new[] { (s, "Desk.ApiKey"), (s, "Agent.StoredPassword"), (s, "Agent"), (empty, "Agent.StoredPassword") })
        {
            var json = WorkflowNotifications.Snapshot(os, type, subject, $"{{{name}}}");
            Assert.DoesNotContain("hash-123", json);
            Assert.DoesNotContain("key-456", json);
            Assert.DoesNotContain("vault-789", json);
            var entry = Entry(json, name);
            Assert.Equal((true, "", 0), (entry.Live, entry.Text, entry.Reads.Count));
        }
    }
}
