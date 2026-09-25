using System.Globalization;
using System.Text.RegularExpressions;
using DevExpress.Data.Filtering;
using DevExpress.Data.Filtering.Exceptions;
using DevExpress.Data.Filtering.Helpers;
using DevExpress.ExpressApp.DC;
using XafStateless.Engine;

namespace XafStateless.Module.Services;

/// <summary>The validator's view of XAF: role names (from <see cref="WorkflowRoleCatalog"/>) and the subject's type info.</summary>
public sealed partial class XafWorkflowEnvironment(IReadOnlyList<string> roleNames, ITypeInfo target) : IWorkflowEnvironment
{
    public bool RoleExists(string role) => roleNames.Contains(role);

    public string? CheckCriteria(string criteria)
    {
        CriteriaOperator op;
        try { op = CriteriaOperator.Parse(criteria); }
        catch (CriteriaParserException ex) { return ex.Message; }
        // ponytail: first-level operands only; members inside aggregate scopes ([Items][...]) are not resolved.
        var unknown = CollectFirstLevelPropertiesHelper.Collect(op).Select(p => p.PropertyName).FirstOrDefault(n => target.FindMember(n) == null);
        return unknown == null ? null : $"'{unknown}' is not a member of {target.Name}";
    }

    public string? CheckEffect(EffectDef effect)
    {
        switch (effect.Kind)
        {
            case EffectKind.SetField:
                if (WorkflowMembers.Supported(target).FirstOrDefault(m => m.Name == effect.Target) is not { } member)
                    return $"'{effect.Target}' cannot be set by a workflow";
                try { ConvertValue(member.MemberType, effect.Value); return null; }
                catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or InvalidCastException)
                { return $"'{effect.Value}' is not a valid {(Nullable.GetUnderlyingType(member.MemberType) ?? member.MemberType).Name} for '{effect.Target}'"; }
            case EffectKind.Notify:
                var bad = Placeholder().Matches(effect.Value).Select(m => m.Groups[1].Value).FirstOrDefault(n => target.FindMember(n) == null);
                return bad == null ? null : $"placeholder '{{{bad}}}' is not a member of {target.Name}";
            default:
                return $"unknown effect kind {effect.Kind}";
        }
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

    [GeneratedRegex(@"\{([^{}]+)\}")] private static partial Regex Placeholder();
}
