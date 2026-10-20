# WF-010: a commit refused by the host's save validation

A host may validate every commit (an `IObjectSpace.Committing` handler that throws XAF's
`DevExpress.Persistent.Validation.ValidationException`; per the DevExpress docs it is `Exception, IUserFriendlyException`,
not a `UserFriendlyException`). Reproduced 2026-10-20 with such a handler in the Module tests (`RefusingSaves`):

- `DefinitionService.Publish` catches `LostRace` and `UserFriendlyException` only: a `ValidationException` from either
  commit inside the publish transaction escapes `Publish`, the transaction rolls back, and the object space is not
  refreshed (it still holds what the first commit accepted: the old version as Retired).
- `WorkflowRuntime.RunDueTimers`: a Wait instance whose subject breaks a rule cannot commit (the runtime touches the
  subject, so the host validates it). Every tick throws an `AggregateException` with the `ValidationException`; the other
  due instances still fire. The instance stays due, so the next tick fails again.
- A user's Fire/Decide on such a subject throws the `ValidationException` to the controller; XAF shows an
  `IUserFriendlyException` as a message and the view's object space is untouched. Fine, unchanged.

## Change

1. `Publish`: `catch (Exception ex) when (LostRace.Is(ex) || ex is IUserFriendlyException)` (was `UserFriendlyException`):
   refresh, return `(false, [ex.Message])`.
2. `RunDueTimers` (Martin, 2026-10-20: "skip + warn"): an instance whose command throws an `IUserFriendlyException` is
   refused, not failed: not counted as fired, not added to the failures, logged as a warning
   (`Workflow timer of instance {InstanceId} refused: {Reason}`) through a new optional `ILogger<WorkflowRuntime>?`
   constructor parameter (DI resolves it in any ASP.NET host; null = no log). The instance stays due, so the warning
   repeats each tick until the record is fixed (accepted). Any other exception is still collected and rethrown as the
   `AggregateException`.
   This covers the runtime's own refusals too (`UserFriendlyException` from an Increment past its range or a rule set
   that cannot set a member, inside a timer): today they also fail every tick; they are the same kind of "this record
   cannot move on" and get the same treatment.
3. `docs/integrating.md` §8: say that a refused instance is logged as a warning and stays due.
4. `WorkflowRuntime` public constructor gains a trailing optional parameter: source compatible; binary compatible only
   with a rebuild (the package is consumed as source-built NuGet, hosts rebuild on upgrade). Patch release 0.3.0? No:
   master already holds XSM-035 (schema change, unreleased); this fix ships with master's next release. A 0.2.2 from
   release/0.2.1 is possible if a host needs it before then - not done unless asked.

## Tests (Module.Tests, LocalDB)

- `Publish_refused_by_save_validation_is_reported_and_leaves_nothing_pending`: v1 Published; the draft's publish commit is
  refused; result not Ok with the rule message, `os.IsModified` false, v1 Published in the space and the database, draft
  still Draft; the same space publishes once the rule lets it.
- `Timer_refused_by_save_validation_is_skipped_and_warned`: two due instances, one subject breaks a rule; the tick returns
  1 and does not throw; the valid one is ordered; the refused one is still waiting and due; one warning naming its id and
  the rule message; a second tick returns 0 and warns again.
- `Timer_failure_that_is_not_a_refusal_still_fails_the_tick`: the same with an `InvalidOperationException` from
  Committing: `AggregateException`, as before.
- `Fire_refused_by_save_validation_throws_to_the_caller_and_commits_nothing`: characterizes the unchanged user path.

Cases checked: refusal in the first or the second publish commit (both inside the same try); a refusal plus a real
failure in one tick (each counted in its own bucket); no logger registered (null-safe); retries: a refusal is not a lost
race, so `Run` does not retry it.

## Revision 1 (agy plan review)

- The demo's `StartRequestsController` batch Start caught `UserFriendlyException` only: a `ValidationException` on one
  request ended the batch and hid the starts already committed. Now `catch (Exception ex) when (ex is IUserFriendlyException)`.
- A guard refusal (`DefinitionGuard`, `StateLockGuard`) inside a timer is also an `IUserFriendlyException`, so it is
  warned, not a tick failure. The runtime never trips the guards itself; only an `IWorkflowAction` that breaks its
  contract can, and the warning names the instance and the guard's message. Accepted as part of "skip + warn".
- Tests added: the Publish test is a theory over the first and the second commit and checks `PublishedBy`/`PublishedUtc`;
  the refusal test counts the object spaces (one attempt per instance); a new test runs a refusal and a failure in one tick.
- `RunDueTimers` doc comment and `docs/integrating.md` §10 updated next to §8.
- Found while testing: after a refused publish, `os.Refresh()` recreates the DbContext, so a `WorkflowDefinition`
  reference taken before is stale (its children read as deleted); the test re-reads the draft, as the view re-reads its
  current object. Same as the existing lost-race path; nothing to change.
