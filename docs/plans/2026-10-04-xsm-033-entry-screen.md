# XSM-033: Entry screen (a workflow start page opened from navigation)

Card: XSM-033 (CARD-2063). The card's assessment stands: XAF already has the view (a non-persistent object's DetailView
opened from a navigation item, docs 113471 and 116106); this card builds the pattern and one demo screen, no new view
type. The card's open question (one generic screen or one per definition) is answered as the card proposed: the demo
screen is generic, the host writes one screen class per entry page it wants.

## Design

**Module (platform-agnostic), `XafStateless.Module`:**
- `IEntryScreen`: a marker interface for non-persistent entry screens (`[DomainComponent]` classes). Nothing to implement.
- `EntryScreenController : ViewController<DetailView>` with `TargetObjectType = typeof(IEntryScreen)`: the activator from
  docs 113471. OnActivated: when the view's object space is a `NonPersistentObjectSpace` and `View.CurrentObject` is null
  (a navigation item with an empty ObjectKey, or a deep link on a fresh circuit), create the screen with
  `ObjectSpace.CreateObject(View.ObjectTypeInfo.Type)` and `RemoveFromModifiedObjects` it, so closing never asks to save
  (docs: the Blazor confirmation controller acts on non-persistent objects only when `AutoSetModifiedOnObjectChange` is
  on, which the module leaves off). It also hides the Save actions of `ModificationsController` on the screen: there is
  nothing to save.
- `WorkflowCommand.Caller(app, os, subjectType)`, a new overload screen controllers use (Codex review 2026-10-04): the
  caller's `Secured` is `WorkflowCommand.SpaceFor(os, subjectType)`: `os` itself when it knows the type by itself
  (`IsKnownType(type, includeAdditionalObjectSpaces: false)`), else the first of its `AdditionalObjectSpaces` that knows
  it (the user's secured EF Core space the host populates, below). Never `CanInstantiate`: on a `CompositeObjectSpace` it
  answers through the additional spaces, so it would hand back the composite. When the application has security
  (`app.Security is IRequestSecurityStrategy`) and the resolved space is not an `ISecuredObjectSpace`, `Caller` throws
  (InvalidOperationException naming the type): a plain space is only accepted in an application without security, as
  the runtime documents. Without this the runtime's Write check (`c.Secured is ISecuredObjectSpace && !CanWrite`) is
  skipped for a NonPersistentObjectSpace caller: a user with Read only could start workflows from a screen. The 2-argument
  `Caller` stays for subject views, whose space is the subject's.
- The host's `Startup`: `.AddNonPersistent()` (the template has it) plus the `OnObjectSpaceCreated` handler from docs
  116106 that calls `PopulateAdditionalObjectSpaces` on every root `CompositeObjectSpace`, so a screen's lookups and nested
  lists reach persistent objects through the user's secured object space. The module cannot do this for the host (it is
  application-builder code); documented in integrating.md with the demo as the copy source.

**Demo, `XafStateless.Demo.Module` + `Demo.Blazor.Server`:**
- `StartRequestsScreen : IEntryScreen, IObjectSpaceLink, INotifyPropertyChanged` (`[DomainComponent]`, Guid key hidden):
  - `Definition` (string, `StringLookup.Single`, required): the Published definitions for PurchaseRequest, read through
    the linked object space (the composite routes `WorkflowDefinition` to the secured EF space).
  - `OnlyLarge` (bool checkbox): list only requests with Amount over 1000.
  - `OpenAfterStart` (bool checkbox, default on): open the started request when exactly one was selected.
  - `Requests` (a `BindingList<PurchaseRequest>`, exposed as `IList<PurchaseRequest>`): the requests without a workflow
    (Amount over 1000 only when OnlyLarge), shown as a nested ListView the user selects rows in; its model sets
    `DataAccessMode="Client"` explicitly (demo xafml), so the materialized list is the contract.
  - **One reload path**, `Reload()`: refresh the linked object space (the host's handler sets
    `AutoRefreshAdditionalObjectSpaces`, so the composite refreshes the EF space too, docs 116106), requery
    `ObjectSpace.GetObjects<PurchaseRequest>(criteria)` through the composite, then Clear + Add into the same BindingList,
    so the nested list editor sees list-changed events. Called when the object space is linked, by OnlyLarge's setter
    (`[ImmediatePostData]`), after Start, and by the Refresh action (the controller handles `ObjectSpace.Reloaded`).
