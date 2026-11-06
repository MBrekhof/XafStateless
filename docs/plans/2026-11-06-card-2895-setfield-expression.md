# CARD-2895: a Set field effect takes an expression (`=[Project.Manager]`)

## Problem

BPG's projects pack fills `TimeEntry.Approver` from `[Project.Manager]` with two automation rules, so at Submit the
Approver can be stale (the Manager changed after the entry's last save), empty (drafts saved before BPG 1.3.0) or null
(the saver's secured space reads Manager as null under an access boundary). The Submitted state's Task `@Approver` and
`Notify @Approver` then go to the wrong user, or Submit is refused. The workflow has to set the member itself, when the
state is entered, from the subject as the command space (non-secured) holds it.

## Design (option B, settled by Claude and Gemini on 2026-11-06 for BPG CARD-2816)

**Syntax.** A Set field effect whose Value starts with `=` is an expression: the rest is XAF criteria language evaluated
on the subject, e.g. `=[Project.Manager]`, `=[Amount] * 2`, `=ToStr([Amount])`, `=Iif([Amount] > 1000, 'big', 'small')`.
Any other Value is a constant, as before.

**Runtime (`WorkflowRuntime.Enter`, case SetField).**
- Expression: `x.Os.GetExpressionEvaluator(subjectType, CriteriaOperator.Parse(rest)).Evaluate(x.Subject)`, in the
  command's non-secured space (as `Fits` evaluates a Decision's criteria and `User(x, member)` reads a user member).
- The member is looked up in `WorkflowMembers.Lockable` (scalars and references) for an expression, in `Supported` for
  a constant (unchanged).
- A reference member: null clears it (the member is loaded first: EF ignores clearing a lazy reference it has not
  loaded); an object must be an instance of the member's type and is taken as `x.Os` holds it (`x.Os.GetObject`).
- A scalar member: the value goes through `RuleSetEvaluator.ConvertOutput(memberType, value)`, the existing
  object-to-member conversion of the rules engine (null into a nullable member is null; enums from names or numbers;
  invariant `Convert.ChangeType` otherwise). `ConvertValue` stays the text-to-member conversion of constants: running an
  evaluated DateTime or decimal through text would lose precision and kind.
- A conversion that fails at runtime (null into a non-nullable member, an overflow) refuses the command with a
  `UserFriendlyException` ("'Amount' cannot be set from '[Budget]': ..."), as a rule set's output does: nothing is
  committed; a timer logs and retries (WF-010).
- It writes through `Write`, so `runtimeScope.Allow` exempts that member (a state lock on it does not refuse the write;
  `StateLockGuard` keys a reference by its navigation name and judges its foreign key, CARD-2735).
- Effects run in their order: a Set field before `Notify @Approver`, and the Task state's `@Approver` assignee (created
  after the effects), see the fresh value.
- `WorkflowNotifications.Fill` does not run on a Set field value: `{Title}` in a constant or in an expression's string
  literal stays literal text (today Fill runs on Run action arguments only; a test pins it).

**Validation at Publish (`XafWorkflowEnvironment.CheckEffect`, SetField).**
- The member must be in `WorkflowMembers.Lockable` for an expression, `Supported` for a constant. A constant on a
  reference member: "'Buyer' is a reference: set it with an expression, e.g. =[Project.Manager]".
- The expression must parse and be non-empty.
- It is converted with DevExpress's `CriteriaToExpressionConverterForObjects` against the subject's CLR type (spike,
  26.1.6): an unknown member on any hop throws `MissingMemberException`; a join (`[<T>][...]`) and a `^.` parent
  reference throw (not supported: refused); an unregistered custom function throws. The resulting LINQ expression's
  `Type` is the result type, and every member it reads, through casts (`[Buyer.<ApplicationUser>LockoutEnd]`) and
  aggregates (`[Buyer.Roles][].Count()`), is a `MemberExpression` in its tree.
