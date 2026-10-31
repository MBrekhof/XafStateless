using DevExpress.Data.Filtering;
using DevExpress.Data.Filtering.Exceptions;
using DevExpress.Data.Filtering.Helpers;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Security;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using XafStateless.Module.BusinessObjects;

namespace XafStateless.Module.Services;

/// <summary>A subject of a participant grant: the class, its navigation item, and optionally a criteria on the subject's
/// members that limits which of its instances, tasks and notifications the role reads (null or empty: all of them).</summary>
public sealed record ParticipantSubject(Type SubjectType, string NavigationItemId, string? Scope = null);

/// <summary>Reusable role seeding for hosts: the designer and participant halves of the workflow permission matrix.
/// Roles deny by default; these only add Allow/Deny entries.
/// Both methods are safe to call on every updater run (XSM-021): they add what is missing and fill only operation states that
/// are still unset. An explicit Allow or Deny already on the role (an admin's customization) is never changed, and a row
/// removed from the arguments is not revoked.</summary>
public static class WorkflowPermissions
{
    public const string NavigationGroup = "Application/NavigationItems/Items/Workflow/Items/";

    static readonly Type[] DefinitionTypes = [typeof(WorkflowDefinition), typeof(WorkflowState), typeof(WorkflowTransition), typeof(WorkflowEffect),
        typeof(WorkflowRuleSet), typeof(WorkflowRuleParam), typeof(WorkflowRule)];
    static readonly Type[] RuntimeTypes = [typeof(WorkflowInstance), typeof(WorkflowTask), typeof(WorkflowNotification)];

    /// <summary>CRUD on the definition types; read on instances, tasks and notifications; the Workflow navigation group.
    /// No role read: role names come from <see cref="WorkflowRoleCatalog"/>.</summary>
    public static void GrantDesigner(PermissionPolicyRole r)
    {
        foreach (var t in DefinitionTypes) Type(r, t, SecurityOperations.CRUDAccess);
        foreach (var t in RuntimeTypes) Type(r, t, SecurityOperations.Read);
        foreach (var t in new[] { typeof(WorkflowDefinition), typeof(WorkflowTask), typeof(WorkflowNotification) })
            Navigation(r, NavigationGroup + ListView(t));
    }

    /// <summary>CRUD on each subject type except writing its <c>Workflow</c> link; read on the definition types; navigation to
    /// Tasks, Notifications and each subject. Runtime rows are read-only (the runtime writes them) and scoped by object permissions:
    /// tasks and notifications addressed to this role (<c>[Role] = r.Name</c>), tasks and notifications addressed to the logged-on
    /// user (a requester's, or a user member's: <c>[UserId] = CurrentUserId()</c>), instances of the granted subject types (one
    /// permission per type, so a subject added later adds one row). A user holding several roles sees the union.</summary>
    /// <param name="subjects">A navigation item id (assumed in the Default group) or a full item path (contains '/').</param>
    /// <remarks>Set the role's Name before calling: the task and notification scope captures it.</remarks>
    public static void GrantParticipant(PermissionPolicyRole r, params (Type subjectType, string navigationItemId)[] subjects) =>
        Participant(r, subjects.Select(s => new ParticipantSubject(s.subjectType, s.navigationItemId)).ToArray());

    /// <summary>As the tuple overload, with an optional <see cref="ParticipantSubject.Scope"/> per subject (CARD-2144): the role then
    /// reads only the instances, tasks and notifications whose subject matches the scope. See <see cref="GrantRoleRows"/>.</summary>
    /// <remarks>One subject is required, so a call without subjects still binds to the tuple overload.</remarks>
    public static void GrantParticipant(PermissionPolicyRole r, ParticipantSubject subject, params ParticipantSubject[] more) =>
        Participant(r, [subject, .. more]);

