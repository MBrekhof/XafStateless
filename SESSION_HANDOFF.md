# Session handoff — 2026-09-26

## Where things stand

`master` is the only branch (`poc/xafstateless` deleted 2026-09-26; it held nothing `master` lacks). Plan: `docs/superpowers/plans/2026-09-25-xafstateless.md`.

- Tasks 1–9 of the plan are implemented, task-reviewed and fixed (cards XSM-001…XSM-009, XSM-011, XSM-012).
- A final whole-branch review ran; its Critical/Important findings were fixed in `5152782` and re-reviewed clean.
- Tests: `dotnet test XafStateless.slnx` → Engine 52, Module 116, all green. E2E (`tests/XafStateless.E2E`, needs the demo running on a freshly seeded database — see README) passes 2/2.
- Docs: `README.md` (pitch, comparison with XAF StateMachine, screenshots, how to run), `docs/integrating.md` (host contract + limits), architecture diagram `docs/architecture/xafstateless-architecture.html`.

## BPGDemo trial — done

The trial integration (plan `docs/superpowers/plans/2026-09-26-bpgdemo-trial.md`, XSM-015/016/017 + report XSM-010) ran on
2026-09-26. Result and findings: `docs/host-trial.md`; doc gaps it found are fixed in `docs/integrating.md`.

- The trial's code is kept on the local BPGDemo branch `trial/xafstateless` (commits `d5b1f93`, `e804d3c`, `b63164f`, never
  pushed); BPGDemo itself stays on `master`. The trial worktree and database `bpgdemoTrial` are removed; recreate them with
  `git -C C:\Projects\BPGDemo worktree add C:\Projects\BPGDemo.trial trial/xafstateless` and the plan's Task 1 update step.
- Follow-up cards on the bpg board (for a BPG/BPGDemo session): WF-007 (hook bypass), JOBS-001 (job plumbing),
  AUTO-003 (outbox Name, unverified), CARD-1825 (tabs, second sighting).

## Follow-ups done (2026-09-26, all in Review)

- XSM-018 `b24ad47`: a definition made with New is numbered max+1 per name, read past security, renumbered after a refused save.
- XSM-021 `b01d81d`: role grants are safe to repeat on every update; the demo and §6 grant on every run.
- XSM-019/020 `a26833a`: workflow images on the module's classes; the E2E journey checks effect editors by role and name.
- XSM-014 `fd4a44d`: Notify can address `@requester`, the user who started the workflow; participants see their own
  notifications. Guards fix the requester and written notifications.
- Architecture diagram and README screenshots refreshed.
- E2E runs against a separate catalog so `XafStatelessDemo` stays untouched: pass
  `--ConnectionStrings:ConnectionString=...Initial Catalog=XafStatelessE2E` to both demo exe calls (seed and run).
  One E2E run failed at opening the finance task while the machine was busy with a Codex review; reruns passed.
  (That failure turned out to be XSM-029, below.)

## Designer round (2026-09-26/27, from the user's demo feedback; card lanes are on the board)

- XSM-022 `058bb82`: Target Type Name picked from the classes implementing IWorkflowSubject; the transition's Trigger
  reads "Action name". XSM-023 `058bb82`: diagram zoom, fit and portrait/landscape.
- XSM-024 `072b724`: the validator names the cause (e.g. "no state is Final") instead of symptoms, as findings tagged
  with their state.
- XSM-025 `3a524ff`: the Target Type list shows class captions, stores the full name.
- XSM-027 + XSM-028 `ad31fbb`: validation while editing (state problems as red rows and red diagram states, the rest as
  messages under the diagram; Publish and Dry run disabled until valid; Validate without saving); diagram colored by step type with a legend; a request's instance view
  shows its diagram with the current state highlighted.
- XSM-026 (spike): design note `docs/ai-assist.md`: AI drafts and fixes that the validator gates, never publishes.
- XSM-029 (parked, low priority): Decide once executed Reject when the E2E clicked Approve, about 1 run in 8, only
  when clicking within milliseconds of the menu opening. Judged an automation timing effect, not a user-facing bug
  (agreed 2026-09-27); the E2E waits 300 ms. Reopen if a user reports "approved, but it got rejected".
- Codex: the daemon works again since codex-cli 0.157.1 (`--no-daemon` no longer needed). Its sandbox sometimes cannot
  read files; the review script then embeds the diff in the prompt. The plugin's stop-time review gate is still off for
  this repo; turning it back on is your call.

## BPG decision (2026-09-26)

BPG adopts XafStateless as the workflow engine for generated apps (bpg WF-008, depends on WF-007). Automation rules stay
for one-step "on save" logic; generated Stateless code and Elsa (bpg WF-001 to WF-006) are dropped. WF-008 lists the
work: packaging, the hook bypass, skeleton wiring, the generator writing spec workflows as seeded definitions, and
checking the WF-001/002 scenarios (parallel branch with join, compensation, attempt-limited loops) against this module.

## Decisions taken on your behalf (highlights)

The full list with costs is in the final session message. The ones most worth a second look:

- In-flight subjects cannot be deleted (any role) — finish or reject first.
- Start and Fire require Write permission on the subject; Read alone is not enough.
- Instance state, task decisions and outbox inserts are writable only by the runtime, for every role including Administrators.
- Admins see every open task (XSM-013); everyone else sees the tasks for their role names. Deciding still needs the task's role.
- The E2E project is outside `XafStateless.slnx` (it needs a running demo).
- Demo seed locks Amount in the Approval state.
