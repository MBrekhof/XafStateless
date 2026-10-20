using DevExpress.Data.Filtering;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Security;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using XafStateless.Demo.Module.BusinessObjects;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Services;

namespace XafStateless.Module.Tests;

/// <summary>XSM-021: the Grant methods are safe to call on every updater run. They add what is missing and never change an
/// explicit state that is already there.</summary>
public class WorkflowPermissionsTests(TestDb db) : IClassFixture<TestDb>
{
    static (Type, string) Subject(Type t) => (t, t.Name + "_ListView");

    /// <summary>Creates role <paramref name="name"/> and applies <paramref name="setup"/> in one commit; returns the role id.</summary>
    Guid Role(string name, Action<PermissionPolicyRole> setup)
    {
        using var os = db.NewObjectSpace();
        var r = os.CreateObject<PermissionPolicyRole>();
        r.Name = name;
        setup(r);
        os.CommitChanges();
        return r.ID;
    }

    /// <summary>Loads the role in a fresh object space, applies <paramref name="grant"/>, commits.</summary>
    void Regrant(Guid id, Action<PermissionPolicyRole> grant)
    {
        using var os = db.NewObjectSpace();
        grant(os.GetObjectByKey<PermissionPolicyRole>(id));
        os.CommitChanges();
    }