    static void Participant(PermissionPolicyRole r, ParticipantSubject[] subjects)
    {
        RequireName(r);
        var scoped = Validate(subjects.Select(s => (s.SubjectType, s.Scope)));   // throws before anything is written
        foreach (var s in subjects)
        {
            Type(r, s.SubjectType, SecurityOperations.CRUDAccess);
            Member(r, s.SubjectType, nameof(IWorkflowSubject.Workflow), SecurityPermissionState.Deny);
            Navigation(r, s.NavigationItemId.Contains('/') ? s.NavigationItemId : "Application/NavigationItems/Items/Default/Items/" + s.NavigationItemId);
        }
        foreach (var t in DefinitionTypes) Type(r, t, SecurityOperations.Read);
        RoleRows(r, scoped);
        RequesterRows(r, scoped);
        AssigneeRows(r, scoped);
        Navigation(r, NavigationGroup + ListView(typeof(WorkflowTask)));
        Navigation(r, NavigationGroup + ListView(typeof(WorkflowNotification)));
    }

    /// <summary>The runtime rows of a participant role, without the subject, definition and navigation rows: Read on tasks and
    /// notifications addressed to the role, and on the instances of the subject types. For hosts that seed those themselves.</summary>
    /// <remarks>Without a scope these are <c>[Role] = r.Name</c> and <c>[SubjectType] = 'T'</c>. A scope (a criteria on the
    /// subject's members, without <c>^.</c>) makes the rows per type and joined to the subject:
    /// <c>[Role] = 'r' And [Instance.SubjectType] = 'T' And [&lt;T&gt;][[ID] = ^.Instance.SubjectId And (scope)]</c>. A role
    /// that has per-type rows keeps getting them: its role-wide row is removed and the types that row served keep plain
    /// per-type rows. A scoped grant removes the wider rows these methods wrote; an admin-edited one only loses its Read.</remarks>
    public static void GrantRoleRows(PermissionPolicyRole r, params (Type subjectType, string? scope)[] subjects)
    {
        RequireName(r);
        RoleRows(r, Validate(subjects));
    }

    /// <summary>Read on the notifications addressed to the logged-on user as a workflow's requester: role-wide
    /// <c>[UserId] = CurrentUserId()</c>, or per type and joined to the subject as <see cref="GrantRoleRows"/> describes.</summary>
    /// <remarks>Pass every subject type whose requester notifications the role should read (null scope: unscoped). Once the
    /// rows are per type, a type left out is not readable, unless the role's instance rows name it.</remarks>
    public static void GrantRequesterRows(PermissionPolicyRole r, params (Type subjectType, string? scope)[] subjects) =>
        RequesterRows(r, Validate(subjects));

    /// <summary>Read on the tasks assigned to the logged-on user through a user member (<c>@Member</c>, CARD-2615): role-wide
    /// <c>[UserId] = CurrentUserId()</c>, or per type and joined to the subject as <see cref="GrantRoleRows"/> describes. A user's
    /// notifications are read through the requester rows (<see cref="GrantRequesterRows"/>): the same criteria.</summary>
    /// <remarks>As for <see cref="GrantRequesterRows"/>, pass every subject type the role's users may be assigned on.</remarks>
    public static void GrantAssigneeRows(PermissionPolicyRole r, params (Type subjectType, string? scope)[] subjects) =>
        AssigneeRows(r, Validate(subjects));

    static void AssigneeRows(PermissionPolicyRole r, List<Subject> subjects) => Rows(r, AssigneeFamily, subjects, ServedTypes(r));

    static void RequireName(PermissionPolicyRole r)
    {
        if (string.IsNullOrEmpty(r.Name)) throw new ArgumentException("Name the role before granting: tasks and notifications are scoped by it.", nameof(r));
    }

    static string ListView(Type t) => t.Name + "_ListView";

    // ---- Runtime rows (CARD-2144) ----
    // Criteria are built as operators and serialized by DevExpress, so role and type names are escaped as literals.

    const string RequesterText = "[UserId] = CurrentUserId()";

