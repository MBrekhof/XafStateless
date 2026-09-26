# Session handoff — 2026-09-26

## Where things stand

Branch `poc/xafstateless` (pushed; not merged to `master`). Plan: `docs/superpowers/plans/2026-09-25-xafstateless.md`.

- Tasks 1–9 of the plan are implemented, task-reviewed and fixed (cards XSM-001…XSM-009, XSM-011, XSM-012).
- A final whole-branch review ran; its Critical/Important findings were fixed in `5152782` and re-reviewed clean.
- Tests: `dotnet test XafStateless.slnx` → Engine 37, Module 87, all green. E2E (`tests/XafStateless.E2E`, needs the demo running on a freshly seeded database — see README) passed 1/1.
- Docs: `README.md` (pitch, comparison with XAF StateMachine, screenshots, how to run), `docs/integrating.md` (host contract + limits), architecture diagram `docs/architecture/xafstateless-architecture.html`.

## Demo gate — waiting for you

Task 10 (trial integration into the target host application, card XSM-010) is **not started**. The plan stops here: review the demo first, then give an explicit go.

Run the demo: see README "How to run" (build, then the exe under `demo/XafStateless.Demo.Blazor.Server/bin/Debug/net10.0/`). Users `designer` / `approver` / `Admin`, empty passwords (development only).

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
