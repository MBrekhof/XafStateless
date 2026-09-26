# Integrating XafStateless into a host application

This guide covers adding XafStateless to an existing XAF Blazor application on EF Core. Every step below is what the demo
(`demo/`) does, and the file that does it is named at each step. When in doubt, copy the demo.

The host needs:
- DevExpress XAF **26.1.4** (Blazor, EF Core, Security with `PermissionPolicyRole` roles);
- EF Core **10.0.10** on **SQL Server**;
- .NET 10.

## 1. References

| Host project | Reference |
|---|---|
| The platform-agnostic module (the one holding the DbContext and business classes) | `src/XafStateless.Module/XafStateless.Module.csproj` |
| The Blazor server project | `src/XafStateless.Blazor/XafStateless.Blazor.csproj` (it brings the Module in) |

For now these are `ProjectReference`s to this repository. There is no NuGet package yet.

## 2. Modules

In the host's module (demo: `XafStateless.Demo.Module/Module.cs`):

```csharp
RequiredModuleTypes.Add(typeof(XafStateless.Module.XafStatelessModule));
```

`XafStatelessModule` itself requires `ConditionalAppearanceModule` and `ValidationModule`.

In the Blazor application builder (demo: `XafStateless.Demo.Blazor.Server/Startup.cs`):

```csharp
builder.Modules
    // ... the host's modules ...
    .Add<XafStateless.Blazor.XafStatelessBlazorModule>();
```

`XafStatelessBlazorModule` holds the string lookup editors and the Diagram view item. It requires `XafStatelessModule`.
You can instead add it with `RequiredModuleTypes.Add(typeof(XafStateless.Blazor.XafStatelessBlazorModule))` in the host's
Blazor module.

## 3. DbContext

Demo file: `XafStateless.Demo.Module/BusinessObjects/DemoDbContext.cs`.

### DbSets

Add one DbSet per workflow entity, plus one for each subject class (see step 7):

```csharp
public DbSet<WorkflowDefinition> WorkflowDefinitions { get; set; }
public DbSet<WorkflowState> WorkflowStates { get; set; }
public DbSet<WorkflowEffect> WorkflowEffects { get; set; }
public DbSet<WorkflowTransition> WorkflowTransitions { get; set; }
public DbSet<WorkflowInstance> WorkflowInstances { get; set; }
public DbSet<WorkflowTask> WorkflowTasks { get; set; }
public DbSet<WorkflowNotification> WorkflowNotifications { get; set; }
```

### Model configuration

In `OnModelCreating`:

```csharp
modelBuilder.UseOptimisticLock();          // REQUIRED
modelBuilder.ConfigureXafStateless();      // unique indexes, key lengths
```

**`UseOptimisticLock()` is required.** The module has no concurrency tokens of its own. Every workflow command touches
the subject, the instance and (for Decide) the task, so XAF's optimistic lock makes two racing commands, or a user save
racing a transition, collide. Exactly one wins, and the loser is retried. Without the lock, the last writer wins silently.
New XAF apps have `UseOptimisticLock()` in the generated DbContext; check that yours still does.

`ConfigureXafStateless()` enforces "at most one Draft and one Published per name" and "one instance per subject" with
filtered unique indexes. The filters use SQL Server syntax and skip soft-deleted rows (`[GCRecord] = 0`), so under
`UseDeferredDeletion` (the template default) a deleted draft does not block its replacement.

### The two guards

Add both interceptors in `OnConfiguring`, so they run for every context, whoever creates it (the XAF provider, the
database updater, the non-secured command spaces, tests):

```csharp
protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) {
    var registered = optionsBuilder.Options.FindExtension<CoreOptionsExtension>()?.Interceptors;
    foreach(var guard in new IInterceptor[] { DefinitionGuard.Instance, StateLockGuard.Instance })
        if(registered?.Contains(guard) != true)
            optionsBuilder.AddInterceptors(guard);
}
```

(`CoreOptionsExtension` is in `Microsoft.EntityFrameworkCore.Infrastructure`, `IInterceptor` in
`Microsoft.EntityFrameworkCore.Diagnostics`, and both guards in `XafStateless.Module.Services`.)