    /// <summary>A subject type and its parsed scope (null: unscoped). <see cref="Type"/> is null for a type known only by name.</summary>
    sealed record Subject(string Name, Type? Type, CriteriaOperator? Scope);

    /// <summary>A kind of runtime row: its target, the leading operand and the role-wide row's text (both null for instances),
    /// and the paths to the subject.</summary>
    sealed record Family(Type Target, CriteriaOperator? Head, string? Wide, string TypePath, string IdPath);

    static Family TaskFamily(string role)
    {
        var head = new BinaryOperator(nameof(WorkflowTask.Role), role);
        return new(typeof(WorkflowTask), head, head.ToString(), "Instance.SubjectType", "Instance.SubjectId");
    }
    static Family NoteFamily(string role) => TaskFamily(role) with { Target = typeof(WorkflowNotification) };
    static readonly Family RequesterFamily = new(typeof(WorkflowNotification), CriteriaOperator.Parse(RequesterText), RequesterText, "Instance.SubjectType", "Instance.SubjectId");
    static readonly Family AssigneeFamily = RequesterFamily with { Target = typeof(WorkflowTask) };
    static readonly Family InstanceFamily = new(typeof(WorkflowInstance), null, null, nameof(WorkflowInstance.SubjectType), nameof(WorkflowInstance.SubjectId));

    static List<Subject> Validate(IEnumerable<(Type type, string? scope)> subjects)
    {
        var result = new List<Subject>();
        foreach (var (type, text) in subjects)
        {
            ArgumentNullException.ThrowIfNull(type);
            var scope = ParseScope(type, text);
            var same = result.FirstOrDefault(s => s.Type == type);
            if (same == null) result.Add(new Subject(type.FullName!, type, scope));
            else if (same.Scope?.ToString() != scope?.ToString())
                throw new ArgumentException($"{type.FullName} is granted twice with different scopes.", nameof(subjects));
        }
        return result;
    }

    static CriteriaOperator? ParseScope(Type type, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        CriteriaOperator scope;
        try { scope = CriteriaOperator.Parse(text); }
        catch (CriteriaParserException ex) { throw new ArgumentException($"The scope of {type.FullName} does not parse: {ex.Message}", nameof(text), ex); }
        if (scope is null) return null;
        if (ParentReference.In(scope))
            throw new ArgumentException($"The scope of {type.FullName} uses '^.': a scope reads the subject's own members only.", nameof(text));
        return scope;
    }

    /// <summary>Finds a parent reference (<c>^.</c>) anywhere in a criteria, inside aggregates and joins too.</summary>
    sealed class ParentReference : ClientCriteriaVisitorBase
    {
        bool found;
        protected override CriteriaOperator Visit(OperandProperty theOperand)
        {
            found |= theOperand.PropertyName == "^" || theOperand.PropertyName.StartsWith("^.", StringComparison.Ordinal);
            return theOperand;
        }
        public static bool In(CriteriaOperator criteria)
        {
            var v = new ParentReference();
            v.Process(criteria);
            return v.found;
        }
    }

    static void RoleRows(PermissionPolicyRole r, List<Subject> subjects)
    {
        var served = ServedTypes(r);   // before the instance rows change
        Rows(r, TaskFamily(r.Name), subjects, served);
        Rows(r, NoteFamily(r.Name), subjects, served);
        Rows(r, InstanceFamily, subjects, served);
    }

    static void RequesterRows(PermissionPolicyRole r, List<Subject> subjects) => Rows(r, RequesterFamily, subjects, ServedTypes(r));

    /// <summary>The subject types the role's instance rows grant without a join: plain and legacy combined rows.</summary>
    static List<string> ServedTypes(PermissionPolicyRole r) =>
        ObjectRows(r, typeof(WorkflowInstance)).SelectMany(o => Match(InstanceFamily, o.Criteria) is { Joined: false } m ? new[] { m.Type }
            : Legacy(o.Criteria) ?? Array.Empty<string>()).Distinct().ToList();

