# BPGDemo Trial Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prove XafStateless drops into the BPGDemo skeleton by following `docs/integrating.md` literally, and write down every touch point and friction in `docs/host-trial.md`.

**Architecture:** The trial lives on a local, unpushed branch `trial/xafstateless` of `C:\Projects\BPGDemo`, checked out in a **separate git worktree** (`C:\Projects\BPGDemo.trial`). BPG copies `C:\Projects\BPGDemo` as its skeleton (`BPG.Api/appsettings.json` → `XAF:SkeletonProjectPath`, read in `SpecGenerationService.cs:171`), so the main working tree must stay on `master` for the whole trial. The module comes in by `ProjectReference` to this repo. The timer and the notification dispatcher run as Hangfire jobs through the skeleton's own job pattern (command + `IJobHandler<T>` + dispatch switches + metadata + seeded `JobDefinition`), not as `BackgroundService`s.

**Tech Stack:** DevExpress XAF 26.1.4 (Blazor, EF Core), EF Core 10.0.10 on SQL Server LocalDB, Hangfire 1.8 (SQL Server storage), .NET 10.

**Spec:** `docs/superpowers/plans/2026-09-25-xafstateless.md` Task 10 (this plan replaces its steps) and `docs/integrating.md` (the host contract under test).

## Global Constraints

- The BPGDemo branch stays **local and unpushed**; merging or pushing is the user's decision after the report.
- `C:\Projects\BPGDemo` (main worktree) stays on `master`. Baseline at plan time: HEAD `83f3a98`, one pre-existing untracked file `bpgdemo/bpgdemo.Module/.xaflogicexplainer` (leave it alone). After every task, `git -C /c/Projects/BPGDemo status --short` must equal that baseline and the branch must be `master`.
- Execute from a session started in `C:\Projects\BPGDemo` (BPG CLAUDE.md: more than a one-line change in BPGDemo needs a session there). The report and doc fixes are committed in `C:\Projects\XafStateless`.
- Follow `docs/integrating.md` literally. A missing or wrong step is a **doc bug**: fix it in `docs/integrating.md` and list it in the report. Known one already (Codex, verified): §3/§4 omit `using XafStateless.Module;`, the namespace of `ConfigureXafStateless` and `AddXafStateless`.
- Do not change XafStateless module code during the trial. A module defect becomes a new XSM card, recorded in the report.
- **Every run of the exe passes `--environment Development`.** Launch profiles do not apply to the exe, and without it the host reads `appsettings.json` and updates the skeleton's own `bpgdemo` catalog. The trial catalog is `bpgdemoTrial`. Before the first `--updateDatabase`, confirm the effective catalog (Task 1 Step 3).
- **Build before every database update or launch** (`dotnet build C:\Projects\BPGDemo.trial\bpgdemo.slnx`, unpiped, "Build succeeded" and exit 0). The exe runs whatever binaries exist.
- The exe, from PowerShell: `cd C:\Projects\BPGDemo.trial\bpgdemo\bpgdemo.Blazor.Server\bin\Debug\net10.0` then `.\bpgdemo.Blazor.Server.exe --environment Development [--updateDatabase --silent]`. Port check before a launch, HTTP health check after, kill it when done.
- Card references: XSM-015 (Task 1), XSM-016 (Task 2), XSM-017 (Task 3), XSM-010 (Task 4, report). Commit messages cite the card.

## Review Focus

1. **Workflow Set-field effects skip BPG's automation rules and webhooks.** The runtime writes subjects through `INonSecuredObjectSpaceFactory` (`WorkflowCommand.cs:29`); the skeleton only announces `IObjectSpaceFactory` spaces to `ObjectSpaceHooks` (`HookedObjectSpaceFactory.cs:32`), and generated rules subscribe to those two paths (`XafAutomationRuleEmitter.cs:71`). A generated app would expect a workflow-set field to fire its rules. Task 2 Step 7 checks it with a positive control; it is not fixed in the trial.
2. **An existing host database gets the workflow tables and keeps its data.** Task 1 builds the untouched skeleton first, seeds it, marks a row, then adds the module and updates.
3. **The Hangfire job runs in the job's authenticated scope.** `XafJobScopeInitializer` signs in user `HangfireJob` (role `BackgroundJobs`) and only warns if that fails; the timer itself uses `WorkflowCaller.System` over a non-secured space, so success alone proves nothing about identity. Task 3 logs the scope's identity from inside the handler.
4. **Web API writes hit the same guards.** Changing a locked field or deleting an in-flight subject through OData must be refused just as in the UI, after a positive control shows the same token can read and write an unlocked request. Task 2 Step 6.
5. **Scheduled jobs actually run and record.** The skeleton's scheduler silently ignores command names missing from its switches, and its recorder links runs to a `JobDefinition` by **Name = command type name** (`JobExecutor.cs:17`, `XafJobExecutionRecorder.cs:33`). Task 3 registers both switches and the metadata, seeds names equal to the command names, and checks `LastRunStatus`.

