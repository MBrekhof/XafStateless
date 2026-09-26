using DevExpress.Data.Filtering;
using DevExpress.ExpressApp.Security;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using XafStateless.Module.BusinessObjects;

namespace XafStateless.Module.Services;

/// <summary>Reusable role seeding for hosts: the designer and participant halves of the workflow permission matrix.
/// Roles deny by default; these only add Allow/Deny entries.
/// Both methods are safe to call on every updater run (XSM-021): they add what is missing and fill only operation states that
/// are still unset. An explicit Allow or Deny already on the role (an admin's customization) is never changed, and a row
/// removed from the arguments is not revoked.</summary>
public static class WorkflowPermissions
{
    public const string NavigationGroup = "Application/NavigationItems/Items/Workflow/Items/";

    static readonly Type[] DefinitionTypes = [typeof(WorkflowDefinition), typeof(WorkflowState), typeof(WorkflowTransition), typeof(WorkflowEffect)];
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
    /// tasks and notifications addressed to this role (<c>[Role] = r.Name</c>), notifications addressed to the logged-on user
    /// (a requester's, <c>[UserId] = CurrentUserId()</c>), instances of the granted subject types (one
    /// permission per type, so a subject added later adds one row). A user holding several roles sees the union.</summary>
    /// <param name="subjects">A navigation item id (assumed in the Default group) or a full item path (contains '/').</param>
    /// <remarks>Set the role's Name before calling: the task and notification scope captures it.</remarks>
    public static void GrantParticipant(PermissionPolicyRole r, params (Type subjectType, string navigationItemId)[] subjects)
    {
        if (string.IsNullOrEmpty(r.Name)) throw new ArgumentException("Name the role before granting: tasks and notifications are scoped by it.", nameof(r));
        foreach (var (type, item) in subjects)
        {
            Type(r, type, SecurityOperations.CRUDAccess);
            Member(r, type, nameof(IWorkflowSubject.Workflow), SecurityPermissionState.Deny);
            Navigation(r, item.Contains('/') ? item : "Application/NavigationItems/Items/Default/Items/" + item);
        }
        foreach (var t in DefinitionTypes) Type(r, t, SecurityOperations.Read);
        // Criteria built as operators and serialized by DevExpress, so role and type names are escaped as literals.
        var ownRole = new BinaryOperator(nameof(WorkflowTask.Role), r.Name).ToString();
        Object(r, typeof(WorkflowTask), ownRole);
        Object(r, typeof(WorkflowNotification), ownRole);
        Object(r, typeof(WorkflowNotification), "[UserId] = CurrentUserId()");   // requester notifications addressed to this user
        foreach (var type in subjects.Select(x => x.subjectType).Distinct())
            Object(r, typeof(WorkflowInstance), new BinaryOperator(nameof(WorkflowInstance.SubjectType), type.FullName!).ToString());
        Navigation(r, NavigationGroup + ListView(typeof(WorkflowTask)));
        Navigation(r, NavigationGroup + ListView(typeof(WorkflowNotification)));
    }

    static string ListView(Type t) => t.Name + "_ListView";

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
