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

Namespaces: the entities are in `XafStateless.Module.BusinessObjects`, the two guards in `XafStateless.Module.Services`,
and `ConfigureXafStateless()` is an extension method in `XafStateless.Module`:

```csharp
using XafStateless.Module;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Services;
```

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
modelBuilder.UseDeferredDeletion(this);    // REQUIRED (maps GCRecord)
modelBuilder.UseOptimisticLock();          // REQUIRED
modelBuilder.ConfigureXafStateless();      // unique indexes, key lengths
```

**`UseOptimisticLock()` is required.** The module has no concurrency tokens of its own. Every workflow command touches
the subject, the instance and (for Decide) the task, so XAF's optimistic lock makes two racing commands, or a user save
racing a transition, collide. Exactly one wins, and the loser is retried. Without the lock, the last writer wins silently.
New XAF apps have `UseOptimisticLock()` in the generated DbContext; check that yours still does.

`ConfigureXafStateless()` enforces "at most one Draft and one Published per name" and "one instance per subject" with
filtered unique indexes. The filters use SQL Server syntax and skip soft-deleted rows (`[GCRecord] = 0`), so a deleted
draft does not block its replacement.

**`UseDeferredDeletion(this)` is required too.** `BaseObject.GCRecord` is `[NotMapped]` until deferred deletion maps
it, and both the index filters and `DefinitionGuard` read that column. New XAF apps have the call in the generated
DbContext (the demo and the BPGDemo skeleton both do); a host that removed it would fail at schema creation. (Read in
code; not tried on a host without it.)

### The two guards

Add both interceptors in `OnConfiguring`, so they run for every context, whoever creates it (the XAF provider, the
database updater, the non-secured command spaces, tests):

```csharp
#pragma warning disable XAF0031 // proxies come from UseConnectionString in the host's WithDbContext; this only adds interceptors
protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) {
    var registered = optionsBuilder.Options.FindExtension<CoreOptionsExtension>()?.Interceptors;
    foreach(var guard in new IInterceptor[] { DefinitionGuard.Instance, StateLockGuard.Instance })
        if(registered?.Contains(guard) != true)
            optionsBuilder.AddInterceptors(guard);
}
#pragma warning restore XAF0031
```

Without the pragma the DevExpress analyzer reports **XAF0031** ("Add the UseChangeTrackingProxies() call") on this
override, because it configures a `DbContextOptionsBuilder` without calling `UseChangeTrackingProxies()`. Here it is a
false positive: XAF's `UseConnectionString` in the host's `WithDbContext` already enables the change-tracking proxies
(DevExpress: "Change Tracking in EF Core DbContext"), and the override only adds interceptors. If your host configures
its provider with `UseSqlServer` instead of `UseConnectionString`, it needs `UseChangeTrackingProxies()` there anyway,
workflow or not.

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
  - refuses any change to the subject's `Workflow` link;
  - refuses deleting a subject (hard, or soft through `GCRecord`) while its workflow is in a non-Final state, for every
    role, Administrators too: "'Chair' cannot be deleted while its workflow is in 'Approval'". A subject whose workflow
    reached a Final state, or that has none, deletes normally. Otherwise the instance and its tasks would be orphaned,
    and a subject deleted in a Wait state would fail the timer on every tick.

## 4. Services

In `ConfigureServices` (demo: `Startup.cs`, where it comes before `AddXaf`):

```csharp
using XafStateless.Module;   // AddXafStateless is an extension method in this namespace
// ...
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

A `ModuleUpdater` runs on every version bump and every `--forceUpdate`. Get or create the role, then call the grant on
every run, the same way the demo's `GrantRole` does. The grant methods are safe to repeat: they add only what is missing.

```csharp
public override void UpdateDatabaseAfterUpdateSchema() {
    base.UpdateDatabaseAfterUpdateSchema();
    GrantRole("Approvers", r => WorkflowPermissions.GrantParticipant(r, (typeof(PurchaseRequest), "PurchaseRequest_ListView")));
    GrantRole("WorkflowDesigners", WorkflowPermissions.GrantDesigner);
    ObjectSpace.CommitChanges();
}

PermissionPolicyRole GrantRole(string name, Action<PermissionPolicyRole> grant) {
    var role = ObjectSpace.FirstOrDefault<PermissionPolicyRole>(r => r.Name == name);
    if(role == null) {
        role = ObjectSpace.CreateObject<PermissionPolicyRole>();
        role.Name = name;          // name the role first: the participant's task scope uses the name
    }
    grant(role);                   // every run: adds only what is missing
    return role;
}
```

**What repeating the grants does and does not do:**
- It adds a missing permission, and fills an operation state that is still unset (a navigation item the role already
  has a row for is left as it is, even with no state). So a host that already had an
  `Approvers` role gets the workflow grants on the next update, and a subject added to `GrantParticipant` later adds its
  own rows.