---

### Task 1 (XSM-015): Worktree, baseline database, references, modules, DbContext, services, host page

**Files (in `C:\Projects\BPGDemo.trial\bpgdemo\`):**
- Modify: `bpgdemo.Blazor.Server/appsettings.Development.json`
- Modify: `bpgdemo.Module/bpgdemo.Module.csproj`, `bpgdemo.Module/Module.cs`, `bpgdemo.Module/BusinessObjects/bpgdemoDbContext.cs`
- Modify: `bpgdemo.Blazor.Server/bpgdemo.Blazor.Server.csproj`, `bpgdemo.Blazor.Server/BlazorModule.cs`, `bpgdemo.Blazor.Server/Startup.cs`, `bpgdemo.Blazor.Server/Pages/_Host.cshtml`

**Interfaces:**
- Produces: a host that builds, starts, and has the seven workflow tables and their filtered unique indexes in `bpgdemoTrial`, with the pre-existing rows intact.

- [ ] **Step 1: Record the baseline and create the worktree.**

```bash
git -C /c/Projects/BPGDemo status --short; git -C /c/Projects/BPGDemo log -1 --format=%h; git -C /c/Projects/XafStateless log -1 --format=%h; git -C /c/Projects/BPG log -1 --format=%h
git -C /c/Projects/BPGDemo worktree add -b trial/xafstateless /c/Projects/BPGDemo.trial master
```

Keep all four outputs for the report.

- [ ] **Step 2: Point Development at the trial catalog.** In `bpgdemo.Blazor.Server/appsettings.Development.json`, merge into the root object:

```json
"ConnectionStrings": {
  "ConnectionString": "Data Source=(localdb)\\mssqllocaldb;Integrated Security=SSPI;MultipleActiveResultSets=True;Initial Catalog=bpgdemoTrial"
}
```

- [ ] **Step 3: Baseline database on the untouched skeleton (Review Focus 2).** Build the solution, then run the exe with `--environment Development --updateDatabase --silent`. Confirm it went to the trial catalog and mark a row:

```bash
sqlcmd -S "(localdb)\mssqllocaldb" -Q "SELECT name FROM sys.databases WHERE name IN ('bpgdemo','bpgdemoTrial')"
sqlcmd -S "(localdb)\mssqllocaldb" -d bpgdemoTrial -Q "SELECT COUNT(*) FROM PermissionPolicyUser; UPDATE PermissionPolicyRoleBase SET Name = Name WHERE Name = 'Default'; SELECT Name FROM PermissionPolicyRoleBase ORDER BY Name"
```

Expected: `bpgdemoTrial` exists; users and roles are seeded. Note the user count and role list. Also create one `JobDefinition` by hand in SQL or the UI, named `trial-baseline-marker`, so there is a distinctive row to find later. If `bpgdemo`'s modification date changed, stop: the environment switch did not take.

- [ ] **Step 4: References (integrating.md §1).**

```xml
<!-- bpgdemo.Module.csproj -->
<ProjectReference Include="..\..\..\XafStateless\src\XafStateless.Module\XafStateless.Module.csproj" />
<!-- bpgdemo.Blazor.Server.csproj -->
<ProjectReference Include="..\..\..\XafStateless\src\XafStateless.Blazor\XafStateless.Blazor.csproj" />
```

Build unpiped. Record every package downgrade/conflict warning (the module pins `Microsoft.Data.SqlClient 6.1.2`; the skeleton floats Hangfire `1.8.*`, MailKit `4.*`) and the resolved versions (`dotnet list C:\Projects\BPGDemo.trial\bpgdemo.slnx package --include-transitive` filtered to SqlClient, EF Core, Hangfire, DevExpress.ExpressApp).

- [ ] **Step 5: Modules (§2).** `Module.cs`, next to the XafLayoutBuilder line: `RequiredModuleTypes.Add(typeof(XafStateless.Module.XafStatelessModule));`. `BlazorModule.cs`, next to its XafLayoutBuilder line: `RequiredModuleTypes.Add(typeof(XafStateless.Blazor.XafStatelessBlazorModule));`.

- [ ] **Step 6: DbContext (§3).** Add `using XafStateless.Module;`, `using XafStateless.Module.BusinessObjects;`, `using XafStateless.Module.Services;`, `using Microsoft.EntityFrameworkCore.Diagnostics;`, `using Microsoft.EntityFrameworkCore.Infrastructure;`. Add the seven DbSets from §3. `UseOptimisticLock()` is already there; add `modelBuilder.ConfigureXafStateless();` after it. Add the `OnConfiguring` override exactly as in §3.

- [ ] **Step 7: Services (§4) and host page (§5).** `Startup.cs`: `using XafStateless.Module;` and, before `services.AddXaf(...)`, `services.AddXafStateless();`. `_Host.cshtml`: the two script tags from §5, directly before line 36 (`blazor.server.js`).

- [ ] **Step 8: Update the existing catalog and verify.** Build, then `--environment Development --updateDatabase --silent`. Then:

```bash
sqlcmd -S "(localdb)\mssqllocaldb" -d bpgdemoTrial -Q "SELECT name FROM sys.tables WHERE name LIKE 'Workflow%' ORDER BY name; SELECT t.name, i.name, i.filter_definition FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id WHERE t.name LIKE 'Workflow%' AND i.is_unique = 1 AND i.has_filter = 1; SELECT COUNT(*) FROM PermissionPolicyUser; SELECT Name FROM JobDefinitions WHERE Name = 'trial-baseline-marker'"
```

Expected: 7 workflow tables; the filtered unique indexes `ModelBuilderExtensions.ConfigureXafStateless` declares (compare with `src/XafStateless.Module/ModelBuilderExtensions.cs`), each filter containing `[GCRecord] = 0`; user count unchanged; the marker row present. (Adjust the `JobDefinitions` table name to whatever `sys.tables` shows.)

- [ ] **Step 9: Fresh-catalog check.** Drop `bpgdemoTrial`, run the update once more, and rerun the table/index query. Expected: the same 7 tables and indexes. Then re-run the update a second time on the populated catalog; expected exit 0.

- [ ] **Step 10: Start the app** (port check, `--environment Development`, health check), log on as `Admin`, confirm **Workflow > Workflow Definition** is in the navigation. Stop the app; confirm the process is gone.

- [ ] **Step 11: Commit (trial branch, local only).** Stage exactly the eight files listed under **Files**; check the worktree has nothing else modified.

```bash
git -C /c/Projects/BPGDemo.trial status --short
git -C /c/Projects/BPGDemo.trial commit -m "Trial: add XafStateless module (XSM-015)"
git -C /c/Projects/BPGDemo status --short   # must equal the baseline
```

### Task 2 (XSM-016): Subject class, roles, Web API, walk the flow, hook check

**Files (trial worktree):**
- Create: `bpgdemo.Module/BusinessObjects/ChangeRequest.cs`
- Modify: `bpgdemo.Module/BusinessObjects/bpgdemoDbContext.cs` (DbSet), `bpgdemo.Module/DatabaseUpdate/Updater.cs` (roles + two users), `bpgdemo.Blazor.Server/Startup.cs` (Web API registration)

**Interfaces:**
- Consumes: Task 1's host.
- Produces: subject type `bpgdemo.Module.BusinessObjects.ChangeRequest`, roles `Approvers` and `WorkflowDesigners`, users `designer` and `approver`.

The skeleton has no business class of its own (generated apps add them), so the trial adds one.

- [ ] **Step 1: The subject (§7).** Check `IWorkflowSubject`'s and `WorkflowInstance`'s namespaces in `src/XafStateless.Module` first; if §7 leaves them unstated, that is a doc bug.

```csharp
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;
using XafStateless.Module.BusinessObjects;

namespace bpgdemo.Module.BusinessObjects
{
    [DefaultClassOptions]
    public class ChangeRequest : BaseObject, IWorkflowSubject
    {
        public virtual string Title { get; set; } = "";
        public virtual string Description { get; set; } = "";
        public virtual bool Implemented { get; set; }
        public virtual WorkflowInstance? Workflow { get; set; }
    }
}
```

Plus `public DbSet<ChangeRequest> ChangeRequests { get; set; }` in the DbContext.

- [ ] **Step 2: Roles and users (§6).** In `Updater.UpdateDatabaseAfterUpdateSchema`, with `using XafStateless.Module.Services;` and the §6 `CreateRole` helper verbatim:

```csharp
var approvers = CreateRole("Approvers", r => WorkflowPermissions.GrantParticipant(r, (typeof(ChangeRequest), "ChangeRequest_ListView")));
var designers = CreateRole("WorkflowDesigners", WorkflowPermissions.GrantDesigner);
```

Add users `designer` (Default + WorkflowDesigners) and `approver` (Default + Approvers) with empty passwords, inside the existing `#if !RELEASE` user block, same pattern as the skeleton's `User`.

- [ ] **Step 3: Web API registration.** The skeleton's registration block is empty (`Startup.cs:61-64`); endpoints exist only for registered types (DevExpress docs, "Web API Service: Add Business Objects"). Add `options.BusinessObject<bpgdemo.Module.BusinessObjects.ChangeRequest>();` there. This is a touch point integrating.md does not mention: add it to §7 as an optional step.

- [ ] **Step 4: Build, update, verify seed.** Build, `--updateDatabase --silent` with `--environment Development`, then:

```bash
sqlcmd -S "(localdb)\mssqllocaldb" -d bpgdemoTrial -Q "SELECT UserName FROM PermissionPolicyUser WHERE UserName IN ('designer','approver'); SELECT Name FROM PermissionPolicyRoleBase WHERE Name IN ('Approvers','WorkflowDesigners')"
```

Expected: 2 + 2 rows. Start the app.

- [ ] **Step 5: Walk the flow in the browser** (webapp-testing skill; screenshots to the scratchpad). The Playwright MCP writes a `.playwright-mcp/` folder into the session's working directory, which is `C:\Projects\BPGDemo` and breaks the baseline: use a C# Playwright scratch project in the scratchpad, or the Chrome extension, instead:
  1. As `designer`: create definition `ChangeFlow`, Target Type Name `bpgdemo.Module.BusinessObjects.ChangeRequest`, states `Draft` (Initial, Manual) → `Review` (Task, role `Approvers`, outcomes approve/reject) → `Done` (Final, effect Set field `Implemented = true`) and `Rejected` (Final); lock `Description` in `Review`. Validate, Publish. Screenshot the diagram.
  2. As `approver`: create a ChangeRequest, Start workflow, fire Draft → Review. Try to edit `Description`: expected refusal. Screenshot.
  3. As `approver`: open the task, Approve. Expected: state `Done`, `Implemented` = true. Screenshot.

- [ ] **Step 6: Web API checks (Review Focus 4).** Create two more requests as `approver`: A (not started) and B (started, in `Review`). Get a token, then run a positive control before the guard checks:

```bash
BASE=http://localhost:<port>
TOKEN=$(curl -s -X POST "$BASE/api/Authentication/Authenticate" -H "Content-Type: application/json" -d '{"userName":"approver","password":""}')
curl -s "$BASE/api/odata/ChangeRequest" -H "Authorization: Bearer $TOKEN" -w "\n%{http_code}\n"
curl -s -X PATCH "$BASE/api/odata/ChangeRequest(<A-id>)" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{"Description":"api edit ok"}' -w "\n%{http_code}\n"
curl -s -X PATCH "$BASE/api/odata/ChangeRequest(<B-id>)" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{"Description":"changed"}' -w "\n%{http_code}\n"
curl -s -X DELETE "$BASE/api/odata/ChangeRequest(<B-id>)" -H "Authorization: Bearer $TOKEN" -w "\n%{http_code}\n"
```

Expected: GET 200, PATCH A 2xx; PATCH B and DELETE B refused (non-2xx) with the guard's message; B unchanged in the database (`sqlcmd` query on `ChangeRequests`). Check the exact body shape of the authenticate call in `API/Security/AuthenticationController.cs` first.

- [ ] **Step 7: Automation-hook check (Review Focus 1).** Scratch code, not committed: in the Blazor server, subscribe to `ObjectSpaceHooks.Created` **and** `XafApplication.ObjectSpaceCreated`, and on each object space's `Committing` log the object-space kind plus, for every modified `ChangeRequest`, its ID and the members EF reports as modified. Then:
  1. Positive control: edit request A's `Title` in the UI. Expected: a log line with A's ID and `Title`.
  2. Run request C through to `Done` (the approve that sets `Implemented`). Expected per the code today: **no** Committing log line with C's ID and `Implemented`, although the database shows `Implemented = 1`.

  Remove the scratch code and confirm `git status` in the worktree shows only the Task 2 files. The report states the hook finding as observed; what generated rules and webhooks would do stays labeled **unverified** (no real generated rule was exercised).

- [ ] **Step 8: Stop the app. Commit (trial branch)** the four files; confirm the main BPGDemo tree equals the baseline.

### Task 3 (XSM-017): Timer and notification dispatcher as Hangfire jobs

**Files (trial worktree):**
- Create: `bpgdemo.Jobs/Commands/RunWorkflowTimersCommand.cs`, `bpgdemo.Jobs/Commands/DispatchWorkflowNotificationsCommand.cs`
- Create: `bpgdemo.Blazor.Server/Handlers/RunWorkflowTimersHandler.cs`, `bpgdemo.Blazor.Server/Handlers/DispatchWorkflowNotificationsHandler.cs`
- Modify: `bpgdemo.Jobs/JobDispatchService.cs` (both switches: `DispatchByNameAsync` line 19, `ScheduleByName` line 74), `bpgdemo.Jobs/CommandMetadataProvider.cs` (the `CommandTypes` dictionary)
- Modify: `bpgdemo.Blazor.Server/Startup.cs` (two `AddJobHandler` lines after line 321), `bpgdemo.Module/DatabaseUpdate/Updater.cs` (two JobDefinitions), `bpgdemo.Blazor.Server/appsettings.Development.json` (`Jobs:UseHangfire` = true)

**Interfaces:**
- Consumes: `WorkflowRuntime.RunDueTimers(Func<IObjectSpace>) : int` (§8), `WorkflowNotification` / `NotificationState` (§9). Verify both namespaces in `src/XafStateless.Module` before building; one §8/§9 gets wrong is a doc bug.

- [ ] **Step 1: Enable Hangfire for the trial.** `appsettings.json:49` has `"Jobs": { "UseHangfire": false }`, which gates Hangfire, `JobSyncService` and the dashboard (`Startup.cs:233`, `:325`, `:385`). In `appsettings.Development.json` add `"Jobs": { "UseHangfire": true }` (merge with Task 1's content). Check which connection string Hangfire uses (`Startup.cs` ~line 244) and that it resolves to a trial catalog, not `bpgdemo`.

- [ ] **Step 2: Commands** (XAF-free, like `ProcessWebhookOutboxCommand`):

```csharp
namespace bpgdemo.Jobs.Commands;

/// <summary>XafStateless trial: fires every due Wait state. Scheduled every minute.</summary>
public record RunWorkflowTimersCommand();
```

```csharp
namespace bpgdemo.Jobs.Commands;

/// <summary>XafStateless trial: delivers Pending workflow notifications. Scheduled every minute.</summary>
public record DispatchWorkflowNotificationsCommand();
```

- [ ] **Step 3: Handlers.** Hangfire's ASP.NET Core activator gives each job run its own DI scope; `JobExecutor` initializes security in it but creates no scope itself. So the handlers take their services by injection; do not add a nested scope.

```csharp
#nullable enable
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Security;
using Microsoft.Extensions.Logging;
using bpgdemo.Jobs;
using bpgdemo.Jobs.Commands;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Services;
using Task = System.Threading.Tasks.Task;

namespace bpgdemo.Blazor.Server.Handlers;

public sealed class RunWorkflowTimersHandler(
    INonSecuredObjectSpaceFactory objectSpaceFactory,
    WorkflowRuntime runtime,
    ISecurityStrategyBase security,
    ILogger<RunWorkflowTimersHandler> logger) : IJobHandler<RunWorkflowTimersCommand>
{
    public Task ExecuteAsync(RunWorkflowTimersCommand command, CancellationToken cancellationToken = default)
    {
        // ponytail: trial evidence for Review Focus 3; drop once the trial is read.
        logger.LogInformation("Workflow timers: running as {User}", security.UserName ?? "(anonymous)");
        var fired = runtime.RunDueTimers(() => objectSpaceFactory.CreateNonSecuredObjectSpace<WorkflowInstance>());
        logger.LogInformation("Workflow timers: {Fired} fired", fired);
        return Task.CompletedTask;
    }
}
```

(Check `ISecurityStrategyBase` resolves from the job scope — `XafJobScopeInitializer.cs` shows what it signs in through; use the same service it uses.)

```csharp
#nullable enable
using DevExpress.ExpressApp;
using Microsoft.Extensions.Logging;
using bpgdemo.Jobs;
using bpgdemo.Jobs.Commands;
using XafStateless.Module.BusinessObjects;
using Task = System.Threading.Tasks.Task;

namespace bpgdemo.Blazor.Server.Handlers;

// ponytail: log sink, as in the demo. Skeleton users have no e-mail address; see the report.
public sealed class DispatchWorkflowNotificationsHandler(
    INonSecuredObjectSpaceFactory objectSpaceFactory,
    ILogger<DispatchWorkflowNotificationsHandler> logger) : IJobHandler<DispatchWorkflowNotificationsCommand>
{
    public Task ExecuteAsync(DispatchWorkflowNotificationsCommand command, CancellationToken cancellationToken = default)
    {
        using var os = objectSpaceFactory.CreateNonSecuredObjectSpace<WorkflowNotification>();
        var pending = os.GetObjectsQuery<WorkflowNotification>()
            .Where(n => n.State == NotificationState.Pending).OrderBy(n => n.CreatedUtc).ToList();
        foreach (var n in pending)
            (n.State, n.Attempts) = (NotificationState.Sent, n.Attempts + 1);
        os.CommitChanges();
        foreach (var n in pending)   // after the commit, so a failed commit is never logged as sent
            logger.LogInformation("Workflow notification {Id} to {Role}: {Body}", n.ID, n.Role, n.Body);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 4: Register everywhere the skeleton looks.**
  - `Startup.cs`, after line 321:

    ```csharp
    services.AddJobHandler<RunWorkflowTimersCommand, bpgdemo.Blazor.Server.Handlers.RunWorkflowTimersHandler>();
    services.AddJobHandler<DispatchWorkflowNotificationsCommand, bpgdemo.Blazor.Server.Handlers.DispatchWorkflowNotificationsHandler>();
    ```
  - `JobDispatchService.cs`: add a `case nameof(RunWorkflowTimersCommand):` and a `case nameof(DispatchWorkflowNotificationsCommand):` to **both** switches, copying the `ProcessWebhookOutboxCommand` case of each switch (parameterless command).
  - `CommandMetadataProvider.cs`: two entries in `CommandTypes`, same shape as `ProcessWebhookOutboxCommand`.
  - `Updater.cs` — seed with **Name equal to the command type name**, because the execution recorder links runs to a definition by Name (`XafJobExecutionRecorder.cs:33`) and `JobExecutor` passes the command type name (`JobExecutor.cs:17`):

    ```csharp
    foreach (var type in new[] { "RunWorkflowTimersCommand", "DispatchWorkflowNotificationsCommand" })
    {
        if (ObjectSpace.FirstOrDefault<JobDefinition>(j => j.Name == type) != null) continue;
        var job = ObjectSpace.CreateObject<JobDefinition>();
        (job.Name, job.JobTypeName, job.CronExpression, job.IsEnabled) = (type, type, "* * * * *", true);
    }
    ObjectSpace.CommitChanges();
    ```

  Record as friction: adding one job takes five edits in four files; and BPG's own generated "Webhook outbox" seed uses a Name different from its command name, so by the same code path its `LastRunStatus` would never update. That second point is **unverified** (read in code only) until this task's Step 6 shows the mechanism on our own jobs; the report says so and proposes a BPG card, not filed.

- [ ] **Step 5: Build, update, verify seed, start.** Build, `--updateDatabase --silent` (Development), then check both JobDefinitions exist. Start the app. Expected in the log: Hangfire started and `JobSyncService` registered both recurring jobs **without** a "Cannot schedule unknown job type" warning. Screenshot the Hangfire dashboard's Recurring Jobs page showing both.

- [ ] **Step 6: Verify timer, notification and recording (Review Focus 3, 5).** As `designer`, publish a new version of `ChangeFlow` with a Wait state (1 minute) between approve and `Done`, and a Notify effect to `Approvers` on entering `Done`. As `approver`, run request D through it. Expected within about 3 minutes:
  - D's state `Done`, `Implemented = 1` (sqlcmd);
  - log: `running as HangfireJob`, then `Workflow timers: 1 fired`;
  - exactly one `WorkflowNotification` row for D's instance with `State = Sent`, `Attempts = 1`, and one log line with its ID; on the next minute's run, no second line for that ID;
  - `RunWorkflowTimersCommand`'s JobDefinition shows `LastRunStatus` success and execution records linked to it;
  - `BackgroundJobs` role unchanged (no workflow permissions granted).

  Record the instance ID, notification ID and Hangfire job IDs for the report. Screenshot D in `Done`. Note in the report: the dispatcher proves the outbox path, not delivery to a person.

- [ ] **Step 7: Stop the app. Run** `dotnet test C:\Projects\BPGDemo.trial\tests\bpgdemo.Jobs.Tests` (unpiped; expected all green — it covers only `bpgdemo.Jobs`, which Step 4 touched). **Commit (trial branch)** the nine files; check the worktree for anything else; confirm the main tree equals the baseline.

### Task 4 (XSM-010): Trial report and doc fixes (in this repo)

**Files (in `C:\Projects\XafStateless`):**
- Create: `docs/host-trial.md`, `docs/images/host-trial/*.png`
- Modify: `docs/integrating.md` (only the doc bugs found in Tasks 1–3)

- [ ] **Step 1: Count the touch points.** `git -C /c/Projects/BPGDemo.trial status --short` (must be clean), then `git -C /c/Projects/BPGDemo.trial diff --stat master..trial/xafstateless`.

- [ ] **Step 2: Write `docs/host-trial.md`**, sections:
  - *Setup*: the three repo revisions from Task 1 Step 1, resolved package versions, the exact commands, `--environment Development`, catalog `bpgdemoTrial`, and exit codes.
  - *Touch points*: the diff-stat table, split into "module integration" (Tasks 1–2) and "skeleton job plumbing" (Task 3).
  - *Doc bugs fixed*: each with its integrating.md section.
  - *Friction*: each with a proposed fix and whether it needs a module change.
  - *Findings for generated apps*: Review Focus 1 (observed hook bypass; rule/webhook impact unverified), the notification-address gap (skeleton users have no e-mail), the Webhook-outbox Name mismatch (unverified), each proposed as a card, none filed.
  - *Evidence*: instance/notification/job IDs, screenshots under `docs/images/host-trial/`.
  - *What it would take to put this in the skeleton for real*: NuGet packaging like XafLayoutBuilder, the BPG E2E gate `GenerationPipelineTests`, the BPG card rule for skeleton work, and how generated apps would get `ConfigureXafStateless` into a DbContext BPG regenerates.

- [ ] **Step 3: Re-run this repo's tests** — `dotnet test XafStateless.slnx` (unpiped): Engine 37, Module 87 green (the trial changes no module code, so any change is a finding).

- [ ] **Step 4: Codex pass** over the report and the trial diff (`codex --no-daemon exec --sandbox read-only`), settle the findings with the user, then commit here: `docs/host-trial.md`, `docs/integrating.md`, `docs/images/host-trial/` — `BPGDemo trial integration report (XSM-010)`. Read the commit's `--stat`, push this repo. The BPGDemo branch and worktree stay local until the user decides.
