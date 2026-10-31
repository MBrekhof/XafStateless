# CARD-2615 (1) + CARD-2713: a user-member task assignee, Notify to a user member, locks before the start

BPG cards, XafStateless half. One release (0.4.0, Martin approves it) covers both; BPG then bumps its reference.
Branch `card-2615-2713`, worktree `C:\Projects\xs-2615`, off master 0752b59 (DevExpress 26.1.4; the uncommitted 26.1.6
bump in the main tree stays out, Martin 2026-10-31).

## Decisions taken (Martin, 2026-10-31)

- CARD-2713: lock until an instance exists, in XafStateless (on the card).
- Empty assignee when a Task state is entered: **refuse the command** (nothing committed; a timer logs and retries, WF-010).
- The record's user member changes while its task is open: **the task stays with the user it opened for** (a snapshot,
  like the requester). The next task the workflow opens reads the new value.
- The DevExpress 26.1.6 bump is not part of this work.

## Part A: CARD-2615 (1)

### Addressing a user member

A **user member** of a subject type: a public persistent reference property whose type implements XAF's `ISecurityUser`
(`DevExpress.ExpressApp.Security`, PermissionPolicyUser implements it) and whose key is a `Guid`. BPG's `@users`
references are exactly this (`ApplicationUser : PermissionPolicyUser`). New `WorkflowMembers.Users(ITypeInfo)`.

A definition addresses one as `@<Member>` (e.g. `@AssignedTo`), next to the existing `@requester`:
- `StateDef.TaskRole = "@AssignedTo"`: the task is assigned to that user. No schema change for definitions (TaskRole is
  the same text column), so `DefinitionStore`, `DefinitionMapper`, `DefinitionService` copy it unchanged.
- `EffectDef(Notify, "@AssignedTo", text)`: the notification goes to that user (`WorkflowNotification.UserId/UserName`,
  the columns the requester notification already uses). The host's mailer already sends UserId notifications (BPG's
  skeleton does), so no host change is needed for mail.
- `EffectDef.UserMember(target)`: the member name for `@X` other than `@requester`, else null. Engine-only, no XAF.
- `@requester` keeps its meaning and is resolved first; a member literally named `requester` cannot be addressed (C#
  members are PascalCase in practice; not worth a syntax).

### Validation (`WorkflowValidator` + `IWorkflowEnvironment`)

- New `IWorkflowEnvironment.CheckUserMember(string member)` with a default implementation that returns a finding
  ("not a reference to the application's users"), so a third-party environment refuses rather than accepts.
  `XafWorkflowEnvironment` implements it with `WorkflowMembers.Users`.
- Task state: `@X` -> `CheckUserMember(X)` instead of `RoleExists`. `@requester` as a task role stays "role does not exist".
- Notify: `@X` -> `CheckUserMember(X)`.
- A role whose name starts with `@` and equals a used target conflicts (today only for `@requester`): generalised to any
  `@` target, for tasks and notifications.

### Runtime (`WorkflowRuntime.Enter`)

- `User(x, member)`: reads the member on the command space's subject; returns `(key, UserName)` for an **active** user,
  else null. Inactive counts as empty: a task for a user who cannot log on would never be decided. (Extends Martin's
  "empty -> refuse" to inactive users; flagged in the report.)
- Task state with `@X`: no user -> `UserFriendlyException("task '<key>' is assigned to '<X>', which is empty or an
  inactive user: set it first")`, thrown out of the command like an Increment overflow (Start/Fire/Decide throw; the UI
  shows it; `RunDueTimers` logs and retries). Else the task gets `UserId`, `UserName`, and `Role = ""`.
- Notify `@X` with no user: skipped with a note in the result message, as a requester Notify without a requester is.

### Runtime rows

