using System.Globalization;
using System.Linq.Expressions;
using RulesEngine.ExpressionBuilders;
using RulesEngine.Models;

namespace XafStateless.Engine;

/// <summary>
/// Evaluates and validates a <see cref="RuleSetDef"/> against a workflow subject using the embedded RulesEngine
/// (<see cref="RuleExpressionParser"/>), with assembly scanning disabled by default so XAF AppDomains are never scanned.
/// </summary>
public static class RuleSetEvaluator
{
    public const string SubjectParamName = "subject";

    static ReSettings CreateSettings(Type subjectType) => new()
    {
        EnableAssemblyScanning = false,
        AutoRegisterInputType = true,
        EnableExceptionAsErrorMessageForRuleExpressionParsing = true,
        CustomTypes = [subjectType],
    };

    /// <summary>Validates all parameter expressions, rule conditions, target members and output expressions of <paramref name="ruleSet"/>
    /// against <paramref name="subjectType"/>. Returns the first error message, or null when valid.</summary>
    public static string? Validate(RuleSetDef ruleSet, Type subjectType, Func<string, Type?> resolveMemberType)
    {
        var parser = new RuleExpressionParser(CreateSettings(subjectType));
        var parameters = new List<ParameterExpression> { Expression.Parameter(subjectType, SubjectParamName) };

        foreach (var p in ruleSet.Params)
        {
            Expression parsed;
            try
            {
                parsed = parser.Parse(p.Expression, parameters.ToArray(), null!);
            }
            catch (Exception ex)
            {
                return $"rule set '{ruleSet.Key}' parameter '{p.Name}': {ex.Message}";
            }
            parameters.Add(Expression.Parameter(parsed.Type, p.Name));
        }

        var allParams = parameters.ToArray();
        foreach (var r in ruleSet.Rules.OrderBy(x => x.Order))
        {
            if (resolveMemberType(r.TargetMember) is not { } memberType)
                return $"rule set '{ruleSet.Key}' rule '{r.Name}': '{r.TargetMember}' cannot be set by a workflow";

            try
            {
                parser.Parse(r.Expression, allParams, typeof(bool));
            }
            catch (Exception ex)
            {
                return $"rule set '{ruleSet.Key}' rule '{r.Name}' condition: {ex.Message}";
            }

            Expression outputExpr;
            try
            {
                outputExpr = parser.Parse(r.OutputExpression, allParams, null!);
            }
            catch (Exception ex)
            {
                return $"rule set '{ruleSet.Key}' rule '{r.Name}' output: {ex.Message}";
            }

            if (!IsCompatibleOutputType(outputExpr.Type, memberType))
                return $"rule set '{ruleSet.Key}' rule '{r.Name}' output: '{outputExpr.Type.Name}' cannot be assigned to '{r.TargetMember}' ({(Nullable.GetUnderlyingType(memberType) ?? memberType).Name})";
        }

        return null;
    }

    /// <summary>Evaluates <paramref name="ruleSet"/> against <paramref name="subject"/> in rule <see cref="RuleItemDef.Order"/>.
    /// Each rule whose condition evaluates to true computes its <see cref="RuleItemDef.OutputExpression"/> and invokes
    /// <paramref name="setMember"/> immediately, so subsequent rules in the same rule set see updated subject properties.</summary>
    public static void Apply(RuleSetDef ruleSet, Type subjectType, object subject, Action<string, object?> setMember)
    {
        var parser = new RuleExpressionParser(CreateSettings(subjectType));
        foreach (var r in ruleSet.Rules.OrderBy(x => x.Order))
        {
            var currentParams = EvaluateParams(parser, ruleSet.Params, subjectType, subject);
            var matched = parser.Evaluate<bool>(r.Expression, currentParams);
            if (!matched) continue;
            var output = parser.Evaluate<object?>(r.OutputExpression, currentParams);
            setMember(r.TargetMember, output);
        }
    }

    static RuleParameter[] EvaluateParams(RuleExpressionParser parser, IReadOnlyList<RuleParamDef> paramDefs, Type subjectType, object subject)
    {
        var list = new List<RuleParameter>(1 + paramDefs.Count)
        {
            new RuleParameter(SubjectParamName, subjectType, subject)
        };
        foreach (var p in paramDefs)
        {
            var paramArray = list.ToArray();
            var expr = parser.Parse(p.Expression, paramArray.Select(x => x.ParameterExpression).ToArray(), null!);
            var val = parser.Evaluate<object?>(p.Expression, paramArray);
            list.Add(new RuleParameter(p.Name, expr.Type, val));
        }
        return list.ToArray();
    }

    static bool IsCompatibleOutputType(Type exprType, Type memberType)
    {
        var target = Nullable.GetUnderlyingType(memberType) ?? memberType;
        var source = Nullable.GetUnderlyingType(exprType) ?? exprType;
        if (source == typeof(object)) return true; // null literal or dynamic object
        if (target.IsAssignableFrom(source)) return true;
        if (IsNumeric(target) && IsNumeric(source)) return true;
        return false;
    }

    static bool IsNumeric(Type t) =>
        !t.IsEnum && Type.GetTypeCode(t) is >= TypeCode.SByte and <= TypeCode.Decimal;

    /// <summary>Converts an evaluated output value to <paramref name="memberType"/> using invariant culture.</summary>
    public static object? ConvertOutput(Type memberType, object? value)
    {
        var underlying = Nullable.GetUnderlyingType(memberType);
        if (value is null)
        {
            if (underlying != null || !memberType.IsValueType) return null;
            throw new InvalidCastException($"Cannot assign null to non-nullable '{memberType.Name}'.");
        }
        var target = underlying ?? memberType;
        if (value is double d && !double.IsFinite(d) || value is float f && !float.IsFinite(f))
            throw new OverflowException($"{value} is not a finite number");   // the database refuses NaN and infinities
        if (target.IsInstanceOfType(value)) return value;
        if (target.IsEnum)
        {
            if (value is string s)
            {
                var parsed = Enum.Parse(target, s);
                return Enum.IsDefined(target, parsed) ? parsed : throw new FormatException($"{value} is not a {target.Name}");
            }
            // Through the underlying type first: Enum.ToObject truncates a long silently.
            var enumVal = Enum.ToObject(target, Convert.ChangeType(value, Enum.GetUnderlyingType(target), CultureInfo.InvariantCulture));
            return Enum.IsDefined(target, enumVal) ? enumVal : throw new FormatException($"{value} is not a {target.Name}");
        }
        if (target == typeof(Guid) && value is string gs) return Guid.Parse(gs);
        return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }
}
