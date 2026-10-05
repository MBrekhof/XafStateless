# XSM-031: Increment effect (attempt-limited loops)

Card: XSM-031 (CARD-1922). Decision: option (a) from the card, a counter effect. Option (b), expression values in
Set field, is out: it needs an expression evaluator on the subject and a change in the consuming generator (its validator rejects `=` values).

## Design

- `EffectKind` gains `Increment`. Target = a numeric member of the subject, Value = the step as text; `""` (the
  designer's default) means 1. Negative steps count down.
- Counter member = integral (`TypeCode` SByte..UInt64) or decimal, nullable included, **not** enum (its TypeCode is
  the underlying integer's), **not** float/double (decimal arithmetic rounds them: Codex reproduced 12345678f + 1 →
  12345681). One predicate, `WorkflowMembers.IsCounter`, shared by lookup, validation and runtime; on top of the Set
  field filter (`WorkflowMembers.Supported`), so `ID`, `Workflow`, the lock field stay excluded. Null reads as 0.
- Runtime (`WorkflowRuntime.Enter`): `current + step` in decimal, back to the member's underlying type with
  `Convert.ChangeType` (nullable unwrapped first, as ConvertValue does), written with `member.SetValue`, then
  `RuntimeScope.Allow` exactly as Set field does, so the increment may write a member locked in the state the instance
  is leaving. A result outside the member's range (int.MaxValue + 1, unsigned 0 − 1) is an `OverflowException` from
  ChangeType: rethrown as `UserFriendlyException("'Revisions' cannot be incremented past its range")` from Enter, before
  CommitChanges, so nothing is committed and the UI shows the message (XAF shows UserFriendlyException as a message).
- Validation (`XafWorkflowEnvironment.CheckEffect`): member supported and numeric ("'Title' is not a number, so it
  cannot be incremented"); step parses as decimal ("'abc' is not a step for 'Amount'"); an integral member refuses a
  fractional step ("'1.5' is not a whole step for 'Version'"). Null Value: refused like Set field only when the kind
  needs one; for Increment null/"" is step 1.
- Designer (`WorkflowEffect.LookupValues`): Increment lists the numeric supported members only.
- A Decision after the Increment sees the new value: `Fits` evaluates on the command space's subject, which Enter
  just modified. That is what makes `[Attempts] >= 3` become true through the workflow itself.

- Demo: `PurchaseRequest.Revisions` (`int?`, "times sent back"), the real nullable counter the tests need; the seeded
  v1 does not use it (a designer can, in a new version).

Not done: Mermaid (effects are not drawn); the consuming generator's validator is its own work.

## Tests (Module tests, LocalDB; Codex review 2026-10-04 added 2, 5, 6, 7)

1. `WorkflowRuntimeTests.Increment_counts_and_decision_sees_it`: PurchaseRequest, Revisions null.
   `draft(Manual, Locked: Revisions) -Fail-> count(Decision, OnEntry Increment Revisions "")`;
   `count -Limit-> failed(Final)` criteria `Revisions >= 2` order 0; `count -Retry-> draft` order 1; `draft -Pass-> passed(Final)`.
   Fire Fail: state draft, Revisions 1 (null → 0 + 1; step "" = 1; the lock on draft did not refuse the runtime's write).
   Fire Fail: state failed, Revisions 2.
2. `WorkflowRuntimeTests.Increment_steps`: step "0.5" on Amount from 10 gives 10.5; step "-1" on Revisions from 3 gives 2.
3. `WorkflowRuntimeTests.Increment_past_range_is_refused_and_commits_nothing`: Revisions = int.MaxValue, Fire: a
   UserFriendlyException naming Revisions; state and Revisions unchanged in the database.
4. The shared `Fixture`'s ordered state gains `Increment Revisions ""`, so `Retry_reloads_after_lost_race` proves exactly
   one increment persists across a retried attempt (Revisions == 1), and the other ordered-path tests run it too.
5. `DefinitionServiceTests.Increment_validation`: via `XafWorkflowEnvironment` on PurchaseRequest and WorkflowDefinition
   (`Version` is an int, `State` an enum): Title refused (not a number), Ordered refused, State refused (enum), "abc"
   refused, "1.5" on Version refused, "2" on Version ok, "" and "-1" on Revisions ok, "0.5" on Amount ok.
6. `Publish_refuses_increment_on_text_member` through DefinitionService.Publish: message contains `effect on 'ordered':`.
7. `DefinitionServiceTests.Increment_target_lookup_lists_counters_only`: a WorkflowEffect of Kind Increment under a
   PurchaseRequest definition lists exactly Amount and Revisions.
8. E2E designer journey step 2b: pick Kind "Increment" (the sentence-case caption renders), pick Target "Amount", then
   switch to Notify as today. `StateLockTests` lock lookup assertion gains Revisions.

## Docs

- `docs/integrating.md` step 7 bullet on members: effects are Set field and Increment; Increment on numeric members.
- `README.md` Status: "Notify, Set field and Increment steps".
