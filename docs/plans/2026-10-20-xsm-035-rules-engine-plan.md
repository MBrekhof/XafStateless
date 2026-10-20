# XSM-035: Embedded RulesEngine & Rule Sets Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Embed the cleaned `Microsoft.RulesEngine` into `XafStateless.Engine` and add definition-owned `WorkflowRuleSet` entities (`Params` + `Rules`) executed via `EffectKind.RunRules` in `XafStateless.Module`.

**Architecture:** `XafStateless.Engine` vendors the cleaned `RulesEngine` source (with assembly scanning disabled by default) and exposes `RuleSetDef` (`RuleParamDef`, `RuleItemDef`), `RuleSetEvaluator`, `WorkflowValidator` checks, and `WorkflowMermaid` details rendering. `XafStateless.Module` adds `WorkflowRuleSet`, `WorkflowRuleParam`, and `WorkflowRule` entities aggregated under `WorkflowDefinition`, wired through `DefinitionMapper`, `DefinitionService.NewDraft`, `DefinitionStore`, `DefinitionGuard`, `WorkflowPermissions`, `XafWorkflowEnvironment`, and `WorkflowRuntime.Enter`.

**Tech Stack:** .NET 10, Stateless 5.20.1, System.Linq.Dynamic.Core 1.6.2, FastExpressionCompiler 5.0.2, FluentValidation 11.11.0, DevExpress XAF / EF Core 26.1.4, xUnit.

**Spec:** `docs/plans/2026-10-20-xsm-035-rules-engine-design.md`

## Global Constraints

- `XafStateless.Engine` must remain free of DevExpress dependencies.
- `RuleSetEvaluator` must default `ReSettings.EnableAssemblyScanning = false` and `AutoRegisterInputType = true` so XAF AppDomains are never scanned.
- The subject is bound as `subject` in all `RuleParamDef.Expression`, `RuleItemDef.Expression`, and `RuleItemDef.OutputExpression` expressions.
- All subject member writes during `EffectKind.RunRules` must go through `WorkflowRuntime.Write` (`RuntimeScope.Allow`) so `StateLockGuard` exempts them while still refusing unauthorized user edits.
- `WorkflowRuleSet`, `WorkflowRuleParam`, and `WorkflowRule` must be guarded by `DefinitionGuard` (frozen when the owning `WorkflowDefinition` is not `Draft`, and `Touch`ing the definition when edited in `Draft`).

## Review Focus

- Sequential rule updates within one `RuleSetDef`: a later rule (`Order = 1`) in the same `RuleSetDef` must see the property value written on `subject` by an earlier rule (`Order = 0`).
- Numeric type conversions and overflow on `OutputExpression`: when an expression returns `int` or `double` (or overflows a narrow integer member like `int` when computing `long`), `RuleSetEvaluator.ConvertOutput` / `WorkflowRuntime` converts it cleanly with `CultureInfo.InvariantCulture` or throws `UserFriendlyException` with nothing committed.
- Nullable target members: an `OutputExpression` evaluating to `null` on a nullable target member (`int?`) sets `null` cleanly, whereas `null` on a non-nullable value-type member is refused cleanly.
- `DefinitionGuard` enforcement on `WorkflowRuleSet`, `WorkflowRuleParam`, and `WorkflowRule`: modifying or deleting a rule or parameter of a `Published` definition is refused at `SaveChanges`, and editing one on a `Draft` increments the owning `WorkflowDefinition.OptimisticLockField`.
- `StateLockGuard` exemption: a state that locks `Amount` transitioning into a state with `EffectKind.RunRules` whose rule updates `Amount` succeeds inside `WorkflowRuntime` without tripping `StateLockGuard`.

---

### Task 1: Vendor Cleaned `RulesEngine` into `XafStateless.Engine` and Add `RuleSetDef` + `RuleSetEvaluator`

**Files:**
- Modify: `src/XafStateless.Engine/XafStateless.Engine.csproj`
- Create: `src/XafStateless.Engine/RulesEngine/` (vendored files from `C:\Projects\RulesEngine\src\RulesEngine` with all 5 bug fixes applied)
- Modify: `src/XafStateless.Engine/WorkflowDef.cs`
- Create: `src/XafStateless.Engine/RuleSetEvaluator.cs`
- Test: `tests/XafStateless.Engine.Tests/WorkflowDefTests.cs`

**Interfaces:**
- Consumes: `RulesEngine.RulesEngine`, `RulesEngine.ExpressionBuilders.RuleExpressionParser`, `RulesEngine.Models.*`
- Produces:
  - `EffectKind.RunRules`
  - `public sealed record RuleParamDef(string Name, string Expression);`
  - `public sealed record RuleItemDef(string Name, string Expression, string TargetMember, string OutputExpression, int Order = 0);`
  - `public sealed record RuleSetDef(string Key, IReadOnlyList<RuleParamDef> Params, IReadOnlyList<RuleItemDef> Rules);`
  - `WorkflowDef(..., IReadOnlyList<RuleSetDef>? RuleSets = null)` with `RuleSetList` and `RuleSet(string key)`
  - `public static class RuleSetEvaluator` with `Validate(RuleSetDef ruleSet, Type subjectType, Func<string, Type?> resolveMemberType)` and `Apply(RuleSetDef ruleSet, Type subjectType, object subject, Action<string, object?> setMember)` and `ConvertOutput(Type memberType, object? value)`

