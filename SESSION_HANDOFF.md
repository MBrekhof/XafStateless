# Session handoff — 2026-10-04

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
