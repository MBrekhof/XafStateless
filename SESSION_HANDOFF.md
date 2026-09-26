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

- Cleaned up 2026-09-26: the trial worktree, branch `trial/xafstateless` and database `bpgdemoTrial` are deleted;
  BPGDemo is back to `master`. The trial's changes are described in `docs/host-trial.md` (Touch points).
- Follow-up cards: XafStateless XSM-018 (Version 0), XSM-019 (a11y), XSM-020 (icon/caption), XSM-021 (idempotent grants);
  bpg WF-007 (hook bypass), JOBS-001 (job plumbing), AUTO-003 (outbox Name, unverified), CARD-1825 (tabs, second sighting).
- Tests: Engine 37, Module 95.

## Needs your action

1. ~~Codex pass over `739823c..9293a0a`~~ — done 2026-09-26 after `codex login`: 3 StateLockGuard defects (forged soft delete, value converters, cross-type Guid collision) plus type-keyed runtime exemptions, all reproduced by failing tests and fixed in `600c782`, `819c4e2`; Codex confirmed closed.
2. **History still contains the old project names.** Current files are clean; earlier commits (plan, README, one demo comment) still mention them. Rewriting history was not done (destructive). Decide whether that matters for this private repo.
3. Two parked doc items (no second fix wave was allowed):
   - `docs/integrating.md` §6: a host that builds its own `WorkflowCaller` over a secured object space must pass the `IRequestSecurityStrategy`, otherwise Start/Fire are refused with the Write message.
   - Limits: a database-level `ON DELETE CASCADE` of a subject that was never loaded bypasses the delete guard (same class as `ExecuteDelete`/raw SQL).

## Decisions taken on your behalf (highlights)

The full list with costs is in the final session message. The ones most worth a second look:

- In-flight subjects cannot be deleted (any role) — finish or reject first.
- Start and Fire require Write permission on the subject; Read alone is not enough.
- Instance state, task decisions and outbox inserts are writable only by the runtime, for every role including Administrators.
- Admins do not see the Tasks list (it is filtered by the user's role names).
- The E2E project is outside `XafStateless.slnx` (it needs a running demo).
- Demo seed locks Amount in the Approval state.
