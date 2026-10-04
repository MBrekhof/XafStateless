# XSM-032: Run action effect (host-registered actions)

Card: XSM-032 (CARD-1923). Decision: option (a) from the card, an effect that runs a host-registered action inside the
transition's command, so cancel can release a student's lesson slots and refund: other records than the subject.
Option (b), "enqueue a host job by name", is a special case: a host action can enqueue its job.

## Design

- `EffectKind.RunAction` (XAF caption "Run action"). Target = the action's name, Value = an argument text with `{Member}`
  placeholders of the subject, filled like a Notify body (`""` = no argument).
- Host contract, in `XafStateless.Module.Services`:
  ```csharp
  public interface IWorkflowAction
  {
      string Name { get; }                       // what the designer picks; unique per host
      void Run(WorkflowActionContext context);
  }
  public sealed record WorkflowActionContext(IObjectSpace ObjectSpace, IWorkflowSubject Subject, string Argument);   // the instance is Subject.Workflow
  ```
  The host registers each action in DI: `services.AddSingleton<IWorkflowAction, ReleaseSlotsAction>()`, before or after
  `AddXafStateless()`. `WorkflowActionCatalog` (singleton, registered by `AddXafStateless`) collects them; a duplicate
  name throws when the catalog is first resolved (the first workflow command, validation or designer lookup; a host that
  wants it at startup resolves the catalog there). `WorkflowRuntime` and `DefinitionService` take the catalog as an
  optional constructor parameter (`WorkflowActionCatalog? actions = null`, Microsoft DI prefers the registered one;
  tests build their own; `WorkflowActionCatalog.Empty` otherwise). Actions are singletons: thread-safe, keeping nothing
  from a command, no scoped constructor dependencies.
- Runtime (`WorkflowRuntime.Enter`): `catalog.Find(name)` (missing: InvalidOperationException, the validator refused
  it at publish, so only a host that lost the registration gets here; nothing is committed), then
  `action.Run(new(x.Os, x.Subject, filled argument))`. The action writes through `context.ObjectSpace`, the command's own
  non-secured object space, so its writes commit with the state change and roll back with it, and its records are not
  checked against the caller's permissions (publishing a definition with an action delegates that host capability).
  An exception from the action aborts the attempt: nothing is committed, a `UserFriendlyException` reaches the user as
  the message, other exceptions propagate (a timer collects them). The command loop retries an exception
  `LostRace` classifies, up to four attempts, an action's included.
- **Actions are trusted host code** (Codex review, settled 2026-10-04). They run inside the command's `RuntimeScope`,
  the runtime's own write permit: the guards do not tell an action's writes from the runtime's, so an action *could*
  change instances, tasks, notifications or the subject's Workflow link, and a member that a Set field of the same
  state exempted passes the lock even when the action set its final value. None of that is enforced; the contract
  forbids it, as it forbids committing: the runtime owns saving and the object space's lifetime, an action only stages
  changes (no CommitChanges, Rollback, Refresh, Dispose, no writes on another connection), finishes before it returns
  (no async void, no background use of the context). What the guards still refuse: a locked member no effect of the
  state writes. Keep actions to writes through `context.ObjectSpace`; external side effects (mail, a job on another
  connection) belong in an outbox row the host delivers, like notifications, because a retried attempt runs the action
  again while only the last attempt commits.
- Validation (`XafWorkflowEnvironment.CheckEffect`, now also given the action names): RunAction → name registered
  ("action 'release-slots' is not registered in this application"), placeholders in Value name members (as Notify).
- Designer: `IStringLookupSource.LookupValues` gains a 3-argument overload with a default body that forwards to the
  2-argument one, `(memberName, roleNames, actionNames)`; the two Blazor lookup editors call it with
  `StringLookup.ActionNames(services)` (the catalog's names). `WorkflowEffect` lists the action names for RunAction.
- Demo: `LogWorkflowAction` (name `log`) in `XafStateless.Demo.Blazor.Server/Services`, logs the argument through
  ILogger; registered in Startup. The seeded v1 is unchanged. The E2E designer journey picks Kind "Run action" and
  Target "log".

Not done: a demo with related records to compensate (the demo has none); BPG's mapping to its automation actions (bpg
board); async actions (Run is synchronous, inside the command).

## Tests (Codex review 2026-10-04 added 2b, 2c, 4b, 7, 8)

Module tests (LocalDB). The shared runtime `Fixture`'s ordered state gains `RunAction refund "Refund for {Title}"`, run by
a fake action that creates a second PurchaseRequest titled with its Argument, so every ordered-path test (including the
mid-commit SQL failure one, which asserts no refund row) covers the action's commit and rollback.
1. `WorkflowRuntimeTests.RunAction_writes_through_the_command_space_and_fills_the_argument`: after Decide → ordered a
   request "Refund for <title>" exists; the context's Subject was the request, its Workflow in state ordered.
2. `RunAction_that_throws_commits_nothing`: the action creates its refund row, then throws `UserFriendlyException("no
   slots")`; Decide throws it; state approval, task Open, no refund row, Ordered false.
   2b. `Failing_effect_after_action_commits_nothing`: Revisions = int.MaxValue, so the fixture's Increment after the
   action overflows: no refund row, state approval.
   2c. `Unregistered_action_at_runtime_commits_nothing`: a runtime with `WorkflowActionCatalog.Empty` on the same
   published fixture: InvalidOperationException naming the action; state approval.
3. `RunAction_cannot_change_a_locked_member`: approval locks Amount; the ordered action sets Amount: the guard refuses
   with "'Amount' is locked in state 'Approval'"; nothing committed.
4. `RunAction_runs_once_per_attempt`: with the lost-race TestHook, the action ran twice, one refund row persists.
5. `DefinitionServiceTests.Publish_refuses_unregistered_action` ("effect on 'ordered':" and the name) and
   `RunAction_argument_placeholder_must_be_a_member`.
6. `DefinitionServiceTests.RunAction_target_lookup_lists_actions` (3-argument LookupValues).
7. `PermissionTests`: a participant who may not write PurchaseRequest rows other than their own (or cannot write
   them at all) fires an authorized transition whose action writes a related request: the row is written; an
   unauthorized caller (no Write on the subject) is refused before the action runs (call count 0).
8. `WorkflowActionTests` (no database): catalog from a ServiceProvider with registrations before and after
   `AddXafStateless`; no actions → empty names; duplicate names throw on resolution; a legacy `IStringLookupSource`
   implementing only the 2-argument LookupValues answers the 3-argument call.
9. E2E designer journey: pick "Run action", Target "log", back to Notify.

## Docs

- `docs/integrating.md`: new step **10. Actions (Run action effects)** before Check it (renumbered 11); the limits
  paragraph on retries.
- `README.md` Status: "Run action runs a host-registered action in the transition's commit".
