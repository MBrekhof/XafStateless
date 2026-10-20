using XafStateless.Engine;

namespace XafStateless.Engine.Tests;

public class WorkflowDefTests
{
    public sealed class SampleSubject
    {
        public string Title { get; set; } = "";
        public decimal Amount { get; set; }
        public int? Revisions { get; set; }
        public int Count { get; set; }
        public bool Ordered { get; set; }
    }

    [Fact]
    public void From_returns_transitions_in_order()
    {
        var def = Fixtures.PurchaseRequest();
        Assert.Equal(["Large", "Small"], def.From("decide-amount").Select(t => t.Trigger));
    }

    [Fact]
    public void RuleSetEvaluator_applies_matching_rules_in_order_with_scoped_params()
    {
        var sample = new SampleSubject { Amount = 1200m, Revisions = 0, Ordered = false };
        var rs = new RuleSetDef("pricing",
            [new RuleParamDef("discount", "subject.Amount * 0.1m"), new RuleParamDef("net", "subject.Amount - discount")],
            [
                new RuleItemDef("mark-ordered", "subject.Amount == 1080m", nameof(SampleSubject.Ordered), "true", Order: 1),
                new RuleItemDef("apply-discount", "subject.Amount >= 1000m", nameof(SampleSubject.Amount), "net", Order: 0),
                new RuleItemDef("skip-small", "subject.Amount < 500m", nameof(SampleSubject.Revisions), "99", Order: 2),
            ]);

        RuleSetEvaluator.Apply(rs, typeof(SampleSubject), sample, (member, val) =>
        {
            var prop = typeof(SampleSubject).GetProperty(member)!;
            prop.SetValue(sample, RuleSetEvaluator.ConvertOutput(prop.PropertyType, val));
        });

        Assert.Equal(1080m, sample.Amount);
        Assert.True(sample.Ordered);
        Assert.Equal(0, sample.Revisions);
    }

    [Fact]
    public void RuleSetEvaluator_converts_nullable_and_numeric_outputs_and_detects_overflow()
    {
        Assert.Null(RuleSetEvaluator.ConvertOutput(typeof(int?), null));
        Assert.Equal(42, RuleSetEvaluator.ConvertOutput(typeof(int?), 42L));
        Assert.Equal(12.5m, RuleSetEvaluator.ConvertOutput(typeof(decimal), 12.5d));
        Assert.Throws<InvalidCastException>(() => RuleSetEvaluator.ConvertOutput(typeof(int), null));
        Assert.Throws<OverflowException>(() => RuleSetEvaluator.ConvertOutput(typeof(int), (long)int.MaxValue + 10L));
    }

    [Fact]
    public void RuleSetEvaluator_validates_params_conditions_members_and_output_types()
    {
        Type? Resolve(string m) => typeof(SampleSubject).GetProperty(m)?.PropertyType;

        var valid = new RuleSetDef("pricing",
            [new RuleParamDef("disc", "subject.Amount * 0.1m")],
            [new RuleItemDef("r1", "disc > 10m", nameof(SampleSubject.Amount), "subject.Amount - disc", Order: 0)]);
        Assert.Null(RuleSetEvaluator.Validate(valid, typeof(SampleSubject), Resolve));

        var badParam = new RuleSetDef("pricing",
            [new RuleParamDef("disc", "subject.NoSuchField + 1")],
            [new RuleItemDef("r1", "true", nameof(SampleSubject.Amount), "10m", Order: 0)]);
        Assert.Contains("parameter 'disc'", RuleSetEvaluator.Validate(badParam, typeof(SampleSubject), Resolve));

        var badCondition = new RuleSetDef("pricing", [],
            [new RuleItemDef("r1", "subject.Title", nameof(SampleSubject.Amount), "10m", Order: 0)]);
        Assert.Contains("rule 'r1' condition", RuleSetEvaluator.Validate(badCondition, typeof(SampleSubject), Resolve));

        var badMember = new RuleSetDef("pricing", [],
            [new RuleItemDef("r1", "true", "UnknownProp", "10m", Order: 0)]);
        Assert.Contains("'UnknownProp' cannot be set by a workflow", RuleSetEvaluator.Validate(badMember, typeof(SampleSubject), Resolve));

        var badOutputType = new RuleSetDef("pricing", [],
            [new RuleItemDef("r1", "true", nameof(SampleSubject.Amount), "\"not-a-decimal\"", Order: 0)]);
        Assert.Contains("rule 'r1' output", RuleSetEvaluator.Validate(badOutputType, typeof(SampleSubject), Resolve));
    }
}
