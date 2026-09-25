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

    /// <summary>CRUD on each subject type except writing its <c>Workflow</c> link; read-only on instances, tasks and
    /// notifications (the runtime writes them); read on the definition types; navigation to Tasks and to each subject.</summary>
    /// <param name="subjects">A navigation item id (assumed in the Default group) or a full item path (contains '/').</param>
    public static void GrantParticipant(PermissionPolicyRole r, params (Type subjectType, string navigationItemId)[] subjects)
    {
        foreach (var (type, item) in subjects)
        {
            r.AddTypePermission(type, SecurityOperations.CRUDAccess, SecurityPermissionState.Allow);
            r.AddMemberPermission(type, SecurityOperations.Write, nameof(IWorkflowSubject.Workflow), null, SecurityPermissionState.Deny);
            r.AddNavigationPermission(item.Contains('/') ? item : "Application/NavigationItems/Items/Default/Items/" + item, SecurityPermissionState.Allow);
        }
        foreach (var t in RuntimeTypes.Concat(DefinitionTypes)) r.AddTypePermission(t, SecurityOperations.Read, SecurityPermissionState.Allow);
        r.AddNavigationPermission(NavigationGroup + ListView(typeof(WorkflowTask)), SecurityPermissionState.Allow);
    }

    static string ListView(Type t) => t.Name + "_ListView";
}
