# XafStateless Implementation Plan

**Goal:** A drop-in XAF module in which end users define workflows as data: they add states, draw transitions, and pick what each state does from a fixed set of step types. The module versions the definitions, validates them before publish, runs them on Stateless, and draws them from the same definition. It must be packaged so the target host application can add it the way it already adds XafLayoutBuilder.

**Architecture:** There are three layers. `XafStateless.Engine` is pure .NET and depends only on Stateless: definition records, the validator, the machine builder, the dry run and Mermaid export. Every rule lives here and is unit-tested without a database. `XafStateless.Module` is the XAF/EF Core module: entities, the immutability interceptor, the publish lifecycle, the runtime (start, fire, decide, timers, effects), and the controllers. `XafStateless.Blazor` renders the diagram. A small generated XAF Blazor app (`demo/`) hosts it, and a trial integration into the target host application proves it is reusable.

**Tech Stack:** .NET 10 (SDK 10.0.401), DevExpress XAF 26.1.4 (Blazor, EF Core), EF Core 10.0.10 on SQL Server LocalDB, Stateless 5.20.1, xUnit 2.9.3 (engine and module tests), Microsoft.Playwright.NUnit (E2E), and Mermaid 11 (bundled JavaScript, no CDN).

**Spec:** this document's **Design** section, together with `README.md` (goal, idea, open questions; the questions were settled 2026-09-25: the four step types plus a decision/branch type, a small XAF host app plus a the target host application trial, and forms first with a generated diagram).

---

## Design

### Positioning: what XAF's StateMachine module promised and what this delivers

Checked against the dxdocs pages 113713, 113714 and 113715.

| | XAF StateMachine | General-purpose engine (earlier proof of concept) | XafStateless |
|---|---|---|---|
| Who defines states | Runtime rows, but each state is a *marker value* of an enum or reference property on the class | IT, in a JSON designer | End users, as rows keyed by a string. No enum and no code change |
| What a state *does* | Nothing: transitions are toolbar Actions plus Conditional Appearance | Anything, but as opaque nodes | A fixed set of step types that users configure and developers extend |
| Waiting / timers | None | Yes, but heavy | The Wait step, fired by a per-minute runner |
| Human tasks | None | Bookmarks plus our own bridge | The Task step, with outcomes = the outgoing triggers |
| Safe editing | Edits apply live to running objects | Versioned workflow definitions | Draft → Published → Retired; running work stays pinned to its version |
| Validation | None | None structural | At publish: reachability, a way out, no stuck wait, no automatic loop, and criteria/role/field checks |
| Lock fields per state | Yes: `StateAppearance`, UI only | n/a | Yes: locked in the UI and refused at save (Task 7b) |
| Diagram | None | Designer canvas | Mermaid, generated from the definition that runs |

### Model

- **Definition** (`WorkflowDefinition`), versioned. It has a `Name` (stable key, e.g. `purchase-request`), `TargetTypeName` (the full CLR name of the subject class), `Version`, `State` (Draft/Published/Retired), `InitialStateKey`, and aggregated `States` and `Transitions`. There is at most one Draft and at most one Published per `Name`, enforced by filtered unique indexes.
- **State** (`WorkflowState`). It has a `Key` (stable string), a `Caption`, a **Kind** that decides how the state is *left*, and ordered **on-entry effects**:
  - `Manual`: users fire outgoing transitions from an Action. A transition may carry a `RequiredRole`.
  - `Task`: on entry the module creates a `WorkflowTask` for `TaskRole`. The task's outcomes are the triggers of the outgoing transitions, and deciding a task fires that trigger.
  - `Wait`: on entry `DueUtc = now + WaitMinutes`. The timer runner fires the single outgoing transition.
  - `Decision`: on entry the module evaluates the outgoing transitions' criteria in `Order` and fires the first match. The last transition has no criteria and serves as the else branch.
  - `Final`: no exit.
- **Effect** (`WorkflowEffect`, belonging to a state, ordered): `Notify` (recipient role plus a template with `{Member}` placeholders, written to an outbox) or `SetField` (member name plus a value, converted to the member's type).
- **Transition** (`WorkflowTransition`): `FromKey`, `Trigger` (the button or outcome caption), `ToKey`, optional `Criteria` (XAF criteria language, evaluated against the subject), optional `RequiredRole`, and `Order`.
- **Instance** (`WorkflowInstance`), one per subject. It holds `SubjectType`, `SubjectId` (Guid), the pinned `DefinitionId`, `StateKey`, `EnteredUtc` and `DueUtc`. There is no custom token: concurrency uses XAF's built-in optimistic lock on `BaseObject` (see Runtime rules).
- **Subject**: any host business class that implements `IWorkflowSubject { WorkflowInstance? Workflow { get; set; } }`. That is one property per class, so list views can show and filter on `Workflow.StateCaption`. The `Workflow` link is **runtime-owned**: `StateLockGuard` refuses any user change to it (Task 7b), and every command checks `instance.SubjectType == subject type` and `instance.SubjectId == subject.ID`.
- **Developer extension point**: a new step type is a new `StateKind` value, plus its branch in the validator and in `WorkflowRuntime.Enter`. A new effect is a new `EffectKind`, plus a validator check and an applier. Nothing else changes.

### Runtime rules

- There is one commit per operation (Start, Fire, Decide, or one timer). The state change(s), effects, task create/supersede and outbox rows all go into a single `CommitChanges`. On a lost race the whole operation is retried up to 4 times, following the earlier proof of concept's `IsLostRace`. What a lost race looks like through XAF is **pinned by the Task 4b spike**, not assumed.
- **Concurrency uses XAF's built-in optimistic lock** (dxdocs 405384: on by default for `BaseObject` in new apps through `modelBuilder.UseOptimisticLock()`; hosts must have it on, and `integrating.md` says so). There are no custom `[ConcurrencyCheck] Token` columns. Every command **touches** the subject, the instance and (for Decide) the task, so XAF advances their lock versions in the command's commit. Consequences:
  - Two commands on the same subject collide on the subject row, so exactly one wins.
  - A user's subject save racing a transition also collides on the subject row. The loser retries: a command reloads and re-checks; a user save re-runs the lock guard against the now-persisted state. So an edit can never slip into a state that locks it.
  - `Touch(os, obj)` is one helper whose mechanism (for example `ObjectSpace.SetModified`, or a direct write of the lock field) is decided by the Task 4b spike.
- A chain of Decisions runs inside the same commit and stops at a depth of 32. The validator also forbids Decision cycles, so the depth limit is only a backstop. Each entered state's effects are applied **before** the next Decision's criteria are evaluated (the engine calls back `onEnter`).
- **The runtime never writes through the user's object space** (Codex review, 2026-09-25). Every operation runs as a *command*:
  1. The controller commits the open view first. Pending user edits go through the normal save path, including `StateLockGuard`, and are never carried along by a transition.
  2. The runtime loads the subject by id in the caller's **secured** object space. This proves the user can read it.
  3. It authorizes against **persisted** data: the transition's `RequiredRole` for Fire, the stored task's `Role` for Decide.
  4. It performs the writes in a **fresh non-secured object space** (`INonSecuredObjectSpaceFactory`) and reloads everything by id on every retry attempt.
  So users never need write permission on `WorkflowInstance`, `WorkflowTask` or `WorkflowNotification`. The timer runner follows the same path as caller `"system"`, skipping steps 1–3.
- Entry points are split by who may cause them. Public `Fire` works only in a **Manual** state. Task states leave only through `Decide`, Wait states only through `RunDueTimers`, and Decision states only automatically.
- **Permission matrix** (seeded in the demo, and documented in `integrating.md`):
  - Administrators: everything.
  - WorkflowDesigners: CRUD on `WorkflowDefinition`, `WorkflowState`, `WorkflowTransition` and `WorkflowEffect`; navigation to Definitions.
  - Approvers (and any subject user): CRUD on the subject class, **except** its `Workflow` member, which the guard enforces; **read-only** on `WorkflowInstance`, `WorkflowTask` and `WorkflowNotification`; read on `WorkflowDefinition` and its children (needed to show state captions); navigation to Tasks and to the subject's list view (the host passes the subject's navigation item id).
  - **Role names** are never read through the user's secured space: standard XAF roles deny role reads. The role lookup (`LookupValues`) and `XafWorkflowEnvironment.RoleExists` go through `WorkflowRoleCatalog`, which reads role names with a **non-secured** object space, the same approach as an existing production TagBox editor. Role names are not sensitive, but the catalogue exposes names only, never role objects.
  - All runtime writes go through the non-secured command object space.
- **Members usable by effects and locks** come from one shared helper, `WorkflowMembers.Supported(ITypeInfo)`, used by the lookup, `CheckEffect`, `CheckMember` and the runtime. A member qualifies when it is public, persistent, writable, browsable, and a scalar type (string, numeric, bool, DateTime, Guid, enum; nullable variants included). The helper **excludes** the key (`ID`), `Workflow`, the optimistic-lock field, references and collections.
- The engine never touches XAF. Criteria, roles and field checks reach it through `IWorkflowEnvironment` (validation) and `Func<string, bool>` (guards).

## Global Constraints

- TFM `net10.0` for every project. `<Nullable>enable</Nullable>` and `<ImplicitUsings>enable</ImplicitUsings>` go in `Directory.Build.props`.
- DevExpress packages are exactly `26.1.4`. EF Core packages are exactly `10.0.10`. Stateless is exactly `5.20.1`. No floating versions.
- The database is SQL Server (LocalDB in development and tests), because it matches the target host application. The filtered indexes use SQL Server `HasFilter` syntax (`Kept simple: SQL Server only; Postgres filter syntax differs — add a provider switch if a non-SQL-Server host appears`).
- `XafStateless.Engine` references **only** Stateless. No DevExpress and no EF.
- `XafStateless.Module` must not reference Hangfire, MailKit or any host project. The host wires timers and notification delivery.
- The subject key is `Guid`, matching XAF `BaseObject` (`Kept simple: Guid-keyed subjects only; add a string SubjectKey if a BaseObjectInt host appears`).
- Configuration lives in code or appsettings, never in environment variables.
- Entities follow the XAF EF Core rules: virtual properties, collections initialized as `ObservableCollection`, and a `DbSet` registered for every entity.
- Controllers follow `xaf-viewcontroller-patterns`: subscribe in `OnActivated` and unsubscribe in `OnDeactivated`, with no `async void`.
- Commits cite the task's card (`XSM-0NN`, minted after plan approval). Stage explicit paths only, never `git add -A` or `-am`.

## Review Focus

1. **A published definition edited in the detail view**: the edit is refused at Save with a readable message, and nothing half-saves. Pinned by Task 5, `Editing_published_rule_is_refused`.
2. **A running instance whose definition was retired mid-flight**: it keeps running on its pinned version, including after an app restart (the store cache is reloaded). Pinned by Task 6, `Instance_on_retired_version_still_fires`.
3. **Two users deciding the same task at once**: exactly one transition happens, and the loser gets "already decided", not an exception. Pinned by Task 6, `Concurrent_decide_one_wins`.
4. **A SetField value that does not convert to the member type** (e.g. `"abc"` into a decimal): refused at *publish*, not at runtime. Pinned by Task 5, `Publish_refuses_unconvertible_setfield`.
5. **A user without the transition's role**: the trigger does not appear in the Transition action, and a direct `Fire` call is refused. Pinned by Task 6, `Fire_without_role_is_refused`.
6. **Bypass attempts**: a direct `Fire` of a task outcome or a Wait exit is refused (Task 6, `Fire_outside_manual_state_is_refused`). An approver cannot change a task's `Role` (Task 7, a permission test). A locked field edited in the same view just before a transition is refused at save, even when the transition writes other fields (Task 7b, `Pending_locked_edit_does_not_ride_along`).
7. **Atomicity**: a failure injected at the database stage, after the first UPDATE has executed, leaves no row changed (Task 6, `Failure_after_effects_rolls_back_everything`).
8. **A user save racing a transition into a locking state**: the save cannot land a locked edit (Task 7b, `Subject_save_and_transition_race_one_loses`).

---

## File Structure

