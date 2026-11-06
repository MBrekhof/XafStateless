using System.Globalization;
using System.Text.RegularExpressions;
using DevExpress.Data.Filtering;
using DevExpress.Data.Filtering.Exceptions;
using DevExpress.Data.Filtering.Helpers;
using DevExpress.Data.Linq;
using DevExpress.ExpressApp.DC;
using XafStateless.Engine;

namespace XafStateless.Module.Services;

/// <summary>The validator's view of XAF: role names (from <see cref="WorkflowRoleCatalog"/>), the subject's type info and the
/// host's action names (from <see cref="WorkflowActionCatalog"/>).</summary>
public sealed partial class XafWorkflowEnvironment(IReadOnlyList<string> roleNames, ITypeInfo target, IReadOnlyList<string>? actionNames = null) : IWorkflowEnvironment
{
    public bool RoleExists(string role) => roleNames.Contains(role);

    public string? CheckCriteria(string criteria)
    {
        CriteriaOperator op;
        try { op = CriteriaOperator.Parse(criteria); }
        catch (CriteriaParserException ex) { return ex.Message; }
        // Kept simple: first-level operands only; members inside aggregate scopes ([Items][...]) are not resolved.
        var unknown = CollectFirstLevelPropertiesHelper.Collect(op).Select(p => p.PropertyName).FirstOrDefault(n => target.FindMember(n) == null);
        return unknown == null ? null : $"'{unknown}' is not a member of {target.Name}";
    }

