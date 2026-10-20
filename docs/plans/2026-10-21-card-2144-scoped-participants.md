# CARD-2144 (MOBILE-007a-WF): participant rows scoped by the subject

BPG card CARD-2144. When a workflow's subject sits inside BPG's access boundary, a participating role reads the tasks,
notifications and instances of records it cannot read. Notification text is filled from the subject's fields. BPG
blocks that combination today (ACCESS_WORKFLOW_UNSUPPORTED, bpg 3f59861).

This plan is the XafStateless half: a way for a host to scope a role's runtime rows by a criteria on the subject.
It lands on master without a release; it ships with 0.3.0. The BPG half comes after, in a BPG worktree:
`WorkflowSeedEmitter.Participant` and `.Requester` call the new row methods with the boundary criteria, the Blocker is
lifted, and an E2E test with two users proves it.

Revision 2 (after the Codex plan review): the legacy combined instance row is narrowed too; a row counts as ours only by
text and state shape; requester notifications are scoped; `^.` is rejected anywhere in a scope; the row writing is split
into two public methods because BPG does not call `GrantParticipant`; types that a role-wide row covered keep their rows
when a role switches to per-type rows.

Revision 3 (after the second Codex review): an admin-edited wider row of ours has its Read cleared instead of being kept;
`GrantRequesterRows` callers pass the full type set; BPG's exact scope shape is marked pending the BPG E2E.

Codex diff review (both reproduced by a failing test, then fixed): a wider row with only an admin's Navigate state was
deleted (Navigate now counts as an edit); a second `params` overload made `GrantParticipant(r)` ambiguous (CS0121).

## Decision: a criteria hook over `SubjectId`, no typed subject link

`WorkflowInstance` stores the subject as `SubjectType` + `SubjectId`. A typed link from the instance to the subject is not
possible in a library:
- EF Core needs a CLR navigation for each target type, and one `SubjectId` column cannot be the foreign key of every
  host table.
- The typed link already exists in the other direction: the subject's `IWorkflowSubject.Workflow`.

A free join reaches the subject by its id: `[<Subject.Full.Name>][[ID] = ^.SubjectId And <scope>]`. dxdocs ("Free Joins")
says free joins work in EF Core-based XAF apps. A spike confirmed it under real security, as a throwaway test on
`TestDb` and `SecuredEFCoreObjectSpaceProvider` with the demo's `PurchaseRequest`. The scope was `[Amount] > 100`, and
again with a nested `[<ApplicationUser>][[ID] = CurrentUserId() ...]` in it.
- Task, notification and instance rows joined that way are filtered in `GetObjectsQuery`, in `GetObjects(criteria)` and
  in `GetObjectByKey` (the in-memory check).
- The subject's `Workflow` link reads as null for an out-of-scope subject.

So no schema change and no migration are needed.

Decide is already consistent with the scope. `WorkflowRuntime.Load` authorizes Start, Fire and Decide by a secured read
of the subject; Decide also checks the task's role. A host that scopes the subject's own read permission with the same
criteria refuses a non-member's Decide before any change. The spike confirmed this from the other side: with the subject
left readable, Decide went through, because the subject read is the authority.

## API (`WorkflowPermissions`)

```csharp
/// A subject of a participant grant: the class, its navigation item, and optionally a criteria on the subject that limits
/// which of its instances, tasks and notifications the role reads (null: all of them, as today).
public sealed record ParticipantSubject(Type SubjectType, string NavigationItemId, string? Scope = null);

public static void GrantParticipant(PermissionPolicyRole r, ParticipantSubject subject, params ParticipantSubject[] more);
// one subject required: GrantParticipant(r) with no subjects still binds to the tuple overload (Codex diff review)
public static void GrantParticipant(PermissionPolicyRole r, params (Type subjectType, string navigationItemId)[] subjects); // unchanged

// The runtime-row halves, for hosts that seed their own roles (BPG's WorkflowSeedEmitter.Participant and .Requester):
public static void GrantRoleRows(PermissionPolicyRole r, params (Type subjectType, string? scope)[] subjects);      // tasks and notifications by role, instances
public static void GrantRequesterRows(PermissionPolicyRole r, params (Type subjectType, string? scope)[] subjects); // notifications to the logged-on user
```

`GrantParticipant` keeps its subject type, member and navigation rows and calls both row methods. The tuple overload maps
to `ParticipantSubject` with no scope.

## Row shapes