```
XafStateless.slnx
Directory.Build.props
src/
  XafStateless.Engine/            pure: WorkflowDef.cs, WorkflowValidator.cs, WorkflowMachine.cs, WorkflowDryRun.cs
  XafStateless.Module/            XAF module
    XafStatelessModule.cs
    BusinessObjects/Workflow*.cs  one file per entity
    ModelBuilderExtensions.cs     ConfigureXafStateless(modelBuilder) — indexes, relations
    Services/DefinitionGuard.cs   SaveChangesInterceptor
    Services/DefinitionService.cs NewDraft / Validate / Publish / DryRun
    Services/DefinitionMapper.cs  entities → WorkflowDef
    Services/DefinitionStore.cs   cache of published/retired WorkflowDef by (name, version)
    Services/XafWorkflowEnvironment.cs  IWorkflowEnvironment over ITypesInfo + roles
    Services/WorkflowRuntime.cs   Start / Fire / Decide / RunDueTimers
    Services/LostRace.cs
    Controllers/*.cs
    ServiceCollectionExtensions.cs AddXafStateless() — store, service, runtime, TimeProvider
    Services/StringLookup.cs       IStringLookupSource + [StringLookup] alias constants (UI-free)
    Services/WorkflowMembers.cs    Supported(ITypeInfo) — the one list of members effects/locks may use
    Services/WorkflowRoleCatalog.cs role names via a non-secured object space
    Services/Concurrency.cs        Touch(os, obj) — mechanism pinned by the Task 4b spike
  XafStateless.Blazor/            XafStatelessBlazorModule.cs, Editors/StringLookupPropertyEditor.cs + StringMultiLookupPropertyEditor.cs (Task 7),
                                  DiagramViewItem + Diagram.razor, wwwroot/mermaid.min.js, wwwroot/diagram.js (Task 8)
demo/                             generated by dotnet new dx.xaf
  XafStateless.Demo.Module/       PurchaseRequest subject, DbContext, Updater (seed users/roles/sample definition)
  XafStateless.Demo.Blazor.Server/
tests/
  XafStateless.Engine.Tests/      xUnit, no DB
  XafStateless.Module.Tests/      xUnit on LocalDB (a fresh database per test class)
  XafStateless.E2E/               Playwright NUnit against the running demo
docs/integrating.md               how a host (the target host application) adds the module
```

---

### Task 1: Solution skeleton and engine definition model

**Files:**
- Create: `XafStateless.slnx`, `Directory.Build.props`
- Create: `src/XafStateless.Engine/XafStateless.Engine.csproj`, `src/XafStateless.Engine/WorkflowDef.cs`
- Create: `tests/XafStateless.Engine.Tests/XafStateless.Engine.Tests.csproj`, `tests/XafStateless.Engine.Tests/Fixtures.cs`, `tests/XafStateless.Engine.Tests/WorkflowDefTests.cs`

**Interfaces:**
- Produces:
```csharp
namespace XafStateless.Engine;
public enum StateKind { Manual, Task, Wait, Decision, Final }
public enum EffectKind { Notify, SetField }
public sealed record EffectDef(EffectKind Kind, string Target, string Value);           // Notify: Target=role, Value=template; SetField: Target=member, Value=text
public sealed record StateDef(string Key, string Caption, StateKind Kind,
    string? TaskRole = null, int? WaitMinutes = null, IReadOnlyList<EffectDef>? OnEntry = null)
{ public IReadOnlyList<EffectDef> Effects => OnEntry ?? []; }
public sealed record TransitionDef(string From, string Trigger, string To,
    string? Criteria = null, string? RequiredRole = null, int Order = 0);
public sealed record WorkflowDef(string Name, int Version, string InitialState,
    IReadOnlyList<StateDef> States, IReadOnlyList<TransitionDef> Transitions)
{
    public StateDef State(string key) => States.First(s => s.Key == key);
    public IEnumerable<TransitionDef> From(string key) => Transitions.Where(t => t.From == key).OrderBy(t => t.Order);
}
```
- `Fixtures.PurchaseRequest()` is the canonical test definition, used by Tasks 2 and 3. Its states are draft (Manual) → approval (Task, role Approvers) → decide-amount (Decision) → wait-cooling (Wait, 1 min) / ordered (Final); it also has rejected (Final).

- [ ] **Step 1: Create the solution and projects**

```bash
cd /c/Projects/XafStateless
dotnet new classlib -n XafStateless.Engine -o src/XafStateless.Engine -f net10.0
dotnet new xunit -n XafStateless.Engine.Tests -o tests/XafStateless.Engine.Tests -f net10.0
rm src/XafStateless.Engine/Class1.cs tests/XafStateless.Engine.Tests/UnitTest1.cs
dotnet add src/XafStateless.Engine package Stateless --version 5.20.1
dotnet add tests/XafStateless.Engine.Tests reference src/XafStateless.Engine
dotnet new sln -n XafStateless --format slnx
dotnet sln XafStateless.slnx add src/XafStateless.Engine tests/XafStateless.Engine.Tests
```

`Directory.Build.props`:
```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```
Remove the `<TargetFramework>` line that the templates put in each csproj. Pin the xunit template's package versions: `xunit` 2.9.3, and keep the template's `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio`, but as exact versions.

- [ ] **Step 2: Write the failing test**

`tests/XafStateless.Engine.Tests/Fixtures.cs`:
```csharp
using XafStateless.Engine;
namespace XafStateless.Engine.Tests;

public static class Fixtures
{
    public static WorkflowDef PurchaseRequest() => new("purchase-request", 1, "draft",
        [
            new("draft", "Draft", StateKind.Manual),
            new("approval", "Approval", StateKind.Task, TaskRole: "Approvers",
                OnEntry: [new(EffectKind.Notify, "Approvers", "Please approve {Title}")]),
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
}
```

`WorkflowDefTests.cs`:
```csharp
using XafStateless.Engine;
namespace XafStateless.Engine.Tests;

public class WorkflowDefTests
{
    [Fact]
    public void From_returns_transitions_in_order()
    {
        var def = Fixtures.PurchaseRequest();
        Assert.Equal(["Large", "Small"], def.From("decide-amount").Select(t => t.Trigger));
    }
}
```

- [ ] **Step 3: Run it and confirm it fails** — `dotnet test tests/XafStateless.Engine.Tests`. Expected: build error, `WorkflowDef` not found.
- [ ] **Step 4: Implement `WorkflowDef.cs`** exactly as in the Interfaces block.
- [ ] **Step 5: Run it and confirm it passes** — Expected: `Passed: 1`.
- [ ] **Step 6: Commit** — `git add XafStateless.slnx Directory.Build.props src/XafStateless.Engine tests/XafStateless.Engine.Tests`, then `git commit -F msg.txt` with the message `Engine: definition model (XSM-001)`.

---

### Task 2: Publish-time validator

**Files:**
- Create: `src/XafStateless.Engine/WorkflowValidator.cs`
- Test: `tests/XafStateless.Engine.Tests/WorkflowValidatorTests.cs`

**Interfaces:**
- Consumes: `WorkflowDef`, `StateDef`, `TransitionDef`, `EffectDef`, and `Fixtures.PurchaseRequest()` from Task 1.
- Produces:
```csharp
public interface IWorkflowEnvironment
{
    bool RoleExists(string role);
    string? CheckCriteria(string criteria);      // null = ok, else the error text
    string? CheckEffect(EffectDef effect);       // null = ok (member exists, writable, value converts; template placeholders exist)
}
public static class WorkflowValidator
{
    public static IReadOnlyList<string> Validate(WorkflowDef def, IWorkflowEnvironment env);   // empty = publishable
}
```

The rules, with one test each. The messages are asserted with `Contains`, so keep the quoted fragment exact:
1. State keys are unique → `duplicate state 'x'`
2. The initial state exists → `initial state 'x' does not exist`
3. Every transition's From/To exists → `transition 'T' refers to unknown state 'x'`
4. A Trigger is unique per From → `duplicate trigger 'T' from 'x'`
5. No self-loop on Decision or Wait → `'x' cannot transition to itself`. Self-loops on Manual/Task are **allowed** (e.g. "Remind", "Resubmit"). Task 3 builds them with `PermitReentry[If]`, because Stateless's `Permit` rejects an identity transition.
6. Every state is reachable from the initial state → `'x' is unreachable`
7. A Final state has no outgoing transitions → `final state 'x' has transitions`
8. A non-Final state has ≥1 outgoing transition → `'x' has no way out`
9. From every state some Final is reachable → `'x' can never finish`
10. Wait: exactly one outgoing transition, with no Criteria and no RequiredRole, and `WaitMinutes > 0` → `wait 'x' needs exactly one unguarded exit` / `wait 'x' needs a duration`
11. Task: `TaskRole` is set and `env.RoleExists`, and its transitions have no RequiredRole → `task 'x' needs a role` / `role 'r' does not exist` / `task 'x' outcome 'T' cannot require a role`
12. Decision: `Order` is unique among the state's transitions; the last transition by Order has no Criteria, and every other one has Criteria; no transition carries a RequiredRole (automatic execution has no user) → `decision 'x' has duplicate order n` / `decision 'x' needs a final else-branch without criteria` / `decision 'x' branch 'T' needs criteria` / `decision 'x' branch 'T' cannot require a role`. Test: shuffle the fixture's transition list before validating and building; the outcome is the same.
13. No Decision→Decision cycle → `decisions 'a' → 'b' form a loop`
14. Manual: every `RequiredRole` exists → `role 'r' does not exist`
15. Every Criteria passes `env.CheckCriteria` → `criteria on 'T': <error>`
16. Every effect passes `env.CheckEffect` → `effect on 'x': <error>`; a Notify's role must exist → `role 'r' does not exist`

- [ ] **Step 1: Write the failing tests.** Use a fake environment, and mutate the fixture with `with` expressions:

```csharp
using XafStateless.Engine;
namespace XafStateless.Engine.Tests;

sealed class FakeEnv(params string[] roles) : IWorkflowEnvironment
{
    public bool RoleExists(string r) => roles.Contains(r);
    public string? CheckCriteria(string c) => c.Contains("Nope") ? "unknown member Nope" : null;
    public string? CheckEffect(EffectDef e) => e.Target == "Nope" ? "unknown member Nope" : null;
}

public class WorkflowValidatorTests
{
    static readonly FakeEnv Env = new("Approvers");
    static IReadOnlyList<string> V(WorkflowDef d) => WorkflowValidator.Validate(d, Env);
    static WorkflowDef Pr => Fixtures.PurchaseRequest();

    [Fact] public void Fixture_is_valid() => Assert.Empty(V(Pr));

    [Fact] public void Unreachable_state()
    {
        var d = Pr with { States = [.. Pr.States, new StateDef("orphan", "Orphan", StateKind.Final)] };
        Assert.Contains(V(d), e => e.Contains("'orphan' is unreachable"));
    }

    [Fact] public void State_without_way_out()
    {
        var d = Pr with { Transitions = Pr.Transitions.Where(t => t.From != "wait-cooling").ToList() };
        Assert.Contains(V(d), e => e.Contains("'wait-cooling' has no way out"));
    }

    [Fact] public void Trap_cycle_can_never_finish()
    {
        var d = Pr with
        {
            States = [.. Pr.States, new StateDef("a", "A", StateKind.Manual), new StateDef("b", "B", StateKind.Manual)],
            Transitions = [.. Pr.Transitions, new TransitionDef("draft", "Park", "a"), new TransitionDef("a", "Go", "b"), new TransitionDef("b", "Back", "a")]
        };
        Assert.Contains(V(d), e => e.Contains("'a' can never finish"));
    }

    [Fact] public void Guarded_wait_is_stuck()
    {
        var d = Pr with { Transitions = Pr.Transitions.Select(t => t.From == "wait-cooling" ? t with { Criteria = "Amount > 0" } : t).ToList() };
        Assert.Contains(V(d), e => e.Contains("wait 'wait-cooling' needs exactly one unguarded exit"));
    }

    [Fact] public void Decision_needs_else()
    {
        var d = Pr with { Transitions = Pr.Transitions.Select(t => t.Trigger == "Small" ? t with { Criteria = "Amount <= 1000" } : t).ToList() };
        Assert.Contains(V(d), e => e.Contains("decision 'decide-amount' needs a final else-branch without criteria"));
    }

    [Fact] public void Decision_duplicate_order_is_refused()
    {
        var d = Pr with { Transitions = Pr.Transitions.Select(t => t.Trigger == "Small" ? t with { Order = 0 } : t).ToList() };
        Assert.Contains(V(d), e => e.Contains("decision 'decide-amount' has duplicate order 0"));
    }

    [Fact] public void Decision_branch_role_is_refused_even_if_role_exists()
    {
        var d = Pr with { Transitions = Pr.Transitions.Select(t => t.Trigger == "Large" ? t with { RequiredRole = "Approvers" } : t).ToList() };
        Assert.Contains(V(d), e => e.Contains("decision 'decide-amount' branch 'Large' cannot require a role"));
    }

    [Fact] public void Shuffled_transitions_validate_and_route_the_same()
    {
        var shuffled = Pr with { Transitions = Pr.Transitions.Reverse().ToList() };
        Assert.Equal(V(Pr), V(shuffled));
    }

    [Fact] public void Decision_loop()
    {
        var d = Pr with
        {
            States = [.. Pr.States, new StateDef("d2", "D2", StateKind.Decision)],
            Transitions = [.. Pr.Transitions.Where(t => t.Trigger != "Small"),
                new TransitionDef("decide-amount", "Small", "d2", Order: 1), new TransitionDef("d2", "Back", "decide-amount")]
        };
        Assert.Contains(V(d), e => e.Contains("form a loop"));
    }

    [Fact] public void Task_role_must_exist()
    {
        var d = Pr with { States = Pr.States.Select(s => s.Key == "approval" ? s with { TaskRole = "Ghosts" } : s).ToList() };
        Assert.Contains(V(d), e => e.Contains("role 'Ghosts' does not exist"));
    }

    [Fact] public void Bad_criteria_and_effect_surface_env_errors()
    {
        var d = Pr with
        {
            Transitions = Pr.Transitions.Select(t => t.Trigger == "Large" ? t with { Criteria = "Nope > 1" } : t).ToList(),
            States = Pr.States.Select(s => s.Key == "ordered" ? s with { OnEntry = [new(EffectKind.SetField, "Nope", "1")] } : s).ToList()
        };
        var errs = V(d);
        Assert.Contains(errs, e => e.Contains("criteria on 'Large': unknown member Nope"));
        Assert.Contains(errs, e => e.Contains("effect on 'ordered': unknown member Nope"));
    }

    [Fact] public void Duplicate_trigger()
    {
        var d = Pr with { Transitions = [.. Pr.Transitions, new TransitionDef("approval", "Approve", "rejected")] };
        Assert.Contains(V(d), e => e.Contains("duplicate trigger 'Approve' from 'approval'"));
    }
}
```
Add one test for each of rules 1, 2, 3, 5, 7, 10 (duration), 11 (outcome with role), 12 (branch without criteria), 14 and 16 (Notify role), in the same shape.