- `DefinitionGuard`:
  - keeps Published and Retired definitions immutable;
  - allows publishing only through `DefinitionService.Publish`;
  - lets instance state and tasks change only through the runtime;
  - lets only the runtime insert notifications. Only inserts are restricted: any other change to a notification (a
    dispatcher's update, a delete) passes.
- `StateLockGuard`:
  - refuses a save that changes a field locked in the subject's current state;
  - refuses any change to the subject's `Workflow` link.

## 4. Services

In `ConfigureServices` (demo: `Startup.cs`, where it comes before `AddXaf`):

```csharp
services.AddXafStateless();
```

This registers `DefinitionStore`, `DefinitionService` and `WorkflowRuntime` as singletons, plus `TimeProvider.System`
(added only if none is registered, so a host or test can supply its own clock). The timer and the notification
dispatcher are **not** registered here; the host owns them (steps 8 and 9).

## 5. Host page: Mermaid

The diagram is drawn in the browser by the bundled Mermaid (no CDN). Add both script tags to the host page **before**
`blazor.server.js` (demo: `XafStateless.Demo.Blazor.Server/Pages/_Host.cshtml`):

```html
<script src="_content/XafStateless.Blazor/mermaid.min.js"></script>
<script src="_content/XafStateless.Blazor/diagram.js"></script>
<script src="_framework/blazor.server.js"></script>
```

Without them the diagram view item cannot draw. It catches the failed script call and shows "Diagram scripts are not
loaded" in place of the diagram; the rest of the view keeps working.

## 6. Roles

Seed the roles in the host's `ModuleUpdater` with `WorkflowPermissions` (`XafStateless.Module.Services`). The demo
version is in `XafStateless.Demo.Module/DatabaseUpdate/Updater.cs`.

A `ModuleUpdater` runs on every version bump and every `--forceUpdate`, so create and grant a role only when it is
missing, the same way the demo's `CreateRole` does:

```csharp
public override void UpdateDatabaseAfterUpdateSchema() {
    base.UpdateDatabaseAfterUpdateSchema();
    CreateRole("Approvers", r => WorkflowPermissions.GrantParticipant(r, (typeof(PurchaseRequest), "PurchaseRequest_ListView")));
    CreateRole("WorkflowDesigners", WorkflowPermissions.GrantDesigner);
    ObjectSpace.CommitChanges();
}

PermissionPolicyRole CreateRole(string name, Action<PermissionPolicyRole> grant) {
    var role = ObjectSpace.FirstOrDefault<PermissionPolicyRole>(r => r.Name == name);
    if(role == null) {
        role = ObjectSpace.CreateObject<PermissionPolicyRole>();
        role.Name = name;          // name the role first: the participant's task scope uses the name
        grant(role);
    }
    return role;
}
```

- **`GrantDesigner(role)`** grants:
  - full CRUD on definitions, states, transitions and effects;
  - read on instances, tasks and notifications;
  - the Workflow navigation items (Definition, Task, Notification).
- **`GrantParticipant(role, params (Type subjectType, string navigationItemId)[] subjects)`** grants:
  - CRUD on each subject class, except writing its `Workflow` link;
  - read on the definition types;
  - read on tasks and notifications addressed to this role, and on instances of the granted subject types;
  - navigation to Workflow Task and to each subject.

  About the arguments:
  - Each tuple is a subject class and its navigation item. An id without `/` is taken as an item in the **Default**
    navigation group (`Application/NavigationItems/Items/Default/Items/<id>`). An id containing `/` is used as the full
    item path.
  - Name the role **before** the call. It throws `ArgumentException` for an unnamed role.
- **The designer actions check role names.** New version, Validate, Publish and Dry run show only to users holding a
  role named `Administrators` or `WorkflowDesigners`. Use exactly those names.
- **Role names in workflows are role names.** A Task state's role, a transition's required role and a Notify
  recipient are all names of `PermissionPolicyRole` rows. The lookups read them through a non-secured object space, so
  users need no read permission on roles.
- The demo also gives its workflow users the template's `Default` role, for My Details and the per-user model store.
  The workflow permissions do not need it.

## 7. A subject class

Any `BaseObject` class becomes a workflow subject by implementing `IWorkflowSubject` (demo:
`XafStateless.Demo.Module/BusinessObjects/PurchaseRequest.cs`):

```csharp
[DefaultClassOptions]
public class PurchaseRequest : BaseObject, IWorkflowSubject
{
    public virtual string Title { get; set; } = "";
    [Precision(18, 2)] public virtual decimal Amount { get; set; }
    public virtual bool Ordered { get; set; }
    public virtual WorkflowInstance? Workflow { get; set; }
}
```

- `ID` comes from `BaseObject`. Subjects must be Guid-keyed.
- Add a DbSet for the class.
- `Workflow` is the link to the running instance. The runtime owns it and the guard refuses user changes. Its display
  text is the current state's caption, so list views can show and filter on it.
- On a subject's detail view, the module adds the **Start workflow** action (Published definitions whose target type is
  this class) and the **Transition** action (the manual transitions the user may fire now).