    static void Rows(PermissionPolicyRole r, Family f, List<Subject> subjects, List<string> served)
    {
        var existing = ObjectRows(r, f.Target);
        var perType = f.Wide == null || subjects.Any(s => s.Scope is not null) || existing.Any(o => Match(f, o.Criteria) != null);
        if (!perType)
        {
            Object(r, f.Target, f.Wide!);   // today's role-wide row
            return;
        }
        var all = subjects.ToList();
        var wideGone = false;
        foreach (var o in existing.Where(o => o.Criteria == f.Wide)) wideGone |= Narrow(o);
        if (wideGone)   // the role-wide row no longer grants: the types it served keep plain per-type rows
            all.AddRange(served.Where(n => all.All(s => s.Name != n)).Select(n => new Subject(n, null, null)));
        foreach (var s in all)
        {
            var row = Row(f, s).ToString();
            if (s.Scope is not null)
                foreach (var o in existing.Where(o => o.Criteria != row && Match(f, o.Criteria)?.Type == s.Name))
                    Narrow(o);
            if (s.Scope is not null && f == InstanceFamily)
                foreach (var o in existing.Where(o => Legacy(o.Criteria)?.Contains(s.Name) == true))
                {
                    var rest = Legacy(o.Criteria)!.Where(n => n != s.Name).ToArray();
                    if (rest.Length == 0) Narrow(o);
                    else if (o.ReadState == SecurityPermissionState.Allow) o.Criteria = new InOperator(nameof(WorkflowInstance.SubjectType), rest).ToString();
                }
            Object(r, f.Target, row);
        }
    }

    /// <summary>The row of family <paramref name="f"/> for subject <paramref name="s"/>: plain, or joined to the subject.</summary>
    static CriteriaOperator Row(Family f, Subject s)
    {
        var ops = new List<CriteriaOperator>();
        if (f.Head is not null) ops.Add(f.Head);
        ops.Add(new BinaryOperator(f.TypePath, s.Name));
        if (s.Scope is not null)
        {
            var key = XafTypesInfo.Instance.FindTypeInfo(s.Type!).KeyMember?.Name
                ?? throw new ArgumentException($"{s.Name} has no key member to join on.");
            ops.Add(new JoinOperand(s.Name, new GroupOperator(GroupOperatorType.And,
                new BinaryOperator(new OperandProperty(key), new OperandProperty("^." + f.IdPath), BinaryOperatorType.Equal), s.Scope)));
        }
        return ops.Count == 1 ? ops[0] : new GroupOperator(GroupOperatorType.And, ops);
    }

    /// <summary>A per-type row these methods build for family <paramref name="f"/>: its subject type, and whether it is joined.</summary>
    static (string Type, bool Joined)? Match(Family f, string? criteria)
    {
        var c = TryParse(criteria);
        if (c is null) return null;
        var ops = c is GroupOperator { OperatorType: GroupOperatorType.And } g ? g.Operands.ToList() : new List<CriteriaOperator> { c };
        var lead = f.Head is null ? 1 : 2;
        if (ops.Count != lead && ops.Count != lead + 1) return null;
        if (f.Head is not null && ops[0].ToString() != f.Head.ToString()) return null;
        if (ops[lead - 1] is not BinaryOperator { OperatorType: BinaryOperatorType.Equal, LeftOperand: OperandProperty p, RightOperand: OperandValue { Value: string type } }
            || p.PropertyName != f.TypePath) return null;
        if (ops.Count == lead) return (type, false);
        return ops[lead] is JoinOperand j && j.JoinTypeName == type ? (type, true) : null;
    }

    /// <summary>The types of a legacy combined instance row (<c>[SubjectType] In (...)</c>, before XSM-021), else null.</summary>
    static string[]? Legacy(string? criteria) =>
        TryParse(criteria) is InOperator { LeftOperand: OperandProperty { PropertyName: nameof(WorkflowInstance.SubjectType) } } i
        && i.Operands.All(o => o is OperandValue { Value: string }) ? i.Operands.Select(o => (string)((OperandValue)o).Value).ToArray() : null;

