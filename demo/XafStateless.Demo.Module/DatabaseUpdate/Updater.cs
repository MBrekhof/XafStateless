using DevExpress.ExpressApp;
using DevExpress.Data.Filtering;
using DevExpress.Persistent.Base;
using DevExpress.ExpressApp.Updating;
using DevExpress.ExpressApp.Security;
using DevExpress.ExpressApp.SystemModule;
using DevExpress.ExpressApp.EF;
using DevExpress.Persistent.BaseImpl.EF;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using XafStateless.Demo.Module.BusinessObjects;
using Microsoft.Extensions.DependencyInjection;
using XafStateless.Engine;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Services;

namespace XafStateless.Demo.Module.DatabaseUpdate;

// For more typical usage scenarios, be sure to check out https://docs.devexpress.com/eXpressAppFramework/DevExpress.ExpressApp.Updating.ModuleUpdater
public class Updater : ModuleUpdater {
    public Updater(IObjectSpace objectSpace, Version currentDBVersion) :
        base(objectSpace, currentDBVersion) {
    }
    public override void UpdateDatabaseAfterUpdateSchema() {
        base.UpdateDatabaseAfterUpdateSchema();
        //string name = "MyName";
        //EntityObject1 theObject = ObjectSpace.FirstOrDefault<EntityObject1>(u => u.Name == name);
        //if(theObject == null) {
        //    theObject = ObjectSpace.CreateObject<EntityObject1>();
        //    theObject.Name = name;
        //}

        // The code below creates users and roles for testing purposes only.
        // In production code, you can create users and assign roles to them automatically, as described in the following help topic:
        // https://docs.devexpress.com/eXpressAppFramework/119064/data-security-and-safety/security-system/authentication
#if !RELEASE
        // If a role doesn't exist in the database, create this role
        var defaultRole = CreateDefaultRole();
        var adminRole = CreateAdminRole();
        var approversRole = CreateRole("Approvers", r => WorkflowPermissions.GrantParticipant(r, (typeof(PurchaseRequest), "PurchaseRequest_ListView")));
        var designersRole = CreateRole("WorkflowDesigners", WorkflowPermissions.GrantDesigner);

        ObjectSpace.CommitChanges(); //This line persists created object(s).

        UserManager userManager = ObjectSpace.ServiceProvider.GetRequiredService<UserManager>();

        // If a user named 'User' doesn't exist in the database, create this user
        if(userManager.FindUserByName<ApplicationUser>(ObjectSpace, "User") == null) {
            // Set a password if the standard authentication type is used
            string EmptyPassword = "";
            _ = userManager.CreateUser<ApplicationUser>(ObjectSpace, "User", EmptyPassword, (user) => {
                // Add the Users role to the user
                user.Roles.Add(defaultRole);
            });
        }

        // If a user named 'Admin' doesn't exist in the database, create this user
        if(userManager.FindUserByName<ApplicationUser>(ObjectSpace, "Admin") == null) {
            // Set a password if the standard authentication type is used
            string EmptyPassword = "";
            _ = userManager.CreateUser<ApplicationUser>(ObjectSpace, "Admin", EmptyPassword, (user) => {
                // Add the Administrators role to the user
                user.Roles.Add(adminRole);
            });
        }

        // Workflow demo users, empty passwords like the template's.
        foreach(var (name, role) in new[] { ("designer", designersRole), ("approver", approversRole) }) {
            if(userManager.FindUserByName<ApplicationUser>(ObjectSpace, name) == null) {
                string EmptyPassword = "";
                _ = userManager.CreateUser<ApplicationUser>(ObjectSpace, name, EmptyPassword, (user) => {
                    user.Roles.Add(role);
                    user.Roles.Add(defaultRole);
                });
            }
        }

        ObjectSpace.CommitChanges(); //This line persists created object(s).

        SeedPurchaseRequestWorkflow();
#endif
    }
    public override void UpdateDatabaseBeforeUpdateSchema() {
        base.UpdateDatabaseBeforeUpdateSchema();
    }
    PermissionPolicyRole CreateRole(string name, Action<PermissionPolicyRole> grant) {
        var role = ObjectSpace.FirstOrDefault<PermissionPolicyRole>(r => r.Name == name);
        if(role == null) {
            role = ObjectSpace.CreateObject<PermissionPolicyRole>();
            role.Name = name;
            grant(role);
        }
        return role;
    }

