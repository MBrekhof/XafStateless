# Session handoff — 2026-11-07

## This session (2026-11-06/07): a Set field takes an expression (CARD-2895, for BPG CARD-2816/2817)

- `b18c388`: a SetField Value starting with `=` is XAF criteria evaluated on the subject in the runtime's command space
  when the state is entered (`=[Project.Manager]`); its target may be a reference. Publish walks the criteria's paths on
  XAF type info (browsable, exact names, no user secrets on any XAF user interface, no joins/casts/`^.`) and types the
  result with DevExpress's criteria-to-LINQ converter; the runtime walks the paths again. `Write` now loads a member
  before writing and exempts every name of its column (`RuntimeScope.AllowColumn`). `ConvertOutput` takes enums through
  their underlying type and refuses NaN/infinities. Demo `PurchaseRequest.Approver` (demo schema only; no library schema
  change). **Compatibility:** a constant starting with `=` is now an expression (`='=x'` for the literal).
- Gemini reviewed plan and diff; every finding settled in `docs/plans/2026-11-06-card-2895-setfield-expression.md`.
  New cards: CARD-2908 (decision: non-admin `GrantDesigner` roles can copy unreadable data through an expression),
  CARD-2909 (Notify/Run action placeholders can name user secrets, unverified).
- **Not released.** 0.5.0 = CARD-2871 + CARD-2895, waits for Martin's go; BPG then maps `"=..."` straight through.
- Tests: Engine 59, Module 323; 10 mutants killed. No E2E run (no UI change beyond the Target lookup listing references).

# Previous session — 2026-11-06

## This session (2026-11-06): requester and assignee rows read their own instance (CARD-2871)

- `10f93f8`: `GrantRequesterRows` / `GrantAssigneeRows` (and so `GrantParticipant`) also write one `WorkflowInstance` row
  per Allow own-notification / own-task row, `[<N>][[Instance.ID] = [^.ID] And (that row)]`, rebuilt from those rows on
  every call (`WorkflowPermissions.InstanceRows`). A `@Member` recipient whose role reads the subject but has no role
  rows now sees the in-app notification text and the task's instance. No schema change; hosts get the rows on their
  next updater run.
- Gemini reviewed plan and diff; the plan's first design (two extra row families) drifted on roles seeded before, so
  the rows are now derived from the own rows. Everything settled in
  `docs/plans/2026-11-06-card-2871-instance-read-for-user-rows.md`.
