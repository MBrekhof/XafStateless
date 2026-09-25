using DevExpress.Data.Filtering;
using DevExpress.ExpressApp.Security;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using XafStateless.Module.BusinessObjects;

namespace XafStateless.Module.Services;

/// <summary>Reusable role seeding for hosts: the designer and participant halves of the workflow permission matrix.
/// Roles deny by default; these only add Allow/Deny entries.</summary>
public static class WorkflowPermissions
{
    public const string NavigationGroup = "Application/NavigationItems/Items/Workflow/Items/";

    static readonly Type[] DefinitionTypes = [typeof(WorkflowDefinition), typeof(WorkflowState), typeof(WorkflowTransition), typeof(WorkflowEffect)];
    static readonly Type[] RuntimeTypes = [typeof(WorkflowInstance), typeof(WorkflowTask), typeof(WorkflowNotification)];

    /// <summary>CRUD on the definition types; read on instances, tasks and notifications; the Workflow navigation group.
    /// No role read: role names come from <see cref="WorkflowRoleCatalog"/>.</summary>
    public static void GrantDesigner(PermissionPolicyRole r)
    {
        foreach (var t in DefinitionTypes) r.AddTypePermission(t, SecurityOperations.CRUDAccess, SecurityPermissionState.Allow);
        foreach (var t in RuntimeTypes) r.AddTypePermission(t, SecurityOperations.Read, SecurityPermissionState.Allow);
        foreach (var t in new[] { typeof(WorkflowDefinition), typeof(WorkflowTask), typeof(WorkflowNotification) })
            r.AddNavigationPermission(NavigationGroup + ListView(t), SecurityPermissionState.Allow);
    }

    /// <summary>CRUD on each subject type except writing its <c>Workflow</c> link; read on the definition types; navigation to
    /// Tasks and to each subject. Runtime rows are read-only (the runtime writes them) and scoped by object permissions:
    /// tasks and notifications addressed to this role (<c>[Role] = r.Name</c>), instances of the granted subject types.
    /// A user holding several roles sees the union.</summary>
    /// <param name="subjects">A navigation item id (assumed in the Default group) or a full item path (contains '/').</param>
    /// <remarks>Set the role's Name before calling: the task and notification scope captures it.</remarks>
    public static void GrantParticipant(PermissionPolicyRole r, params (Type subjectType, string navigationItemId)[] subjects)
    {
        if (string.IsNullOrEmpty(r.Name)) throw new ArgumentException("Name the role before granting: tasks and notifications are scoped by it.", nameof(r));
        foreach (var (type, item) in subjects)
        {
            r.AddTypePermission(type, SecurityOperations.CRUDAccess, SecurityPermissionState.Allow);
            r.AddMemberPermission(type, SecurityOperations.Write, nameof(IWorkflowSubject.Workflow), null, SecurityPermissionState.Deny);
            r.AddNavigationPermission(item.Contains('/') ? item : "Application/NavigationItems/Items/Default/Items/" + item, SecurityPermissionState.Allow);
        }
        foreach (var t in DefinitionTypes) r.AddTypePermission(t, SecurityOperations.Read, SecurityPermissionState.Allow);
        // Criteria built as operators and serialized by DevExpress, so role and type names are escaped as literals.
        var ownRole = new BinaryOperator(nameof(WorkflowTask.Role), r.Name).ToString();
        r.AddObjectPermission(typeof(WorkflowTask), SecurityOperations.Read, ownRole, SecurityPermissionState.Allow);
        r.AddObjectPermission(typeof(WorkflowNotification), SecurityOperations.Read, ownRole, SecurityPermissionState.Allow);
        if (subjects.Length > 0)
            r.AddObjectPermission(typeof(WorkflowInstance), SecurityOperations.Read, new InOperator(nameof(WorkflowInstance.SubjectType), subjects.Select(x => x.subjectType.FullName!)).ToString(), SecurityPermissionState.Allow);
        r.AddNavigationPermission(NavigationGroup + ListView(typeof(WorkflowTask)), SecurityPermissionState.Allow);
    }

    static string ListView(Type t) => t.Name + "_ListView";
}