- [ ] **Step 1: Copy cleaned `RulesEngine` source files into `src/XafStateless.Engine/RulesEngine/` and add package references to `XafStateless.Engine.csproj`**

Add `FastExpressionCompiler` (`5.0.2`), `FluentValidation` (`11.11.0`), and `System.Linq.Dynamic.Core` (`1.6.2`) to `src/XafStateless.Engine/XafStateless.Engine.csproj`, and copy the `.cs` files (excluding `Properties/AssemblyInfo.cs`) from `C:\Projects\RulesEngine\src\RulesEngine` with the 5 fixes applied into `src/XafStateless.Engine/RulesEngine/`.

- [ ] **Step 2: Write the failing engine tests in `tests/XafStateless.Engine.Tests/WorkflowDefTests.cs`**

```csharp
[Fact]
public void RuleSetEvaluator_applies_matching_rules_in_order_with_scoped_params()
{
    var sample = new SampleSubject { Amount = 1200m, Revisions = 0, Ordered = false };
    var rs = new RuleSetDef("pricing",
        [new RuleParamDef("discount", "subject.Amount * 0.1m"), new RuleParamDef("net", "subject.Amount - discount")],
        [
            new RuleItemDef("apply-discount", "subject.Amount >= 1000m", nameof(SampleSubject.Amount), "net", Order: 0),
            new RuleItemDef("mark-ordered", "subject.Amount == 1080m", nameof(SampleSubject.Ordered), "true", Order: 1),
        ]);

    RuleSetEvaluator.Apply(rs, typeof(SampleSubject), sample, (member, val) =>
    {
        var prop = typeof(SampleSubject).GetProperty(member)!;
        prop.SetValue(sample, RuleSetEvaluator.ConvertOutput(prop.PropertyType, val));
    });

    Assert.Equal(1080m, sample.Amount);
    Assert.True(sample.Ordered);
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/XafStateless.Engine.Tests/XafStateless.Engine.Tests.csproj --filter "FullyQualifiedName~RuleSetEvaluator"`
Expected: FAIL (types `RuleSetDef`, `RuleParamDef`, `RuleItemDef`, `RuleSetEvaluator` do not exist yet)

- [ ] **Step 4: Implement `WorkflowDef.cs` additions and `src/XafStateless.Engine/RuleSetEvaluator.cs`**

Update `src/XafStateless.Engine/WorkflowDef.cs` to add `EffectKind.RunRules`, `RuleParamDef`, `RuleItemDef`, `RuleSetDef`, and `WorkflowDef.RuleSets`, and create `src/XafStateless.Engine/RuleSetEvaluator.cs`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/XafStateless.Engine.Tests/XafStateless.Engine.Tests.csproj`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/XafStateless.Engine tests/XafStateless.Engine.Tests
git commit -m "feat(engine): vendor cleaned RulesEngine and add RuleSetDef + RuleSetEvaluator"
```

---

### Task 2: Extend `WorkflowValidator` and `WorkflowMermaid` in `XafStateless.Engine`

**Files:**
- Modify: `src/XafStateless.Engine/WorkflowValidator.cs`
- Modify: `src/XafStateless.Engine/WorkflowMermaid.cs`
- Test: `tests/XafStateless.Engine.Tests/WorkflowValidatorTests.cs`
- Test: `tests/XafStateless.Engine.Tests/WorkflowDefTests.cs`

**Interfaces:**
- Consumes: `RuleSetDef`, `RuleParamDef`, `RuleItemDef`, `EffectKind.RunRules`
- Produces:
  - `IWorkflowEnvironment.CheckRuleSet(RuleSetDef ruleSet) => null;`
  - Structural validation findings in `WorkflowValidator.Check` for `def.RuleSetList` and `EffectKind.RunRules`
  - `WorkflowMermaid` details label `rules <keys>` when `Details: true`

- [ ] **Step 1: Write failing validator and Mermaid tests in `tests/XafStateless.Engine.Tests/WorkflowValidatorTests.cs` and `WorkflowDefTests.cs`**