- `StartRequestsController : ObjectViewController<DetailView, StartRequestsScreen>` (demo module): a `Start` SimpleAction,
  always active; it validates when executed (no selection-driven enablement, so no SelectionChanged plumbing): no
  Definition → "Pick a workflow"; no selected rows → "Select at least one request". The selected rows come from the
  `Requests` editor (`View.GetItems<ListPropertyEditor>().Single(e => e.PropertyName == nameof(Requests))` →
  `ListView.SelectedObjects`), their ids captured before anything reloads. For each id run
  `runtime.Start(caller, typeof(PurchaseRequest), id, Definition)` as its own command (the runtime's unit is one subject),
  with `WorkflowCommand.Caller(Application, View.ObjectSpace, typeof(PurchaseRequest))`. Report both halves: "Started n
  of m." plus the first refusal's text when any failed (a refused second start does not hide a committed first one);
  then `Reload()`. When OpenAfterStart, exactly one request was selected and it started: `e.ShowViewParameters.CreatedView`
  = that request's DetailView, loaded by id into a new object space from `Application.CreateObjectSpace(typeof(PurchaseRequest))`.
- Navigation: demo `Model.DesignedDiffs.xafml` adds `Default/Items/StartRequests` with `View="StartRequestsScreen_DetailView"`,
  `ObjectKey=""`, caption "Start requests". The Approvers role gets Navigate on it (Updater, idempotent like the others).
- Blazor host: the `OnObjectSpaceCreated` handler in `Startup.cs`.

Not done: a screen designer, the ActionView type the card ruled out, per-definition screens, multi-type screens.

## Tests

1. Module tests (LocalDB), `EntryScreenTests`: `WorkflowCommand.SpaceFor` on a `NonPersistentObjectSpace` whose
   AdditionalObjectSpaces holds an EF space returns the EF space for PurchaseRequest (the exact predicate error Codex
   found: `CanInstantiate` would return the composite) and the composite itself for the screen type; on a plain EF
   space returns it; the secured-space requirement throws for a plain space when security is configured and accepts it
   when not. `PermissionTests`: a reader (Read only on PurchaseRequest) whose caller carries the composite space
   resolved through `SpaceFor` is refused at Start with "requires write access"; the approver through the same path
   starts.
2. E2E, new test in `DesignerJourneyTests` (same seeded database; unique request names, no dependence on empty lists):
   the approver creates three requests without starting them (A 50, B 2000, C 70); opens "Start requests" from the
   navigation by a plain click (no Refresh, so initialization is what is tested); Start with nothing picked shows "Pick a
   workflow"; OnlyLarge on lists B only, off lists all three; picks purchase-request, selects A and C, Start → "Started 2
   of 2", the list keeps B only; selects B with OpenAfterStart on, Start → the request's tab opens with Workflow "Draft";
   closes it, reopens the screen from the navigation: a fresh screen (no definition, OpenAfterStart on) and no save
   prompt. Screenshots `10-start-requests-screen`, `10b-started`. Partial success (one refused among several) is not
   staged in the E2E: the runtime's refusals are unit-tested, the controller only concatenates the two halves.
3. The demo's `--updateDatabase` run (part of the E2E cycle) proves the navigation permission seeds.

Found by the E2E (2026-10-04): (a) the request opened after Start must open in the screen's own tab
(`TargetWindow.Current`): a new tab is skipped when the request is already open in a stale tab, which then shows it
without its workflow; (b) XAF 26.1 Blazor restores a user's tabs at the next login and caps them at 10
(`BlazorMdiShowViewStrategy.MaxTabLimit`), refusing to open an eleventh: the tests now run in a fixed order and the
entry-screen test closes the restored tabs first. Neither is a module defect.

## Docs

- `docs/integrating.md`: new step **11. Entry screens** (pattern, host Startup handler, the SecuredSpaceFor rule, the
  activator, gotchas from the card: RemoveFromModifiedObjects, nested lists need the additional object space, deep links);
  Check it becomes 12.
- `README.md`: Status line; the approver row of the Try table gains "Start requests".