- [ ] **Step 2: Run them and confirm they fail** — `dotnet test tests/XafStateless.Engine.Tests --filter WorkflowValidatorTests`. Expected: build error, `WorkflowValidator` not found.
- [ ] **Step 3: Implement it.** Structural rules (1-4) come first; when any of them fails, return early, because the graph rules assume a well-formed graph. Reachability is a BFS from `InitialState`. "Can finish" is a reverse BFS from every Final. The Decision loop check is a DFS over edges whose source and target are both Decisions, and it reports the first cycle as `decisions 'a' → 'b' form a loop`.

```csharp
namespace XafStateless.Engine;

public static class WorkflowValidator
{
    public static IReadOnlyList<string> Validate(WorkflowDef def, IWorkflowEnvironment env)
    {
        var errs = new List<string>();
        var keys = new HashSet<string>();
        foreach (var s in def.States) if (!keys.Add(s.Key)) errs.Add($"duplicate state '{s.Key}'");
        if (!keys.Contains(def.InitialState)) errs.Add($"initial state '{def.InitialState}' does not exist");
        foreach (var t in def.Transitions)
            foreach (var k in new[] { t.From, t.To }.Where(k => !keys.Contains(k)))
                errs.Add($"transition '{t.Trigger}' refers to unknown state '{k}'");
        foreach (var g in def.Transitions.GroupBy(t => (t.From, t.Trigger)).Where(g => g.Count() > 1))
            errs.Add($"duplicate trigger '{g.Key.Trigger}' from '{g.Key.From}'");
        if (errs.Count > 0) return errs;   // graph rules below assume a well-formed graph

        var reach = Bfs([def.InitialState], k => def.From(k).Select(t => t.To));
        var finishers = Bfs(def.States.Where(s => s.Kind == StateKind.Final).Select(s => s.Key),
                             k => def.Transitions.Where(t => t.To == k).Select(t => t.From));
        foreach (var s in def.States)
        {
            var outs = def.From(s.Key).ToList();
            if (!reach.Contains(s.Key)) errs.Add($"'{s.Key}' is unreachable");
            if (s.Kind == StateKind.Final) { if (outs.Count > 0) errs.Add($"final state '{s.Key}' has transitions"); }
            else if (outs.Count == 0) errs.Add($"'{s.Key}' has no way out");
            else if (!finishers.Contains(s.Key)) errs.Add($"'{s.Key}' can never finish");
            if (s.Kind is StateKind.Decision or StateKind.Wait && outs.Any(t => t.To == s.Key))
                errs.Add($"'{s.Key}' cannot transition to itself");
            switch (s.Kind)
            {
                case StateKind.Wait:
                    if (outs.Count != 1 || outs[0].Criteria != null || outs[0].RequiredRole != null)
                        errs.Add($"wait '{s.Key}' needs exactly one unguarded exit");
                    if (s.WaitMinutes is not > 0) errs.Add($"wait '{s.Key}' needs a duration");
                    break;
                case StateKind.Task:
                    if (string.IsNullOrWhiteSpace(s.TaskRole)) errs.Add($"task '{s.Key}' needs a role");
                    else if (!env.RoleExists(s.TaskRole)) errs.Add($"role '{s.TaskRole}' does not exist");
                    foreach (var t in outs.Where(t => t.RequiredRole != null))
                        errs.Add($"task '{s.Key}' outcome '{t.Trigger}' cannot require a role");
                    break;
                case StateKind.Decision when outs.Count > 0:
                    foreach (var g in outs.GroupBy(t => t.Order).Where(g => g.Count() > 1))
                        errs.Add($"decision '{s.Key}' has duplicate order {g.Key}");
                    foreach (var t in outs.Where(t => t.RequiredRole != null))
                        errs.Add($"decision '{s.Key}' branch '{t.Trigger}' cannot require a role");
                    if (outs[^1].Criteria != null) errs.Add($"decision '{s.Key}' needs a final else-branch without criteria");
                    foreach (var t in outs.Take(outs.Count - 1).Where(t => t.Criteria == null))
                        errs.Add($"decision '{s.Key}' branch '{t.Trigger}' needs criteria");
                    break;
            }
            foreach (var e in s.Effects)
            {
                if (e.Kind == EffectKind.Notify && !env.RoleExists(e.Target)) errs.Add($"role '{e.Target}' does not exist");
                if (env.CheckEffect(e) is { } err) errs.Add($"effect on '{s.Key}': {err}");
            }
        }
        foreach (var t in def.Transitions)
        {
            if (t.RequiredRole != null && !env.RoleExists(t.RequiredRole)) errs.Add($"role '{t.RequiredRole}' does not exist");
            if (t.Criteria != null && env.CheckCriteria(t.Criteria) is { } err) errs.Add($"criteria on '{t.Trigger}': {err}");
        }
        if (DecisionLoop(def) is { } loop) errs.Add($"decisions {string.Join(" → ", loop.Select(k => $"'{k}'"))} form a loop");
        return errs;
    }

    static HashSet<string> Bfs(IEnumerable<string> start, Func<string, IEnumerable<string>> next)
    {
        var seen = new HashSet<string>(start); var q = new Queue<string>(seen);
        while (q.TryDequeue(out var k)) foreach (var n in next(k)) if (seen.Add(n)) q.Enqueue(n);
        return seen;
    }

    static List<string>? DecisionLoop(WorkflowDef def)
    {
        var decisions = def.States.Where(s => s.Kind == StateKind.Decision).Select(s => s.Key).ToHashSet();
        var done = new HashSet<string>();
        List<string>? Dfs(string k, List<string> path)
        {
            if (path.Contains(k)) return path[path.IndexOf(k)..];
            if (!done.Add(k)) return null;
            path.Add(k);
            foreach (var t in def.From(k).Where(t => decisions.Contains(t.To)))
                if (Dfs(t.To, path) is { } c) return c;
            path.RemoveAt(path.Count - 1);
            return null;
        }
        return decisions.Select(d => Dfs(d, [])).FirstOrDefault(c => c != null);
    }
}
```
- [ ] **Step 4: Run them and confirm they pass** — all green.
- [ ] **Step 5: Commit** — `Engine: publish-time validator (XSM-002)`.

---

### Task 3: Machine, dry run and Mermaid

**Files:**
- Create: `src/XafStateless.Engine/WorkflowMachine.cs`, `src/XafStateless.Engine/WorkflowDryRun.cs`, `src/XafStateless.Engine/WorkflowMermaid.cs`
- Test: `tests/XafStateless.Engine.Tests/WorkflowMachineTests.cs`

**Interfaces:**
- Consumes: `WorkflowDef` and `Fixtures` from Task 1.
- Produces:
```csharp
public sealed record Step(string From, string Trigger, string To);
public sealed class WorkflowMachine
{
    public const int MaxDecisionDepth = 32;
    public WorkflowMachine(WorkflowDef def, Func<string> getState, Action<string> setState, Func<string, bool> criteria,
                           Action<string>? onEnter = null);  // called with each entered state key, BEFORE the next Decision is evaluated
    public IReadOnlyList<TransitionDef> Available();                 // outgoing transitions of the current state whose Criteria pass (Manual/Task only; empty for Wait/Decision/Final)
    public IReadOnlyList<Step> Fire(string trigger);                 // fires, then auto-runs Decisions; returns every step taken, in order
    public IReadOnlyList<Step> EnterInitial();                       // sets the initial state; auto-runs it when the initial state is a Decision
    public IReadOnlyList<Step> FireTimer();                          // current state must be Wait; fires its single exit (+ Decision chain)
}
public static class WorkflowMermaid
{
    // Our own stateDiagram-v2 text straight from the definition. It needs no machine, so a broken draft still draws.
    // States get generated ids (s0, s1, …) with `state "Caption" as s0`; edges are `s0 --> s1 : Trigger`.
    // Captions/triggers: '"' → '\'', ':' and newlines → ' '. Transitions to unknown keys are skipped (the validator reports them).
    public static string Format(WorkflowDef def);
}
public static class WorkflowDryRun
{
    // Walks a path of triggers from the initial state with fixed criteria answers; never persists. Returns steps or the refusal.
    public static (IReadOnlyList<Step> Steps, string? Error) Run(WorkflowDef def, IEnumerable<string> triggers, Func<string, bool> criteria);
}
```
- How it works: Stateless `StateMachine<string, string>` is built from `getState`/`setState`, and every transition is configured as `Configure(from).PermitIf(trigger, to, () => criteria(t.Criteria))` (or `Permit` when there are no criteria). When `from == to`, use `PermitReentryIf`/`PermitReentry` instead. After each state change (including a reentry), the machine calls `onEnter(state)` and only then evaluates the next Decision, so a SetField effect on an entered state is visible to the guard that follows. `Fire` checks `machine.CanFire(trigger)` and throws `InvalidOperationException($"'{trigger}' is not possible from '{state}'")` when it cannot. After each fire, while the current state is a Decision, the machine picks `def.From(state).First(t => t.Criteria is null || criteria(t.Criteria))` and fires it. After `MaxDecisionDepth` rounds it throws.