    public string? CheckEffect(EffectDef effect)
    {
        if (effect.Kind == EffectKind.Increment)
        {
            if (WorkflowMembers.Counters(target).FirstOrDefault(m => m.Name == effect.Target) is not { } counter)
                return $"'{effect.Target}' is not a whole-number or decimal member of {target.Name}, so it cannot be incremented";
            if (!TryStep(effect.Value, out var step)) return $"'{effect.Value}' is not a step for '{effect.Target}'";
            return WorkflowMembers.IsIntegral(counter.MemberType) && step != decimal.Truncate(step)
                ? $"'{effect.Value}' is not a whole step for '{effect.Target}'" : null;
        }
        if (effect.Kind == EffectKind.RunRules) return null;
        if (effect.Kind == EffectKind.RunAction)
            return (actionNames ?? []).Contains(effect.Target)
                ? BadPlaceholder(effect.Value ?? "")
                : $"action '{effect.Target}' is not registered in this application";
        if (effect.Value is null)   // a nulled column; the record's type says non-null, the database does not
            return effect.Kind == EffectKind.SetField ? $"'{effect.Target}' needs a value" : "a notification needs a text";
        switch (effect.Kind)
        {
            case EffectKind.SetField:
                if (Expression(effect.Value) is { } expression) return CheckExpression(effect.Target, expression);
                if (WorkflowMembers.Supported(target).FirstOrDefault(m => m.Name == effect.Target) is not { } member)
                    return WorkflowMembers.Lockable(target).Any(m => m.Name == effect.Target)
                        ? $"'{effect.Target}' is a reference: set it with an expression, e.g. =[{effect.Target}]"
                        : $"'{effect.Target}' cannot be set by a workflow";
                try { ConvertValue(member.MemberType, effect.Value); return null; }
                catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or InvalidCastException)
                { return $"'{effect.Value}' is not a valid {(Nullable.GetUnderlyingType(member.MemberType) ?? member.MemberType).Name} for '{effect.Target}'"; }
            case EffectKind.Notify:
                return BadPlaceholder(effect.Value);
            default:
                return $"unknown effect kind {effect.Kind}";
        }
    }

    /// <summary>A Set field Value that starts with '=' is an expression (CARD-2895): the text after it; null for a constant.</summary>
    internal static string? Expression(string? value) => value is ['=', .. var rest] ? rest : null;

    /// <summary>A Set field expression at publish: <paramref name="memberName"/> is a scalar or reference a state may lock;
    /// the expression parses; every path in it, inside aggregates too, names visible members of XAF's type info exactly
    /// (as the runtime's evaluator resolves them) and no <see cref="WorkflowMembers.UserSecrets"/> of a user (<see cref="Paths"/>);
    /// and its result type, as DevExpress's criteria-to-LINQ converter infers it on the subject's CLR type, fits the member.</summary>
    string? CheckExpression(string memberName, string expression)
    {
        if (WorkflowMembers.Lockable(target).FirstOrDefault(m => m.Name == memberName) is not { } member)
            return $"'{memberName}' cannot be set by a workflow";
        string Invalid(string why) => $"'={expression}' is not a valid expression for '{memberName}': {why}";
        CriteriaOperator? op;
        try { op = CriteriaOperator.Parse(expression); }
        catch (CriteriaParserException ex) { return Invalid(ex.Message); }
        if (op is null) return $"'{memberName}' needs an expression after '='";   // empty, blank or a comment only
        if (op.Accept(new Paths(target)) is { } bad) return Invalid(bad);
        Type type;
        try { type = new CriteriaToExpressionConverterForObjects().Convert(System.Linq.Expressions.Expression.Parameter(target.Type), op).Type; }
        catch (Exception ex) { return Invalid(ex.Message); }

        static Type Strip(Type t) => Nullable.GetUnderlyingType(t) ?? t;
        static bool IsNumber(Type t) => !t.IsEnum && Type.GetTypeCode(t) is >= TypeCode.SByte and <= TypeCode.Decimal;
        var (from, to) = (Strip(type), Strip(member.MemberType));
        var fits = member.MemberTypeInfo is { IsPersistent: true }
            ? to.IsAssignableFrom(from)
            : from == to || (IsNumber(from) && IsNumber(to))
              || (to.IsEnum && (from == typeof(string) || (!from.IsEnum && WorkflowMembers.IsIntegral(from))));   // a name or a number
        return fits ? null : $"'={expression}' gives a {from.Name}, which does not fit '{memberName}' ({to.Name})";
    }

    /// <summary>The first path of a criteria the runtime should not evaluate, as a reason; null when every path is fine. A path
    /// resolves hop by hop on XAF's type info, case-sensitively as XAF's evaluator does, from the subject or, inside an
    /// aggregate, from the collection's element. Refused: an unknown or hidden (non-browsable) member, a user secret, a join
    /// (it reads any record), a '^.' parent and a type cast (both left out to keep the walk simple).</summary>
    internal sealed class Paths(ITypeInfo subject) : IClientCriteriaVisitor<string?>
    {
        readonly Stack<ITypeInfo> scope = new([subject]);

        string? First(IEnumerable<CriteriaOperator?> operands) =>
            operands.Select(o => o is null ? null : o.Accept(this)).FirstOrDefault(e => e != null);

        public string? Visit(BetweenOperator o) => First([o.TestExpression, o.BeginExpression, o.EndExpression]);
        public string? Visit(BinaryOperator o) => First([o.LeftOperand, o.RightOperand]);
        public string? Visit(UnaryOperator o) => First([o.Operand]);
        public string? Visit(InOperator o) => First([o.LeftOperand, .. o.Operands]);
        public string? Visit(GroupOperator o) => First(o.Operands);
        public string? Visit(OperandValue o) => null;
        public string? Visit(FunctionOperator o) => First(o.Operands);
        public string? Visit(JoinOperand o) => "a join ([<Type>][...]) is not supported";
        public string? Visit(OperandProperty o) => Resolve(o.PropertyName, out _);

        public string? Visit(AggregateOperand o)
        {
            if (o.IsTopLevel || o.CollectionProperty is null) return "an aggregate needs a collection";
            if (Resolve(o.CollectionProperty.PropertyName, out var list) is { } bad) return bad;
            if (list is not { IsList: true, ListElementTypeInfo: { } element }) return $"'{o.CollectionProperty.PropertyName}' is not a collection";
            scope.Push(element);
            try { return First([o.Condition, o.AggregatedExpression, .. o.CustomAggregateOperands ?? []]); }
            finally { scope.Pop(); }
        }

        string? Resolve(string path, out IMemberInfo? last)
        {
            (last, var type) = (null, scope.Peek());
            foreach (var hop in path.Split('.'))
            {
                if (hop.StartsWith('^') || hop.Contains('<')) return $"'{path}': '^.' and type casts are not supported";
                if (type?.FindMember(hop) is not { } m) return $"'{hop}' is not a member of {type?.Name}";
                if (!m.IsVisible || (WorkflowMembers.IsUserType(type.Type) && WorkflowMembers.UserSecrets.Contains(hop)))
                    return $"'{hop}' of {type.Name} cannot be read by a workflow";
                (last, type) = (m, m.MemberTypeInfo);
            }
            return null;
        }
    }

    /// <summary>The first {Placeholder} of <paramref name="template"/> that is not a member of the subject, as a finding; null when all are.</summary>
    string? BadPlaceholder(string template)
    {
        var bad = Placeholder().Matches(template).Select(m => m.Groups[1].Value).FirstOrDefault(n => target.FindMember(n) == null);
        return bad == null ? null : $"placeholder '{{{bad}}}' is not a member of {target.Name}";
    }

    /// <summary>A lock (the validator's only use): a scalar or a reference (<see cref="WorkflowMembers.Lockable"/>).</summary>
    public string? CheckMember(string member) =>
        WorkflowMembers.Lockable(target).Any(m => m.Name == member) ? null : $"'{member}' is not a member of {target.Name} that a workflow may use";

    public string? CheckUserMember(string member) =>
        WorkflowMembers.Users(target).Any(m => m.Name == member) ? null : $"'{member}' is not a reference of {target.Name} to the application's users";

    public string? CheckRuleSet(RuleSetDef ruleSet) =>
        RuleSetEvaluator.Validate(ruleSet, target.Type, m => WorkflowMembers.Supported(target).FirstOrDefault(x => x.Name == m)?.MemberType);

    /// <summary>An Increment's step: null or "" is 1, else an invariant-culture decimal.</summary>
    internal static bool TryStep(string? value, out decimal step)
    {
        step = 1;
        return string.IsNullOrWhiteSpace(value) || decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out step);
    }

    /// <summary>The one text-to-member conversion (validation now, SetField at runtime): invariant culture; "" is null for a nullable member.</summary>
    internal static object? ConvertValue(Type memberType, string value)
    {
        var t = Nullable.GetUnderlyingType(memberType);
        if (t != null && value.Length == 0) return null;
        t ??= memberType;
        var c = CultureInfo.InvariantCulture;
        if (t.IsEnum)
        {
            var v = Enum.Parse(t, value);
            return Enum.IsDefined(t, v) ? v : throw new FormatException($"{value} is not a {t.Name}");
        }
        if (t == typeof(Guid)) return Guid.Parse(value);
        if (t == typeof(bool)) return bool.Parse(value);
        if (t == typeof(DateTime)) return DateTime.Parse(value, c, DateTimeStyles.RoundtripKind);
        return Convert.ChangeType(value, t, c);
    }

    [GeneratedRegex(@"\{([^{}]+)\}")] internal static partial Regex Placeholder();
}
