using System.Globalization;
using System.Text.RegularExpressions;
using DevExpress.Data.Filtering;
using DevExpress.Data.Filtering.Exceptions;
using DevExpress.Data.Filtering.Helpers;
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
        if (effect.Kind == EffectKind.RunAction)
            return (actionNames ?? []).Contains(effect.Target)
                ? BadPlaceholder(effect.Value ?? "")
                : $"action '{effect.Target}' is not registered in this application";
        if (effect.Value is null)   // a nulled column; the record's type says non-null, the database does not
            return effect.Kind == EffectKind.SetField ? $"'{effect.Target}' needs a value" : "a notification needs a text";
        switch (effect.Kind)
        {
            case EffectKind.SetField:
                if (WorkflowMembers.Supported(target).FirstOrDefault(m => m.Name == effect.Target) is not { } member)
                    return $"'{effect.Target}' cannot be set by a workflow";
                try { ConvertValue(member.MemberType, effect.Value); return null; }
                catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or InvalidCastException)
                { return $"'{effect.Value}' is not a valid {(Nullable.GetUnderlyingType(member.MemberType) ?? member.MemberType).Name} for '{effect.Target}'"; }
            case EffectKind.Notify:
                return BadPlaceholder(effect.Value);
            default:
                return $"unknown effect kind {effect.Kind}";
        }
    }

    /// <summary>The first {Placeholder} of <paramref name="template"/> that is not a member of the subject, as a finding; null when all are.</summary>
    string? BadPlaceholder(string template)
    {
        var bad = Placeholder().Matches(template).Select(m => m.Groups[1].Value).FirstOrDefault(n => target.FindMember(n) == null);
        return bad == null ? null : $"placeholder '{{{bad}}}' is not a member of {target.Name}";
    }

    public string? CheckMember(string member) =>
        WorkflowMembers.Supported(target).Any(m => m.Name == member) ? null : $"'{member}' is not a member of {target.Name} that a workflow may use";

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