    /// <summary>The purchase-request workflow (the engine tests' fixture, plus Amount locked in approval), published as v1 once.</summary>
    static readonly WorkflowDef PurchaseRequestWorkflow = new("purchase-request", 1, "draft",
        [
            new("draft", "Draft", StateKind.Manual),
            new("approval", "Approval", StateKind.Task, TaskRole: "Approvers",
                OnEntry: [new(EffectKind.Notify, "Approvers", "Please approve {Title}")], Locked: ["Amount"]),   // the amount under approval cannot change
            new("decide-amount", "Amount check", StateKind.Decision),
            new("wait-cooling", "Cooling-off", StateKind.Wait, WaitMinutes: 1),
            new("ordered", "Ordered", StateKind.Final, OnEntry: [new(EffectKind.SetField, "Ordered", "true")]),
            new("rejected", "Rejected", StateKind.Final),
        ],
        [
            new("draft", "Submit", "approval"),
            new("approval", "Approve", "decide-amount"),
            new("approval", "Reject", "rejected"),
            new("decide-amount", "Large", "wait-cooling", Criteria: "Amount > 1000", Order: 0),
            new("decide-amount", "Small", "ordered", Order: 1),
            new("wait-cooling", "Cooled", "ordered"),
        ]);

    void SeedPurchaseRequestWorkflow() {
        var def = PurchaseRequestWorkflow;
        if(ObjectSpace.FirstOrDefault<WorkflowDefinition>(d => d.Name == def.Name) != null) return;
        var d = DefinitionMapper.ToDraft(ObjectSpace, def, typeof(PurchaseRequest).FullName!);
        ObjectSpace.CommitChanges();   // Publish needs a saved draft and nothing else pending
        var r = ObjectSpace.ServiceProvider.GetRequiredService<DefinitionService>().Publish(ObjectSpace, d, "setup", WorkflowRoleCatalog.Names(ObjectSpace));
        if(!r.Ok) throw new InvalidOperationException("Seeding the purchase-request workflow failed: " + string.Join("; ", r.Messages));
    }

    PermissionPolicyRole CreateAdminRole() {
        PermissionPolicyRole adminRole = ObjectSpace.FirstOrDefault<PermissionPolicyRole>(r => r.Name == "Administrators");
        if(adminRole == null) {
            adminRole = ObjectSpace.CreateObject<PermissionPolicyRole>();
            adminRole.Name = "Administrators";
            adminRole.IsAdministrative = true;
        }
        return adminRole;
    }
    PermissionPolicyRole CreateDefaultRole() {
        PermissionPolicyRole defaultRole = ObjectSpace.FirstOrDefault<PermissionPolicyRole>(role => role.Name == "Default");
        if(defaultRole == null) {
            defaultRole = ObjectSpace.CreateObject<PermissionPolicyRole>();
            defaultRole.Name = "Default";

            defaultRole.AddObjectPermissionFromLambda<ApplicationUser>(SecurityOperations.Read, cm => cm.ID == (Guid)CurrentUserIdOperator.CurrentUserId(), SecurityPermissionState.Allow);
            defaultRole.AddNavigationPermission(@"Application/NavigationItems/Items/Default/Items/MyDetails", SecurityPermissionState.Allow);
            defaultRole.AddMemberPermissionFromLambda<ApplicationUser>(SecurityOperations.Write, "ChangePasswordOnFirstLogon", cm => cm.ID == (Guid)CurrentUserIdOperator.CurrentUserId(), SecurityPermissionState.Allow);
            defaultRole.AddMemberPermissionFromLambda<ApplicationUser>(SecurityOperations.Write, "StoredPassword", cm => cm.ID == (Guid)CurrentUserIdOperator.CurrentUserId(), SecurityPermissionState.Allow);
            defaultRole.AddTypePermissionsRecursively<PermissionPolicyRole>(SecurityOperations.Read, SecurityPermissionState.Deny);
            defaultRole.AddObjectPermission<ModelDifference>(SecurityOperations.ReadWriteAccess, "UserId = ToStr(CurrentUserId())", SecurityPermissionState.Allow);
            defaultRole.AddObjectPermission<ModelDifferenceAspect>(SecurityOperations.ReadWriteAccess, "Owner.UserId = ToStr(CurrentUserId())", SecurityPermissionState.Allow);
            defaultRole.AddTypePermissionsRecursively<ModelDifference>(SecurityOperations.Create, SecurityPermissionState.Allow);
            defaultRole.AddTypePermissionsRecursively<ModelDifferenceAspect>(SecurityOperations.Create, SecurityPermissionState.Allow);
        }
        return defaultRole;
    }
}
