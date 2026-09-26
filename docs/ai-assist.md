# AI assist in the designer: design note (XSM-026 spike)

**Status: design only, nothing built.** This note answers the spike card's questions. Build work would become its own cards.

## What it is for

Three uses, in the order they would pay off:

1. **Draft a definition from a sentence.** "Purchase requests over 1000 also need Finance, then wait a day before ordering"
   becomes a draft definition. The designer reviews it, the validator checks it, a person publishes it.
2. **One-click fixes from validator findings.** Since XSM-024 each finding names its cause and, where it has one, its
   state ("'End' has no way out: make it Final, or add a transition from it"). A fix proposal turns that into a concrete
   change the designer applies or rejects.
3. **"Why is my request stuck?"** for participants, answered from the instance, its open tasks and its timer.

What it is not for: explaining a validation error. That is the validator's job, done deterministically since XSM-024,
and a model would only rephrase it.

## The rule that makes it safe: the model proposes, the engine decides

- The model never writes entities. It returns a **`WorkflowDef`-shaped JSON document** (states, transitions, effects),
  the same record the engine already validates, draws and dry-runs.
- The module maps that document into a **Draft** through `DefinitionMapper.ToDraft`, runs `DefinitionService.Validate`,
  and shows the result: the same red states, Problem column and diagram as a hand-made draft (XSM-027).
- **It never publishes.** Publish stays a person's action with its own server-side validation, so a model cannot put a
  flow into use.
- A proposal that fails validation is shown with its findings, not silently repaired. A second model call may be offered
  ("fix these findings"), and its output goes through the same gate.

## What the model sees

For a draft, only what a designer could type into the definition anyway:

- the target subject type's **workflow members** as `WorkflowMembers.Supported` lists them (name, type): what Set field
  effects and locks may use (criteria are checked separately and may reach further; restricting a model's criteria to
  these members would be an extra rule of the build);
- the **role names** from `WorkflowRoleCatalog` (names only, no users);
- the **step types** and their rules, as a short fixed text generated from the validator's rules;
- for a fix proposal: the current draft as `WorkflowDef` JSON plus its findings.

It sees **no subject rows, no users and no sent notifications**. It does see the definition itself, and a definition can
contain business content: notification templates, Set field values and literals in criteria. A build must say so where
the designer triggers it, or offer to send the draft without those texts. The "stuck request" use (3) is the
exception and needs its own permission check: only the instance, its tasks and timer, read through the asking user's
secured object space, never the subject's other members.

## Where the model comes from

The host's AI setup, not a second stack in the module:

- BPGDemo (and so BPG's generated apps) already has an **`AiSettings`** entity and a **`TornadoClientFactory`**, and
  bpg AI-001 plans the XafTornado assistant for generated apps. The module would define one small interface (for example
  `IWorkflowDraftAssistant.Propose(prompt, context) -> WorkflowDef JSON`) and the host implements it on its own client.
- Credentials stay where the host keeps them (encrypted in its database, managed in its settings UI), never in an
  environment variable or in this module.
- Without a registered implementation the designer shows no AI actions: the module works exactly as today.

## Cost and latency (estimates, not measured)

- A draft request is one call with a few thousand tokens of context (members, roles, rules) and a short JSON answer.
  Expect seconds, not milliseconds; the UI shows it as a background action with a busy state.
- Validation, drawing and dry run of the proposal are the existing local code: milliseconds.
- Nothing runs unless a designer asks, so cost scales with designer actions, not with workflow traffic. The runtime
  (Start, Fire, Decide, timers) never calls a model.

## Open questions for a build card

- Which model and provider the host uses, and whether the answer must be forced into JSON by the provider (structured
  output) or parsed and re-asked on failure.
- Whether "draft from a sentence" should also propose the subject type's missing members, or stop at what exists.
- Whether the same generator serves bpg WF-008 (spec workflow to seeded definition), which would make the JSON contract
  the shared piece between BPG and this module. That is the main reason to design it once.