All criteria are built as operators (`BinaryOperator`, `JoinOperand`, `GroupOperator.And`) and serialized by DevExpress,
so names and the scope are quoted and grouped correctly. `T` is `subjectType.FullName`; `J(path)` is
`[<T>][[ID] = ^.<path> And (S)]`.

| Row | Role-wide (today) | Per type, unscoped T | Per type, scoped T |
| --- | --- | --- | --- |
| Task / notification by role | `[Role] = 'r'` | `[Role] = 'r' And [Instance.SubjectType] = 'T'` | the same `And J(Instance.SubjectId)` |
| Notification to the requester | `[UserId] = CurrentUserId()` | `[UserId] = CurrentUserId() And [Instance.SubjectType] = 'T'` | the same `And J(Instance.SubjectId)` |
| Instance | (always per type) | `[SubjectType] = 'T'` | `[SubjectType] = 'T' And J(SubjectId)` |

Each of the two notification row kinds (by role, to the requester) is role-wide or per type, decided per role and kind:
- per type when the call has a scoped subject, or the role already has a per-type row of that kind (the cross-call
  guard: a later unscoped call cannot bring the role-wide row back);
- otherwise role-wide, exactly as today.

## Scope validation (before anything is written)

- null, empty or whitespace: no scope.
- Unparsable (`CriteriaOperator.Parse` throws): `ArgumentException`.
- A parent reference (`^.`) anywhere in the scope: `ArgumentException`. Inside the join `^.` would point at the task or
  instance; inside the scope's own aggregates its binding is unverified. BPG's boundary criteria use none. Found by a
  `ClientCriteriaVisitorBase` subclass (DevExpress.Data, public) that visits every `OperandProperty`, inside aggregates
  and joins too.
- The same type twice in one call: taken once when the scopes are equal (compared after parse and serialize), otherwise
  `ArgumentException`.
- The role has no name: `ArgumentException` (as today).

Supported scope shapes, each tested on the three reads: plain members, a collection aggregate, a nested free join with
`CurrentUserId()`. BPG's reference-then-collection shape (`[Project.Members][[ID] = CurrentUserId()]`) is not proven by
these tests (the secured test model has no subject with a reference); it stays pending the BPG E2E test, which lists
tasks and notifications (SQL) and opens a task's detail view (by key) for a member and a non-member.

## Repeating the grant (XSM-021) and narrowing

An all-unscoped call on a role without per-type rows writes exactly today's rows: none of the existing tests change.