- Tests: Engine 59, Module 259, E2E 5/5; three mutations of `InstanceRows` killed.
- **0.5.0 published** to the private feed (Engine, Module, Blazor; Martin's go 2026-11-07), release commit `70a44f2`:
  CARD-2788 (notification snapshot: schema change `WorkflowNotifications.Snapshot nvarchar(max) NULL`), CARD-2794
  (DevExpress range `[26.1.6,26.2)`: hosts must be on 26.1.6), CARD-2871. Packed and tested in a clean worktree of
  `70a44f2`, because another session (CARD-2895, SetField expressions) had uncommitted work in this checkout; none of it
  is in 0.5.0. A scratch restore of `XafStateless.Blazor` 0.5.0 from the feed resolves Module/Engine 0.5.0 and
  DevExpress 26.1.6. Next for BPG: bump to 0.5.0, add the CARD-2788 E2E check, drop the interim `GrantRoleRows` grant
  (BPG CARD-2615).
- Decisions (Martin): CARD-2810 → Middle Tier stays unsupported (already documented; card in Review). XSM-030 → not now,
  card in Backlog until BPG WF-008 needs fork/join.

# Previous session — 2026-11-01 (late)

## This session (2026-11-01, late): a notification keeps its values (CARD-2788, CARD-2783)

- `881842f`, `a20ea26`, `0bed660` (branch `card-2788`, fast-forwarded into master): the runtime stores each Notify
  placeholder's text at notify time plus the records it read, in the shadow column `WorkflowNotifications.Snapshot`
  (declared by `ConfigureXafStateless`; no XAF member). `Render` replays the permission checks per recipient on those
  records, loaded again through a secured query. A criteria-hidden or deleted record masks, with or without prefetch
  (CARD-2783's docs sentence is fixed by behaviour). Calculated members, collections, composite keys and references
  without a stored default member render live, as 0.4 did. **Schema change**: `WorkflowNotifications.Snapshot
  nvarchar(max) NULL`. Demo `PurchaseRequest.Summary` ([NotMapped], hidden) exists for the tests.
- Three Gemini reviews (plan, diff, diff); each finding settled in
  `docs/plans/2026-11-01-card-2788-notification-snapshot.md`. Findings left open became cards: temporary IDENTITY keys
  (CARD-2809), Middle Tier exposure (CARD-2810, a decision), untested chained default member (CARD-2811), guards reading
  the navigation before the FK (CARD-2812).
- **Not released.** 0.5.0 waits for Martin's go; BPG then bumps and adds the E2E check from CARD-2788.
- `0df3de4`: Martin's DevExpress 26.1.6 bump committed (his go): the demo pins 26.1.6, the packages' range is
  `[26.1.6,26.2)` from 0.5.0 on (hosts must be on 26.1.6; BPGDemo already resolves it). On 26.1.6 a combo box sets
  `aria-controls` only once its dropdown is open, which broke the E2E `Pick` helper (CARD-2794); it now waits for the
  open dropdown. E2E 5/5 on 26.1.6, three runs.
- Board: CARD-2778 (MainPage NRE) closed as XAF's own (`MainPage.razor.cs:317-348`); CARD-2767 (flaky fixtures) not
  reproduced in three runs, hypothesis on the card, moved to Backlog.
- Tests: Engine 59, Module 248, E2E 5/5; 16 of 17 mutations killed (survivor = CARD-2811).
- Scratchpad tools worth keeping in mind: an E2E cycle script (fresh `XafStatelessE2E`, seed, run, stop, drop) and a
  mutation runner; both were session-local.

# Previous session — 2026-11-01

## This session (2026-11-01): mail bodies rendered per recipient (CARD-2597), reference locks (CARD-2735)

- Branch `card-2597-2735` (commit hash in git log; fast-forwarded into master): a Notify stores its template
  (`WorkflowNotification.Body`, new column `IsTemplate`: a **schema change**); hosts render it per recipient with
  `WorkflowNotifications.Render(note, securedSpace, security)`, which masks what that recipient may not read. The
  in-app list shows `WorkflowNotification.Text`, rendered for the viewer. A state can lock a reference member
  (`WorkflowMembers.Lockable`; `StateLockGuard` judges its foreign key). Plan, reviews and verification:
  `docs/plans/2026-11-01-card-2597-2735-per-recipient-mail-and-reference-locks.md`.
- **0.4.0 published** to the private feed (Engine, Module, Blazor; Martin's go 2026-11-01): CARD-2615 (1), CARD-2713,
  CARD-2597, CARD-2735. Schema change for every host (WorkflowTask UserId/UserName + index, WorkflowNotification
  IsTemplate). DevExpress range still [26.1.4,26.2): Martin's uncommitted 26.1.6 bump is not in it. Then BPGDemo's
  `DispatchWorkflowNotificationsHandler` must call `Render` per recipient inside its signed-in scope (it mails
  `notification.Body` today, which would now be the raw template), and BPG's `SpecWorkflowChecks` resolves
  `LockedPropertyIds` against relations too.
- Reviews ran on Codex again (Gemini's quota out all session).
- Tests: Engine 59, Module 217, E2E 5/5, browser smoke.

## Previous session (2026-10-31/11-01): user-member assignee and Notify (CARD-2615 (1)), locks before the start (CARD-2713)

- `e1c816f` (branch `card-2615-2713`, fast-forwarded into master): a Task state's role or a Notify recipient can be a
  user member of the subject, `@Buyer` (a reference to a Guid-keyed `ISecurityUser`). The task gets
  `WorkflowTask.UserId/UserName` (**schema change**: two columns and a `(UserId, State)` index) and only that user
  decides it; an empty or inactive member refuses entering the task (Martin); the task keeps its user when the member
  changes (Martin). New `WorkflowPermissions.GrantAssigneeRows` (GrantParticipant includes it). A subject without a
  workflow link is locked as in the initial state of every Published definition for its type, in `StateLockGuard` and
  `StateLockAppearanceController` (CARD-2713). Plan, both reviews and verification:
  `docs/plans/2026-10-31-card-2615-2713-user-assignee-and-unstarted-locks.md`.
- Reviews ran on Codex (gpt-6-luna): Gemini's quota was used up for the session.
- **Not released.** 0.4.0 waits for Martin's go; BPG then bumps its reference and uses `@<member>` in
  `SpecWorkflowChecks`/`WorkflowSeedEmitter` and calls `GrantAssigneeRows`.
- The uncommitted DevExpress 26.1.6 bump in the main tree stays out of this work (Martin).
- Tests: Engine 59, Module 196, E2E 5/5 (fresh `XafStatelessE2E`), browser smoke of both features.
- Demo launch traps (README fixed): the E2E connection-string argument must come **first** (after
  `--updateDatabase --forceUpdate --silent` it is swallowed as `--silent`'s value and the seed updates
  `XafStatelessDemo`, which happened twice this session: it got this branch's nullable columns); the exe run from
  `bin` needs `--environment Development` or the packages' static files 404 and the login page never renders.

# Previous session — 2026-10-22

## This session (2026-10-21/22): scoped participant rows, release 0.3.0

- `8177ad8`: a host can scope a participant role's runtime rows by a criteria on the subject
  (`GrantParticipant(role, ParticipantSubject, ...)`, `GrantRoleRows`, `GrantRequesterRows`). A scope of `"False"`
  hides every row from a role that cannot read the subject.
- `45f7273`: **0.3.0 published** to the private feed (Engine, Module, Blazor). It includes XSM-035 (rule sets, three new
  entities = a schema change for every host) and WF-010. New Engine deps: FastExpressionCompiler 5.0.2,
  FluentValidation 11.11.0, System.Linq.Dynamic.Core 1.6.2. The vulnerability warnings on restore (MessagePack,
  System.Security.Cryptography.Pkcs/Xml) come through DevExpress 26.1.4 and were already there in 0.2.1.
- `1424f34`: tests only, in `ScopedParticipantTests`. The first checks that a `"False"` scope hides every row. The second
  covers the grant set for a role that can write the subject but isn't named in the workflow: it reads the definition
  and its scoped instances, it can Start and Fire, and `StateLockGuard` refuses any change to the workflow link outside
  the runtime.
- E2E needs a freshly seeded catalog (`XafStatelessE2E`, see README): the demo DB `XafStatelessDemo` was stale and made
  `Designer_adds_a_finance_step...` time out.
- Tests: Engine 58, Module 175, E2E 5/5.

# Previous session — 2026-10-21

## This session (2026-10-21): WF-010, a commit refused by the host's save validation

A host that validates every commit (an `IObjectSpace.Committing` handler throwing XAF's `ValidationException`, an
`IUserFriendlyException` but not a `UserFriendlyException`, as BPG's generated save validation does) broke two paths.
`DefinitionService.Publish` now catches `IUserFriendlyException` and returns the rule's message. `RunDueTimers` skips a
refused timer and logs a warning through a new optional `ILogger<WorkflowRuntime>` constructor parameter (Martin:
"skip + warn"). The instance stays due, so the warning repeats each tick. Other exceptions still fail the tick. The demo's
batch Start no longer stops at a refused request. Plan: `docs/plans/2026-10-20-wf-010-validation-refusals.md`; test
helper `tests/XafStateless.Module.Tests/RefusingSaves.cs`. Tests: Engine 58, Module 153, green. Not released: it ships
with master's next release (0.3.0 or higher).

# Previous session — 2026-10-20

## This session (2026-10-20): 0.2.1 security patch, diagram labels drew markup as HTML

Reported by a downstream host and reproduced in Chromium: on the published 0.2.0 (Mermaid 11, default HTML labels,
`innerHTML`), a state caption such as `<img src=//example.invalid/pixel.png>Draft` (also a trigger, task role, criteria
or locked member) became a live `<img>` and the browser fetched the URL. The renderer swap of 2026-10-18 (5811008,
beautiful-mermaid escapes label text; only attribute-free `<b>`/`<i>`/`<u>`/`<s>` become SVG `<tspan>`) already fixes it,
but it was never published. `WorkflowMermaid.Escape` keeps `<` and `>` on purpose (`Amount > 1000` is a legitimate caption).

Decision (Martin away, taken by Claude, Codex agreed): release **0.2.1 from branch `release/0.2.1` = 5811008 + this
patch**, not from master. Master's XSM-035 commits (rules engine, three new entities = a schema change for every host)
had no release decision. 0.2.0 → 0.2.1 has no schema change; hosts do see the new diagram look (ELK layout).
New regression test `tests/XafStateless.E2E/DiagramMarkupTests.cs` (no demo needed): fails on 0.2.0's scripts (5 live
`<img>`), passes on 0.2.1, light and dark. Tests: `dotnet test XafStateless.slnx` → Engine 53, Module 141, green.
The branch is merged into master; the next master release must be 0.3.0 or higher.

# Previous session — 2026-10-18

## This session (2026-10-18): Diagram renderer switched to beautiful-mermaid 1.1.3

Replaced the bundled Mermaid 11.17.2 (3.5 MB, Dagre) in `src/XafStateless.Blazor/wwwroot/mermaid.min.js` with a minified
browser build of [`beautiful-mermaid`](https://github.com/lukilabs/beautiful-mermaid) 1.1.3 (1.5 MB, ELK orthogonal
layout, synchronous `renderMermaidSVG`). Updated `src/XafStateless.Blazor/wwwroot/diagram.js` to bridge `classDef`/`class`
statements onto `<g class="node" data-id="...">` in declaration order (`kind*` → `problem` → `current`), strip the Google
Fonts `@import` for offline rendering, and switch light/dark themes in place via `--bg`/`--fg` CSS variables. Refreshed
`docs/screenshots/diagram-v2-landscape.png` and `docs/integrating.md` §5. Tests: `dotnet test XafStateless.slnx` → Engine
53, Module 141; `dotnet test tests\XafStateless.E2E` → 3/3, all green.

# Previous session — 2026-10-16

## This session (2026-10-16): Diagram "Show details" toggle

Added a `Show details` / `Hide details` toggle button to the Mermaid workflow diagram (`Diagram.razor`, `DiagramModel`,
`DiagramViewItem`, `WorkflowMermaid` with `DiagramStyle(Details: true)`). When enabled, state boxes include task roles,
wait durations (`min`), and locked fields (`locks ...`), and transition labels include required roles (`(...)`) and
criteria (`[...]`). Bumped `PackageVersion` to `0.2.0` and published `XafStateless.Engine`, `XafStateless.Module`, and
`XafStateless.Blazor` `0.2.0` to the package feed. Tests: `dotnet test XafStateless.slnx` → Engine 53, Module 141, all green.

# Previous session — 2026-10-05

## This session (2026-10-05): XSM-034, the public-readiness pass (`a4c1c03`, Review)

Martin decided the repo will become public. The tree was scrubbed of shop-internal context (private host product,
sibling project, machine paths, board, tool-skill and `ponytail:` markers); `docs/host-trial.md` is host-neutral and
five screenshots that showed the host's name are gone; `docs/superpowers/plans` is now `docs/plans`; the architecture
PNGs were regenerated. No history rewrite (the scan found no secrets). Kept on purpose: Author metadata, repo URLs,
XSM ids in code, Codex attributions in docs, and this file. Still Martin's call before going public: whether this
handoff file stays in a public tree, and whether the packages move from the private GitHub feed to nuget.org.

# Previous session — 2026-10-04

## Where things stand

`master` is the only branch. Tests: `dotnet test XafStateless.slnx` → Engine 52, Module 141, all green. E2E
(`tests/XafStateless.E2E`, three tests, ordered; needs the demo on a freshly seeded database, see README) passes 3/3.
Docs: `README.md`, `docs/integrating.md` (host contract, steps 1–12), `docs/host-trial.md`, architecture diagram
`docs/architecture/xafstateless-architecture.html` (components unchanged this session, not redrawn).

## This session (2026-10-04): the four open cards, easiest first, Codex reviewing every plan and diff

- **XSM-031 `5383939` (Review)**: `EffectKind.Increment` adds a step (Value, "" = 1) to a whole-number or decimal member;
  a Decision after it sees the new value, so a workflow can limit attempts. Enums and floats excluded; past the member's
  range the command is refused with nothing committed. Demo: `PurchaseRequest.Revisions` (int?).
- **XSM-032 `e731fe9` (Review)**: `EffectKind.RunAction` runs a host-registered `IWorkflowAction` (DI; `WorkflowActionCatalog`)
  inside the transition's command and object space, for records other than the subject. Actions are **trusted host
  code** (contract in `IWorkflowAction` and integrating.md step 10; not enforced). `IStringLookupSource` gained a
  3-argument `LookupValues` (action names). Demo action `log`.
- **XSM-033 `8ac12bb` (Review)**: entry screens. `IEntryScreen` + `EntryScreenController` (module), demo "Start requests"
  screen and controller, host Startup handler populating additional object spaces, and
  `WorkflowCommand.Caller(app, os, subjectType)`: a screen's command must carry the user's **secured** object space,
  resolved behind the non-persistent one; otherwise the runtime's Write check is skipped (Codex caught that
  `CanInstantiate` would return the composite; `IsKnownType(type, false)` is used). integrating.md step 11.
- **XSM-030 (Todo, design only, awaiting Martin)**: `docs/plans/2026-10-04-xsm-030-fork-join-design.md`.
  Option A (branches as child instances) recommended by me and Codex; Codex's cost 7–10 days, not my 4–5; its smallest
  honest slice and the decisions Martin must take are on the card and in the note. **No code written.**
- Smaller: the demo seed uses `nameof(PurchaseRequest.Amount)` / `Ordered` (`5aeea01`); Admin only decides tasks after a
  new logon with the Approvers role (role list is read at logon), and a fresh database has no requests, so no tasks.

## Things learned (keep)

- **XAF 26.1 Blazor restores a user's open tabs at the next login and caps them at 10** (`BlazorMdiShowViewStrategy.MaxTabLimit`;
  an eleventh view silently does not open). The demo raises the cap to 30 and closes the oldest tab instead of blocking
  (`Program.cs`); the E2E tests still run in a fixed order (`[Order]`) and the entry-screen test closes restored tabs
  first (`CloseAllTabs`), so they stay valid on a host with the default. A view opened for an object already open in another tab is skipped in
  favour of that (stale) tab: the Start controller opens the request in the screen's own tab (`TargetWindow.Current`).
- The E2E cycle, in one go (PowerShell or bash, from the repo root): drop `XafStatelessE2E`, run the demo exe with
  `--updateDatabase --forceUpdate --silent` and the E2E connection string, start it on :5000 with the same string, wait for
  HTTP 200, `dotnet test tests\XafStateless.E2E --no-build`, kill the exe, drop the database. Screenshots land in
  `tests/XafStateless.E2E/screenshots/` (gitignored); failures in OpenTask and the entry test take `zz-*` shots.
- Codex (plugin 1.0.5, codex-cli 0.157.x): a review started through the `codex:codex-rescue` subagent returns a task id;
  wait with the `until ... status ... | grep running` loop in the background, then `result <id>`. Its findings this session
  were all real (float rounding in decimal arithmetic, the sum overflow, the `CanInstantiate` trap, the unique index that
  would block fork children). The stop-time review gate for this repo is still off; turning it on is Martin's call.

## Open on the board (XafStateless)

XSM-030 (Todo, needs Martin's decisions above). XSM-029 parked in Backlog (automation timing; reopen on a user report).
XSM-031/032/033 in Review for Confirm Done.