Test duplicate rule set keys, missing rule set on `EffectKind.RunRules`, empty rules list, reserved `"subject"` parameter name, duplicate parameter/rule names, duplicate rule orders, `IWorkflowEnvironment.CheckRuleSet` error propagation, and `WorkflowMermaid.Format` with `Details: true` showing `rules pricing`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/XafStateless.Engine.Tests/XafStateless.Engine.Tests.csproj`
Expected: FAIL on the new validation and Mermaid assertions

- [ ] **Step 3: Implement `WorkflowValidator.cs` and `WorkflowMermaid.cs` updates**

Add `CheckRuleSet` to `IWorkflowEnvironment`, structural and environment checks in `WorkflowValidator.Check`, and `rules ...` detail line in `WorkflowMermaid.StateLabel`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/XafStateless.Engine.Tests/XafStateless.Engine.Tests.csproj`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/XafStateless.Engine/WorkflowValidator.cs src/XafStateless.Engine/WorkflowMermaid.cs tests/XafStateless.Engine.Tests
git commit -m "feat(engine): validate RuleSetDef and EffectKind.RunRules and show rules in Mermaid details"
```

---

### Task 3: Add `WorkflowRuleSet`, `WorkflowRuleParam`, and `WorkflowRule` Entities, Mapping, Store, Guards, Permissions, and Runtime to `XafStateless.Module`

**Files:**
- Create: `src/XafStateless.Module/BusinessObjects/WorkflowRuleSet.cs`
- Create: `src/XafStateless.Module/BusinessObjects/WorkflowRuleParam.cs`
- Create: `src/XafStateless.Module/BusinessObjects/WorkflowRule.cs`
- Modify: `src/XafStateless.Module/BusinessObjects/WorkflowDefinition.cs`
- Modify: `src/XafStateless.Module/BusinessObjects/WorkflowEffect.cs`
- Modify: `src/XafStateless.Module/Services/DefinitionMapper.cs`
- Modify: `src/XafStateless.Module/Services/DefinitionService.cs`
- Modify: `src/XafStateless.Module/Services/DefinitionStore.cs`
- Modify: `src/XafStateless.Module/Services/DefinitionGuard.cs`
- Modify: `src/XafStateless.Module/Services/WorkflowPermissions.cs`
- Modify: `src/XafStateless.Module/Services/XafWorkflowEnvironment.cs`
- Modify: `src/XafStateless.Module/Services/WorkflowRuntime.cs`
- Modify: `demo/XafStateless.Demo.Blazor.Server/BusinessObjects/StatelessDemoDbContext.cs` (and `tests/XafStateless.Module.Tests/TestDb.cs`)
- Test: `tests/XafStateless.Module.Tests/WorkflowRuntimeTests.cs`, `DefinitionServiceTests.cs`, `WorkflowPermissionsTests.cs`

**Interfaces:**
- Consumes: `RuleSetDef`, `RuleParamDef`, `RuleItemDef`, `RuleSetEvaluator`, `EffectKind.RunRules`
- Produces:
  - Entities `WorkflowRuleSet`, `WorkflowRuleParam`, `WorkflowRule`
  - `XafWorkflowEnvironment.CheckRuleSet(RuleSetDef ruleSet)`
  - `WorkflowRuntime.Enter` handling `EffectKind.RunRules`

- [ ] **Step 1: Write failing module tests in `tests/XafStateless.Module.Tests/`**

Add tests in `WorkflowRuntimeTests.cs`, `DefinitionServiceTests.cs`, and `WorkflowPermissionsTests.cs` covering:
1. `RunRules_computes_params_and_updates_locked_members_and_decision_sees_it`
2. `RunRules_overflow_is_refused_and_commits_nothing`
3. `DefinitionServiceTests`: `RuleSet_validation` (bad param expression, bad condition expression, unsupported target member, incompatible output type, valid rule set), `NewDraft_copies_rule_sets`, `DefinitionGuard_freezes_published_rule_sets_and_touches_draft`, and `LookupValues` on `WorkflowEffect` (`RunRules`) and `WorkflowRule` (`TargetMember`).
4. `WorkflowPermissionsTests`: designer role has CRUD on `WorkflowRuleSet`, `WorkflowRuleParam`, `WorkflowRule`, and participant role has Read.

- [ ] **Step 2: Run module tests to verify they fail**

Run: `dotnet test tests/XafStateless.Module.Tests/XafStateless.Module.Tests.csproj`
Expected: FAIL (missing entity types and runtime/validator logic)

- [ ] **Step 3: Implement the Module entities, mapper, store, guard, environment validation, permissions, and runtime execution**

Create `WorkflowRuleSet.cs`, `WorkflowRuleParam.cs`, `WorkflowRule.cs`, and update `WorkflowDefinition.cs`, `WorkflowEffect.cs`, `DefinitionMapper.cs`, `DefinitionService.cs`, `DefinitionStore.cs`, `DefinitionGuard.cs`, `WorkflowPermissions.cs`, `XafWorkflowEnvironment.cs`, `WorkflowRuntime.cs`, `TestDb.cs`, and `StatelessDemoDbContext.cs`.

- [ ] **Step 4: Run full solution tests to verify all Engine and Module tests pass**

Run: `dotnet test XafStateless.slnx`
Expected: PASS (all Engine and Module tests green)

- [ ] **Step 5: Commit**

```bash
git add src/XafStateless.Module demo/XafStateless.Demo.Blazor.Server tests/XafStateless.Module.Tests docs/plans
git commit -m "feat(module): add WorkflowRuleSet entities, validation, guards, and EffectKind.RunRules runtime"
```