Narrowing has to take effect: XAF grants the union of matching Allow rows, so a wider row left behind keeps the access.
When a row kind goes per type, and for each scoped subject T, the row methods remove the wider rows they themselves build:
- by role (task and notification): the role-wide `[Role] = 'r'`;
- requester: the role-wide `[UserId] = CurrentUserId()`;
- for scoped T, each kind: the per-type unscoped row for T, and joined rows for T with a different scope (recognised by
  parsing: an `And` group whose leading operands are the per-type row's and whose last operand is a join on `T`);
- instances, for scoped T: `[SubjectType] = 'T'`, joined instance rows for T with a different scope, and the legacy
  combined row `[SubjectType] In (...)` (written before XSM-021, never shipped in a package): rewritten without T, or
  removed when T was its only type.

What happens to a wider row whose text is one these methods build (Revision 3):
- Read Allow and Write, Delete, Navigate unset (exactly what they write): the row is removed.
- Read Allow and another state set (an admin's edit): only the Read state is cleared; the admin's other states stay. The
  narrowing of reads always takes effect.
- Read Deny or unset: left as it is (it does not widen reads).
Rows of any other text are never touched (the XSM-021 contract); a host or admin row of its own text that grants more is
the host's to narrow. An admin row with exactly our text cannot be told apart from ours and is treated as ours; the
host's own code asked for the narrowing. Documented.

No type loses its rows by the switch. When a kind goes from role-wide to per type, it also gets unscoped per-type rows for
every type the role-wide row served that the role's rows show: the types of its plain and legacy instance rows. A host
that wants one of them scoped passes it with a scope. `GrantRequesterRows` cannot see what a requester-only role served
(BPG's `Requester` writes no instance rows), so its callers pass every type whose requester notifications stay readable,
unscoped ones with a null scope. Documented on the method.

Unscoped after scoped for the same type: the plain per-type rows for T are added (the host changed its mind; the last
call's intent wins). The joined rows stay; they are narrower than the plain ones and do no harm. Documented and tested.

`docs/integrating.md` gets the scoped form, the row table, the scope rules, and an exception note under "never revokes":
a scoped grant replaces the wider rows these methods once wrote.

## Not changed

- `GrantDesigner`: designers read every runtime row. They are a design-time role and outside the boundary question.
- Runtime and engine: no change.

## Tests (`XafStateless.Module.Tests`, real security on `TestDb`)

`ScopedParticipantTests` (the spike turned into tests, `PurchaseRequest` as the subject):
- Two requests on either side of the scope. The scoped user reads only the in-scope task, notification and instance, by
  query, by `GetObjects(criteria)` and by key; the out-of-scope subject's `Workflow` reads null. Run for each supported
  shape: `[Amount] > 100`; `[<ApplicationUser>][[ID] = CurrentUserId() And [Roles][[Name] = 'Scoped']]` (nested join
  with a collection aggregate).
- Decide: with the subject's own read scoped by the same criteria, the out-of-scope task is refused
  ("does not exist or cannot be read") and the in-scope one is decided.
- Requester: the user starts a workflow on an in-scope request, and the request is then moved out of scope (Amount
  lowered, standing in for lost membership). A requester notification created after that is not readable, and neither
  is the earlier one. A request that stays in scope keeps its requester notifications readable.
- Plain per-type rows under security: the role is granted `PurchaseRequest` scoped and then unscoped (the role stays
  per type); the user reads both requests' tasks, notifications and instances.

`WorkflowPermissionsTests` (rows only):
- A scoped grant twice adds nothing the second time.
- Unscoped, then scoped: the role-wide and plain rows of the scoped type are gone and the joined rows are there.
- Unscoped for A and B, then scoped for A only: B keeps unscoped per-type task, notification and requester rows.
- A changed scope replaces the old joined rows and keeps none of them.
- Legacy `[SubjectType] In ('A', 'B')`, then scoped A: the row becomes `In ('B')`. Legacy `In ('A')`, then scoped A: the row is gone.
- An admin's Deny on a wider row, and an admin's Deny on the current joined row, both survive a regrant.
- A wider row with Read Allow and Write Allow (admin): after a scoped grant its Read is unset and its Write Allow stays.
- Requester upgrade: a role with only `[UserId] = CurrentUserId()`, then `GrantRequesterRows` with scoped A and unscoped
  B: the role-wide row is gone, A's joined and B's plain per-type rows are there.
- Scoped, then an unscoped call for another subject: no role-wide row comes back.
- Scoped, then unscoped for the same type: the plain rows are added and the joined rows stay.
- An unparsable scope, `^.` at the top and inside an aggregate, and the same type with two scopes: each throws
  `ArgumentException` and the role is unchanged.
- `GrantRoleRows` and `GrantRequesterRows` alone write only their rows (no type, member or navigation rows).
- The tuple overload's rows are unchanged; the existing tests stay green as they are.

A role with one scoped and one unscoped subject is tested on rows only: the secured model has one `IWorkflowSubject`
(`PurchaseRequest`; `OtherSubject` lives only in the plain EF test context). Both row shapes it combines are read under
security above.

## Case list (self-review before the Codex hand-off)

- Scope syntax: plain member, collection aggregate, nested free join, Or/Not groups (wrapped by `GroupOperator`), quoted
  literals with `'` and `^.` inside a string literal (a literal, not a property: allowed).
- null/empty/whitespace scope: unscoped.
- Several subjects in one call, mixed; the same type twice, equal or different scopes.
- Repeat interaction: twice; scope changed; unscoped after scoped (other type, same type); wide to per type with
  previously served types; legacy `In` rows with one or several types; admin Deny on old and current rows; admin row with
  Write set.
- Every caller: the demo `Updater` (tuple overload), the tests, BPG later (`GrantRoleRows`, `GrantRequesterRows`).
  No caller passes zero subjects to `GrantParticipant` (the two `params` overloads would be ambiguous with none); checked
  with git grep over XafStateless and BPG: every call passes at least one.
- Roles: a user holding a scoped and an unscoped role for one subject sees the union (XAF). Documented.
- Type names: the join uses `FullName` (dxdocs: types from different namespaces need it). Nested CLR types (`+`) are not
  supported as subjects by this path; not seen in BPG or the demo.
- Requester per-type mode: a participant role's requester rows now cover only the types passed (and those its instance
  rows served). Documented.

## Review asks

1. The ownership rule (text plus state shape) and the removal list: does any row XafStateless wrote still grant wider
   access after a scoped grant, and can any row an admin wrote be removed beyond the documented identical-row case?
2. The switch from role-wide to per type: are the served types derived correctly from the instance rows?
3. The requester scoping and its per-type consequence.
4. The `^.` rejection and the supported-shape tests: enough for the SQL and in-memory paths?
