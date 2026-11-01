# Integrating XafStateless into a host application

This guide covers adding XafStateless to an existing XAF Blazor application on EF Core. Every step below is what the demo
(`demo/`) does, and the file that does it is named at each step. When in doubt, copy the demo.

The host needs:
- DevExpress XAF **26.1.4** (Blazor, EF Core, Security with `PermissionPolicyRole` roles);
- EF Core **10.0.10** on **SQL Server**;
- .NET 10.

## 1. References

| Host project | Package |
|---|---|
| The platform-agnostic module (the one holding the DbContext and business classes) | `XafStateless.Module` (brings `XafStateless.Engine`) |
| The Blazor server project | `XafStateless.Blazor` (brings the Module in) |

The packages are on the private GitHub Packages feed `https://nuget.pkg.github.com/MBrekhof/index.json` (add it to the
host's `nuget.config` with a token that can read packages). They accept DevExpress 26.1.4 and any later 26.1 patch, never
26.2. The Module references EF Core InMemory, Proxies and Relational 10.0.10 on purpose: DevExpress's EF Core package
only asks for 8.0.28 or higher, and these keep the host's whole EF Core family on 10.0.10. This repository's demo and
tests use `ProjectReference`s to the same projects.

**Schema.** The package ships no migration. The seven workflow tables come from XAF's schema update, so after adding the
package (or moving to a new version) run the host once with its database update switch (XAF's
`--updateDatabase` switch); outside a debugger a schema mismatch otherwise stops the app at startup.

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
modelBuilder.ConfigureXafStateless();      // unique indexes, key lengths, the notification snapshot column
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
DbContext (the demo and the trial host both do); a host that removed it would fail at schema creation. (Read in
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
  - treats a subject without a workflow (a new one, or one not started) as being in the **initial state** of every
    Published definition for its class: the fields those initial states lock (all of them, when a class has several
    definitions) cannot be set before the workflow starts: "'Stage' is locked until its workflow starts". A new subject
    may keep the value a new object of its class starts with (property initializers and `OnCreated` defaults); an
    existing one may not change it. A field locked only in a later state stays editable until then. Draft and Retired
    definitions lock nothing;
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

The diagram is drawn in the browser by the bundled [`beautiful-mermaid`](https://github.com/lukilabs/beautiful-mermaid)
renderer (`mermaid.min.js`, no CDN). Add both script tags to the host page **before** `blazor.server.js` (demo:
`XafStateless.Demo.Blazor.Server/Pages/_Host.cshtml`):

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
  next run recreates a deleted row. Revoke in the role itself. The one exception is a scoped subject (below): a scope
  narrows, so it replaces the wider rows the grant once wrote.
- Roles seeded before this change carry one combined instance permission (`SubjectType In (...)`). The next run adds one
  row per subject type next to it; both grant the same access, so the old row can stay or be deleted. A scoped grant
  takes its type out of that row.

- **`GrantDesigner(role)`** grants:
  - full CRUD on definitions, states, transitions and effects;
  - read on instances, tasks and notifications;
  - the Workflow navigation items (Definition, Task, Notification).
- **`GrantParticipant(role, params (Type subjectType, string navigationItemId)[] subjects)`** grants:
  - CRUD on each subject class, except writing its `Workflow` link;
  - read on the definition types;
  - read on tasks and notifications addressed to this role, on tasks and notifications addressed to the logged-on user
    (see "The requester" and "A user member" in step 9), and on instances of the granted subject types;
  - navigation to Workflow Task, Workflow Notification and each subject.

  About the arguments:
  - Each tuple is a subject class and its navigation item. An id without `/` is taken as an item in the **Default**
    navigation group (`Application/NavigationItems/Items/Default/Items/<id>`). An id containing `/` is used as the full
    item path.
  - Name the role **before** the call. It throws `ArgumentException` for an unnamed role.
- **Scoping a participant by the subject.** When the subject itself is readable only in part (records scoped by
  membership, say), scope the role's runtime rows the same way. Pass `ParticipantSubject` instead of the tuple, with a
  criteria on the subject's members:

  ```csharp
  WorkflowPermissions.GrantParticipant(r, new ParticipantSubject(typeof(Project), "Project_ListView",
      "[Members][[ID] = CurrentUserId()]"));
  ```

  The role then reads only the instances, tasks and notifications whose subject matches. The rows join to the subject
  through the instance's `SubjectId` (a free join; `T` is the subject's full type name):

  | Row | Without a scope | With scope S |
  | --- | --- | --- |
  | Task, notification to the role | `[Role] = 'r'` | `[Role] = 'r' And [Instance.SubjectType] = 'T' And [<T>][[ID] = [^.Instance.SubjectId] And S]` |
  | Notification to the requester or a user, task assigned to a user | `[UserId] = CurrentUserId()` | the same with `[UserId] = CurrentUserId()` in front |
  | Instance | `[SubjectType] = 'T'` | `[SubjectType] = 'T' And [<T>][[ID] = [^.SubjectId] And S]` |

  - Once a role has a scoped subject, its task and notification rows are per type: an unscoped subject of the same role
    gets `[Role] = 'r' And [Instance.SubjectType] = 'T'`, and later calls keep it that way.
  - The scope reads the subject's own members. It may not use `^.`, and it must parse; either throws `ArgumentException`
    before anything is written, as does one type passed with two different scopes.
  - A scoped grant removes the wider rows the grant methods wrote: the role-wide rows, the plain rows of the scoped type,
    and joined rows with another scope. A row an admin edited (another state set) keeps its other states but loses its
    Read Allow. A row with a Deny stays. Rows with any other criteria are never touched: narrow those yourself. The types
    a removed role-wide row served keep plain per-type rows.
  - Passing a type unscoped after it was scoped adds its plain rows again: the last call wins.
  - Scope the subject's own read permission with the same criteria. Deciding a task is authorized by reading its
    subject, so a user outside the scope cannot decide its tasks.
  - Hosts that seed their own participant roles can call the row halves alone: `GrantRoleRows(role, params (Type, string?
    scope)[])` (tasks and notifications to the role, instances) and `GrantRequesterRows(role, ...)` (notifications to the
    requester, or to a user member). `GrantRequesterRows` cannot tell which types a requester-only role served before, so pass every type whose
    requester notifications stay readable, unscoped ones with a null scope. `GrantAssigneeRows(role, ...)` adds read on
    the tasks assigned to the logged-on user through a user member (step 9), with the same arguments and the same rule:
    give it to every role whose users can be assigned, with every subject type they can be assigned on.
- **The designer actions check role names.** New version, Validate, Publish and Dry run show only to users holding a
  role named `Administrators` or `WorkflowDesigners`. Use exactly those names.
- **The designer validates as it edits.** The definition view validates what is on screen, saved or not, on every
  change: a state with a problem gets a red row and a Problem text in the States list and a red box in the diagram
  (problems of the definition as a whole are listed under the diagram), and
  Publish and Dry run stay disabled (their tooltip names the first problem) until it validates. Validate shows the
  result without saving. The service validates again on Publish and Dry run. Edits saved in a separate state or
  transition tab reach the definition view when it refreshes (its Refresh action).
- **Start and Transition need Write on the subject.** Reading a subject is not enough to start its workflow or to fire
  one of its manual transitions, even one without a required role: the runtime checks Write on the subject through the
  caller's security strategy (`IsGrantedExtensions.CanWrite`) and refuses with "changing the workflow of this
  PurchaseRequest requires write access to it". A caller that cannot read the subject at all is refused as well. Deciding a
  task is authorized by the task's role instead, or, for a task assigned to a user, by being that user (the caller's
  `UserId`; administrators included, no role counts). The timer runs as the system and is not checked.
- **Code that calls the runtime itself passes the security strategy.** The module's actions build their
  `WorkflowCaller` with `WorkflowCommand.Caller(app, objectSpace)`, which does this. A host that constructs a
  `WorkflowCaller` itself over a secured object space (a Web API endpoint, a job acting for a user) must pass the
  `IRequestSecurityStrategy` that secures it as `Security`; without it, Start and Fire are refused with the same "requires
  write access" message, because the Write check cannot run. A plain, non-secured object space needs no strategy.
  Pass the user's key as `UserId` too: Start records it as the requester (step 9). Without it the workflow has no
  requester, and requester notifications are skipped.
- **Role membership is read from the logged-on user** (`IPermissionPolicyUser.Roles`) and stays cached until the next
  logon, so a role granted or revoked meanwhile changes what the workflow actions allow only after the user logs on again.
- **Role names in workflows are role names.** A Task state's role, a transition's required role and a Notify
  recipient are all names of `PermissionPolicyRole` rows, except `@requester` (a Notify recipient) and `@Member` (a user
  member, a Task state's role or a Notify recipient; step 9); a role named like one of them makes a definition that uses
  it fail validation. The lookups read them through a non-secured object space, so
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
  text is the current state's caption, so list views can show and filter on it. Opening it shows the instance with the
  diagram of the version it runs on, its current state highlighted.
- On a subject's detail view, the module adds the **Start workflow** action (Published definitions whose target type is
  this class) and the **Transition** action (the manual transitions the user may fire now). Both are hidden from a user
  without Write on the subject.
- A subject cannot be deleted while its workflow is in a non-Final state (see the guards in step 3). Before its workflow
  starts, the fields its definitions lock in their initial state are locked (step 3); the detail view disables their
  editors too.
- A **user member** is a public, persistent reference to the application's users: a Guid-keyed class implementing XAF's
  `ISecurityUser` (`PermissionPolicyUser` and the template's `ApplicationUser` are), e.g.
  `public virtual ApplicationUser? Buyer { get; set; }`. A Task state's role or a Notify recipient can address it as
  `@Buyer` (step 9).
- The members that effects (Set field, Increment) can use are the public, persistent, writable, browsable
  scalar members: string, number, bool, DateTime, Guid and enum. `ID`, `Workflow`, the lock field, references and
  collections are excluded. Field locks take those plus references (CARD-2735): a locked `Project` refuses a save that
  changes its key (the navigation or the foreign key, shadow or declared), and its lookup editor is disabled. Not a
  one-to-one's inverse side, whose key sits on the other row. Increment counts on whole-number and decimal members only (null counts as 0; the step is the
  effect's Value, 1 when empty), so a Decision after it can limit attempts: `rework` increments `Attempts`, and the
  Decision's first branch `Attempts >= 3` leads to a Final state. A result outside the member's range refuses the
  command and commits nothing.

A designer then creates a definition, picks the subject class in **Target Type Name** (the list offers every persistent,
non-abstract class implementing `IWorkflowSubject`, by caption; the full class name is stored), and publishes it.

## 8. Timer (Wait states)

`WorkflowRuntime.RunDueTimers(Func<IObjectSpace> newCommandSpace)` fires every Wait state whose time is due. It returns
the number fired. Call it from any scheduler about once a minute.

A timer whose command is refused with an `IUserFriendlyException` (the host's save validation throws XAF's
`ValidationException` because the subject breaks a rule; an Increment past its range; an action's refusal) is skipped
and logged as a warning through `ILogger<WorkflowRuntime>` (`Workflow timer of instance {InstanceId} refused:
{Reason}`). Nothing of it is committed and it stays due, so each tick tries again and warns again until the record is
fixed. Other exceptions fail the tick: `RunDueTimers` throws an `AggregateException` after the other instances have run.

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
scheduler, not both. `docs/host-trial.md` shows this on a host with a command/handler job framework (the trial
host), where Hangfire's activator already gives each job run its own scope.

## 9. Notification delivery

A Notify effect writes a `WorkflowNotification` row (the outbox), in the same commit as the state change:
- the recipient, exactly one of:
  - `Role`: a role name, for a Notify addressed to a role;
  - `UserId` (with `UserName` as a display snapshot): a single user, for a Notify addressed to `@requester` or to a
    user member (`@Buyer`);
- `Body`: the Notify text as written, with its `{Member}` or `{Reference.Member}` placeholders **not** filled in
  (`IsTemplate` = true; since 0.4.0). The host fills it per recipient (below);
- `State` = `Pending`, plus `Attempts`, `LastError` and `CreatedUtc`.

**The requester.** The Target lookup of a Notify effect offers `@requester` first: the user who started the workflow.
Start records that user on the instance (`StartedById`, `StartedBy`) before the initial state runs, so a Notify in the
initial state reaches them too; later steps address the same user whoever moves the workflow (an approver, the timer).
The requester is fixed once the instance exists. A workflow started without a user (a `WorkflowCaller` without `UserId`)
has no requester: its requester notifications are skipped, and the result message says so. Participants read the
notifications addressed to them in the Workflow Notification list (`[UserId] = CurrentUserId()`), next to their role's.
Users must be Guid-keyed (XAF's `BaseObject` users are); another key type is refused at Start.

**A user member.** The Target lookup of a Notify effect, and a Task state's role lookup, also offer the subject's user
members as `@Member` (step 7). Each reads the user the member holds when the state is entered:
- A Notify to `@Buyer` goes to that user (`UserId`, `UserName`). An empty member, or an inactive user, skips the
  notification and the result message says so.
- A Task state with role `@Buyer` opens a task assigned to that user: `WorkflowTask.UserId` and `UserName` are set and
  `Role` is empty. Only that user decides it (by key; no role counts, administrators neither), and the Workflow Task list
  shows it to them next to their roles' tasks. An empty member, or an inactive user, **refuses** the command that would
  enter the state ("task 'review' is assigned to 'Buyer', which is empty or an inactive user: set it first"), and nothing
  is committed; a timer into it is skipped with a warning and stays due. The task keeps its user: changing the member
  while the task is open does not move it; the next task the workflow opens reads the new value. Users read their tasks
  through `[UserId] = CurrentUserId()` (`GrantParticipant` adds it; `GrantAssigneeRows` on its own, step 6).

A notification's recipient, `Body`, `IsTemplate`, its snapshot, `Instance` and `CreatedUtc` are fixed once written;
delivery may change `State`, `Attempts` and `LastError`.

**The snapshot** (CARD-2788, 0.5.0): when the runtime writes a notification it also stores each placeholder's value at
that moment, and the records it read, in the column `Snapshot` (`nvarchar(max) NULL`) of the notifications table. It is
an EF shadow property declared by `ConfigureXafStateless()` (step 3), not a member of `WorkflowNotification`: it holds
values a recipient may not be allowed to read, so no view, criteria, permission or Web API reaches it. **Schema change**
for a host upgrading from 0.4.x: add the column (nullable; existing rows stay `NULL` and keep rendering live). A host
whose model lacks it writes no snapshot.

The host delivers the rows. Read the `Pending` rows through a non-secured object space, deliver each one (resolve the
role, or the user by `UserId`, to addresses however the host does that; a user that no longer exists is a failed
delivery), then set `State` to `Sent`, or to `Failed` with `LastError`, and increase `Attempts`.

**Render the text per recipient** (CARD-2597): `WorkflowNotifications.Render(note, space, security)`, once for every
recipient, with a scope signed in as that recipient: its secured object space and its `ISecurityStrategyBase` (as
`IRequestSecurityStrategy`). The note may come from the dispatcher's non-secured space: only its subject's type and key
are read, and the subject is loaded in `space`. Each placeholder shows its value **at notify time** (a later change, such
as a rejection reason cleared on resubmit, does not rewrite the notice), and the permission checks run now, for that
recipient, on the records the placeholder read then (loaded again by key through a secured query):
- a member they may not read (member deny, or a reference to a type they may not read): `*******`
  (`WorkflowNotifications.Masked`);
- a reference to a record their criteria hide, or one deleted since: `*******`, with or without
  `PreFetchReferenceProperties()`;
- the whole subject unreadable, or not found: every placeholder `*******`;
- a reference empty at notify time: empty.

Checks judge the records as they are now: with `PreFetchReferenceProperties()`, a reference reassigned since to a record
the recipient may not read can mask the old value even if they could read that one (it never shows a hidden value). A
role notification is rendered once per user of the role, never once per row: a text rendered for one user may hold what
another may not read. `security: null` fills everything in a non-secured space (for a log, never for mail) and masks
everything in a secured one. A row written by 0.4.x (no snapshot) is filled from the subject as it is at render time; a
row written before 0.4.0 (`IsTemplate` false) holds the text filled then and is returned as stored. The demo's `LogNotificationDispatcher` (`XafStateless.Demo.Blazor.Server/`) logs each row with
`security: null` and marks it Sent every 30 seconds. It has the same shape as the timer: a `BackgroundService` with a
scope per tick.

```csharp
using var scope = scopes.CreateScope();
using var os = scope.ServiceProvider.GetRequiredService<INonSecuredObjectSpaceFactory>().CreateNonSecuredObjectSpace<WorkflowNotification>();
var pending = os.GetObjectsQuery<WorkflowNotification>().Where(n => n.State == NotificationState.Pending).OrderBy(n => n.CreatedUtc).ToList();
foreach (var n in pending)
{
    // for each user of role n.Role (or the user n.UserId): sign them in on a scope of their own, then
    //   var body = WorkflowNotifications.Render(n, securedSpace, security);
    // and deliver body to them
    (n.State, n.Attempts) = (NotificationState.Sent, n.Attempts + 1);
}
os.CommitChanges();
```

## 10. Actions (Run action effects)

A **Run action** effect runs code of the host when its state is entered: the way a workflow changes records other than
its subject (release a student's lesson slots and refund on cancel, archive the file on close). The host writes the
action and registers it; the designer picks it by name; the validator refuses a name the host has not registered.

```csharp
using XafStateless.Module.Services;

public sealed class ReleaseSlotsAction(ILogger<ReleaseSlotsAction> log) : IWorkflowAction
{
    public string Name => "release-slots";            // the Target the designer picks

    public void Run(WorkflowActionContext context)    // context: ObjectSpace, Subject, Argument
    {
        var student = (Student)context.Subject;       // its Workflow is the instance, already in the entered state
        foreach (var slot in context.ObjectSpace.GetObjectsQuery<LessonSlot>().Where(s => s.Student!.ID == student.ID && s.Date > DateTime.Today))
            slot.Student = null;                      // staged; committed with the state change
        log.LogInformation("{Argument}", context.Argument);   // the effect's Value, placeholders filled: "Released {Name}'s slots"
    }
}

// Startup.ConfigureServices, before or after AddXafStateless():
services.AddSingleton<IWorkflowAction, ReleaseSlotsAction>();
```

The demo registers one action, `log` (`XafStateless.Demo.Blazor.Server/Services/LogWorkflowAction.cs`), which only logs.

**Actions are trusted host code.** `Run` executes inside the workflow command, and `context.ObjectSpace` is the command's
own non-secured object space: what the action stages there commits with the state change and rolls back with it, in one
transaction, and is not checked against the caller's permissions (a reader who may decide a task can trigger an action
that writes rows they could not). Publishing a definition with a Run action delegates that capability to the designer.
The contract an action keeps, because nothing enforces it:

- It only stages changes. The runtime owns saving and the object space's lifetime: no `CommitChanges`, `Rollback`,
  `Refresh` or `Dispose` on the context's object space, no writes on another connection.
- It finishes before it returns: no `async void`, no background use of the context. Actions are singletons: thread-safe,
  keeping nothing from a command, no scoped constructor dependencies.
- It leaves the workflow's own rows alone: instances, tasks, notifications, the subject's `Workflow` link. It runs with
  the runtime's write permit, so the guards cannot tell its writes from the runtime's.
- It does not change the subject's locked members. The lock guard refuses that, unless a Set field or Increment of the
  same state writes the member too (their exemption covers the whole command). Change the subject with those effects.
- An exception aborts the command with nothing committed; an `IUserFriendlyException` (`UserFriendlyException`,
  `ValidationException`) reaches the user as the message, or in a timer is logged as a warning (step 8); other
  exceptions propagate (the timer collects them). A command is retried on a lost race, up to four attempts, so an
  action can run more than once while only the last attempt commits: external side effects (mail, a payment, a job on
  another connection) belong in an outbox row the host delivers, like notifications (step 9), never in the action.

`WorkflowActionCatalog` (registered by `AddXafStateless`) collects the actions; two with the same name, or one with an
empty name, fail when the catalog is first resolved (the first command, validation or designer lookup). A host that wants
that at startup resolves the catalog there. A definition whose action a later host version no longer registers fails at
runtime with "workflow action '…' is not registered in this application", committing nothing.

## 11. Entry screens

Many applications start work not from a subject's list but from a page of its own: a lookup, a few checkboxes, a list to
pick from, and a Start button that leads to the next screen. In XAF that page is a **non-persistent object's DetailView
opened from a navigation item** (DevExpress docs 113471); this module adds the small parts that make it a workflow entry
screen (XSM-033). The demo's `StartRequestsScreen` (`XafStateless.Demo.Module/BusinessObjects/`) and
`StartRequestsController` (`.../Controllers/`) are the copy source.

1. **The screen class**: `[DomainComponent]`, implementing `IEntryScreen` (a marker from `XafStateless.Module.Controllers`),
   `IObjectSpaceLink` and `INotifyPropertyChanged`, with a hidden Guid `[Key]`. Bool properties are checkboxes, enums are
   dropdowns, a string with `[EditorAlias(StringLookup.Single)]` plus `IStringLookupSource` is a lookup (the demo's
   `Workflow` lists the published definitions for the subject type), and an `IList<Subject>` property backed by a
   `BindingList<Subject>` is a nested list the user selects rows in. Load the list in one `Reload()` method, called when
   the object space is linked, when a filter checkbox changes (`[ImmediatePostData]`), after Start and on Refresh: refresh
   the additional object spaces, requery through the screen's object space (`GetObjects<Subject>(criteria)`), then refill
   the same BindingList so the nested list sees list-changed events.
2. **The navigation item**: in the module's `Model.DesignedDiffs.xafml`, an item with `View="<Screen>_DetailView"` and no
   ObjectKey. `EntryScreenController` (this module) then creates a fresh screen whenever the view opens (also on a deep link
   in a new circuit), marks it unmodified so leaving never asks to save, and hides Save. Set the nested list's
   `DataAccessMode="Client"` in the same file. Grant the role Navigate on the item (the demo's Updater does it next to
   `GrantParticipant`).
3. **The host's Startup**: the non-persistent object space must reach persistent objects (docs 116106). After
   `.AddNonPersistent()`:
   ```csharp
   builder.ObjectSpaceProviders.Events.OnObjectSpaceCreated = context => {
       if (context.ObjectSpace is CompositeObjectSpace { Owner: not CompositeObjectSpace } composite)
           composite.PopulateAdditionalObjectSpaces(
               context.ServiceProvider.GetRequiredService<IObjectSpaceProviderService>(),
               context.ServiceProvider.GetRequiredService<IObjectSpaceCustomizerService>());
   };
   ```
   The additional space is the user's secured EF Core space, so the screen's lookups and lists obey the user's permissions.
4. **The Start action** (a controller on the screen's DetailView): read the selected rows from the nested list's editor
   (`View.GetItems<ListPropertyEditor>()` → `ListView.SelectedObjects`) and capture their ids before anything reloads; then
   for each run `WorkflowRuntime.Start(caller, typeof(Subject), id, definition)`, one command per subject. **Build the caller
   with the subject type: `WorkflowCommand.Caller(Application, View.ObjectSpace, typeof(Subject))`.** The two-argument
   `Caller` takes the view's object space as the user's secured space; a screen's space is non-persistent, and the runtime's
   Write check only knows secured spaces, so with it a user with Read only could start workflows. The three-argument form
   resolves the persistent space behind the non-persistent one (by the space's own known types, not `CanInstantiate`, which
   a composite answers through its additional spaces) and refuses an unsecured one when the application has security.
   Report both halves ("Started 2 of 3." plus the first refusal), reload, and open the next screen through
   `e.ShowViewParameters.CreatedView` with the subject loaded by id into a new object space, in the screen's own tab
   (`TargetWindow.Current`): a new tab is skipped when the subject is already open in another tab, and that stale tab
   would show it without its workflow.

## 12. Check it

Run the host, log on as a designer, and open **Workflow > Workflow Definition**. Create a definition for the subject
class and publish it: the diagram draws under the States and Transitions tabs. Then, as a participant, open a subject
and run **Start workflow**. `tests/XafStateless.E2E` walks this whole path against the demo.

## Limits

- **SQL Server only.** The filtered unique indexes use SQL Server `HasFilter` syntax, and the lost-race detection recognizes SQL
  Server's unique-key violations (`SqlException` 2601/2627).
- **Guid-keyed subjects and users only.** The instance stores `SubjectId` as a Guid (matching XAF `BaseObject`). A
  subject on `BaseObjectInt` is not supported, and neither is a user key other than Guid (the requester is stored by id).
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