- It never changes an explicit Allow or Deny already on a matching row. Rows are matched by type, by member, and for
  object permissions by the exact criteria text: a state an admin set on a seeded row survives, but if the admin edits a
  seeded row's criteria, the next run adds the original row again next to it. To narrow a seeded grant, set a Deny
  rather than rewriting its criteria. (The `Workflow` link stays protected by
  the `StateLockGuard` whatever the permission says.)
- It never revokes. Removing a subject from the arguments, or deleting a seeded row, does not take the access away; the
  next run recreates a deleted row. Revoke in the role itself.
- Roles seeded before this change carry one combined instance permission (`SubjectType In (...)`). The next run adds one
  row per subject type next to it; both grant the same access, so the old row can stay or be deleted.

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
- **Start and Transition need Write on the subject.** Reading a subject is not enough to start its workflow or to fire
  one of its manual transitions, even one without a required role: the runtime checks Write on the subject through the
  caller's security strategy (`IsGrantedExtensions.CanWrite`) and refuses with "changing the workflow of this
  PurchaseRequest requires write access to it". A caller that cannot read the subject at all is refused as well. Deciding a
  task is authorized by the task's role instead. The timer runs as the system and is not checked.
- **Code that calls the runtime itself passes the security strategy.** The module's actions build their
  `WorkflowCaller` with `WorkflowCommand.Caller(app, objectSpace)`, which does this. A host that constructs a
  `WorkflowCaller` itself over a secured object space (a Web API endpoint, a job acting for a user) must pass the
  `IRequestSecurityStrategy` that secures it as `Security`; without it, Start and Fire are refused with the same "requires
  write access" message, because the Write check cannot run. A plain, non-secured object space needs no strategy.
- **Role membership is read from the logged-on user** (`IPermissionPolicyUser.Roles`) and stays cached until the next
  logon, so a role granted or revoked meanwhile changes what the workflow actions allow only after the user logs on again.
- **Role names in workflows are role names.** A Task state's role, a transition's required role and a Notify
  recipient are all names of `PermissionPolicyRole` rows. The lookups read them through a non-secured object space, so
  users need no read permission on roles.
- The demo also gives its workflow users the template's `Default` role, for My Details and the per-user model store.
  The workflow permissions do not need it.

## 7. A subject class

Any `BaseObject` class becomes a workflow subject by implementing `IWorkflowSubject` (demo:
`XafStateless.Demo.Module/BusinessObjects/PurchaseRequest.cs`). `IWorkflowSubject` and `WorkflowInstance` are in
`XafStateless.Module.BusinessObjects`:

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
- `WorkflowInstance?` needs a nullable context. A host project without `<Nullable>enable</Nullable>` (the XAF template
  default) gets warning CS8632 on it: put `#nullable enable` at the top of the subject's file, or drop the `?`.
- **Web API (optional).** A host that exposes the subject through the XAF Web API registers it like any business
  object, in `AddXafWebApi` → `ConfigureOptions`: `options.BusinessObject<PurchaseRequest>();`. The guards apply to
  API writes too: changing a locked field or deleting a subject whose workflow is in a non-Final state returns 400
  with the guard's message.
- `Workflow` is the link to the running instance. The runtime owns it and the guard refuses user changes. Its display
  text is the current state's caption, so list views can show and filter on it.
- On a subject's detail view, the module adds the **Start workflow** action (Published definitions whose target type is
  this class) and the **Transition** action (the manual transitions the user may fire now). Both are hidden from a user
  without Write on the subject.
- A subject cannot be deleted while its workflow is in a non-Final state (see the guards in step 3).
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
scheduler, not both. `docs/host-trial.md` shows this on a host with a command/handler job framework (the BPGDemo
skeleton), where Hangfire's activator already gives each job run its own scope.

## 9. Notification delivery

A Notify effect writes a `WorkflowNotification` row (the outbox), in the same commit as the state change:
- `Role`: the recipient role name;
- `Body`: the template with `{Member}` placeholders filled in. A placeholder reads any member of the subject, whatever the
  recipient role's member-level Read permissions: designers are trusted not to put hidden members into a notification;
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
  Keep such writes away from workflow tables and locked subject fields. The same holds for a database-level
  `ON DELETE CASCADE`: a subject deleted by the database as part of deleting its parent, without being loaded into the
  context, is not seen by the delete guard.
- **Single app instance** for the notification dispatcher. It takes no lease, so two nodes would both send the same
  notifications. The timer is safe on several nodes: each timer command Touches the instance, so the second node finds the
  timer no longer due, or loses the race and skips it.
- **Lock value comparisons happen in column form.** The lock guard converts a locked member's value with the member's
  mapped EF converter (value converters and enums alike) and compares it with the raw database column using the provider
  value comparer, so converted members are compared correctly. Where the ADO reader returns a different CLR type than the
  converter's provider type, the two compare unequal: the save is then refused, never allowed.
- **`PermissionPolicyRole` roles and `IPermissionPolicyUser` users.** Role names come from `PermissionPolicyRole`, and
  the current user's roles from `IPermissionPolicyUser.Roles`.
- **Criteria members are top-level.** A transition's criteria and the lock and effect members use the subject's own
  members, not dotted paths.