- **Secrets**: a member read on a type implementing `ISecurityUser` named `StoredPassword`, `ChangePasswordOnFirstLogon`,
  `AccessFailedCount`, `LockoutEnd`, `Roles` or `UserLogins` is refused (BPG's `UserSecretMembers` list): such a value
  would be copied into a member other users read. New `WorkflowMembers.UserSecrets`.
- **Type fit** (nullable stripped on both sides): a reference member takes a result type assignable to it; a scalar
  member takes the same type, any numeric into any numeric (non-enum; an overflow refuses at runtime), or an integral
  into an enum. `object` (a bare `null`) fits nothing: `=null` is refused (clearing is not asked for; YAGNI).
- Any conversion exception becomes the finding "'=…' is not a valid expression for 'Approver': <message>".

**Designer.** The Target lookup of a Set field offers `Lockable` members (references included); the effect shows as an
ordinary Set field with the `=` value (no designer or diagram change).

**Demo.** `PurchaseRequest.Approver` (a second user member, `ApplicationUser?`) so the tests and the demo show the BPG case
(`Approver` from `=[Buyer]`). Demo schema only; the library has no schema change.

## Security

Only an Administrator writes definitions (`WorkflowPermissions.Configure`). The expression is evaluated in the
non-secured command space, as Decision criteria and `@Member` reads already are. What it can read is bounded by the
subject's member graph (joins are refused) minus the user secrets.

## Compatibility

A published constant that starts with `=` changes meaning in 0.5.0: Publish refuses it on the next edit, and a published
definition would evaluate it at runtime (a parse error refuses the command). A literal `=` text is written as an
expression of a string: `='=x'`. Release notes and `integrating.md` say so.

## Cases checked (self-review)

- Value null (nulled column): unchanged finding "needs a value", before the `=` test.
- `=` alone / `=   `: "needs an expression".
- Parse error; unknown member on hop 1 and hop 2; join; `^.`; custom function not registered: each a finding.
- Secret on a direct path, through a cast, inside an aggregate (`Roles`, `UserLogins`): refused. `[Buyer.UserName]` fine.
- Result type: reference into reference (fits), wrong reference type (refused: `=[Workflow]` into `Approver`), string
  into decimal (refused), int into decimal (fits), int into enum (fits), bool into bool, `null` (refused).
- Target `Workflow`, `ID`, a collection: not in `Lockable`, refused for an expression too.
- Runtime: reference set; null clears; the lock on the member does not refuse; a Notify and a Task after it address the
  new value; Decision after it sees it; nullable source null into non-nullable scalar refuses the command and commits
  nothing; `{Title}` stays literal; a constant SetField behaves as before (existing tests).
- Every caller of the touched code: `CheckEffect` (validator, tests), `Enter` (Start/Fire/Decide/timers), the
  `WorkflowEffect.LookupValues` (designer). `RunRules` keeps `Supported`.

## Gemini plan review (2026-11-07) and how each finding was settled

The review ran while the first implementation was already in the tree, so it read both.

1. *Runtime exceptions escape the refusal* (a parse error of a pre-0.5.0 `=` constant, `InvalidPropertyPathException`, a
   comment-only expression that parses to null and evaluates to `true`, null into a NOT NULL string column failing at
   commit). **Fixed**: `Evaluate` refuses a null parse, catches `CriteriaParserException` and
   `InvalidPropertyPathException` with the rest, and refuses null into a member whose column (a reference: its foreign
   key) is required by the EF model. Tests: `A_definition_published_before_with_an_equals_constant_refuses_instead_of_failing`,
   `An_empty_value_into_a_required_member_refuses_the_command`, `A_timer_whose_expression_is_refused_is_skipped_and_stays_due`.
2. *A lock on `BuyerId` refuses the runtime's own Set field of `Buyer`* (and the other way round): the exemption was keyed
   by the written name, the guard by the lock's name, both meaning one column. **Fixed**: `Write` exempts every name of the
   column (`StateLockGuard.Aliases`: the member, its foreign key, every reference held by that key). Test:
   `ReferenceLockTests.The_runtime_exemption_covers_every_name_of_the_column` (OtherSubject, declared key).
3. *Re-fetch by key before the type check.* **Fixed**: the value must be an instance of the member's type, then
   `x.Os.GetObject(value)` (handles a new object an earlier Run action created).
4. *The LINQ converter is not a sound proxy for XAF's evaluator* (case-insensitive members, CLR-only members such as
   `ObjectSpace`, `Iif(True, ...)` dropping the dead branch before the secret walk). **Fixed**: paths are checked on the
   criteria tree against XAF's type info (`XafWorkflowEnvironment.Paths`): exact names, browsable members only, no user
   secrets, no joins, casts or `^.`, inside aggregates too. The converter now only infers the result type; it is stricter
   than the evaluator on mixed types (`Iif([Ordered], [Amount], 0)` needs `0m`), documented and tested. Null propagation on
   a dotted value-type path (`[Workflow.EnteredUtc]` typed non-null) is refused at runtime when the target is non-nullable.
