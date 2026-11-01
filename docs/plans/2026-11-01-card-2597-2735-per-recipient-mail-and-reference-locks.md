# CARD-2597 + CARD-2735 (XafStateless half): per-recipient mail bodies; reference locks

Both go into the unreleased 0.4.0 with CARD-2615 (1) and CARD-2713.

## A. CARD-2597: a workflow mail's body rendered per recipient

Today `WorkflowRuntime.Fill` renders a Notify body once, at state entry, in the command's non-secured space:
`{Customer}` puts the referenced record's text into the mail even for a recipient who may not read it.

Change:
1. **The notification keeps the template.** `WorkflowNotification.Body` stores the Notify text unrendered (no new
   column, no schema change). A null Notify text still throws (validation refuses it), now as
   `InvalidOperationException("a notification needs a text")`.
2. **`WorkflowNotifications.Render(WorkflowNotification note, IObjectSpace space, IRequestSecurityStrategy? security)`**
   (new public static, Module/Services): the body for one recipient.
   - The subject is loaded in `space` by `note.Instance.SubjectType/SubjectId` (type resolved through `space.TypesInfo`).
   - Each `{Path}` placeholder (a member or a dotted path, as validation accepts) is walked hop by hop
     (`IMemberInfo.GetPath()`); each hop needs `security.CanRead(space, owner, member)` (dxdocs IsGrantedExtensions).
     A final reference value also needs `CanRead` on its default member (object-level when it has none): `{Customer}`
     is the customer's name, so a recipient who may not read Customer (type deny) or that customer (criteria) sees the mask.
   - Denied anywhere, or the subject not found in `space`: the placeholder renders as `Masked` = `"*******"`.
   - Otherwise the text is what Fill produced before: `Convert.ToString(value, InvariantCulture) ?? ""`; an
     unknown member renders "" (as before).
   - `security == null`: no masking (a host that renders for a log, as the demo does). Documented as such.
   - The host signs the recipient in (BPGDemo's dispatcher already does, per recipient, for `RecordAccess.MayRead`) and
     passes that scope's secured space and `ISecurityStrategyBase as IRequestSecurityStrategy`.
3. One walker serves both: the runtime's RunAction argument still fills at command time, unsecured (trusted host code),
   through the same function with no security.
4. Values are now those at dispatch time, not at state entry. The dispatcher runs right after the commit; documented.
5. Old Pending rows hold rendered text: rendering again leaves them as they are unless a value contained `{X}`.
6. Demo `LogNotificationDispatcher` renders with its non-secured space and no security (it logs). docs/integrating.md
   section on notifications: Body is a template; mail it through Render from the recipient's secured space.

Tests (Module, real XAF security, PermissionTests-style secured providers):
- the row's Body is the template, not the filled text;
- a readable member renders; a member-denied one masks (`AddMemberPermission` deny Read on Amount);
- `{Buyer}` (reference to ApplicationUser): masked for a user without read on ApplicationUser; the user name for one with;
  masked when an object criterion allows only another user;
- a dotted path `{Buyer.UserName}` masks on either hop;
- a subject the recipient cannot load: every placeholder masked; text outside placeholders kept;
- `security: null` renders everything (the old Fill output).

## B. CARD-2735 (XafStateless half): lock reference members

Decided on the card (bpg-04, 2026-11-01): a state may lock a reference member; it changes when its key does.

1. `WorkflowMembers.Lockable(target)` = `Supported` plus **references**: public, persistent, writable, visible,
   non-list members whose type is a persistent class; never `Workflow`, the key or the lock field; never a one-to-one
   (an associated member that is not a list: the key may sit on the other row, which this row's lock cannot see).
2. `XafWorkflowEnvironment.CheckMember` (used only for locks) and the `LockedMembers` lookup use `Lockable`.
   SetField, Increment and rule outputs stay on `Supported`.
3. `StateLockGuard`: a locked member resolves to its column: the property itself, or, for a reference, the dependent
   side's single foreign-key property (`FindNavigation(m).ForeignKey`, when this entity is the dependent and the key has
   one property). Modified: `IsModified` on that column, then judged against the database by that column (RawSql reads
   property names). Added before the start: the column against the baseline instance's value (null for a reference).
   `DetectChanges` (already first) fixes FKs up from navigations. The refusal still names the member.
   The FK is usually a shadow property (no CLR member): `e.Property(fk)` and RawSql both take shadow names; the Added
   baseline for a reference is the baseline instance's navigation key (null when it has none), not a CLR FK getter.
4. `StateLockAppearanceController`: unchanged (the view item ID of a reference is the member name). A test checks.

Tests (B): a published definition locking `Buyer`; changing Buyer after the start is refused (by navigation and by FK);
an unchanged save passes; an unstarted new request with a Buyer is refused when the initial state locks it; validation
accepts `Buyer`, refuses `Workflow`; the appearance seam returns Buyer as locked.

## Plan review (Codex gpt-6-luna; Gemini's quota was out) and how each finding was settled

1. Old Pending rows hold filled text that could contain `{X}`: **accepted**. New column `IsTemplate` (false on old
   rows, returned as stored); fixed in DefinitionGuard; tested.
2. The note and the secured space come from different spaces: **already so**. Only `SubjectType`/`SubjectId` are read;
   the tests render a note from a non-secured space with a secured one.
3. Shadow and CLR foreign keys, forged OriginalValue: **accepted**. `OtherSubject.BuyerId` (CLR FK) added to the test
   subjects; tests for the navigation, the shadow FK, a CLR FK, a detached Update and a forged original key.
4. An initialized reference baseline: **partly**. The baseline uses the fresh instance (CLR getter, or the navigation's
   key), as scalar locks do; only the default-null case is tested (no subject initializes a reference).
5. Dotted paths: `FindMember` resolves them already (validation passes `{Buyer.UserName}`); a null hop renders "" and
   is tested.
6. A blanked object: covered by the criteria tests (masked or empty, never the value).
7. One-to-one detection: the predicate is `AssociatedMemberInfo is not { IsList: false }`; **untested** (no one-to-one
   subject in the tests).
8. Unknown subject type: masks every placeholder; tested.
9. Per-recipient rendering: the tests render one note for several users with different results; the docs require one
   Render per recipient.

## Found while verifying

- The demo's Workflow Notification list showed `Body`, now a template ("Your request {Title} was ordered"): the E2E
  caught it. Added `WorkflowNotification.Text` (NotMapped), rendered in the space that loaded the row with that space's
  security strategy; `Body` is hidden in views. Tested in a secured space.
- A reference hidden by the recipient's criteria comes back null from the secured space, so it renders empty (as their
  views show it), not masked. A member-level or type-level deny masks.

## Diff review (Codex)

One Minor: no test isolates a denial on the second hop of `{Buyer.UserName}`. `A_reference_whose_default_member_is_denied_is_masked`
does (Buyer readable, UserName denied); a mutation that checks only the first hop fails it.

## Verification

- `dotnet test XafStateless.slnx`: Engine 59, Module 217.
- 17 mutations (scratchpad `mutate2597.py`), each failing a test.
- E2E 5/5 on a freshly seeded `XafStatelessE2E`.
- Browser smoke on the built demo: a draft that locks Buyer disables the Buyer lookup (a control request on the seeded
  workflow keeps it editable), a new unstarted request too; the notification list shows the rendered text; the demo
  log shows rendered mails.
