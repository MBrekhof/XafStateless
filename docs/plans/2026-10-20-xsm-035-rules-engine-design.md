# XSM-035: Embedded RulesEngine & Rule Sets (`EffectKind.RunRules`)

## Context & Goal

`XafStateless` intentionally keeps state-machine transitions and effects minimal and verifiable: transition guards use DevExpress `CriteriaOperator`, `SetField` sets a literal constant, `Increment` adds a constant step, `Notify` creates an outbox notification, and `RunAction` executes host-registered C# code.

To let end users configure **multi-step calculations and conditional business rules** (e.g., computing discount tiers, risk scores, fees, or derived flags from intermediate variables) without writing or deploying host C# code, `XafStateless` embeds the cleaned `Microsoft.RulesEngine` core inside `XafStateless.Engine` and exposes definition-owned **Rule Sets** (`WorkflowRuleSet`) executed via `EffectKind.RunRules`.

---

## Design

### 1. Vendored `RulesEngine` inside `XafStateless.Engine`

- The cleaned `RulesEngine` source (with the 5 upstream bug fixes: non-mutating `WorkflowsToInject` with cycle guard, full-type cache keys, case-insensitive nested operators, recursive/cycle-safe `FormatErrorMessages`, and prefix-safe cache eviction + stale `GlobalParams` cleanup) lives under `src/XafStateless.Engine/RulesEngine/`.
- `XafStateless.Engine.csproj` adds package references for `System.Linq.Dynamic.Core` (`1.6.2`), `FastExpressionCompiler` (`5.0.2`), and `FluentValidation` (`11.11.0`).
- `RuleSetEvaluator` configures `ReSettings { EnableAssemblyScanning = false, AutoRegisterInputType = true }` by default so evaluating or validating rules in an XAF process never scans all loaded `AppDomain` assemblies.

### 2. Engine Records (`WorkflowDef.cs`) & `RuleSetEvaluator` (`RuleSetEvaluator.cs`)

- `EffectKind` gains `RunRules` (XAF caption `"Run rules"`):
  - `Target` = the `Key` of a `RuleSetDef` in the same `WorkflowDef`.
  - `Value` = `""` (unused; ignored if blank).
- Engine definition records in `WorkflowDef.cs`:
  ```csharp
  public sealed record RuleParamDef(string Name, string Expression);

  public sealed record RuleItemDef(string Name, string Expression, string TargetMember, string OutputExpression, int Order = 0);

  public sealed record RuleSetDef(string Key, IReadOnlyList<RuleParamDef> Params, IReadOnlyList<RuleItemDef> Rules);
  ```
- `WorkflowDef` gains an optional parameter `IReadOnlyList<RuleSetDef>? RuleSets = null`:
  ```csharp
  public sealed record WorkflowDef(string Name, int Version, string InitialState,
      IReadOnlyList<StateDef> States, IReadOnlyList<TransitionDef> Transitions,
      IReadOnlyList<RuleSetDef>? RuleSets = null)
  {
      public IReadOnlyList<RuleSetDef> RuleSetList => RuleSets ?? [];
      public RuleSetDef? RuleSet(string key) => RuleSetList.FirstOrDefault(r => r.Key == key);
      ...
  }
  ```
