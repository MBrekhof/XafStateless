using DevExpress.ExpressApp.EFCore.Updating;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using DevExpress.Persistent.BaseImpl.EF;
using DevExpress.ExpressApp.Design;
using DevExpress.ExpressApp.EFCore.DesignTime;
using XafStateless.Module;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Services;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace XafStateless.Demo.Module.BusinessObjects;


[TypesInfoInitializer(typeof(DbContextTypesInfoInitializer<DemoEFCoreDbContext>))]
public class DemoEFCoreDbContext : DbContext {
    public DemoEFCoreDbContext(DbContextOptions<DemoEFCoreDbContext> options) : base(options) {
    }
    //public DbSet<ModuleInfo> ModulesInfo { get; set; }
    public DbSet<ModelDifference> ModelDifferences { get; set; }
    public DbSet<ModelDifferenceAspect> ModelDifferenceAspects { get; set; }
    public DbSet<PermissionPolicyRole> Roles { get; set; }
    public DbSet<XafStateless.Demo.Module.BusinessObjects.ApplicationUser> Users { get; set; }
    public DbSet<XafStateless.Demo.Module.BusinessObjects.ApplicationUserLoginInfo> UserLoginsInfo { get; set; }
    public DbSet<WorkflowDefinition> WorkflowDefinitions { get; set; }
    public DbSet<WorkflowState> WorkflowStates { get; set; }
    public DbSet<WorkflowEffect> WorkflowEffects { get; set; }
    public DbSet<WorkflowTransition> WorkflowTransitions { get; set; }
    public DbSet<WorkflowInstance> WorkflowInstances { get; set; }
    public DbSet<WorkflowTask> WorkflowTasks { get; set; }
    public DbSet<WorkflowNotification> WorkflowNotifications { get; set; }
    public DbSet<PurchaseRequest> PurchaseRequests { get; set; }

    // The workflow guards run for every context, whoever builds it (XAF provider, updater, tests).
    // XAF0031 is a false positive here: UseConnectionString in Startup enables the proxies; this only adds interceptors.
#pragma warning disable XAF0031
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) {
        var registered = optionsBuilder.Options.FindExtension<CoreOptionsExtension>()?.Interceptors;
        foreach(var guard in new Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] { DefinitionGuard.Instance, StateLockGuard.Instance })
            if(registered?.Contains(guard) != true)
                optionsBuilder.AddInterceptors(guard);
    }
#pragma warning restore XAF0031

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        base.OnModelCreating(modelBuilder);
        modelBuilder.UseDeferredDeletion(this);
        modelBuilder.UseOptimisticLock();
        modelBuilder.SetOneToManyAssociationDeleteBehavior(DeleteBehavior.SetNull, DeleteBehavior.Cascade);
        modelBuilder.HasChangeTrackingStrategy(ChangeTrackingStrategy.ChangingAndChangedNotificationsWithOriginalValues);
        modelBuilder.UsePropertyAccessMode(PropertyAccessMode.PreferFieldDuringConstruction);
        modelBuilder.Entity<XafStateless.Demo.Module.BusinessObjects.ApplicationUserLoginInfo>(b => {
            b.HasIndex(nameof(DevExpress.ExpressApp.Security.ISecurityUserLoginInfo.LoginProviderName), nameof(DevExpress.ExpressApp.Security.ISecurityUserLoginInfo.ProviderUserKey)).IsUnique();
        });
        modelBuilder.Entity<ModelDifference>()
            .HasMany(t => t.Aspects)
            .WithOne(t => t.Owner)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.ConfigureXafStateless();
    }
}