- [ ] **Step 1: Write the failing tests**
```csharp
using XafStateless.Engine;
namespace XafStateless.Engine.Tests;

public class WorkflowMachineTests
{
    static (WorkflowMachine m, Func<string> state) Make(decimal amount)
    {
        var s = "draft";
        var m = new WorkflowMachine(Fixtures.PurchaseRequest(), () => s, v => s = v, c => c == "Amount > 1000" && amount > 1000);
        return (m, () => s);
    }

    [Fact] public void Small_amount_auto_routes_through_decision_to_ordered()
    {
        var (m, st) = Make(50);
        m.Fire("Submit");
        var steps = m.Fire("Approve");
        Assert.Equal(["decide-amount", "ordered"], steps.Select(x => x.To));
        Assert.Equal("ordered", st());
    }

    [Fact] public void Large_amount_waits_then_timer_orders()
    {
        var (m, st) = Make(5000);
        m.Fire("Submit"); m.Fire("Approve");
        Assert.Equal("wait-cooling", st());
        Assert.Empty(m.Available());
        m.FireTimer();
        Assert.Equal("ordered", st());
    }

    [Fact] public void Impossible_trigger_throws_readable()
    {
        var (m, _) = Make(1);
        var ex = Assert.Throws<InvalidOperationException>(() => m.Fire("Approve"));
        Assert.Contains("'Approve' is not possible from 'draft'", ex.Message);
    }

    [Fact] public void Available_lists_task_outcomes()
    {
        var (m, _) = Make(1);
        m.Fire("Submit");
        Assert.Equal(["Approve", "Reject"], m.Available().Select(t => t.Trigger));
    }

    [Fact] public void Mermaid_is_exact_and_escapes_captions()
    {
        var def = new WorkflowDef("t", 1, "a",
            [new("a", "Draft \"new\"", StateKind.Manual), new("b", "Done: ok", StateKind.Final)],
            [new("a", "Send back", "b"), new("a", "Ghost", "nope")]);
        Assert.Equal(
            "stateDiagram-v2\n    state \"Draft 'new'\" as s0\n    state \"Done  ok\" as s1\n    [*] --> s0\n    s0 --> s1 : Send back\n    s1 --> [*]\n",
            WorkflowMermaid.Format(def));
    }

    [Fact] public void Self_loop_on_task_reenters_guarded_and_unguarded()
    {
        foreach (var crit in new string?[] { null, "Amount > 1000" })
        {
            var baseDef = Fixtures.PurchaseRequest();
            var def = baseDef with { Transitions = [.. baseDef.Transitions, new("approval", "Remind", "approval", Criteria: crit)] };
            var s = "draft"; var entered = new List<string>();
            var m = new WorkflowMachine(def, () => s, v => s = v, c => c == "Amount > 1000", onEnter: entered.Add);
            m.Fire("Submit"); m.Fire("Remind");
            Assert.Equal("approval", s);
            Assert.Equal(["approval", "approval"], entered);
        }
    }

    [Fact] public void Entry_effect_changes_decision_branch()
    {
        // onEnter("decide-amount") flips amount to 5000 before the decision is evaluated → the route goes to wait-cooling, not ordered
        var amount = 50m; var s = "draft";
        var m = new WorkflowMachine(Fixtures.PurchaseRequest(), () => s, v => s = v,
            c => c == "Amount > 1000" && amount > 1000, onEnter: k => { if (k == "decide-amount") amount = 5000; });
        m.Fire("Submit"); m.Fire("Approve");
        Assert.Equal("wait-cooling", s);
    }

    [Fact] public void Dry_run_reports_refusal_without_state()
    {
        var (steps, err) = WorkflowDryRun.Run(Fixtures.PurchaseRequest(), ["Submit", "Cooled"], _ => false);
        Assert.Single(steps);
        Assert.Contains("'Cooled' is not possible from 'approval'", err);
    }
}
```
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement it.** `WorkflowMermaid.Format` is about 15 lines of `StringBuilder` (`Kept simple: own formatter instead of Stateless.Graph.MermaidGraph, because its escaping of spaces is unverified and it needs a constructible machine`). Task 8 confirms in the browser that the output renders. `Available()` returns `[]` unless the current state's Kind is Manual or Task, and otherwise returns `def.From(state).Where(t => t.Criteria is null || criteria(t.Criteria))`. `WorkflowDryRun.Run` keeps state in a local variable, calls `EnterInitial()`, then calls `Fire` on each trigger inside a try/catch, returning the steps taken so far together with `ex.Message`.
- [ ] **Step 4: Run them and confirm they pass.**
- [ ] **Step 5: Commit** — `Engine: Stateless machine, decision chain, dry run, Mermaid (XSM-003)`.

---

### Task 4: Demo host, module project and entities

**Files:**
- Create (generated): `demo/XafStateless.Demo.Module/**`, `demo/XafStateless.Demo.Blazor.Server/**`
- Create: `src/XafStateless.Module/XafStateless.Module.csproj`, `src/XafStateless.Module/XafStatelessModule.cs`, `src/XafStateless.Module/ModelBuilderExtensions.cs`, `src/XafStateless.Module/Services/LostRace.cs`
- Create: `src/XafStateless.Module/BusinessObjects/WorkflowDefinition.cs`, `WorkflowState.cs`, `WorkflowEffect.cs`, `WorkflowTransition.cs`, `WorkflowInstance.cs`, `WorkflowTask.cs`, `WorkflowNotification.cs`, `IWorkflowSubject.cs`
- Create: `demo/XafStateless.Demo.Module/BusinessObjects/PurchaseRequest.cs`
- Modify: `demo/XafStateless.Demo.Module/BusinessObjects/*DbContext.cs` (DbSets + `ConfigureXafStateless()`), `demo/XafStateless.Demo.Module/Module.cs` (RequiredModuleTypes)
- Create: `tests/XafStateless.Module.Tests/XafStateless.Module.Tests.csproj`, `tests/XafStateless.Module.Tests/TestDb.cs`, `tests/XafStateless.Module.Tests/SchemaTests.cs`

**Interfaces:**
- Produces (namespace `XafStateless.Module.BusinessObjects`; all entities derive from `DevExpress.Persistent.BaseImpl.EF.BaseObject`, so the key is `Guid ID`):
```csharp
public enum DefinitionState { Draft, Published, Retired }
public class WorkflowDefinition : BaseObject {
  public virtual string Name { get; set; } = "";            // stable key
  public virtual string TargetTypeName { get; set; } = "";  // full CLR name of an IWorkflowSubject class
  public virtual int Version { get; set; }
  public virtual DefinitionState State { get; set; }
  public virtual string InitialStateKey { get; set; } = "";
  public virtual string? PublishedBy { get; set; }
  public virtual DateTime? PublishedUtc { get; set; }
  [Aggregated] public virtual IList<WorkflowState> States { get; set; } = new ObservableCollection<WorkflowState>();
  [Aggregated] public virtual IList<WorkflowTransition> Transitions { get; set; } = new ObservableCollection<WorkflowTransition>();
  [NotMapped, Browsable(false)] public Type? TargetType => XafTypesInfo.Instance.FindTypeInfo(TargetTypeName)?.Type;
}
public class WorkflowState : BaseObject {
  public virtual WorkflowDefinition? Definition { get; set; }
  public virtual string Key { get; set; } = ""; public virtual string Caption { get; set; } = "";
  public virtual StateKind Kind { get; set; }                    // XafStateless.Engine.StateKind
  public virtual string? TaskRole { get; set; } public virtual int? WaitMinutes { get; set; }
  [Aggregated] public virtual IList<WorkflowEffect> Effects { get; set; } = new ObservableCollection<WorkflowEffect>();
}
public class WorkflowEffect : BaseObject { public virtual WorkflowState? State { get; set; } public virtual int Order { get; set; }
  public virtual EffectKind Kind { get; set; } public virtual string Target { get; set; } = ""; public virtual string Value { get; set; } = ""; }
public class WorkflowTransition : BaseObject { public virtual WorkflowDefinition? Definition { get; set; }
  public virtual string FromKey { get; set; } = ""; public virtual string Trigger { get; set; } = ""; public virtual string ToKey { get; set; } = "";
  [CriteriaOptions(nameof(TargetType))] public virtual string? Criteria { get; set; }
  public virtual string? RequiredRole { get; set; } public virtual int Order { get; set; }
  [NotMapped, Browsable(false)] public Type? TargetType => Definition?.TargetType; }
public class WorkflowInstance : BaseObject {
  public virtual string SubjectType { get; set; } = ""; public virtual Guid SubjectId { get; set; }
  public virtual WorkflowDefinition? Definition { get; set; }      // pinned version
  public virtual string StateKey { get; set; } = ""; public virtual string StateCaption { get; set; } = "";
  public virtual DateTime EnteredUtc { get; set; } public virtual DateTime? DueUtc { get; set; }
  }
public enum TaskState { Open, Decided, Superseded }
public class WorkflowTask : BaseObject {
  public virtual WorkflowInstance? Instance { get; set; } public virtual string StateKey { get; set; } = "";
  public virtual string Role { get; set; } = "";                  // runtime-owned copy of TaskRole, for the "my tasks" filter; users have read-only access
  // No Outcomes column: outcomes = def.From(StateKey) of the instance's pinned definition, so there is no delimiter and nothing user-editable.
  public virtual string Subject { get; set; } = "";               // display text
  public virtual TaskState State { get; set; } public virtual string? DecidedBy { get; set; } public virtual string? Outcome { get; set; }
  public virtual DateTime? DecidedUtc { get; set; } }
public enum NotificationState { Pending, Sent, Failed }
public class WorkflowNotification : BaseObject { public virtual string Role { get; set; } = ""; public virtual string Body { get; set; } = "";
  public virtual WorkflowInstance? Instance { get; set; } public virtual NotificationState State { get; set; } public virtual int Attempts { get; set; }
  public virtual string? LastError { get; set; } public virtual DateTime CreatedUtc { get; set; } }
public interface IWorkflowSubject { Guid ID { get; } WorkflowInstance? Workflow { get; set; } }

public static class ModelBuilderExtensions
{
    // Indexes: unique (Name, Version); filtered unique Name WHERE State=0 (one Draft) and WHERE State=1 (one Published);
    // unique (SubjectType, SubjectId) on WorkflowInstance; index DueUtc on WorkflowInstance; index (Role, State) on WorkflowTask.
    public static ModelBuilder ConfigureXafStateless(this ModelBuilder mb);
}
```
- **Host contract** (repeated in `docs/integrating.md` in Task 9): add `RequiredModuleTypes.Add(typeof(XafStatelessModule))`, seven `DbSet`s, `modelBuilder.ConfigureXafStateless()` and `modelBuilder.UseOptimisticLock()` in `OnModelCreating`, `optionsBuilder.AddInterceptors(DefinitionGuard.Instance, StateLockGuard.Instance)` in `OnConfiguring` (Tasks 5 and 7b), `services.AddXafStateless()` (Task 5), and `IWorkflowSubject` on each subject class.
- `LostRace` (in `Services/LostRace.cs`) is created here rather than in Task 6, because the schema test uses it: `Is(ex)` = `DbUpdateConcurrencyException` or `IsUniqueViolation(ex)`; `IsUniqueViolation(ex)` = a `SqlException` with number 2601/2627 in the inner-exception chain.

- [ ] **Step 1: Generate the demo app**
```bash
cd /c/Projects/XafStateless/demo
dotnet new dx.xaf -n XafStateless.Demo -orm EFCore -db SqlServer -p Blazor -s Password -m Appearance,Validation
```
Then check that the generated projects reference DevExpress `26.1.4` and EF Core `10.0.10`; if not, pin them. Move the generated projects under `demo/`, add them to `XafStateless.slnx`, and delete any generated sln. Point the connection string at `(localdb)\mssqllocaldb`, `Initial Catalog=XafStatelessDemo`. Build with `dotnet build XafStateless.slnx`, judging by the exit code and `Build succeeded`.
- [ ] **Step 2: Create `src/XafStateless.Module`** (`dotnet new classlib`). It references `XafStateless.Engine` and the packages `DevExpress.ExpressApp` 26.1.4, `DevExpress.ExpressApp.EFCore` 26.1.4, `DevExpress.Persistent.BaseImpl.EFCore` 26.1.4, `DevExpress.ExpressApp.ConditionalAppearance` 26.1.4 and `DevExpress.ExpressApp.Validation` 26.1.4. `XafStatelessModule : ModuleBase` requires `ConditionalAppearanceModule` and `ValidationModule`.
- [ ] **Step 3: Verify `CriteriaOptions`** with dxdocs (`devexpress_docs_search`: "CriteriaOptions attribute property name returning Type"). Confirm that a `[NotMapped]` property of type `Type` on the same object works. If it does not, record the finding in the task's report and use the documented alternative. Do not guess.
- [ ] **Step 4: Write the entities and `ModelBuilderExtensions`** as in the Interfaces block. Add `PurchaseRequest : BaseObject, IWorkflowSubject` with `Title` (string), `Amount` (decimal, `[Precision(18,2)]`), `Ordered` (bool) and `Workflow` (`WorkflowInstance?`). Register the DbSets in the demo DbContext and call `ConfigureXafStateless()`.
- [ ] **Step 5: Write the failing schema test.** `TestDb` creates a fresh LocalDB database named `XafStateless_Test_{Guid:N}` from the demo DbContext, calls `EnsureCreated`, and drops the database on Dispose. It exposes `IObjectSpace NewObjectSpace()` through an `EFCoreObjectSpaceProvider` for the demo context. Before writing the provider constructor, verify it with dxdocs ("EFCoreObjectSpaceProvider constructor 26.1 unit test").
```csharp
public class SchemaTests : IClassFixture<TestDb>
{
    readonly TestDb db; public SchemaTests(TestDb db) => this.db = db;
    [Fact] public void Second_draft_for_same_name_is_refused()
    {
        using var os = db.NewObjectSpace();
        foreach (var _ in new[] { 1, 2 }) { var d = os.CreateObject<WorkflowDefinition>(); d.Name = "x"; d.Version = _; d.State = DefinitionState.Draft; d.TargetTypeName = typeof(PurchaseRequest).FullName!; }
        var ex = Assert.ThrowsAny<Exception>(() => os.CommitChanges());
        Assert.True(LostRace.IsUniqueViolation(ex), ex.ToString());   // SqlException 2601/2627 somewhere in the chain, not just any failure
    }
}
```
- [ ] **Step 6: Run it and confirm it fails, then passes after Step 4 is wired up.** Run with `dotnet test tests/XafStateless.Module.Tests`.
- [ ] **Step 7: Smoke-test the running demo.** Check the port first (`netstat -ano | grep :5000`), then run the built exe `demo/XafStateless.Demo.Blazor.Server/bin/Debug/net10.0/XafStateless.Demo.Blazor.Server.exe`. Expect `curl` to return 200. Log in as Admin, check that "Workflow Definition" appears in navigation, then kill the process.
- [ ] **Step 8: Commit** — `Module: entities, demo host, schema (XSM-004)`.