    /// <summary>Every permission row of the role, as comparable text.</summary>
    List<string> Rows(Guid id)
    {
        using var os = db.NewObjectSpace();
        var r = os.GetObjectByKey<PermissionPolicyRole>(id);
        var rows = new List<string>();
        foreach (var t in r.TypePermissions)
        {
            rows.Add($"T {t.TargetType.Name} r{t.ReadState} w{t.WriteState} c{t.CreateState} d{t.DeleteState} n{t.NavigateState}");
            rows.AddRange(t.ObjectPermissions.Select(o => $"O {t.TargetType.Name} {o.Criteria} r{o.ReadState} w{o.WriteState} d{o.DeleteState}"));
            rows.AddRange(t.MemberPermissions.Select(m => $"M {t.TargetType.Name} {m.Members} {m.Criteria} r{m.ReadState} w{m.WriteState}"));
        }
        rows.AddRange(r.NavigationPermissions.Select(n => $"N {n.ItemPath} {n.NavigateState}"));
        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    [Fact] public void Granting_twice_adds_nothing_the_second_time()
    {
        void Both(PermissionPolicyRole r)
        {
            WorkflowPermissions.GrantDesigner(r);
            WorkflowPermissions.GrantParticipant(r, Subject(typeof(PurchaseRequest)));
        }
        var id = Role($"twice-{Guid.NewGuid():N}", Both);
        var first = Rows(id);
        Regrant(id, Both);
        Assert.Equal(first, Rows(id));
    }

    [Fact] public void An_existing_role_without_grants_gets_them()
    {
        var name = $"existing-{Guid.NewGuid():N}";
        var id = Role(name, _ => { });   // the host already had a role of this name
        Regrant(id, r => WorkflowPermissions.GrantParticipant(r, Subject(typeof(PurchaseRequest))));
        var expected = Role($"fresh-{Guid.NewGuid():N}", r => { r.Name = name; WorkflowPermissions.GrantParticipant(r, Subject(typeof(PurchaseRequest))); });
        Assert.Equal(Rows(expected), Rows(id));
    }

    [Fact] public void Adding_a_subject_later_adds_only_its_rows()
    {
        var id = Role($"grow-{Guid.NewGuid():N}", r => WorkflowPermissions.GrantParticipant(r, Subject(typeof(PurchaseRequest))));
        var before = Rows(id);
        Regrant(id, r => WorkflowPermissions.GrantParticipant(r, Subject(typeof(PurchaseRequest)), Subject(typeof(ApplicationUser))));
        var added = Rows(id).Except(before).ToList();
        Assert.All(before, row => Assert.Contains(row, Rows(id)));   // nothing changed or removed
        Assert.Contains(added, row => row.StartsWith("T ApplicationUser", StringComparison.Ordinal));
        Assert.Contains(added, row => row.StartsWith("O WorkflowInstance", StringComparison.Ordinal) && row.Contains(typeof(ApplicationUser).FullName!));
        Assert.DoesNotContain(added, row => row.Contains(nameof(PurchaseRequest)));
    }

    [Fact] public void Explicit_states_an_admin_set_are_kept()
    {
        var name = $"custom-{Guid.NewGuid():N}";
        var id = Role(name, r =>
        {
            r.AddTypePermission<WorkflowDefinition>(SecurityOperations.Read, SecurityPermissionState.Deny);      // admin: no definitions
            r.AddNavigationPermission(WorkflowPermissions.NavigationGroup + "WorkflowTask_ListView", SecurityPermissionState.Deny);
            r.AddMemberPermission<PurchaseRequest>(SecurityOperations.Write, nameof(IWorkflowSubject.Workflow), null, SecurityPermissionState.Allow);
        });
        Regrant(id, r => WorkflowPermissions.GrantParticipant(r, Subject(typeof(PurchaseRequest))));
        var rows = Rows(id);
        Assert.Contains(rows, row => row.StartsWith("T WorkflowDefinition rDeny", StringComparison.Ordinal));
        Assert.Contains($"N {WorkflowPermissions.NavigationGroup}WorkflowTask_ListView Deny", rows);
        Assert.Single(rows, row => row.StartsWith($"M PurchaseRequest {nameof(IWorkflowSubject.Workflow)}", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.StartsWith($"M PurchaseRequest {nameof(IWorkflowSubject.Workflow)}", StringComparison.Ordinal) && row.EndsWith("wAllow", StringComparison.Ordinal));
    }

    [Fact] public void An_existing_object_permission_keeps_its_explicit_state()
    {
        var name = $"objdeny-{Guid.NewGuid():N}";
        var ownTasks = new BinaryOperator(nameof(WorkflowTask.Role), name).ToString();
        var id = Role(name, r => r.AddObjectPermission<WorkflowTask>(SecurityOperations.Read, ownTasks, SecurityPermissionState.Deny));
        Regrant(id, r => WorkflowPermissions.GrantParticipant(r, Subject(typeof(PurchaseRequest))));
        Assert.Single(Rows(id), row => row.StartsWith($"O WorkflowTask {ownTasks} ", StringComparison.Ordinal));
        Assert.Contains(Rows(id), row => row.StartsWith($"O WorkflowTask {ownTasks} rDeny", StringComparison.Ordinal));
    }

    [Fact] public void A_legacy_combined_instance_row_stays_and_the_second_regrant_adds_nothing()
    {
        var legacy = new InOperator(nameof(WorkflowInstance.SubjectType), [typeof(PurchaseRequest).FullName!]).ToString();
        var id = Role($"legacy-{Guid.NewGuid():N}", r =>
        {
            WorkflowPermissions.GrantParticipant(r, Subject(typeof(PurchaseRequest)));
            // Before the commit DevExpress lists a new object permission twice (same instance): hence Distinct.
            var inst = r.TypePermissions.Single(t => t.TargetType == typeof(WorkflowInstance)).ObjectPermissions.Distinct().Single();
            inst.Criteria = legacy;   // what roles seeded before XSM-021 carry
        });
        Regrant(id, r => WorkflowPermissions.GrantParticipant(r, Subject(typeof(PurchaseRequest))));
        var afterFirst = Rows(id);
        Assert.Contains(afterFirst, row => row.StartsWith($"O WorkflowInstance {legacy} ", StringComparison.Ordinal));
        Assert.Equal(2, afterFirst.Count(row => row.StartsWith("O WorkflowInstance", StringComparison.Ordinal)));
        Regrant(id, r => WorkflowPermissions.GrantParticipant(r, Subject(typeof(PurchaseRequest))));
        Assert.Equal(afterFirst, Rows(id));
    }

    [Fact] public void Designer_and_participant_roles_cover_rule_set_entities()
    {
        var designerId = Role($"des-{Guid.NewGuid():N}", WorkflowPermissions.GrantDesigner);
        var designerRows = Rows(designerId);
        foreach (var t in new[] { nameof(WorkflowRuleSet), nameof(WorkflowRuleParam), nameof(WorkflowRule) })
            Assert.Contains($"T {t} rAllow wAllow cAllow dAllow n", designerRows);

        var partId = Role($"part-{Guid.NewGuid():N}", r => WorkflowPermissions.GrantParticipant(r, Subject(typeof(PurchaseRequest))));
        var partRows = Rows(partId);
        foreach (var t in new[] { nameof(WorkflowRuleSet), nameof(WorkflowRuleParam), nameof(WorkflowRule) })
            Assert.Contains($"T {t} rAllow w c d n", partRows);
    }
}