- **Expression bindings & evaluation (`RuleSetEvaluator`):**
  - The workflow subject is bound as parameter `subject` of the workflow's `TargetType` (e.g., `subject.Amount > 1000`).
  - `RuleSetDef.Params` are compiled in order as `ScopedParam`s: each parameter expression may reference `subject` and any earlier `RuleParamDef` by `Name` (e.g., `baseDiscount = subject.Amount * 0.1m`, `totalDiscount = baseDiscount + 25m`).
  - `RuleSetDef.Rules` are evaluated in ascending `Order`:
    1. `Params` are evaluated against the current `subject` state.
    2. Each rule's boolean `Expression` is evaluated via `RulesEngine` (`subject` + `Params`).
    3. When `Expression` evaluates to `true`, `OutputExpression` is evaluated against `subject` + `Params` and converted to `TargetMember`'s CLR type, then written to `subject` via the caller's setter callback before the next rule in `Rules` runs (so later rules in the same `RuleSetDef` observe earlier rules' updates on `subject`).

### 3. Validation (`WorkflowValidator.cs` & `XafWorkflowEnvironment.cs`)

- **Structural checks in `WorkflowValidator.Check`:**
  - Empty `RuleSetDef.Key` (`"a rule set has no key"`) or duplicate `RuleSetDef.Key` (`"duplicate rule set '{r.Key}'"`).
  - Empty `RuleSetDef.Rules` (`"rule set '{r.Key}' has no rules"`).
  - `RuleParamDef`: empty `Name` (`"rule set '{r.Key}' has a parameter with no name"`), name `"subject"` (`"rule set '{r.Key}' parameter 'subject' is reserved"`), duplicate parameter `Name` (`"rule set '{r.Key}' has duplicate parameter '{p.Name}'"`), empty `Expression` (`"rule set '{r.Key}' parameter '{p.Name}' has no expression"`).
  - `RuleItemDef`: empty `Name` (`"rule set '{r.Key}' has a rule with no name"`), duplicate rule `Name` (`"rule set '{r.Key}' has duplicate rule '{rule.Name}'"`), duplicate `Order` (`"rule set '{r.Key}' has duplicate order {g.Key}"`), empty `Expression`, empty `TargetMember`, empty `OutputExpression`.
  - `EffectKind.RunRules`: `def.RuleSet(e.Target) == null` reports `"effect on '{s.Key}': rule set '{e.Target}' does not exist"` (tagged with state `s.Key` so the state highlights red in the designer list and diagram).
- **Type & expression checks in `IWorkflowEnvironment.CheckRuleSet(RuleSetDef ruleSet)` (default interface method returning `null`, implemented in `XafWorkflowEnvironment`):**
  - Compiles each `RuleParamDef.Expression` in sequence against `subject` (`target.Type`) and prior parameters; a parse error reports `"rule set '{r.Key}' parameter '{p.Name}': {err}"`.
  - Checks that each `RuleItemDef.TargetMember` is in `WorkflowMembers.Supported(target)` (`"rule set '{r.Key}' rule '{rule.Name}': '{rule.TargetMember}' cannot be set by a workflow"`).
  - Compiles each `RuleItemDef.Expression` as `bool` against `subject` + parameters; a parse error reports `"rule set '{r.Key}' rule '{rule.Name}' condition: {err}"`.
  - Compiles each `RuleItemDef.OutputExpression` against `subject` + parameters and verifies its return type can be assigned or converted to `TargetMember`'s underlying type; an error reports `"rule set '{r.Key}' rule '{rule.Name}' output: {err}"`.

### 4. Module Entities, Mapping, Store, Guards & Runtime (`XafStateless.Module`)

- **Entities (`BusinessObjects/`):**
  - `WorkflowDefinition` gains `[Aggregated] public virtual IList<WorkflowRuleSet> RuleSets { get; set; } = new ObservableCollection<WorkflowRuleSet>();`
  - `WorkflowRuleSet : BaseObject`: `Definition`, `[RuleRequiredField] Key`, `[Aggregated] IList<WorkflowRuleParam> Params`, `[Aggregated] IList<WorkflowRule> Rules`.
  - `WorkflowRuleParam : BaseObject`: `RuleSet`, `Order`, `[RuleRequiredField] Name`, `[RuleRequiredField] Expression`.
  - `WorkflowRule : BaseObject, IStringLookupSource`: `RuleSet`, `Order`, `[RuleRequiredField] Name`, `[RuleRequiredField] Expression`, `[RuleRequiredField, EditorAlias(StringLookup.Single)] TargetMember`, `[RuleRequiredField] OutputExpression`.
    - `LookupValues` for `nameof(TargetMember)` returns `WorkflowMembers.Supported(target).Select(m => m.Name)`.
- **`WorkflowEffect`:**
  - `LookupValues` for `EffectKind.RunRules` returns `State?.Definition?.RuleSets.Select(r => r.Key).Where(k => !string.IsNullOrWhiteSpace(k)) ?? []`.
  - `XafWorkflowEnvironment.CheckEffect`: when `effect.Kind == EffectKind.RunRules`, returns `null` (existence of `effect.Target` in `def.RuleSets` is checked in `WorkflowValidator.Check`).
- **Lifecycle & Persistence:**
  - `DefinitionMapper.ToDef` and `ToDraft`: map `RuleSets`, `Params` (ordered by `Order`), and `Rules` (ordered by `Order`).
  - `DefinitionService.NewDraft`: deep-copies `src.RuleSets` (with their `Params` and `Rules`) into the new draft.
  - `DefinitionStore.Read`: reads `WorkflowRuleSet`, `WorkflowRuleParam`, and `WorkflowRule` via `RawSql` so published snapshots include `RuleSets` with zero ORM tracking dependencies.
  - `DefinitionGuard`: tracks `Changed<WorkflowRuleSet>`, `Changed<WorkflowRuleParam>`, and `Changed<WorkflowRule>`, resolves their owner `WorkflowDefinition` (through `RuleSet.Definition`), refuses modifications when the owner is not a `Draft`, and `Touch`es the owner `WorkflowDefinition` on Draft edits.
  - `WorkflowPermissions`: grants `WorkflowDesigners` CRUD access to `WorkflowRuleSet`, `WorkflowRuleParam`, and `WorkflowRule`, and read access to participants as needed.
- **Runtime (`WorkflowRuntime.Enter`):**
  - `case EffectKind.RunRules:` resolves `x.Def!.RuleSet(effect.Target)` (throws `InvalidOperationException` if missing), runs `RuleSetEvaluator.Apply(ruleSet, x.Type.Type, x.Subject, (memberName, val) => ...)`:
    - Each matched rule writes its target member through `Write(x, member, m => RuleSetEvaluator.ConvertOutput(m.MemberType, val))`, which sets the property on `x.Subject` and calls `runtimeScope.Allow(...)` so `StateLockGuard` permits the write even if the member was locked in the state being left.
    - Overflow or conversion failures throw `UserFriendlyException` before `CommitChanges`, rolling back the command cleanly.
    - Any subsequent `StateKind.Decision` in `RunDecisions` immediately sees the updated subject fields.
- **Diagram (`WorkflowMermaid`):**
  - When `style.Details == true`, a state with `EffectKind.RunRules` effects appends `rules <target1>, <target2>` to its state box label alongside roles, wait durations, and locked fields.
