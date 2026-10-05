# XSM-030: Parallel branches with a join — design options

Card: XSM-030 (CARD-1921). Status: **design, not implementation**. The card's obstacle is an invariant of the module (one
current state per instance, `WorkflowInstance.StateKey`, 34 uses across runtime, guards, tasks, timer, diagram); which
way to relax it is a decision for Martin, with Codex's second opinion attached below.

## What the target scenarios need (from the card)

- Helpdesk: "waiting for parts" alongside the fix: two branches, each with its own waits and tasks.
- Driving school: the document check alongside invoice → wait-for-payment: again branches with sub-flows.
- Both continue when **both** branches are done (a join). Today both validate only serialised.

## Options

### A. Branches as child instances (recommended)

A new `StateKind.Fork`. The states of a branch belong to the fork: `WorkflowState.ParentKey` (designer: a "Branch of"
lookup listing the Fork states), so a definition stays one graph; a branch's initial state is the one no transition inside
the branch enters, and it ends in its own Final state(s). The fork has exactly one unguarded exit, the join.

Runtime: entering a Fork creates one child `WorkflowInstance` per branch (`Parent` link, `BranchKey`), on the same subject
and the same pinned definition, started at the branch's initial state. A child runs with everything that exists
today, unchanged: its own tasks (Task states), timers (Wait, `DueUtc` on the child), Decisions, effects (on the shared
subject), field locks. When a child enters a Final state and every sibling is Final, the parent fires the join (inside the
same command, like a Decision). A manual transition out of the Fork before the join (a Cancel) ends the running
branches: their open tasks Superseded, their DueUtc cleared, their state set to a built-in "cancelled" marker.

What changes where:
- Engine: `StateKind.Fork`, `StateDef.ParentKey`; validator rules (a Fork has 2+ branches, each branch well-formed and
  finishing in a Final of its own, no transition crosses a branch boundary, the join exit unguarded); machine: per
  instance as today, plus "fork entered → children" and "all children Final → join" hooks; Mermaid: branches as composite
  states with `--` concurrency separators (`state fork { branchA -- branchB }`), which Mermaid stateDiagram-v2 draws.
- Module: `WorkflowInstance.Parent`, `BranchKey`; runtime: Enter of a Fork spawns children, Enter of a child's Final checks
  the siblings and fires the parent's join, Cancel ends children; Load by task resolves the child instance (tasks already
  carry their instance); the lock guard and appearance controller take the union of the parent's and the live children's
  locks; DefinitionGuard: children created and deleted by the runtime only, deleted with the parent; the subject's
  `Workflow` link stays the parent; its display text becomes "Fork caption" with the branch states in the instance view.
- Designer: the "Branch of" lookup on a state; the instance view lists child instances; the diagram's current-state mark
  marks the parent's state and each live child's.
- Tests: validator rules; runtime fork/join/cancel/timer-in-branch/lock-union; E2E: a two-branch version of
  purchase-request (finance check alongside a supplier confirmation task) walked end to end.

Cost estimate: 4 to 5 days (engine 1, runtime and guards 2, designer and diagram 0.5, tests and E2E 1, scenario
re-check 0.5). Everything that exists keeps its guarantees per instance; the invariant becomes "one current state per
instance, and a fork's branches are instances".

### B. A set of active states on one instance

`StateKey` becomes a list (`ActiveStates`), one Stateless machine per active state, the join an AND over branch
terminals. Every consumer of `StateKey` changes (tasks' "is current" check, timers' DueUtc per state, the lock guard's
"the state's locks", the delete guard's "non-Final", Decide/Fire/Outcomes, the diagram's current mark, the subject's
display text, raw-SQL reads in both guards). Estimate 1.5 to 2 weeks, with a schema change on a table the guards read
raw. Not recommended: A gives the same behaviour by composition.

### C. Parallel tasks inside one state

A Task state with several assignments (roles) that completes when all are decided. One to two days, but it covers "two
approvals at once" only: the scenario branches have waits and decisions of their own (invoice → wait for payment), which this
cannot express. Could be a later addition on top of A for the simple case.

### D. Keep serialising (status quo)

Documented workaround; the slower branch waits for the other. Zero cost; the scenarios stay approximations.

## Decisions for Martin

1. Do A now, or leave XSM-030 open until a consumer needs it? (A is the largest card so far; it reshapes the instance
   view and the diagram.)
