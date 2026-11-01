# CARD-2788 (+ CARD-2783): a notification keeps the values of its moment, still masked per recipient

## Problem

Since 0.4.0 (CARD-2597) a `WorkflowNotification` stores the Notify template and `WorkflowNotifications.Render` fills it at
send/view time from the subject as it is then. BPG's projects pack notifies "Your hours for {Description} are rejected:
{RejectionReason}" and clears `RejectionReason` on resubmit, so the in-app notice later reads "...are rejected: " (and a
mail sent after a quick resubmit too). Martin's decision on the card (option a): snapshot each placeholder's value when
the notification is created; a value the recipient may not read stays masked.

CARD-2783: docs/integrating.md step 9 says a reference hidden by the recipient's criteria renders empty; in a host with
`PreFetchReferenceProperties()` it renders `*******`. The snapshot design below makes both setups mask it.

## Design

1. **New column `WorkflowNotification.Snapshot`** (`string?`, JSON, hidden in every view; a schema change). Written by
   the runtime with the template, fixed afterwards (`DefinitionGuard.CheckNotifications` adds it to the fixed columns).
   Shape: `{ "<placeholder>": { "Text": "...", "Keys": ["<key>", ...] } }`, one entry per distinct placeholder.
   - `Text`: what `Fill` produced unsecured at that point of the state's effects (pre-0.4.0 semantics: a SetField before
     the Notify is visible, one after it is not).
   - `Keys`: the key (`IObjectSpace.GetKeyValueAsString`) of every record the placeholder read after the subject, in
     walk order: each reference hop's value, and for a reference placeholder the referenced record (its default member).
     A null hop ends the list (text "").
   - Recorded through `Fill`'s existing `mayRead` callback (always true): per placeholder the callback sees the subject
     first, then exactly the objects the render checks. The first call is dropped by position, not by identity (a
     reference may point at the subject itself).