    static CriteriaOperator? TryParse(string? criteria)
    {
        if (string.IsNullOrWhiteSpace(criteria)) return null;
        try { return CriteriaOperator.Parse(criteria); } catch (CriteriaParserException) { return null; }
    }

    /// <summary>The role's object permissions on <paramref name="target"/>, a snapshot (a new row is listed twice before the commit).</summary>
    static List<PermissionPolicyObjectPermissionsObject> ObjectRows(PermissionPolicyRole r, Type target) =>
        r.TypePermissions.Where(t => t.TargetType == target).SelectMany(t => t.ObjectPermissions).Distinct().ToList();

    /// <summary>Takes away a wider row's Read Allow: a row exactly as these methods write it is deleted; an admin-edited one
    /// (another state set) only has its Read cleared. A Deny or unset Read is left alone. True if the read was taken away.</summary>
    static bool Narrow(PermissionPolicyObjectPermissionsObject o)
    {
        if (o.ReadState != SecurityPermissionState.Allow) return false;
        if (o.WriteState is null && o.DeleteState is null && o.NavigateState is null)
        {
            o.TypePermissionObject?.ObjectPermissions.Remove(o);
            ((IObjectSpaceLink)o).ObjectSpace?.Delete(o);
        }
        else o.ReadState = null;
        return true;
    }

    static IEnumerable<string> Operations(string operations) => operations.Split(SecurityOperations.Delimiter, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Allow <paramref name="operations"/> on <paramref name="type"/> where the state is still unset.</summary>
    static void Type(PermissionPolicyRole r, Type type, string operations)
    {
        var p = r.EnsureTypePermission(type);
        foreach (var op in Operations(operations))
            switch (op)
            {
                case SecurityOperations.Read: p.ReadState ??= SecurityPermissionState.Allow; break;
                case SecurityOperations.Write: p.WriteState ??= SecurityPermissionState.Allow; break;
                case SecurityOperations.Create: p.CreateState ??= SecurityPermissionState.Allow; break;
                case SecurityOperations.Delete: p.DeleteState ??= SecurityPermissionState.Allow; break;
                case SecurityOperations.Navigate: p.NavigateState ??= SecurityPermissionState.Allow; break;
            }
    }

    /// <summary>Read Allow on the rows of <paramref name="type"/> matching <paramref name="criteria"/>: the existing row with the
    /// same criteria text if there is one, else a new one. Only an unset Read state is filled.</summary>
    static void Object(PermissionPolicyRole r, Type type, string criteria)
    {
        var t = r.EnsureTypePermission(type);
        var p = t.ObjectPermissions.FirstOrDefault(o => string.Equals(o.Criteria, criteria, StringComparison.Ordinal))
            ?? t.CreateObjectPermission();
        p.Criteria = criteria;
        p.ReadState ??= SecurityPermissionState.Allow;
    }

    /// <summary>A Write <paramref name="state"/> on member <paramref name="member"/> of every <paramref name="type"/> row, unless a
    /// permission for exactly that member (without criteria) already sets Write.</summary>
    static void Member(PermissionPolicyRole r, Type type, string member, SecurityPermissionState state)
    {
        var t = r.EnsureTypePermission(type);
        var p = t.MemberPermissions.FirstOrDefault(m => m.Members == member && string.IsNullOrEmpty(m.Criteria)) ?? t.CreateMemberPermission();
        p.Members = member;
        p.WriteState ??= state;
    }

    /// <summary>Navigate Allow on <paramref name="itemPath"/> unless the role already has a permission for it (whatever its state).</summary>
    static void Navigation(PermissionPolicyRole r, string itemPath)
    {
        if (r.NavigationPermissions.All(n => n.ItemPath != itemPath)) r.AddNavigationPermission(itemPath, SecurityPermissionState.Allow);
    }
}