2. If A: branches defined inside the same definition (the proposal: a "Branch of" lookup on states; one version, one
   diagram) or as separate definitions referenced by name (simpler runtime, but each branch is its own versioned
   definition and the diagram cannot draw the whole)?
3. If A: what a Cancel out of a fork does to running branches (proposal above: end them, supersede their tasks), and
   whether a branch may itself fork (proposal: no, one level).

## Codex's second opinion (2026-10-04, read-only review against the tree)

Agrees with A as the direction and with same-definition pinning and one-level forks as defaults; says the note is not
implementation-ready and understates the cost: "children run unchanged" is not true against this tree. Its findings:

1. **A unique index blocks child creation**: `ModelBuilderExtensions.cs` allows one live instance per (SubjectType,
   SubjectId), and `LostRace` classifies unique violations as lost races, so entering a fork would retry four times and
   report a concurrency failure. Uniqueness must become root-only plus per fork activation and branch; existing
   databases need the index change.
2. **`Load`'s link check fails for children**: it requires `subject.Workflow == instance`; tasks and timers on a child
   must validate child → activation → root instead.
3. **Branch identity and entry states must be explicit** (which branch owns a state, which state is its entry): inferring
   the entry from "no incoming edge" breaks on retry loops, and the validator's reachability starts at the definition's
   initial state only. Validate per branch.
4. **The join needs an activation identity and must count staged changes**: Parent + BranchKey cannot tell successive
   visits of the same fork apart; the last child is Final only in the command's object space when the join is evaluated,
   so a database count misses it. Create the full child set first, evaluate with command-state values, advance the parent
   once; forbid fork re-entry in the first slice.
5. **Cancel contradicts the "one unguarded exit" rule, and a "cancelled" marker state key is unsafe**: manual actions
   leave Manual states only, and a key missing from the definition makes an already-selected timer throw. Use an explicit
   cancellation status and keep a valid state key.
6. **"Children deleted with the parent" conflicts with retained task history** (tasks are never deleted and keep their
   instance): keep completed and cancelled children; protect the new Parent/branch/activation fields in DefinitionGuard.
7. **Agreement: the shared subject's lock version is a sound serialisation point** (two sibling completions conflict,
   the loser retries and can perform the join), provided child completion, parent advance, effects and tasks stay in one
   object space and one commit, never through a recursive public command. New race tests: sibling completions, completion
   vs cancel, timer vs cancel, parent-entry failure rolling the child back.
8. **Lock union**: read the activation's children through the guard's raw SQL, not tracked navigations; note that one
   branch's Set field exemption (per subject and member) can bypass another branch's lock.
9. **Branch manual actions and task navigation are missing**: `Fire` addresses the root, so a child in a Manual state has
   no exit; "Open task" finds the root's single task only. First slice: reject Manual states in branches.
10. **Also touched**: the definition snapshot and the New-version clone must carry branch metadata; Dry run needs branch
    routing or an explicit "unsupported"; children must inherit the requester or their notifications are skipped; host
    actions need the executing instance; Mermaid's `classDef` inside composite states needs a rendering spike.
11. **Cost**: 7 to 10 developer days for the full feature, not 4 to 5. Smallest honest slice: one-level forks with
    explicit branches and entry states in the same definition; Task, Wait, Decision and Final inside branches; a
    transactional join and cancel; retained history; the persisted lock union; basic branch and task lists; fork re-entry
    and unsupported shapes refused at publish; composite-diagram polish and branch manual actions deferred. Both
    scenarios proven in both completion orders. Wait is elapsed time only: a real payment or parts arrival still needs an
    explicit task outcome.
12. **B is shown unfavourably** (a fair B, typed execution-token rows, converges on A; the twofold cost gap is not
    established); **C really is insufficient** (one state and one deadline per instance).
13. **More decisions for the owner**: whether any Final counts as success or a rejection changes the join; whether
    repeated fork activation is required; who may cancel and whether committed branch effects need compensation; whether
    branches may overwrite the same subject fields; how external completion is signalled; whether basic branch and task
    visibility is enough for the first delivery. One-transaction commits, link validation, unique activation membership
    and retry tests are implementation requirements, not owner choices.

Net: option A, scoped to Codex's smallest slice, at 7 to 10 days. The decision whether to spend that now is Martin's.
