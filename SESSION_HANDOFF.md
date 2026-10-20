# Session handoff — 2026-10-21

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