- `WorkflowTask` gains `UserId` (Guid?, hidden like the notification's) and `UserName` (string?). **Schema change**: two
  nullable columns on the task table (hosts update their database, as for 0.3.0's entities).
- `DefinitionGuard.CheckTasks`: `UserId`, `UserName` are fixed after insert, like Role (even inside the runtime scope).
- Decide and Outcomes: a task with a `UserId` is decided only by the caller whose `UserId` matches; roles do not count,
  administrators neither (today's rule for role tasks: only the role decides). Refusal text: "this task is assigned to
  '<UserName>'".
- `MyTasksController.Criteria(roles, administrator, userId)`: open tasks of the user's roles **or** assigned to the user.
- `WorkflowPermissions`: a new row family, Read on `WorkflowTask` where `[UserId] = CurrentUserId()` (same text as the
  requester notification row, plain or per type and scoped like every other family). `GrantParticipant` adds it; a new
  public `GrantAssigneeRows(role, subjects)` for hosts that seed rows themselves (BPG calls `GrantRoleRows` /
  `GrantRequesterRows` today, not `GrantParticipant`). Notifications to a user member are already readable through the
  requester row `[UserId] = CurrentUserId()`; the docs say so.
- Lookups: `WorkflowState.TaskRole` offers roles plus `@<user member>`; Notify's target offers `@requester`,
  `@<user member>`, roles.

### Demo

`PurchaseRequest.Buyer` (`ApplicationUser?`), the user member the tests use. No explicit FK property: the `Workflow`
link has none either, and an explicit `BuyerId` Guid would show up as a lockable/settable scalar in
`WorkflowMembers.Supported`.

## Part B: CARD-2713, locks before the start

A subject **without a persisted workflow link** (Added, or Modified with a null link in the database) is treated as in
the initial state of its workflow: the members locked in the **initial state of every Published definition whose
TargetTypeName is the subject's EF entity type** (union over definitions) are locked. Retired and Draft definitions do
not count. A type with no published definition locks nothing (today's behaviour).

### StateLockGuard

- Unlinked subjects are collected next to the linked ones. One raw query (`RawSql`, same connection and transaction, no
  security) reads the Published definitions for their types (ID, TargetTypeName, InitialStateKey, State), one more the
  states of those definitions (Key, LockedMembers). `RawSql.Rows` gains a string-key overload (its key list is
  Guid-only today); the core takes `object` keys.
- **Modified** unlinked subject: a locked member that is modified and not exempted by the runtime scope is judged against
  the database exactly as today (provider form, `Same`).
- **Added** subject (no database row): a locked member's value is compared with a **fresh instance** of the entity type:
  `Activator.CreateInstance` (XAF EF entities have a parameterless constructor; `ObjectSpace.CreateObject` needs it too)
  then `IXafEntityObject.OnCreated()`, so property initializers and OnCreated defaults (BPG emits spec defaults there)
  are the baseline. Both values go through the property's provider converter and comparer, like `Same`.
  - An `OnCreated` that throws (it needs the object space, which the fresh instance has none of) is caught and the
    constructor's values are the baseline. Ceiling: such a host whose OnCreated sets a locked member gets its new
    records refused; no known host does that (BPG's OnCreated assigns literals). `ponytail:` comment names it.
- Refusal text: "'<Member>' is locked until its workflow starts".
- Inside a runtime scope the exemptions are unchanged: Start (persisted link null, so these rules apply) changes only
  the link and the initial state's SetField members, which `RuntimeScope.Allow` exempts. A `RunAction` creating or
  changing another unstarted subject is now held to its initial-state locks too (intended: "every path").
- Deletes of unstarted subjects are not affected.
- Cost: two extra queries per save that touches an unstarted subject. `ponytail:` no cache; cache the per-type lock set
  in `DefinitionStore` if a host measures it.

### StateLockAppearanceController

`Locks()` for a current object whose `Workflow` is null: lockable = every member any state of the type's published
definitions locks; locked = the union of their initial states' locks. The definitions come from a new
`DefinitionStore.PublishedFor(os, type)` (raw query for the ids, then the cached `Get`), so a role without Read on
definitions still sees the editors disabled. Known limit: a user who can read the subject but not its instance (a scoped
participant row) sees `Workflow` as null and gets the initial state's editor locks; before this change they got none.
UI only; the guard judges the persisted link.

## Tests (test-first; Module.Tests, LocalDB)

New class `UserAssigneeTests` (own TestDb):
1. Task `@Buyer`: the task has UserId/UserName, Role ""; the buyer decides; another user in every role is refused and
   sees no outcomes.
2. Empty Buyer: Submit throws the refusal; the instance stays in draft and no task exists. Same for an inactive buyer.
3. Buyer changed while the task is open: the task keeps the first buyer; the first buyer still decides.
4. Notify `@Buyer`: notification with UserId/UserName and no Role; empty Buyer: no notification, the note in the message.
5. Validation: `@Buyer` valid for a task and a notification; `@Title` and `@Nope` are findings; a role named `@Buyer`
   conflicts.
6. DefinitionGuard: changing a task's UserId outside (and inside) the runtime scope is refused.
7. Permissions (secured, as PermissionTests): `GrantAssigneeRows` lets a user read only the tasks assigned to them; the
   task list criteria includes them.
8. A timer into a task state with an empty Buyer: logged, not fired, still due.

Engine.Tests: the validator's `@X` routing with a fake environment (default `CheckUserMember` refuses).

New class `UnstartedLockTests` (own TestDb, one published definition locking `Ordered` in its initial state and
`Revisions` only in a later state, plus a second published definition locking `Title` in its initial state):
1. A new request with Ordered = true is refused; with the default it saves; Revisions is free before the start (the
   TimeEntry.Hours case).
2. An existing unstarted request: changing Ordered is refused, Amount is free; Title refused (union of definitions).
3. Start still works, including an initial-state SetField of a locked member; after the start the current state's locks
   rule (Ordered unlocked in a later state).
4. A Draft definition's initial locks do not count; neither do a Retired version's (publish v2 without the lock).
5. Baseline unit test: a type with an OnCreated default and one whose OnCreated throws.
6. Appearance controller: an unstarted request's detail view has the Ordered editor disabled (as the existing
   controller tests do, if any; otherwise `Locks()` through the internal seam).

Existing tests that create PurchaseRequests run in their own TestDb per class; the new definitions live only in the new
classes' databases, so they cannot lock other classes' records.

Mutation check after green: revert each rule once (union, Published filter, Added baseline, OnCreated call, UserId
authorization, empty refusal, guard fixed columns, criteria Or) and confirm a test fails.

## Docs and release

- `docs/integrating.md`: user-member addressing, `GrantAssigneeRows`, the unstarted locks, the schema change.
- `SESSION_HANDOFF.md` at the end.
- 0.4.0 (`PackageVersion`) only after Martin's go; then publish Engine, Module, Blazor to the private feed.

## Plan review (Codex gpt-6-luna, 2026-10-31; Gemini's quota was used up)

All six findings accepted; none changes the design:
1. Major: the unstarted path runs before the guard's early return for linked subjects; test: a RunAction (RefundAction.Also) writing a locked member of a second, unstarted request is refused.
2. Major: an OnCreated that throws may have half-set the fresh instance. On a throw, the baseline is a second, constructor-only instance. Unit test: OnCreated sets the locked member, then throws.
3. Major: Criteria, Outcomes and Decide change together; PermissionTests.Listed passes the user id; the assignee is tested through Criteria and the runtime.
4. Minor: validation tests add a notification target colliding with a role, and an unrelated `@` role that collides with nothing.
5. Minor: a mixed save (two Added, one Modified, one soft-deleted unstarted request) checks only the live Added/Modified ones.
6. Minor: `StateLockAppearanceController.Locks` becomes an internal static seam (store, object space, current object), tested for the union of two Published definitions and the Draft/Retired filter.

Found while planning: WorkflowRuntimeTests publish definitions that lock Amount/Revisions in their initial state (the Counting theory, RunRules). Under Part B those Published definitions lock every later new request in that class's database; the affected tests get a leading unlocked state or their own subject values, whichever keeps what they test.

## Diff review (Codex gpt-6-luna, 2026-10-31; Gemini's quota still used up)

No findings. Codex read `git diff master` and both new test files.

## Found after the review

- The browser smoke showed a task captioned with its user: XAF picks a default member by name ("Name" first, contains-match), so the new `UserName` would caption every task, empty for a role's task. `WorkflowTask` now declares `[DefaultProperty(nameof(Subject))]` (test `A_task_is_captioned_by_its_subject_not_its_user`, red first).
- A `(UserId, State)` index on tasks, next to `(Role, State)`, for the task list.

## Verification

- `dotnet test XafStateless.slnx`: Engine 59, Module 196 (was 58 / 175).
- Mutation check: 16 mutations (`mutate.py` in the session scratchpad), each failed a test.
- E2E against a freshly seeded `XafStatelessE2E`: 5/5.
- Browser smoke (scratch C# Playwright, as `approver`): a task assigned through `@Buyer` shows in the approver's task list, is captioned by its subject, and is decided; an unstarted request's `Ordered` (locked in the initial state) is disabled and `Amount` is not, existing and new.
- Demo launch note: the connection-string argument must come before `--updateDatabase --forceUpdate --silent` (the command-line config reads `--silent` as a key and takes the next argument as its value), and running the exe from `bin` needs `--environment Development` for the packages' static files.
