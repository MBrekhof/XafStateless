# Session handoff — 2026-09-26

## Where things stand

`master` is the only branch (`poc/xafstateless` deleted 2026-09-26; it held nothing `master` lacks). Plan: `docs/superpowers/plans/2026-09-25-xafstateless.md`.

- Tasks 1–9 of the plan are implemented, task-reviewed and fixed (cards XSM-001…XSM-009, XSM-011, XSM-012).
- A final whole-branch review ran; its Critical/Important findings were fixed in `5152782` and re-reviewed clean.
- Tests: `dotnet test XafStateless.slnx` → Engine 37, Module 95, all green. E2E (`tests/XafStateless.E2E`, needs the demo running on a freshly seeded database — see README) passed 1/1.
- Docs: `README.md` (pitch, comparison with XAF StateMachine, screenshots, how to run), `docs/integrating.md` (host contract + limits), architecture diagram `docs/architecture/xafstateless-architecture.html`.

## BPGDemo trial — done

The trial integration (plan `docs/superpowers/plans/2026-09-26-bpgdemo-trial.md`, XSM-015/016/017 + report XSM-010) ran on
2026-09-26. Result and findings: `docs/host-trial.md`; doc gaps it found are fixed in `docs/integrating.md`.

- The trial's code is kept on the local BPGDemo branch `trial/xafstateless` (commits `d5b1f93`, `e804d3c`, `b63164f`, never
  pushed); BPGDemo itself stays on `master`. The trial worktree and database `bpgdemoTrial` are removed; recreate them with
  `git -C C:\Projects\BPGDemo worktree add C:\Projects\BPGDemo.trial trial/xafstateless` and the plan's Task 1 update step.
- Follow-up cards: XafStateless XSM-018 (Version 0), XSM-019 (a11y), XSM-020 (icon/caption), XSM-021 (idempotent grants);
  bpg WF-007 (hook bypass), JOBS-001 (job plumbing), AUTO-003 (outbox Name, unverified), CARD-1825 (tabs, second sighting).
- Tests: Engine 37, Module 95.

## Decisions taken on your behalf (highlights)

The full list with costs is in the final session message. The ones most worth a second look:

- In-flight subjects cannot be deleted (any role) — finish or reject first.
- Start and Fire require Write permission on the subject; Read alone is not enough.
- Instance state, task decisions and outbox inserts are writable only by the runtime, for every role including Administrators.
- Admins see every open task (XSM-013); everyone else sees the tasks for their role names. Deciding still needs the task's role.
- The E2E project is outside `XafStateless.slnx` (it needs a running demo).
- Demo seed locks Amount in the Approval state.