---

### Task 4b: Concurrency spike — pin how XAF's optimistic lock behaves (Codex re-review #3/#4)

Nothing in Tasks 5–7b may assume a lost-race shape until this task has pinned it with tests.

**Files:**
- Create: `src/XafStateless.Module/Services/Concurrency.cs` (`Touch`), `tests/XafStateless.Module.Tests/ConcurrencySpikeTests.cs`
- Modify: `src/XafStateless.Module/Services/LostRace.cs` (match what the spike finds)

- [ ] **Step 1:** Read dxdocs 405384 and the `OptimisticLock` / `IOptimisticLock` / `OptimisticLockHandling` API pages. Confirm that the generated demo DbContext calls `UseOptimisticLock()`, and find the name of the lock-version member on `BaseObject`.
- [ ] **Step 2: Write the spike tests**. They go through `TestDb`'s **production-shaped provider**, the same `EFCoreObjectSpaceProvider` configuration as the demo, including `UseOptimisticLock()`, **not** raw EF:
  - `Lost_race_in_non_ui_object_space_surfaces_as`: OS-A and OS-B load the same `PurchaseRequest`. B changes Title and commits. A changes Amount and commits. **Record** what happens: which exception type (and inner chain) is thrown, or whether XAF merges silently. Assert the recorded behaviour.
  - `Touch_advances_lock_version`: load a subject, call `Concurrency.Touch(os, subject)` with **no** property change, and commit. The lock version advanced. A second object space that loaded the old version then fails its commit the same way as in the first test.
  - `Stale_user_save_behaviour`: the same race, but OS-A is a **user** object space built exactly as the demo builds one (secured provider, the demo's `OptimisticLockHandling`/`Detection` settings), and OS-B is a command that only Touches. **Record** whether OS-A's save (i) fails with the lost-race shape, or (ii) merges. In case (ii), record whether the merge re-issues a `SaveChanges` that an interceptor sees (count `SavingChanges` calls with a test interceptor). Task 7b's lock guard is only safe under (i), or under (ii) with a guard pass on the final save.
  - `Merge_is_disabled_for_commands`: if the first test shows a silent merge, find the documented way to make a **non-UI** object space fail instead of merging: the provider option `OptimisticLockHandling`, the `[OptimisticLock]` attribute on module entities, or a separate provider for commands. Apply it and make the first test fail loudly.
- [ ] **Step 3:** Set `LostRace.Is` to exactly the recorded shape, with a `DbUpdateConcurrencyException` fallback, plus `IsUniqueViolation`. Write `Concurrency.Touch` from the `Touch_advances_lock_version` finding.
- [ ] **Step 4: Gate.** **Stop and settle with the user** before Task 5 if any of these holds:
  - a lost race cannot be made to fail loudly in the command object space;
  - `Touch` cannot advance the version;
  - a stale **user** save can merge without a `SaveChanges` pass that the guard sees (`Stale_user_save_behaviour` case (ii) with no second pass). The candidate fix to bring then is a documented per-class `[OptimisticLock]` or provider setting that makes user saves fail rather than merge. Record the findings (exception type, merge setting, Touch mechanism) in the task report and in a comment at the top of `Concurrency.cs`.
- [ ] **Step 5: Commit** — `Module: pin XAF optimistic-lock behaviour; Touch + LostRace (XSM-012)`.

---

### Task 5: Immutability guard, mapper and publish lifecycle

**Files:**
- Create: `src/XafStateless.Module/Services/DefinitionGuard.cs`, `DefinitionMapper.cs`, `DefinitionStore.cs`, `XafWorkflowEnvironment.cs`, `DefinitionService.cs`
- Test: `tests/XafStateless.Module.Tests/DefinitionServiceTests.cs`

**Interfaces:**
- Consumes: the entities (Task 4), `WorkflowValidator` and `IWorkflowEnvironment` (Task 2), and `WorkflowDryRun` (Task 3).
- Produces:
```csharp
public sealed class DefinitionGuard : SaveChangesInterceptor {
  public static readonly DefinitionGuard Instance = new();
  public static IDisposable AllowPublishing();   // AsyncLocal<bool> scope
  // SavingChanges/SavingChangesAsync → Check(ctx) refuses (UserFriendlyException) anything not in this allow-list:
  //  WorkflowDefinition
  //   Added:    only with State = Draft (an Added Published/Retired row is refused, even inside AllowPublishing).
  //   Modified: if the PERSISTED State is Draft → any header field except State; State Draft→Published only inside
  //             AllowPublishing (and then PublishedBy/PublishedUtc may change too).
  //             If the persisted State is Published → only State Published→Retired, and only inside AllowPublishing.
  //             Retired → nothing. Any →Draft transition is refused.
  //   Deleted:  only when the persisted State is Draft.
  //  WorkflowState / WorkflowEffect / WorkflowTransition: Added/Modified/Deleted only when the owning definition's
  //   persisted State is Draft; re-parenting refused. Each such write Touches the owning definition (Concurrency.Touch),
  //   so a Publish racing a draft edit collides on the definition row.
  //  WorkflowInstance: Definition, SubjectType and SubjectId are immutable once set.
  //  WorkflowTask (rule ADDED IN TASK 6, which creates the scope): Instance, StateKey, Role, Subject immutable after insert;
  //   inserts and State/DecidedBy/Outcome/DecidedUtc changes only inside the runtime write scope (RuntimeScope.Current != null).
}
public static class DefinitionMapper { public static WorkflowDef ToDef(WorkflowDefinition d); }
public sealed class DefinitionStore {   // singleton; caches Published+Retired only
  public WorkflowDef Get(IObjectSpace os, Guid definitionId);
  public WorkflowDefinition? Published(IObjectSpace os, string name);
}
public static class WorkflowRoleCatalog {   // role NAMES only; the caller passes a NON-secured object space (no DI lifetime coupling)
  public static IReadOnlyList<string> Names(IObjectSpace nonSecured);
}
public static class WorkflowMembers {                                           // the one member whitelist (Design → Runtime rules)
  public static IReadOnlyList<IMemberInfo> Supported(ITypeInfo target);
}
public sealed class XafWorkflowEnvironment(IReadOnlyList<string> roleNames, ITypeInfo target) : IWorkflowEnvironment {
  // RoleExists: roleNames (from WorkflowRoleCatalog.Names) contains it (never a secured role query; designers cannot read roles).
  // CheckCriteria: CriteriaOperator.Parse + every OperandProperty resolves via target.FindMember; parse errors → message.
  // CheckEffect: SetField → member ∈ WorkflowMembers.Supported(target) (else "'ID' cannot be set by a workflow"), value converts
  //              (Convert.ChangeType / Enum.Parse / bool/Guid/DateTime, InvariantCulture);
  //              Notify → every {Name} placeholder resolves via target.FindMember.
}
public sealed record DefinitionResult(bool Ok, IReadOnlyList<string> Messages);
public sealed class DefinitionService(DefinitionStore store) {
  public WorkflowDefinition NewDraft(IObjectSpace os, string name);            // copies Published (deep: states, effects, transitions) into Draft v(max+1); if no Published, an empty Draft v1
  // roleNames = WorkflowRoleCatalog.Names(nonSecuredOs), fetched by the caller (controller/test) so designers never need role reads
  public DefinitionResult Validate(IObjectSpace os, WorkflowDefinition d, IReadOnlyList<string> roleNames);
  public DefinitionResult Publish(IObjectSpace os, WorkflowDefinition d, string caller, IReadOnlyList<string> roleNames);  // validate → in one DB transaction inside AllowPublishing: retire current Published, publish d; lost race → refusal message
  public (IReadOnlyList<Step> Steps, string? Error) DryRun(WorkflowDefinition d, IEnumerable<string> triggers, ISet<string> trueCriteria);
}
```

- [ ] **Step 1: Write the failing tests** (LocalDB via `TestDb`, with the roles `Approvers` and `Administrators` seeded):
  - `Publish_valid_draft_retires_previous`: publish v1, create NewDraft, publish v2. Assert that v1 is Retired and v2 Published.
  - `Publish_invalid_draft_lists_errors_and_stays_draft`: a draft with an orphan state. Assert `Ok == false`, the message contains `'orphan' is unreachable`, and the State is still Draft.
  - `Editing_published_rule_is_refused`: after publish, change `Transitions[0].Trigger` and commit. Assert `UserFriendlyException`. Roll back, then assert that the database value is unchanged.
  - `Publish_refuses_unconvertible_setfield`: a SetField of `Amount` = `"abc"`. The messages contain `effect on 'ordered':`.
  - `Criteria_with_unknown_member_is_refused`: criteria `Nope > 1`. The message contains `criteria on`.
  - `NewDraft_copies_published_deeply`: the counts of states, effects and transitions are equal and every Id differs.
  - `Changing_instance_definition_is_refused`.
  - `Published_header_is_frozen`: after publish, changing `InitialStateKey`, `Name` or `TargetTypeName` is refused, and so is setting State back to Draft.
  - `Inserting_published_definition_is_refused`: a new definition with `State = Published` is refused, both outside and inside `AllowPublishing`.
  - `Retired_is_frozen`: every change to a Retired definition is refused.
  - `SetField_on_ID_is_refused`: the message contains `cannot be set by a workflow`.
- [ ] **Step 2: Wire the guard first, then run the tests and confirm they fail for the right reason.** Wire it into the demo DbContext (`OnConfiguring` → `AddInterceptors(DefinitionGuard.Instance)`, only when it is not already present) **before** running the tests, because `TestDb` builds on that DbContext.
- [ ] **Step 3: Implement it.** Follow the earlier proof of concept's transition-set guard interceptor for the shape. Read the earlier proof of concept's transition-set guard and publish service for the shape. Reimplement against the new entities; do not copy the files. `Publish` gets the `DbContext` from `((EFCoreObjectSpace)os).DbContext` and uses `Database.BeginTransaction()`.
- [ ] **Step 4: Run them and confirm they pass.**
- [ ] **Step 5: Registration.** Create `src/XafStateless.Module/ServiceCollectionExtensions.cs` with `public static IServiceCollection AddXafStateless(this IServiceCollection s)`, which registers `DefinitionStore`, `DefinitionService` and (from Task 6 on) `WorkflowRuntime` as singletons, plus `TimeProvider.System` via `TryAddSingleton`, so a test or host can pre-register a fake clock. Call it from the demo `Startup.cs`. It is the **only** registration call a host makes for the module's services. Hosted services (timer, dispatcher) stay host-owned. Add a test that builds a `ServiceCollection`, calls `AddXafStateless()` and resolves each service.
- [ ] **Step 6: Commit** — `Module: definition guard, mapper, publish lifecycle (XSM-005)`.

---

### Task 6: Runtime: start, fire, decide, timers, effects

**Files:**
- Create: `src/XafStateless.Module/Services/WorkflowRuntime.cs` (`LostRace` and `Concurrency.Touch` already exist from Tasks 4/4b)
- Modify: `src/XafStateless.Module/Services/Concurrency.cs` (add `RuntimeScope`), `Services/DefinitionGuard.cs` (add the `WorkflowTask` rule)
- Test: `tests/XafStateless.Module.Tests/WorkflowRuntimeTests.cs`

**Interfaces:**
- Consumes: `DefinitionStore` (Task 5), `WorkflowMachine` (Task 3), and the entities (Task 4).
- Produces:
```csharp
public sealed record RunResult(bool Ok, string Message, IReadOnlyList<Step> Steps);
// Who is acting. Secured = the caller's secured object space (read + authorization only, never written by the runtime);
// NewCommandSpace = a factory for non-secured object spaces (INonSecuredObjectSpaceFactory), in which the runtime does its writes.
public sealed record WorkflowCaller(string Name, Func<string, bool> InRole, IObjectSpace? Secured, Func<IObjectSpace> NewCommandSpace)
{ public static WorkflowCaller System(Func<IObjectSpace> newCommandSpace) => new("system", _ => true, null, newCommandSpace); }
public sealed class WorkflowRuntime(DefinitionStore store, TimeProvider clock) {
  public RunResult Start(WorkflowCaller c, Type subjectType, Guid subjectId, string definitionName);  // pins Published; refuses if already started or none published
  public IReadOnlyList<TransitionDef> Available(WorkflowCaller c, Type subjectType, Guid subjectId); // Manual state only; filtered by RequiredRole
  public IReadOnlyList<string> Outcomes(WorkflowCaller c, Guid taskId);                              // def.From(task.StateKey) triggers, from the pinned definition
  public RunResult Fire(WorkflowCaller c, Type subjectType, Guid subjectId, string trigger);        // Manual states ONLY
  public RunResult Decide(WorkflowCaller c, Guid taskId, string outcome);                           // the only way out of a Task state
  public int RunDueTimers(Func<IObjectSpace> newCommandSpace);                                       // the only way out of a Wait state; caller "system"
}
```
- **Every operation is a command** (see Design → Runtime rules). On each attempt (up to 4, retried on `LostRace.Is`) it:
  1. creates a **new** command object space;
  2. reloads by id: the subject, instance and task. It never reuses objects from a previous attempt, because `Rollback` invalidates them;
  3. checks the **links**: the subject type is a registered, Guid-keyed `IWorkflowSubject`; `instance.SubjectType == subjectType.FullName`; `instance.SubjectId == subject.ID`; `subject.Workflow == instance`; and for Decide, `task.Instance == instance`. Any mismatch → `Ok == false`, `"workflow link mismatch"`;
  4. re-checks authorization against the reloaded, persisted rows. When `c.Secured` is set, the subject must also load via `c.Secured.GetObjectByKey`, which proves read permission;
  5. runs the machine with `onEnter = Enter`;
  6. calls `Concurrency.Touch` on the subject, the instance and (for Decide) the task, so any concurrent writer to those rows loses;
  7. calls `CommitChanges` once, then disposes the object space.
  Steps 5–7 run inside `using var scope = RuntimeScope.Open()` (`Services/Concurrency.cs`; an `AsyncLocal` holding the current scope, created **in this task**), which is disposed only after the commit returns or throws, because guards run during `SavingChanges`. A retry opens a fresh scope. Task 6 extends `DefinitionGuard` with the `WorkflowTask` rule, which needs this scope. Task 7b adds `scope.Allow(subjectId, member)` pairs to it.
- **Start** additionally refuses:
  - when `definitionName` has no Published version;
  - when `TargetTypeName != subjectType.FullName` (`"definition 'x' is for T"`);
  - when the subject already has a `Workflow`.
- **Entry-point rules:**
  - `Fire` refuses unless the current state's Kind is Manual (`"'T' can only be decided through its task"` for Task, `"'x' leaves by its timer"` for Wait), and refuses when `!c.InRole(t.RequiredRole)`.
  - `Decide` refuses when the task is not Open (`"already decided"`), when `!c.InRole(task.Role)` with `task.Role` read from the database, or when the outcome is not in `Outcomes`.
  - `RunDueTimers` fires only instances with `DueUtc <= now` whose state is Wait. It uses the **same** 4-attempt retry, re-checking "state is Wait and due" after each reload. An instance is skipped (counted as not fired, no error) only when a re-check shows another writer already moved it, or after all 4 attempts are exhausted.
  - Internally, all three call the same private `Run(...)`. Only the public entry checks differ.
- **Enter(state)**, the `onEnter` callback, runs for each entered state **before** the next Decision is evaluated:
  1. supersede the instance's open tasks;
  2. set `StateKey`, `StateCaption` and `EnteredUtc`, and clear `DueUtc`;
  3. apply the effects in `Order`:
     - `SetField` goes through `ITypeInfo.FindMember(target).SetValue(subject, converted)`, and only for members in `WorkflowMembers.Supported` (a second check after publish). Task 7b additionally records `(subjectId, member)` into the command's exemption scope;
     - `Notify` adds a `WorkflowNotification` whose body has its placeholders replaced;
  4. by Kind:
     - `Task` → a new `WorkflowTask` (Role = TaskRole, Subject = the subject's `DefaultProperty` text);
     - `Wait` → `DueUtc = now + WaitMinutes`.
  There is nothing to bump per Enter: the single commit advances each touched row's lock version once.
- Guards are evaluated with `os.GetExpressionEvaluator(subject's type, CriteriaOperator.Parse(c)).Fit(subject)` on the command object space's subject. Because it is the same object `Enter` just modified, a SetField on a Decision state is visible to that Decision's criteria. **Before implementing, verify that API against dxdocs** ("IObjectSpace GetExpressionEvaluator Fit"); if it differs, use the documented evaluator.
- `Decide` sets `Decided`, `DecidedBy`, `Outcome` and `DecidedUtc` in the same commit as the fire.

- [ ] **Step 1: Write the failing tests.** Seed the published `Fixtures`-equivalent definition through `DefinitionService` in the fixture, and use a fake `TimeProvider`. `TestDb` gains `WorkflowCaller Caller(string name, params string[] roles)`, which uses a plain object space for both `Secured` and `NewCommandSpace` (security is covered separately in Task 7).
  - `Start_pins_published_version_and_enters_initial`
  - `Submit_creates_task_and_notification`: after `Fire("Submit")`, one Open task has Role `Approvers` with `Outcomes(taskId)` = `["Approve", "Reject"]`, and one Pending notification's body contains the Title.
  - The runtime test fixture's definition adds a **Notify effect on `ordered`** (`"Ordered: {Title}"`), so the small-amount path produces an outbox row that the tests below can assert on.
  - `Decide_small_amount_reaches_ordered_and_sets_field`: `Ordered == true`, the state is `ordered`, the "Ordered:" notification exists, and the instance's lock version advanced exactly **once** (read as pinned in Task 4b), even though two states (decide-amount, ordered) were entered. Exactly one `SaveChanges` ran, counted by a test `SaveChangesInterceptor`.
  - `Fire_outside_manual_state_is_refused`: after Submit, `Fire("Approve")` returns `Ok == false` with `can only be decided through its task`, and the task is still Open. Likewise, `Fire("Cooled")` in wait-cooling before due is refused with `leaves by its timer`.
  - `Start_with_wrong_target_type_is_refused`: a definition for another type → `Ok == false`, and no instance is created.
  - `Link_mismatch_is_refused`: make the **subject back-link** inconsistent through a non-secured object space. Point subject A's `Workflow` at subject B's instance (bypassing the guard with a raw `DbContext` without interceptors). Decide on A's instance's task returns `workflow link mismatch`, because `subject.Workflow != instance`.
  - `Task_instance_binding_is_immutable`: changing an existing `WorkflowTask.Instance`, `StateKey` or `Role` is refused at save by `DefinitionGuard` (add it to the guard's allow-list: tasks are runtime-written only, and only `State`/`DecidedBy`/`Outcome`/`DecidedUtc` may change after insert, and only inside a runtime scope).
  - The tests below use **one** seam: `internal Action<string, int>? TestHook` on `WorkflowRuntime` (point, attempt number; the module gets `InternalsVisibleTo` for the test project). It is invoked with `"after-load"` after step 3 and with `"before-commit"` just before `CommitChanges`.
  - `Failure_after_effects_rolls_back_everything`: the failure is injected at the **database stage**. The test's command context sets `optionsBuilder.UseSqlServer(cs, o => o.MaxBatchSize(1))`, so every DML statement is its own `DbCommand`. A test `DbCommandInterceptor` lets the first UPDATE/INSERT **execute** (it records the command text in **both** `NonQueryExecuted` and `ReaderExecuted`, since EF may issue DML as a reader, e.g. `INSERT … OUTPUT`), then throws in `NonQueryExecuting`/`ReaderExecuting` of the next DML command. The test asserts both that one write executed and that the injection fired. After Decide (small amount), the call throws or returns `Ok == false`. In a fresh object space: `Ordered` is still false, the state is still `approval`, the task is still Open, the "Ordered:" notification does not exist, and the instance lock version is unchanged. This proves EF's SaveChanges transaction rolled back a write that had already executed.
  - `Retry_reloads_after_lost_race`: on the first `"before-commit"` only, the hook touches and commits the instance from a second object space. The operation succeeds on attempt 2, and its "Ordered:" notification exists exactly once.
  - `Large_amount_waits_until_timer`: `RunDueTimers` before due returns 0. Advance the clock 61 s; `RunDueTimers` returns 1 and the state is `ordered`.
  - `Fire_without_role_is_refused`: a transition with `RequiredRole = "Administrators"`, and a user who is not in the role. The call returns `Ok == false` and `Available` omits the trigger.
  - `Concurrent_decide_one_wins`: two Decides run on two threads. The `"after-load"` hook calls `Barrier(2).SignalAndWait(TimeSpan.FromSeconds(10))` **only on each call's first attempt** (the hook receives the attempt number; a retry skips it, otherwise the loser's retry would wait forever), and the test fails if the wait times out. This way both **command** object spaces have loaded the Open task before either commits. Exactly one returns `Ok`. The other loses at commit, retries, reloads, and returns `already decided`. There is one transition, the task ends Decided exactly once, and there is exactly one "Ordered:" notification.
  - `Instance_on_retired_version_still_fires`: start on v1, publish v2 (with a different trigger name), create a new `DefinitionStore` (a cold cache). Fire the v1 trigger on the old instance: it succeeds.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement it.** (`LostRace` was ported in Task 4 from the earlier proof of concept's `driver/DrivingSchoolAdministration/DrivingSchoolAdministration.Blazor.Server/Workflow/TaskDecisionService.cs` lines 97-105, `IsLostRace`.) Register `WorkflowRuntime` in `AddXafStateless()`. Take the shape of `RunDueTimers` from `Handlers/JourneyTimersHandler.cs` / `JourneyBaseline.RunTimers()`: due ids in id order, one object space and commit each, the retry and skip rules above, and collect other failures into an `AggregateException` that is rethrown.
- [ ] **Step 4: Run them and confirm they pass.**
- [ ] **Step 5: Add a timer host to the demo.** `demo/XafStateless.Demo.Blazor.Server/WorkflowTimerService.cs : BackgroundService` uses a `PeriodicTimer` of 30 s and calls `runtime.RunDueTimers(() => nonSecuredFactory.CreateNonSecuredObjectSpace<WorkflowInstance>())`. Verify `INonSecuredObjectSpaceFactory` with dxdocs, including its DI lifetime: if it is scoped, the service creates a scope per tick. Here, only check that the exe starts and logs one tick without errors. The **end-to-end timer check** (a large-amount request reaching `ordered` within about 90 s) moves to Task 7 Step 4, where the controllers and seed exist. A second service, `LogNotificationDispatcher : BackgroundService`, marks Pending notifications Sent and logs them (`Kept simple: log sink; the target host application plugs MailKit in Task 10`).
- [ ] **Step 6: Commit** — `Module: runtime — start, fire, decide, timers, effects (XSM-006)`.

---

### Task 7: Controllers and editor forms

**Files:**
- Create: `src/XafStateless.Module/Controllers/DefinitionController.cs`, `SubjectWorkflowController.cs`, `WorkflowTaskController.cs`
- Create: `src/XafStateless.Module/BusinessObjects/DryRunParameters.cs` (`[DomainComponent]`, non-persistent)
- Create: `src/XafStateless.Module/Services/StringLookup.cs`, `src/XafStateless.Module/Services/WorkflowPermissions.cs`
- Create: `src/XafStateless.Blazor/XafStateless.Blazor.csproj` (Razor class library, moved here from Task 8), `XafStatelessBlazorModule.cs`, `Editors/StringLookupPropertyEditor.cs`, `Editors/StringMultiLookupPropertyEditor.cs`, `Editors/StringMultiLookupEdit.razor`, `Editors/StringMultiLookupEditModel.cs`
- Modify: the entities, to add `[Appearance]` rules per Kind, `RuleRequiredField` where needed, `[EditorAlias]` on the lookup strings, and `IStringLookupSource`
- Modify: `demo/XafStateless.Demo.Blazor.Server` (reference `XafStateless.Blazor` and register `XafStatelessBlazorModule`)
- Test: `tests/XafStateless.Module.Tests/PermissionTests.cs` (required), `ControllerSmokeTests.cs` (optional; the E2E in Task 9 is the real check)

**Interfaces:**
- Consumes: `DefinitionService` (Task 5) and `WorkflowRuntime` (Task 6).
- Produces the following actions (their ids are used by the E2E in Task 9):
  - `DefinitionController : ObjectViewController<DetailView, WorkflowDefinition>`, active for the roles `Administrators` or `WorkflowDesigners`.
    - `Workflow.NewVersion` (SimpleAction)
    - `Workflow.Validate` (SimpleAction; shows the messages via `Application.ShowViewStrategy.ShowMessage`)
    - `Workflow.Publish` (SimpleAction)
    - `Workflow.DryRun` (PopupWindowShowAction with `DryRunParameters { string Path; string TrueCriteria; string Result }`)
    - Each action saves the draft first.
    - For a non-Draft definition, the controller sets `View.AllowEdit["NotDraft"] = false`. This fixes the earlier proof of concept's caveat that a Published definition was only refused at Save.
  - `SubjectWorkflowController : ViewController<DetailView>` with `TargetObjectType = typeof(IWorkflowSubject)`.
    - `Workflow.Start` (SingleChoiceAction). Its items are the Published definition names whose `TargetTypeName` equals the view's type. It is visible when `Workflow == null`.
    - `Workflow.Transition` (SingleChoiceAction, `ShowItemsOnClick`). Its items come from `runtime.Available(...)`, and it is refilled on `CurrentObjectChanged` and after execution.
  - `WorkflowTaskController : ObjectViewController<DetailView, WorkflowTask>`.
    - `Workflow.Decide` (SingleChoiceAction). Its items are `runtime.Outcomes(caller, task.ID)`. It is active when the State is Open and the user is in `task.Role`.
    - Outcomes come from the pinned definition, not code, which fixes the earlier proof of concept's hard-coded outcome list.
  - **All three workflow actions follow the command path.** They:
    1. `View.ObjectSpace.CommitChanges()` if it is modified. If that save is refused (for example a locked field), the action stops and shows the message;
    2. build a `WorkflowCaller`:
       - `Name` = the current user name;
       - `InRole` = the current user's role names;
       - `Secured` = `View.ObjectSpace`;
       - `NewCommandSpace` = `() => serviceProvider.GetRequiredService<INonSecuredObjectSpaceFactory>().CreateNonSecuredObjectSpace(typeof(WorkflowInstance))`;
    3. call the runtime with the ids;
    4. `View.ObjectSpace.Refresh()` so the view shows the committed result;
    5. show `RunResult.Message` when `!Ok`.
- Editor appearance on `WorkflowState`:
  - hide `TaskRole` unless `Kind = 'Task'`;
  - hide `WaitMinutes` unless `Kind = 'Wait'`.
- Editor appearance on `WorkflowTransition`:
  - hide `RequiredRole` when the from-state is Task, Wait or Decision (`[NotMapped] FromKind` helper property);
  - hide `Order` unless the from-state is Decision.
- **String lookups: custom Blazor property editors** (settled with the user 2026-09-25, option b; dxdocs 404597 confirms `DataSourceProperty` does not do this, and prescribes a `BlazorPropertyEditorBase` with a `DxComboBoxModel<string,string>`). The model stays string-keyed.
  - Module, UI-free, in `Services/StringLookup.cs`:
    - `public interface IStringLookupSource { IEnumerable<string> LookupValues(string memberName, Func<IReadOnlyList<string>> roleNames); }`. The **editor** supplies `roleNames`: `WorkflowRoleCatalog.Names(...)` over a non-secured object space from `application.ServiceProvider` (`IComplexViewItem.Setup`, as in that TagBox editor). The entities therefore never query roles through the designer's secured space, and do not need `IObjectSpaceLink` for roles.
    - `public static class StringLookup { public const string Single = "XafStateless.StringLookup"; public const string Multi = "XafStateless.StringMultiLookup"; }`
    - The entities implement `IStringLookupSource`. Members come from `WorkflowMembers.Supported`:
      - `WorkflowDefinition.InitialStateKey` → the definition's state keys;
      - `WorkflowTransition.FromKey`/`ToKey` → `Definition.States` keys; `RequiredRole` → role names;
      - `WorkflowState.TaskRole` → role names; `LockedMembers` (Task 7b, `Multi`) → the subject type's editable members;
      - `WorkflowEffect.Target` → role names when Kind = Notify, and the subject's editable members when Kind = SetField.
    - Mark `Kind` `[ImmediatePostData]` so the list follows the change.
    - Each such property gets `[EditorAlias(StringLookup.Single)]`.
  - Blazor, `Editors/StringLookupPropertyEditor.cs`: `[PropertyEditor(typeof(string), StringLookup.Single, false)]`, `BlazorPropertyEditorBase`, `CreateComponentModel` → `DxComboBoxModel<string,string>` with `AllowUserInput = false`, `FilteringMode = Contains`, and `Data` = `(CurrentObject as IStringLookupSource)?.LookupValues(PropertyName, roleNames) ?? []`. `Data` is reloaded in the **`ReadValueCore()` override**, which re-runs whenever the editor re-reads the object, including after an `ImmediatePostData` sibling changes.
  - Blazor, `Editors/StringMultiLookupPropertyEditor.cs` (`StringLookup.Multi`), plus `StringMultiLookupEdit.razor` and `StringMultiLookupEditModel.cs`:
    - **Pattern (settled 2026-09-25):** an existing production TagBox string-lookup editor in a sibling XAF project.
      - `TagBoxStringPropertyEditor : BlazorPropertyEditorBase, IComplexViewItem`.
      - A custom `ComponentModelBase` model whose property names match the razor `[Parameter]`s.
      - A razor component wrapping `DxTagBox` with `AllowCustomTags="false"`.
      - `ReadValueCore` sets `Value` + `LookupItems`, `GetControlValueCore` returns `Value`, `ApplyReadOnly` sets `ReadOnly`, and `CreateViewComponentCore` renders plain read-only text in list cells (the docs 402189 `InputTextModel`).
    - **Reimplement it; do not copy.** Adapt:
      - the lookup comes from `IStringLookupSource.LookupValues(PropertyName, roleNames)` instead of `[TagBoxLookup]` + a DB query;
      - the separator is `;`, not `,`;
      - drop the customer-scope filter and the Dutch strings;
      - keep the "value not in the list" item, so a stale member still shows and can be removed;
      - keep the `MaxLength` guard only if `LockedMembers` gets a size limit.
    - The single-select combo reuses the same razor-model shape if `DxComboBoxModel` alone cannot refresh `Data` from `ReadValueCore`.
  - Defense in depth: the publish validator already rejects unknown states, roles and members, so a value set through the Web API or an import is still caught.
- **Permissions**: `Services/WorkflowPermissions.cs` holds reusable seeding helpers, so hosts (the target host application) do not re-derive the matrix from Design → Runtime rules:
  - `GrantDesigner(PermissionPolicyRole r)`: CRUD on the four definition types; read on instance, task and notification; navigation to the Workflow group (Definitions, Tasks, Notifications). No role read: roles come from `WorkflowRoleCatalog`;
  - `GrantParticipant(PermissionPolicyRole r, params (Type subjectType, string navigationItemId)[] subjects)`:
    - CRUD on each subject type, with a member-level **deny write** on `Workflow` (the guard enforces it too);
    - **read-only** on `WorkflowInstance`, `WorkflowTask` and `WorkflowNotification`;
    - read on the definition types;
    - navigation to Tasks and to each subject's navigation item.
  - Verify the member-permission and navigation-permission APIs with the DevExpress docs before writing them.
- Navigation: a "Workflow" group with Definitions, Tasks (a default list view filtered to Open tasks of the user's roles via `CriteriaOperator` and `CurrentUserId`) and Notifications.

- [ ] **Step 1:** Create `XafStateless.Blazor` (Razor class library; DevExpress `DevExpress.ExpressApp.Blazor` 26.1.4) with `XafStatelessBlazorModule`. Reference it from the demo Blazor server and register the module.
- [ ] **Step 2:** Write `StringLookup`, the two editors, `IStringLookupSource` on the entities, the controllers and the appearance rules.
- [ ] **Step 3:** Seed the demo `Updater.cs`:
  - the roles `Approvers` (`GrantParticipant(r, (typeof(PurchaseRequest), "PurchaseRequest_ListView"))`; confirm the generated navigation item id) and `WorkflowDesigners` (`GrantDesigner(r)`);
  - the users Admin, `designer` (WorkflowDesigners) and `approver` (Approvers), all with the password `""` in development only, following the template's pattern;
  - a Published `purchase-request` v1 equal to `Fixtures.PurchaseRequest()`.
- [ ] **Step 3b: Permission tests** (`PermissionTests.cs`, LocalDB). They use a secured object space for a real `approver`/`designer` user: `SecuredEFCoreObjectSpaceProvider` + `SecurityStrategyComplex`; verify the constructors and the logon call with dxdocs ("unit test SecuredEFCoreObjectSpaceProvider SecurityStrategyComplex 26.1").
  - `Approver_can_start_fire_and_decide`: a `WorkflowCaller` with Secured = the approver's secured object space and NewCommandSpace = non-secured. The whole purchase-request journey succeeds.
  - `Approver_cannot_edit_task_role`: load the task in the approver's secured object space, set `Role = "WorkflowDesigners"`, commit → refused, and the database value is unchanged.
  - `Approver_cannot_create_definitions` / `Designer_can_create_draft`.
  - `Designer_can_publish_under_security`: under the designer's secured object space, `NewDraft` → add a Task state with role `Approvers` → `Validate`/`Publish`, with `roleNames` from `WorkflowRoleCatalog.Names(nonSecured)`, succeeds. Show that the designer's **secured** space cannot read `PermissionPolicyRole` (the reason for the catalogue), and that `LookupValues("TaskRole", …)` still lists `Approvers`.
  - `Approver_cannot_change_workflow_link`: in the approver's secured space, set `request.Workflow = null` and commit → refused.
- [ ] **Step 4:** Build, run the exe, and click through by hand: designer creates v2 and adds a new state "Finance check" (Task, role Approvers) between approval and the decision; Validate → errors → fix → Publish; approver starts a request and decides it. Check that a request started on v1 is still on v1. **Timer end-to-end** (moved from Task 6): start a request with Amount 5000, decide Approve, and see it reach `ordered` within about 90 s with no manual action. Check that the log shows the notification dispatched.
- [ ] **Step 5: Commit** — `Module: definition, subject and task controllers; editor forms (XSM-007)`.

---

### Task 7b: Per-state field locking (XAF StateMachine `StateAppearance` parity)

Settled with the user on 2026-09-25: yes.

**Files:**
- Modify: `src/XafStateless.Engine/WorkflowDef.cs` (add `LockedMembers` to `StateDef`), `WorkflowValidator.cs` (rule 17)
- Modify: `src/XafStateless.Module/BusinessObjects/WorkflowState.cs` (add `LockedMembers`), `Services/DefinitionMapper.cs`, `Services/XafWorkflowEnvironment.cs`
- Create: `src/XafStateless.Module/Controllers/StateLockAppearanceController.cs`, `src/XafStateless.Module/Services/StateLockGuard.cs`
- Modify: `src/XafStateless.Module/Services/WorkflowRuntime.cs` (record each SetField into the existing `RuntimeScope` via `scope.Allow`), the demo DbContext (register `StateLockGuard.Instance`)
- Test: `tests/XafStateless.Engine.Tests/WorkflowValidatorTests.cs`, `tests/XafStateless.Module.Tests/StateLockTests.cs`

**Interfaces:**
- Consumes: the entities, `DefinitionStore` and `WorkflowRuntime` (Tasks 4-6).
- Produces:
  - `StateDef` gains `IReadOnlyList<string>? Locked = null` (member names), with `public IReadOnlyList<string> LockedMembers => Locked ?? [];`.
  - `IWorkflowEnvironment` gains `string? CheckMember(string member);` (null means the member is in `WorkflowMembers.Supported`).
  - Validator rule 17: every locked member passes `CheckMember`, otherwise the error is `lock on 'x': <error>`.
  - `WorkflowState.LockedMembers`: a string that is `;`-joined in the database, edited with `[EditorAlias(StringLookup.Multi)]` (Task 7). Member names cannot contain `;`, so the delimiter is safe.
  - `StateLockAppearanceController : ViewController<DetailView>` (**DetailView only**: a ListView holds rows in different states, and a rule derived from `CurrentObject` would apply to all of them; list cells are not editable here anyway), with `TargetObjectType = typeof(IWorkflowSubject)`.
    - In `OnActivated` it takes `Frame.GetController<AppearanceController>()` and subscribes `CollectAppearanceRules`.
    - A private `Reapply()` calls `ResetRulesCache()` **then** `Refresh()` every time, because the rule set depends on the current state and a cached set would be stale. `Reapply()` runs on activation, on `CurrentObjectChanged`, and after every workflow action (the actions call it through the controller).
    - The order follows dxdocs 113374 and is the same hook XAF StateMachine uses.
    - For each locked member of the current object's state, the handler adds an `IAppearanceRuleProperties` with `TargetItems = member`, `AppearanceItemType = "ViewItem"`, `Enabled = false` and `Context = "DetailView"`.
    - It unsubscribes in `OnDeactivated`.
  - The appearance rule only affects the UI. **`StateLockGuard : SaveChangesInterceptor`** closes the gap for the Web API, imports and code: for every Modified `IWorkflowSubject` entry, it refuses (`UserFriendlyException "'Amount' is locked in state 'Approval'"`) a change to a member locked in the instance's **persisted** state. "Persisted" means the `StateKey` **queried from the database at `SavingChanges`** (`AsNoTracking`), not a tracked value that may be stale.
    - **Member-precise exemption with a commit-spanning lifetime** (review findings 1 and re-review 5):
      - The runtime's existing `RuntimeScope` (Task 6) gains an exemption set, and `scope.Allow(subjectId, member)` records one pair per SetField.
      - That scope already spans the machine run **and** `CommitChanges` (per attempt, fresh on a retry), so the guard sees the pairs during `SavingChanges`.
      - The guard exempts **only** modified properties whose pair is recorded; every other modified property is still checked.
      - Combined with the command path (the view commits through the guard first), a pending user edit can never ride along with a transition.
    - **Workflow link**: outside a runtime scope, the guard refuses any change to `IWorkflowSubject.Workflow` (`"the workflow link is managed by the workflow"`), including clearing it. Locks are resolved from the **persisted** `Workflow` value (the original value from the change tracker), so clearing the link in the same save cannot dodge a lock.
    - **Race closure**: no custom token. A user's subject save modifies the subject row, so XAF's optimistic lock advances its version. Every command also `Touch`es the subject. A save and a concurrent transition therefore collide on the subject row. A command that commits second fails and retries. A user save that commits second either fails (4b case i) or is merged through another `SavingChanges` pass that re-runs this guard against the new state (4b case ii). The 4b gate rules out the third possibility, a merge the guard never sees. No write to `WorkflowInstance` happens from a user's save, so the approver's read-only permission is never an issue.
    - The guard is exposed as `StateLockGuard.Instance`, registered next to `DefinitionGuard` in the host's `OnConfiguring`, and listed in `docs/integrating.md`.

- [ ] **Step 1: Write the failing tests**
  - Engine: `Lock_on_unknown_member_is_refused`, where `FakeEnv.CheckMember` returns an error for `"Nope"`.
  - Module:
    - `Locked_member_edit_is_refused_on_save`: purchase-request with `approval.Locked = ["Amount"]`. Submit, change Amount, commit. Assert `UserFriendlyException`, with a message that contains `'Amount' is locked`.
    - `Unlocked_member_edit_is_allowed`: Title in approval.
    - `SetField_may_write_member_locked_in_source_state`: `approval.Locked = ["Amount", "Ordered"]`. Decide (small amount) runs `ordered`'s SetField `Ordered = true` while the **persisted** state is still `approval`. It succeeds, which proves the exemption is alive during `SavingChanges`.
    - `Lock_ends_when_state_changes`: Amount is editable again after Reject, when `rejected` has no locks.
    - `Pending_locked_edit_does_not_ride_along`: in approval (Amount locked), with a Decide whose path SetFields `Ordered`. Change Amount in the user's object space, then run the controller command path (commit first). The commit is refused, the Decide never runs, and Amount is unchanged in the database. Also, directly: inside a `RuntimeScope` that allows only `Ordered`, an object space that also has a modified `Amount` is refused on `Amount`.
    - `Clearing_workflow_link_does_not_dodge_lock`: in approval (Amount locked), set `Workflow = null` **and** change Amount in one save. It is refused, and the refusal names the workflow link.
    - `Subject_save_and_transition_race_one_loses`: OS-A loads the subject in the unlocked `draft` and edits Amount. The runtime fires Submit into approval (Amount locked) and commits. Then OS-A commits. **Expected:** OS-A's first save is refused, with **either** `'Amount' is locked` (the guard queried the now-persisted `approval` state) **or** the lost-race shape pinned by Task 4b (it lost on the subject's lock version). Either is correct; the assertion is that Amount is unchanged in the database. Two separate assertions cover the pieces, and both follow the behaviour **pinned in Task 4b**: (a) with no lock configured, the same interleaving either fails with the lost-race shape (4b case i), or merges with an extra `SavingChanges` pass that the guard sees (4b case ii; assert the pass count ≥ 2); (b) after OS-A reloads and re-applies the edit, the save is refused with `'Amount' is locked`. The mirror case: OS-A commits first, then the command's commit loses, retries, and succeeds with the edited Amount (a legitimate pre-lock edit).
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement it.** Before writing it, check the concrete `IAppearanceRuleProperties` implementation to use: search DevExpress sources for a public class implementing it (the installed DevExpress sources, `DevExpress.ExpressApp.ConditionalAppearance`), and fall back to a small private implementation only when none is public.
- [ ] **Step 4: Run them and confirm they pass. Then check the UI** with the exe: in Approval, the Amount editor is disabled; after Reject, it is editable. Take one screenshot each.
- [ ] **Step 5: Commit** — `Module: per-state field locking — appearance + save guard (XSM-011)`.

---

### Task 8: Diagram (Blazor)

**Files:**
- Create (in the `XafStateless.Blazor` project that exists since Task 7): `DiagramViewItem.cs`, `Diagram.razor`, `wwwroot/diagram.js`, `wwwroot/mermaid.min.js`
- Modify: `XafStatelessBlazorModule.cs` (register the view item)

**Interfaces:**
- Consumes: `DefinitionMapper.ToDef` (Task 5) and `WorkflowMermaid.Format` (Task 3).
- Produces: a custom `ViewItem` registered for `WorkflowDefinition` DetailView as `Diagram`, added to the layout via `Model.DesignedDiffs.xafml` in the module. `Diagram.razor` calls `diagram.render(element, text)` through `IJSRuntime`, and `diagram.js` calls `mermaid.render`. The diagram re-renders after Save or Publish (`ObjectSpace.Committed`).

- [ ] **Step 1:** Download Mermaid 11 into `wwwroot`: `curl -sL https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.min.js -o src/XafStateless.Blazor/wwwroot/mermaid.min.js`. Record the exact resolved version in a comment at the top of `diagram.js`. Load it with `<script src="_content/XafStateless.Blazor/mermaid.min.js">` from the host's `_Host.cshtml`/`App.razor` (documented in `docs/integrating.md`).
- [ ] **Step 2:** Verify the XAF Blazor custom ViewItem pattern with dxdocs ("XAF Blazor custom ViewItem razor component"), then implement it.
- [ ] **Step 3:** Draw the diagram from the **current object's** definition, including an unsaved Draft (map it, call `WorkflowMermaid.Format`; no machine is built, so duplicate triggers, self-loops or unknown states cannot throw here). Show the validator messages **under** the diagram when there are any.
- [ ] **Step 4:** Check it visually. Run the exe, open purchase-request v1 and screenshot the diagram (Playwright C#, light theme plus one dark XAF theme). A trigger caption with a space (for example "Send back") must still render.
- [ ] **Step 5: Commit** — `Blazor: Mermaid diagram of the definition (XSM-008)`.

---

### Task 9: End-to-end journey and integration doc

**Files:**
- Create: `tests/XafStateless.E2E/XafStateless.E2E.csproj`, `tests/XafStateless.E2E/DesignerJourneyTests.cs`
- Create: `docs/integrating.md`
- Modify: `README.md` (Status plus a short "How to run")

- [ ] **Step 1: Write the journey test** (NUnit `PageTest`, base URL from `appsettings.e2e.json`, not an environment variable). Each step takes a screenshot:
  1. The designer logs in and runs New version on purchase-request, which creates v2.
  2. The designer adds the state `finance` (Task, role Approvers) and re-routes `approval --Approve--> finance --Approve--> decide-amount`.
  3. Publish is refused with `'finance' has no way out` while the finance edge is missing. Add the edge, and Publish succeeds.
  4. The diagram shows `finance`.
  5. The approver creates a PurchaseRequest (Amount 50) and runs Start → Transition "Submit".
  6. The approver opens the task, Decides Approve, then decides the finance task Approve. The request ends in state `Ordered`, with `Ordered` checked.
  7. A request started on v1 before step 3 still shows v1 transitions.
- [ ] **Step 2:** Run it against the built exe: check the port, start, health-check, run `dotnet test tests/XafStateless.E2E`, then kill the exe and verify it has stopped.
- [ ] **Step 3:** Write `docs/integrating.md`. Cover the host contract from Tasks 4, 5 and 7b (both interceptors, `modelBuilder.UseOptimisticLock()` **required**, `services.AddXafStateless()`, a reference to `XafStateless.Blazor` plus its module); the role seed through `WorkflowPermissions.GrantDesigner`/`GrantParticipant`; the timer (a `BackgroundService`, or a Hangfire recurring job calling `RunDueTimers`); notification delivery (implement a dispatcher over `WorkflowNotification`); the `mermaid.min.js` script tag; the role seed; and `IWorkflowSubject` on a class.
- [ ] **Step 4: Commit** — `E2E designer journey; integration guide (XSM-009)`.

---

### GATE: the user reviews the demo before Task 10

Settled with the user on 2026-09-25: "first a demo using this repo, then start the host integration". After Task 9, **stop**. Hand over the exact directory and exe to run the demo, and the E2E screenshots, and wait for an explicit go before touching the target host repository. Feedback on the demo becomes new tasks here, not changes made during the trial.

### Task 10: Trial integration into the target host application (branch, not merged)

**Files (in the target host repository, on a trial branch):**
- Modify: `the host module csproj`, `Module.cs`, `the host DbContext`, one subject class
- Modify: `the host Blazor module`, `Startup.cs`, the host page (script tag)
- Create: `a host job command for the workflow timers` and its handler, following the existing `IJobHandler<T>` pattern, plus a seeded `JobDefinition` with cron `* * * * *`

- [ ] **Step 1:** Consume the module through a **ProjectReference to this repo** (`..\..\..\XafStateless\src\...`). Packaging to NuGet waits until the trial passes (`Kept simple: ProjectReference for the trial; pack like XafLayoutBuilder once it is adopted`).
- [ ] **Step 2:** Follow `docs/integrating.md` **literally**. Every step that is missing or wrong in the doc is a doc bug: fix the doc in this repo and note it in the trial report.
- [ ] **Step 3:** Pick one existing the target host application business class as the subject. If none fits, add a minimal `ChangeRequest` class in the trial branch only, then define a three-state flow in the UI.
- [ ] **Step 4:** Build, run and walk the flow once. Confirm the Hangfire job fires a Wait. Screenshot it.
- [ ] **Step 5:** Write `docs/host-trial.md` in this repo, listing each touch point in the target host application with its line count and every friction found. Commit it here. The the target host application branch stays local and unpushed until the user decides.
- [ ] **Step 6: Commit (XafStateless)** — `the target host application trial integration report (XSM-010)`.

---

## Out of scope (deliberately)

- Per-state appearance beyond locking (colors, hiding fields). Locking is covered by Task 7b; the rest can come later if someone asks.
- A drag-and-drop diagram editor. It comes back only if forms prove too clumsy in the Task 9 journey.
- Migrating running instances to a new version. They stay pinned, the same as the earlier proof of concept.
- Multi-node timer or dispatcher safety. The target is a single instance, as the earlier proof of concept tested (`Kept simple: add a lease column when a second node appears`).
- Non-SQL-Server providers, and non-Guid subject keys (see Global Constraints).
- NuGet packaging (after the Task 10 trial).

## Settled decisions (2026-09-25)

1. Per-state field locking: **yes**, as Task 7b (the UI through `CollectAppearanceRules`, plus a save-time guard).
2. Codex plan review (12 findings, all accepted):
   - The runtime runs as a command in a non-secured object space, after the view commits, with authorization from persisted rows and a reload by id on each retry.
   - Public `Fire` works in Manual states only.
   - `onEnter` runs before each Decision.
   - The token goes up once per operation.
   - Self-loops on Manual/Task states use `PermitReentry`.
   - `WorkflowTask.Outcomes` is dropped.
   - The lock exemption covers named fields only, and a subject save bumps its instance's token.
   - There is a full permission matrix plus `WorkflowPermissions` helpers.
   - There is a single `AddXafStateless()` registration.
   - The tests are tightened: the unique-violation assertion, the injected rollback, and the retry reload.
3. Codex re-review (14 findings, all accepted):
   - Concurrency moves to XAF's built-in optimistic lock: no custom tokens, and every command Touches the rows it depends on. The Task 4b spike pins the lost-race shape and the Touch mechanism, with a stop gate.
   - The `Workflow` link is runtime-owned and link-checked.
   - Published and Retired definition headers are frozen.
   - The exemption scope spans the commit.
   - Role names come from a non-secured catalogue, with designer navigation granted.
   - Start checks the target type.
   - One whitelist decides which members effects and locks may use.
   - Tests: barrier-synchronized races, a DB-stage failure, an outbox row to assert on, and an explicit expected refusal order.
   - Decision orders must be unique, and Decision branches cannot require a role.
   - The lock appearance controller is DetailView-only and resets its cache on every reapply.
   - Guards are wired before their tests run, and the timer end-to-end check moves to Task 7.
   - Timer retry rules are unified.
   - Mermaid comes from our own formatter, so a broken draft still draws.
4. Lookups for states, roles and members: **custom Blazor string lookup editors** (option b; the model stays string-keyed), in `XafStateless.Blazor`, created in Task 7. The multi-select follows that project's `Editors/TagBox` (a custom razor `DxTagBox` + `ComponentModelBase`, refreshed in `ReadValueCore`).
5. Order: **the demo in this repo first**, with a gate after Task 9, **then** the the target host application trial.
