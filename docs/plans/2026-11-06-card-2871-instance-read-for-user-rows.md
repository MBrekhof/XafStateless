# CARD-2871: requester and assignee rows read their instance

## Problem

`WorkflowNotification.Text` renders in the viewer's secured space; `WorkflowNotifications.Render` reads `note.Instance`
(SubjectType, SubjectId) and masks everything when the instance is null. `GrantRoleRows` grants Read on
`WorkflowInstance`; `GrantRequesterRows` and `GrantAssigneeRows` grant only `[UserId] = CurrentUserId()` on notifications
and tasks. A user who reads their own notification or task only through those rows (a `@Member` recipient whose role
reads the subject but has no role rows) sees the notification fully masked and the task's Instance empty.

Reproduced: `NotificationRenderTests.A_user_addressed_notification_renders_for_a_requester_rows_role` (unscoped and
scoped) and `A_user_assigned_task_shows_its_instance_for_an_assignee_rows_role` fail with `Instance` null.

## Fix (library, permissions only)

Two new row families in `WorkflowPermissions`, written by the existing `Rows` machinery:

- `RequesterInstanceFamily`: target `WorkflowInstance`, head
  `[<XafStateless.Module.BusinessObjects.WorkflowNotification>][[Instance.ID] = ^.ID And [UserId] = CurrentUserId()]`,
  type/id paths `SubjectType` / `SubjectId` (as `InstanceFamily`).
- `AssigneeInstanceFamily`: the same with `WorkflowTask`.

`RequesterRows` also writes `RequesterInstanceFamily`; `AssigneeRows` also writes `AssigneeInstanceFamily`. So, like the
note/task rows they mirror:

- unscoped and no per-type rows yet: one role-wide row (the head alone): an instance one of the user's own notifications
  (tasks) points at;
- per type: `head And [SubjectType] = 'T'`, plus the subject join `[<T>][[ID] = ^.SubjectId And (scope)]` when scoped.
  Moving from role-wide to scoped narrows the wide row as for every other family.

The user reads exactly the instances of the notifications/tasks they already read (same type list and scope), nothing
wider. The instance carries state key, due time, definition link and subject type/key: no subject data.

Not chosen: `Render` reading the instance pointer with raw SQL. It would fix the text but leave the list's Instance column
and the task's Instance empty for these users, and bypass the permission system for one more read.

## Cases checked (self-review)

- `ServedTypes` (role-wide → per-type migration of `InstanceFamily`) must not count the new rows: `Match(InstanceFamily,
  …)` returns null for them (first operand is a join, not `[SubjectType] = 'T'`) and `Legacy` matches only `In`. The
  legacy-combined-row branch in `Rows` is guarded by `f == InstanceFamily`.
- Idempotent: `Object` reuses a row with the same criteria text; second call adds nothing (test).
- `GrantParticipant` calls RequesterRows/AssigneeRows, so participant roles get the new rows too (a union with their role
  rows; no narrowing of the role rows, since families narrow only their own rows).
- Scope parse/validation unchanged (shared `Validate`).
- Other users' instances: not readable (test). Out-of-scope subject: not readable when scoped, readable unscoped (test).
- Existing hosts: the rows appear when the host's updater calls the grant again (it runs every start; XSM-021).

## Gemini plan review (2026-11-06) and how each finding was settled

1. *Unscoped grant writes a role-wide instance row; a second role's wide row widens another role's scope.* By design: the
   instance row mirrors the user's own-notification row, which is already role-wide in the same cases (CARD-2144,
   documented). The user never reads an instance whose notification (task) row they cannot read. Nothing wider.
2. *Two families computed independently drift on roles seeded before 0.5.0* (an added unscoped type wrote a role-wide
   instance row past the scope; types a narrowed wide row served got no instance row). **Fixed by a redesign**: the
   instance rows are no longer a `Family` of their own. `InstanceRows(r, f)` builds one per Allow row of the
   requester/assignee family, `[<N>][[Instance.ID] = [^.ID] And (that row)]` (the inner subject join's `^.` still binds
   to the notification), writes the missing ones and narrows those whose source row is gone or denied. Tests:
   `On_a_role_seeded_before_the_instance_rows_follow_its_existing_rows`, `A_denied_own_row_takes_its_instance_row_away`,
   a changed scope in `Requester_and_assignee_rows_read_their_own_instances`.
3. *Participants get redundant EXISTS rows; no index on `WorkflowNotification.UserId`.* Kept: leaving them out of
   `GrantParticipant` would make the user's instance read depend on the role rows staying in step with the own rows
   (an admin edit breaks that). The EXISTS correlates on the `Instance` foreign key, which EF indexes; `UserId` is a
   residual filter on that seek, so no new index (and no schema change).
4. *No prefetch or list-query coverage.* Fixed: both render tests run with and without `PreFetchReferenceProperties`
   and check the list query too.
5. *Docs.* Fixed: the criteria table and step 6 in `integrating.md` (including: these roles read no definitions).
6. *Raw-SQL alternative.* Gemini agrees it is worse (leaves `{Workflow…}` placeholders, the Instance columns and the
   subject's Open task action broken for these users).

## Gemini diff review (2026-11-06)

No security finding (it checked the nested join's `^.` binding, `And` flattening, null and admin-edited rows, rows listed
twice before commit), idempotency and docs. Three findings, all fixed:

1. *Narrowing recognised a row of this kind by its first operand only*: an admin's join row such as
   `[<N>][[Instance.ID] = [^.ID] And [Role] = 'Auditors']` would lose its Read. Now the rest must also be an own row of
   the family.
2. *Surviving mutations* (delete instead of `Narrow`; no first-operand check): `A_denied_own_row_takes_its_instance_row_away`
   now carries an admin edit on an instance row of this kind and two admin join rows. Three mutations of `InstanceRows`
   (delete, no remainder check, no first-operand check) each fail a test.
3. *The assignee render test ran unscoped only*: now scoped too, with an out-of-scope task.

## Tests

The two new tests above, plus `WorkflowPermissionsTests` asserting the written rows (wide, per type, scoped, re-grant).
Full Module suite. No E2E change (the demo's roles are participants with role rows).