- The members that effects (Set field) and field locks can use are the public, persistent, writable, browsable scalar
  members: string, number, bool, DateTime, Guid and enum. `ID`, `Workflow`, the lock field, references and collections
  are excluded.

A designer then creates a definition with the subject's full class name as **Target Type Name**, and publishes it.

## 8. Timer (Wait states)

`WorkflowRuntime.RunDueTimers(Func<IObjectSpace> newCommandSpace)` fires every Wait state whose time is due. It returns
the number fired. Call it from any scheduler about once a minute.

`INonSecuredObjectSpaceFactory` is registered **scoped**, so create a DI scope per run. The demo's `BackgroundService`
(`XafStateless.Demo.Blazor.Server/WorkflowTimerService.cs`):

```csharp
public sealed class WorkflowTimerService(IServiceScopeFactory scopes, WorkflowRuntime runtime, ILogger<WorkflowTimerService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var factory = scope.ServiceProvider.GetRequiredService<INonSecuredObjectSpaceFactory>();
                var fired = runtime.RunDueTimers(() => factory.CreateNonSecuredObjectSpace<WorkflowInstance>());
                log.LogInformation("Workflow timer tick: {Fired} fired", fired);
            }
            catch (Exception ex) { log.LogError(ex, "Workflow timer tick failed"); }   // keep the host alive; the next tick retries
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
```

Register it with `services.AddHostedService<WorkflowTimerService>();`.

Any scheduler the host already runs works the same way. For example, a **Hangfire recurring job** (cron `* * * * *`)
whose job method opens a scope and calls `RunDueTimers` with an `INonSecuredObjectSpaceFactory` from that scope. Run one
scheduler, not both.

## 9. Notification delivery

A Notify effect writes a `WorkflowNotification` row (the outbox), in the same commit as the state change:
- `Role`: the recipient role name;
- `Body`: the template with `{Member}` placeholders filled in;
- `State` = `Pending`, plus `Attempts`, `LastError` and `CreatedUtc`.

The host delivers the rows. Read the `Pending` rows through a non-secured object space, deliver each one (resolve the
role to users or addresses however the host does that), then set `State` to `Sent`, or to `Failed` with `LastError`,
and increase `Attempts`. The demo's `LogNotificationDispatcher` (`XafStateless.Demo.Blazor.Server/`) only logs each row
and marks it Sent every 30 seconds. It has the same shape as the timer: a `BackgroundService` with a scope per tick.

```csharp
using var scope = scopes.CreateScope();
using var os = scope.ServiceProvider.GetRequiredService<INonSecuredObjectSpaceFactory>().CreateNonSecuredObjectSpace<WorkflowNotification>();
var pending = os.GetObjectsQuery<WorkflowNotification>().Where(n => n.State == NotificationState.Pending).OrderBy(n => n.CreatedUtc).ToList();
foreach (var n in pending)
{
    // deliver n.Body to the users of role n.Role here
    (n.State, n.Attempts) = (NotificationState.Sent, n.Attempts + 1);
}
os.CommitChanges();
```

## 10. Check it

Run the host, log on as a designer, and open **Workflow > Workflow Definition**. Create a definition for the subject
class and publish it: the diagram draws under the States and Transitions tabs. Then, as a participant, open a subject
and run **Start workflow**. `tests/XafStateless.E2E` walks this whole path against the demo.

## Limits

- **SQL Server only.** The filtered unique indexes use SQL Server `HasFilter` syntax, and the lost-race detection recognizes SQL
  Server's unique-key violations (`SqlException` 2601/2627).
- **Guid-keyed subjects only.** The instance stores `SubjectId` as a Guid (matching XAF `BaseObject`). A subject on
  `BaseObjectInt` is not supported.
- **The guards are SaveChanges interceptors.** EF Core `ExecuteUpdate`/`ExecuteDelete`, raw SQL, and anything else that
  skips `SaveChanges` bypasses them, including immutable published definitions, runtime-owned rows and field locks.
  Keep such writes away from workflow tables and locked subject fields.
- **Single app instance** for the timer and the notification dispatcher. They take no lease, so two nodes would both
  fire the same timers and send the same notifications.
- **`PermissionPolicyRole` roles and `IPermissionPolicyUser` users.** Role names come from `PermissionPolicyRole`, and
  the current user's roles from `IPermissionPolicyUser.Roles`.
- **Criteria members are top-level.** A transition's criteria and the lock and effect members use the subject's own
  members, not dotted paths.
