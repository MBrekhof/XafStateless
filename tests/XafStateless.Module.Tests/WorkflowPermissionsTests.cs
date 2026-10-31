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

    // ---- CARD-2144: participant rows scoped by a criteria on the subject ----

    static readonly string Pr = typeof(PurchaseRequest).FullName!, Au = typeof(ApplicationUser).FullName!;
    const string Scope = "[Amount] > 100", Scope2 = "[Amount] > 200";
    const string Requester = "[UserId] = CurrentUserId()";

    static ParticipantSubject Scoped(Type t, string? scope) => new(t, t.Name + "_ListView", scope);

    static string RoleRow(string role, string type, string? scope = null) =>
        $"[Role] = '{role}' And [Instance.SubjectType] = '{type}'" + (scope == null ? "" : $" And [<{type}>][[ID] = [^.Instance.SubjectId] And {scope}]");
    static string RequesterRow(string type, string? scope = null) =>
        $"{Requester} And [Instance.SubjectType] = '{type}'" + (scope == null ? "" : $" And [<{type}>][[ID] = [^.Instance.SubjectId] And {scope}]");
    /// <summary>A task row for the logged-on user's own tasks (CARD-2615): the requester row's text, on WorkflowTask.</summary>
    static string AssigneeRow(string type, string? scope = null) => RequesterRow(type, scope);
    static IEnumerable<string> Sorted(params string[] criteria) => criteria.Select(Allowed).Order(StringComparer.Ordinal);
    static string InstanceRow(string type, string? scope = null) =>
        $"[SubjectType] = '{type}'" + (scope == null ? "" : $" And [<{type}>][[ID] = [^.SubjectId] And {scope}]");

    /// <summary>The object permission rows of <paramref name="target"/>, as "criteria r{Read} w{Write} d{Delete}".</summary>
    List<string> Objects(Guid id, string target) =>
        Rows(id).Where(r => r.StartsWith($"O {target} ", StringComparison.Ordinal)).Select(r => r[($"O {target} ").Length..]).ToList();

    static string Allowed(string criteria) => $"{criteria} rAllow w d";

    [Fact] public void A_scoped_grant_writes_joined_rows_and_no_wider_ones()
    {
        var name = $"sc-{Guid.NewGuid():N}";
        var id = Role(name, r => WorkflowPermissions.GrantParticipant(r, Scoped(typeof(PurchaseRequest), Scope)));
        Assert.Equal(Sorted(RoleRow(name, Pr, Scope), AssigneeRow(Pr, Scope)), Objects(id, nameof(WorkflowTask)));
        Assert.Equal(new[] { Allowed(RequesterRow(Pr, Scope)), Allowed(RoleRow(name, Pr, Scope)) }.Order(StringComparer.Ordinal),
            Objects(id, nameof(WorkflowNotification)));
        Assert.Equal([Allowed(InstanceRow(Pr, Scope))], Objects(id, nameof(WorkflowInstance)));
        var first = Rows(id);
        Regrant(id, r => WorkflowPermissions.GrantParticipant(r, Scoped(typeof(PurchaseRequest), Scope)));
        Assert.Equal(first, Rows(id));
    }

    [Fact] public void An_empty_scope_is_the_unscoped_grant()
    {
        var name = $"empty-{Guid.NewGuid():N}";
        var a = Role(name, r => WorkflowPermissions.GrantParticipant(r, Subject(typeof(PurchaseRequest))));
        var b = Role($"x-{Guid.NewGuid():N}", r => { r.Name = name; WorkflowPermissions.GrantParticipant(r, Scoped(typeof(PurchaseRequest), "  ")); });
        Assert.Equal(Rows(a), Rows(b));
    }

    [Fact] public void Unscoped_then_scoped_removes_the_wider_rows()
    {
        var name = $"narrow-{Guid.NewGuid():N}";
        var id = Role(name, r => WorkflowPermissions.GrantParticipant(r, Subject(typeof(PurchaseRequest))));
        Regrant(id, r => WorkflowPermissions.GrantParticipant(r, Scoped(typeof(PurchaseRequest), Scope)));
        Assert.Equal(Sorted(RoleRow(name, Pr, Scope), AssigneeRow(Pr, Scope)), Objects(id, nameof(WorkflowTask)));
        Assert.Equal(new[] { Allowed(RequesterRow(Pr, Scope)), Allowed(RoleRow(name, Pr, Scope)) }.Order(StringComparer.Ordinal),
            Objects(id, nameof(WorkflowNotification)));
        Assert.Equal([Allowed(InstanceRow(Pr, Scope))], Objects(id, nameof(WorkflowInstance)));
    }

    [Fact] public void Narrowing_one_type_keeps_the_other_types_the_role_wide_rows_served()
    {
        var name = $"served-{Guid.NewGuid():N}";
        var id = Role(name, r => WorkflowPermissions.GrantParticipant(r, Subject(typeof(PurchaseRequest)), Subject(typeof(ApplicationUser))));
        Regrant(id, r => WorkflowPermissions.GrantParticipant(r, Scoped(typeof(PurchaseRequest), Scope)));
        Assert.Equal(Sorted(RoleRow(name, Au), RoleRow(name, Pr, Scope), AssigneeRow(Au), AssigneeRow(Pr, Scope)), Objects(id, nameof(WorkflowTask)));
        Assert.Contains(Allowed(RequesterRow(Au)), Objects(id, nameof(WorkflowNotification)));
        Assert.Contains(Allowed(RoleRow(name, Au)), Objects(id, nameof(WorkflowNotification)));
        Assert.Contains(Allowed(InstanceRow(Au)), Objects(id, nameof(WorkflowInstance)));
    }

    [Fact] public void A_changed_scope_replaces_the_old_joined_rows()
    {
        var name = $"rescope-{Guid.NewGuid():N}";
        var id = Role(name, r => WorkflowPermissions.GrantParticipant(r, Scoped(typeof(PurchaseRequest), Scope)));
        Regrant(id, r => WorkflowPermissions.GrantParticipant(r, Scoped(typeof(PurchaseRequest), Scope2)));
        Assert.DoesNotContain(Rows(id), row => row.Contains(Scope, StringComparison.Ordinal));
        Assert.Equal(Sorted(RoleRow(name, Pr, Scope2), AssigneeRow(Pr, Scope2)), Objects(id, nameof(WorkflowTask)));
        Assert.Equal([Allowed(InstanceRow(Pr, Scope2))], Objects(id, nameof(WorkflowInstance)));
    }

    [Fact] public void A_legacy_combined_instance_row_loses_the_scoped_type()
    {
        foreach (var (types, expected) in new (string[], string?)[] { ([Pr, Au], new InOperator(nameof(WorkflowInstance.SubjectType), [Au]).ToString()), ([Pr], null) })
        {
            var legacy = new InOperator(nameof(WorkflowInstance.SubjectType), types).ToString();
            var id = Role($"legacy-{Guid.NewGuid():N}", r => r.AddObjectPermission<WorkflowInstance>(SecurityOperations.Read, legacy, SecurityPermissionState.Allow));
            Regrant(id, r => WorkflowPermissions.GrantParticipant(r, Scoped(typeof(PurchaseRequest), Scope)));
            var rows = Objects(id, nameof(WorkflowInstance));
            Assert.DoesNotContain(rows, row => row.StartsWith(legacy + " ", StringComparison.Ordinal));
            Assert.Contains(Allowed(InstanceRow(Pr, Scope)), rows);
            Assert.Equal(expected == null ? 1 : 2, rows.Count);
            if (expected != null) Assert.Contains(Allowed(expected), rows);
        }
    }

    [Fact] public void Admin_edits_survive_narrowing_but_an_edited_wider_row_loses_its_read()
    {
        var name = $"admin-{Guid.NewGuid():N}";
        var ownRole = new BinaryOperator(nameof(WorkflowTask.Role), name).ToString();
        var id = Role(name, r =>
        {
            WorkflowPermissions.GrantParticipant(r, Subject(typeof(PurchaseRequest)));
            r.AddObjectPermission<WorkflowInstance>(SecurityOperations.Read, InstanceRow(Pr, Scope), SecurityPermissionState.Deny);
        });
        Regrant(id, r =>
        {
            var notes = r.TypePermissions.Single(t => t.TargetType == typeof(WorkflowNotification)).ObjectPermissions;
            notes.Single(o => o.Criteria == ownRole).ReadState = SecurityPermissionState.Deny;       // admin: Deny on a wider row
            r.TypePermissions.Single(t => t.TargetType == typeof(WorkflowTask)).ObjectPermissions
                .Single(o => o.Criteria == ownRole).WriteState = SecurityPermissionState.Allow;      // admin: another state set
            notes.Single(o => o.Criteria == Requester).NavigateState = SecurityPermissionState.Allow;  // admin: Navigate only
        });
        Regrant(id, r => WorkflowPermissions.GrantParticipant(r, Scoped(typeof(PurchaseRequest), Scope)));
        Assert.Contains($"{ownRole} rDeny w d", Objects(id, nameof(WorkflowNotification)));
        Assert.Contains($"{ownRole} r wAllow d", Objects(id, nameof(WorkflowTask)));
        using (var os = db.NewObjectSpace())
        {
            var requester = os.GetObjectByKey<PermissionPolicyRole>(id).TypePermissions.Single(t => t.TargetType == typeof(WorkflowNotification))
                .ObjectPermissions.Single(o => o.Criteria == Requester);
            Assert.Equal((null, SecurityPermissionState.Allow), (requester.ReadState, requester.NavigateState));
        }
        Assert.Equal([$"{InstanceRow(Pr, Scope)} rDeny w d"], Objects(id, nameof(WorkflowInstance)));
    }

    [Fact] public void A_later_unscoped_call_for_another_type_does_not_bring_the_role_wide_rows_back()
    {
        var name = $"guard-{Guid.NewGuid():N}";
        var id = Role(name, r => WorkflowPermissions.GrantParticipant(r, Scoped(typeof(PurchaseRequest), Scope)));
        Regrant(id, r => WorkflowPermissions.GrantParticipant(r, Subject(typeof(ApplicationUser))));
        var ownRole = new BinaryOperator(nameof(WorkflowTask.Role), name).ToString();
        Assert.Equal(Sorted(RoleRow(name, Au), RoleRow(name, Pr, Scope), AssigneeRow(Au), AssigneeRow(Pr, Scope)), Objects(id, nameof(WorkflowTask)));
        Assert.DoesNotContain(Objects(id, nameof(WorkflowNotification)), row => row.StartsWith(ownRole + " r", StringComparison.Ordinal) || row.StartsWith(Requester + " r", StringComparison.Ordinal));
        Assert.Contains(Allowed(RequesterRow(Au)), Objects(id, nameof(WorkflowNotification)));
    }

    [Fact] public void Unscoped_after_scoped_for_the_same_type_adds_the_plain_rows()
    {
        var name = $"widen-{Guid.NewGuid():N}";
        var id = Role(name, r => WorkflowPermissions.GrantParticipant(r, Scoped(typeof(PurchaseRequest), Scope)));
        Regrant(id, r => WorkflowPermissions.GrantParticipant(r, Subject(typeof(PurchaseRequest))));
        Assert.Equal(Sorted(RoleRow(name, Pr), RoleRow(name, Pr, Scope), AssigneeRow(Pr), AssigneeRow(Pr, Scope)), Objects(id, nameof(WorkflowTask)));
        Assert.Equal(new[] { Allowed(InstanceRow(Pr)), Allowed(InstanceRow(Pr, Scope)) }.Order(StringComparer.Ordinal), Objects(id, nameof(WorkflowInstance)));
    }

    [Fact] public void An_invalid_scope_throws_before_anything_is_written()
    {
        var id = Role($"bad-{Guid.NewGuid():N}", r => WorkflowPermissions.GrantParticipant(r, Subject(typeof(PurchaseRequest))));
        var before = Rows(id);
        foreach (var subjects in new ParticipantSubject[][]
                 {
                     [Scoped(typeof(PurchaseRequest), "[Amount] >")],                                     // unparsable
                     [Scoped(typeof(PurchaseRequest), "[^.Role] = 'x'")],                                 // parent at the top
                     [Scoped(typeof(PurchaseRequest), $"[<{Au}>][[UserName] = ^.Title]")],                // parent inside a join
                     [Scoped(typeof(PurchaseRequest), Scope), Scoped(typeof(PurchaseRequest), Scope2)],  // one type, two scopes
                 })
        {
            Assert.Throws<ArgumentException>(() => Regrant(id, r => WorkflowPermissions.GrantParticipant(r, subjects[0], subjects[1..])));
            Assert.Equal(before, Rows(id));
        }
        Regrant(id, r => WorkflowPermissions.GrantParticipant(r, Scoped(typeof(PurchaseRequest), "[Title] <> '^.x'"),   // a literal, not a path
            Scoped(typeof(PurchaseRequest), "[Title]  <>  '^.x'")));                                                       // the same scope twice
    }

    [Fact] public void A_participant_grant_without_subjects_still_compiles_and_writes_the_role_wide_rows()
    {
        var name = $"none-{Guid.NewGuid():N}";
        var id = Role(name, r => WorkflowPermissions.GrantParticipant(r));
        Assert.Equal(Sorted(new BinaryOperator(nameof(WorkflowTask.Role), name).ToString(), Requester), Objects(id, nameof(WorkflowTask)));
        Assert.Empty(Objects(id, nameof(WorkflowInstance)));
    }

    [Fact] public void The_row_methods_write_only_runtime_rows()
    {
        var name = $"rows-{Guid.NewGuid():N}";
        var id = Role(name, r =>
        {
            WorkflowPermissions.GrantRoleRows(r, (typeof(PurchaseRequest), Scope));
            WorkflowPermissions.GrantRequesterRows(r, (typeof(PurchaseRequest), Scope));
        });
        var rows = Rows(id);
        Assert.DoesNotContain(rows, row => row.StartsWith("N ", StringComparison.Ordinal) || row.StartsWith("M ", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, row => row.StartsWith("T PurchaseRequest", StringComparison.Ordinal) || row.StartsWith("T WorkflowDefinition", StringComparison.Ordinal));
        Assert.Equal([Allowed(RoleRow(name, Pr, Scope))], Objects(id, nameof(WorkflowTask)));
        Assert.Equal([Allowed(InstanceRow(Pr, Scope))], Objects(id, nameof(WorkflowInstance)));
    }

    [Fact] public void A_requester_only_role_narrows_to_the_types_passed()
    {
        var id = Role($"req-{Guid.NewGuid():N}", r => WorkflowPermissions.GrantRequesterRows(r));
        Assert.Equal([Allowed(Requester)], Objects(id, nameof(WorkflowNotification)));
        Regrant(id, r => WorkflowPermissions.GrantRequesterRows(r, (typeof(PurchaseRequest), Scope), (typeof(ApplicationUser), null)));
        Assert.Equal(new[] { Allowed(RequesterRow(Au)), Allowed(RequesterRow(Pr, Scope)) }.Order(StringComparer.Ordinal), Objects(id, nameof(WorkflowNotification)));
        Assert.DoesNotContain(Rows(id), row => row.StartsWith("O WorkflowInstance", StringComparison.Ordinal) || row.StartsWith("O WorkflowTask", StringComparison.Ordinal));
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