2. **`Render(note, space, security)`**:
   - `IsTemplate` false: `Body` as stored (pre-0.4.0 rows), unchanged.
   - `Snapshot` null (0.4.0 rows): live render, unchanged.
   - `security` null: each placeholder's snapshot `Text` (a log; no load).
   - Otherwise: subject loaded by key in `space` as today (unknown type / not found: every placeholder masked). Per
     placeholder, replay the walk on the recorded records: the path is resolved from the subject's type info
     (`FindMember`; unknown member renders "" as today); hop i's owner is the subject (i = 0) or the record
     `Keys[i-1]` loaded with `space.GetObjectByKey(hops[i-1].MemberType, space.GetObjectKey(hops[i-1].MemberType, key))`.
     Masked when a recorded record cannot be loaded (deleted, or hidden by the recipient's criteria/type permission) or
     `security.CanRead(space, owner, hop.Name)` is false; for a reference placeholder also when the referenced record
     cannot be loaded or `CanRead(record, DefaultMember?.Name)` is false. Keys exhausted before the end: the value was
     null at creation; the snapshot text ("") after the hops checked so far, as Fill does today.
   - Else the snapshot `Text`. A placeholder missing from the snapshot (cannot happen for a runtime row) masks.
3. Permission checks run on the records as they are now (current criteria), values come from the snapshot. A reference
   changed since the notice is judged on the record the notice names, not the new one.
4. `WorkflowNotification.Text` (in-app list) goes through `Render`: unchanged code.
5. RunAction's argument keeps filling live, unsecured, at command time (unchanged).
6. Docs: integrating.md step 9 rewritten (values are those at notify time; a criteria-hidden reference masks; deleted
   records mask; 0.4.0 rows still render live and their criteria-hidden reference depends on the host's prefetch
   setting); the fixed-columns sentence gains `Snapshot`.

## Self-review: cases and where each is covered

| Case | Test (Module, NotificationRenderTests) |
|---|---|
| scalar, reference, dotted path snapshot text | `Without_security_everything_renders` (snapshot) |
| value changed after notify (the card's bug) | `Values_are_those_at_notify_time` (Title + Buyer changed/cleared, both renders keep the old text) |
| member deny still masks after change | same test, NoAmount |
| reference default member denied | existing `A_reference_whose_default_member_is_denied_is_masked` |
| reference hidden by criteria: masked (CARD-2783) | `A_reference_the_criteria_do_not_allow_is_masked` (was "renders empty") |
| reference type not readable | existing `A_denied_member_and_an_unreadable_reference_type_are_masked` |
| referenced record deleted since | `A_deleted_referenced_record_masks` |
| reference null at creation | `A_reference_empty_at_notify_time_renders_empty` |
| subject unreadable / unknown type | existing theory + unknown-type test (secured) |
| security null, unknown subject | renders the snapshot |
| 0.4.0 row (no snapshot) renders live | `A_row_without_snapshot_renders_live` |
| pre-0.4.0 row | existing |
| SetField after Notify in the same state not in the text | `A_later_effect_of_the_same_state_is_not_in_the_text` |
| Snapshot fixed once written | DefinitionGuard test next to the Body one |
| self-reference / duplicate placeholder | duplicate: template repeats {Title}; self-reference: not in the demo model (untested, positional drop) |
| in-app list text | existing `The_list_text_is_rendered_in_the_viewers_space` |

## Plan review (Gemini, gemini-4-argon-high) and how each finding was settled

The design above was revised; the implemented design is the "Revised design" section below.

1. **Critical, accepted.** A `Snapshot` CLR property would be readable by every participant through any secured read
   (column chooser, criteria, Web API, BPG's assistant), unmasked. Settled differently from Gemini's two proposals
   (member deny per role: fails open for a host's own roles; a separate table: still an XAF type an AllowAll role
   reads): the snapshot is an **EF shadow property** declared in `ConfigureXafStateless`. XAF builds members by
   reflection (`EFCoreTypeInfoSource` : `ReflectionTypeInfoSource`, 26.1 source), so it is no XAF member: no view,
   criteria, permission or post-processor sees it. Written through the command space's entry, read with `RawSql` (plain
   ADO, outside security). Tested: no XAF member, a criteria on it fails.
2. **Critical, accepted.** `GetObjectByKey` returns an entity already tracked (prefetched by another row) without
   running the secured query. Records are loaded with a secured query by key (`GetObjects(type, [Key] = k)`), which the
   database filters; the subject too. Tested with prefetch on, two notes in one secured space.
3. **Critical, accepted.** Keys are written `Convert.ToString(GetKeyValue, InvariantCulture)` and parsed invariantly by
   the key member's type (Guid, string, IConvertible); a parse failure or an unsupported key masks.
4. **Major, partly.** Under prefetch, `CanRead(owner, "Buyer")` judges the owner's current target: a reference reassigned
   since to a record hidden from the recipient masks the old (readable) value. That fails closed (never shows a hidden
   value); accepted and documented. Gemini's type-level check would ignore criteria member permissions (fails open).
5. **Major, accepted.** A reference shows its runtime type's default member (`BaseObject.ToString`); the check now uses
   the runtime type info (`TypesInfo.FindTypeInfo` unwraps proxies, 26.1 `TypesInfo.cs:104-117`), in both paths. An
   upcast hop (`<Company>TaxId`) on another subtype renders "" without a read; interface hops are no problem since
   records are stored with their runtime type. Not tested (no derived subject in the demo model).
6. **Major, accepted.** Reads are recorded only on persistent owners (`{Title.Length}` records the subject's Title only).
7. **Major, accepted.** One walk per distinct placeholder returns its text and its reads; no positional index math.
8. **Major, accepted.** Tests run with and without prefetch; multi-hop `{Workflow.Definition.Name}`; `{Buyer}` repeated;
   OneUser reassigned/cleared cases; snapshot confidentiality.
9. **Minor, moot/accepted.** Not an XAF member, so the secured post-processor never blanks it; docs updated.
10. **Minor, accepted.** Unreadable JSON masks every placeholder; the schema note goes into integrating.md.

## Revised design

- `WorkflowNotification` shadow column `Snapshot` (`nvarchar(max) NULL`), declared by `ConfigureXafStateless`. A host
  without it (not calling `ConfigureXafStateless`) gets no snapshot: rows render live, as 0.4.0.
- JSON: `{ "<placeholder>": { "Text": "...", "Reads": [ { "Type": "<runtime type full name>", "Key": "<invariant>",
  "Member": "<member or null>" } ] } }`.
- `Walk(typesInfo, keyOf, subject, path)`: per hop, a null value renders ""; a non-persistent owner is read without a
  check; an owner not of the hop's declaring type renders ""; else a read (owner, hop). A final value of a persistent
  runtime type adds a read (value, its runtime type's DefaultMember, null = the object).
- `Render`: pre-0.4.0 rows as stored; no snapshot: live walk on the subject loaded by secured query, masked when any
  read is denied; snapshot + `security` null: the texts; snapshot: subject by secured query (not found: all masked),
  each read's record by secured query by key (cached per call), masked when not found, unparsable or `CanRead` false.
- `DefinitionGuard`: `Snapshot` joins the fixed columns when the model has it.

## Diff review (Gemini, on 881842f) and how each finding was settled

The findings fall into one family: what the replay can and cannot judge. Leaks (1, 2, 4) were chased; the rest fail late
(3, 6, 7, 8, 9) and got one guard each or a card.

1. **Critical, accepted.** A calculated member, or a reference shown by ToString, was snapshotted unsecured while only the
   outer member was checked. Umbrella: snapshot only what a permission check can judge (stored members; a reference's
   stored scalar default member, whose value is now the text instead of ToString). Anything else is an entry marked
   `Live`: filled at render time in the recipient's space, as 0.4 did. Demo `PurchaseRequest.Summary` ([NotMapped],
   hidden) tests it. A reference whose default member is calculated or a reference: same rule, untested (no such type in
   the demo model; the mutation survives).
2. **Major, partly.** The shadow value is loaded into a secured DbContext (reachable only by host code through
   `EF.Property`, which is trusted) and XAF's Middle Tier copies shadow properties into the client payload. Gemini's
   shared-type entity was tried and dropped: XAF needs change-tracking-proxy-capable bags (Dictionary fails at model build)
   and `EFCoreExportedTypeHelper` exports every entity type's CLR type, so a custom bag class would become an XAF type.
   Kept the column; integrating.md says Middle Tier hosts are not supported. **Decision for Martin** (handoff).
3. **Major, accepted.** Null-filled JSON is validated (any null text, reads, type or key masks every placeholder); the
   subject type must have a Guid key.
4. **Major, accepted.** Render needs an EF Core object space; anything else (composite, non-persistent) masks every
   placeholder.
5. **Major, partly.** (1) The list-text test cannot give the secured test space a ServiceProvider; the real path (strategy
   from the space's ServiceProvider) is covered by the E2E, checked by a mutation run. (2) The query probe asserts an
   exception. (3) Both reassignment directions added, with and without prefetch.
6. **Minor, accepted.** The EFCoreCollection is disposed.
7. **Minor, carded.** An IDENTITY-keyed record created in the same command records a temporary key and masks (fails
   closed).
8. **Minor, accepted.** A notification deleted since it was loaded masks every placeholder.
9. **Minor, accepted, wider than reported.** The guard judges only the columns a save writes: an attached untracked row
   carries null shadow values (the snapshot and the instance key), and neither Attach nor Update writes them (verified:
   EF leaves unknown shadow values unmodified). Tested both.

## Third review (Gemini, on a20ea26)

No Critical. The same family again (what the replay can judge); leaks fixed, the rest fixed when cheap or carded.

1. **Major, accepted.** `{Buyer.Roles.Count}`: a collection hop counted rows the recipient's criteria hide. Collection hops
   are not judged (Live). Tested.
2. **Major, accepted.** A strategy with a non-secured space (a dispatcher passing its outbox space) rendered what the
   strategy denies; a disposed space threw. Now a secured space needs its strategy and a strategy its secured space; a
   disposed space masks. Tested (non-secured + strategy).
3. **Major, accepted.** A reference whose default member is a reference showed via ToString without checking the inner
   record. The default member is followed while it is a reference (cycle-safe), each record read; calculated or
   collection default members are Live; no default member: ToString, Live. Untested (CARD-2811).
4. **Major, carded (CARD-2812).** The guards read a reference's key from the navigation before the FK property; contrived
   (host code detaching the principal), shared by CheckTasks and StateLockGuard.
5. **Minor, partly.** The non-EF test now uses a composite space with the EF space added. Gemini's premise (it then knows
   the subject type) did not reproduce: `IsKnownType` stays false; the gate masks either way.
6. **Minor, accepted.** A damaged entry next to sound ones masks all; the delivery test checks the instance key survives and
   that Update with a changed Body is refused.
7. **Minor, accepted.** A record with a composite key makes its placeholder Live; single-key lookups use `KeyMembers: [k]`.

Verification: Engine 59, Module 248; 16 of 17 mutations killed (survivor: CARD-2811).

## Not in scope

The release (0.5.0 to the private feed) waits for Martin's go; BPG then bumps and adds its E2E check.