5. *A non-admin `GrantDesigner` role can copy data it cannot read* (the card assumed Administrators only, as in BPG). Not
   fixed here: a design decision. Documented in `integrating.md`; **CARD-2908** asks Martin (keep / check the publisher's
   read permission at Publish / administrators only). The hidden-member rule (finding 11) closes the cheap part.
6. *Comment-only expression throws NullReferenceException at Publish.* **Fixed** with 1 (null parse = "needs an expression").
7. *Any enum into any enum.* **Fixed**: an enum takes a non-enum whole number or a name.
8. *Enum from a name refused at Publish though the runtime converts it.* **Fixed**: a string fits an enum (an unknown name
   refuses at runtime).
9. Covered by 1.
10. *Missing tests* (secured caller, Decision after, timer, enum, comment, wrong case, `ObjectSpace`). **Added** all:
    `PermissionTests.A_set_field_expression_reads_what_the_callers_space_cannot` proves the caller's secured space reads
    `Buyer` as null while the Set field still sets `Approver`.
11. *Allow-list by `IsVisible` instead of a name list.* **Taken in part**: a hidden member is refused on every hop; the name
    list stays for user members that are browsable (`Roles`, `ChangePasswordOnFirstLogon`).

Found while building it: Notify and Run action placeholders do not apply these hop rules (`{Buyer.StoredPassword}`):
**CARD-2909** (unverified).

Mutation check: 8 mutants (`Aliases` itself, lazy load, required, parser catch, secrets, hidden members, enum rule,
reference type check), each killed by a test. Not covered: `Write` skipping `Aliases` (diff review 7).

## Gemini diff review (2026-11-07) and how each finding was settled

1. *An `=` constant from before 0.5.0 that parses (`=[^.Title]`, `=Ascii('')`) throws `IndexOutOfRangeException` at
   runtime.* **Fixed**: `Evaluate` runs `Paths` before evaluating and catches `IndexOutOfRangeException` too.
2. *`GetObject` returning null for an object not in the database bypasses the required check.* **Fixed**: refused
   ("the object it gives is not in the database"). Not reproduced in a test: no browsable member of the demo yields a
   detached persistent object.
3. *A `long` into an `int` enum is truncated by `Enum.ToObject`.* **Fixed** in `RuleSetEvaluator.ConvertOutput` (shared
   with rule sets): through the enum's underlying type first, so it overflows. Engine test.
4. *NaN and infinities pass `ConvertOutput` and fail at commit.* **Fixed** in `ConvertOutput`: refused as an overflow.
   Engine test.
5. *XAF interfaces that declare user secrets (`ISecurityUserWithRoles`, `ISecurityUserLockout`,
   `IAuthenticationStandardUser`, `IPermissionPolicyUser`) do not extend `ISecurityUser`.* **Fixed**:
   `WorkflowMembers.IsUserType` covers them all. Test per interface.
6. *Pre-0.5.0 rows are evaluated unchecked.* **Fixed** with 1. Test `An_unchecked_expression_is_checked_when_it_runs`.
7. *The alias test would pass with `Write` not calling `Aliases`.* **Fixed in part**: the exemption is now one call,
   `RuntimeScope.AllowColumn`, which `Write` and the test both use. A runtime test on `OtherSubject` (the only subject with
   a declared foreign key) is not possible: the runtime needs an XAF object space, and the tests' provider builds the demo
   context, which does not map `OtherSubject`. `Write` calling `Allow` instead of `AllowColumn` would survive.
8. *Join and `^.` cases asserted the generic text.* **Fixed**: they assert the walk's own reasons, plus `Iif(True, 'a', [^.Title])`.
9. *No runtime enum test.* Not added: `PurchaseRequest` has no enum member, and the conversion is `ConvertOutput`, now
   unit-tested for numbers, names, unknown names and overflow.
10. *Docs*: `integrating.md` now says the object is taken as the command space holds it, the paths are checked again at
    runtime, and only target members are top-level. The mutation note is corrected below.

Mutation check after the diff review: removing the runtime path check, and shrinking `Aliases` to the member itself, are
each killed.

## Tests (Module)

New `SetFieldExpressionTests`: validation findings (each case above), lookup offers `Approver`/`Buyer`, runtime
reference/scalar/null/lock/order/refusal/literal-braces. Existing lookup and member-list assertions updated for the new
demo member.
