using DevExpress.ExpressApp.DataLocking;
using DevExpress.ExpressApp.DC;
using XafStateless.Module.BusinessObjects;

namespace XafStateless.Module.Services;

/// <summary>The one whitelist of subject members that effects and locks may use.</summary>
public static class WorkflowMembers
{
    /// <summary>Public, persistent, writable, browsable scalars (string, numeric, bool, DateTime, Guid, enum; nullable too).
    /// Never the key, <c>Workflow</c>, the optimistic-lock field, references or collections.</summary>
    public static IReadOnlyList<IMemberInfo> Supported(ITypeInfo target) => target.Members
        .Where(m => m.IsPublic && m.IsProperty && m.IsPersistent && !m.IsReadOnly && m.IsVisible && !m.IsKey && !m.IsList
                    && m.Name is not (nameof(IWorkflowSubject.Workflow) or nameof(IOptimisticLock.OptimisticLockField))
                    && IsScalar(m.MemberType))
        .ToList();

    static bool IsScalar(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t == typeof(Guid) || Type.GetTypeCode(t) is >= TypeCode.Boolean and <= TypeCode.DateTime or TypeCode.String;
    }

    /// <summary>The supported members an Increment effect may count on: whole-number or decimal, nullable or not.</summary>
    public static IReadOnlyList<IMemberInfo> Counters(ITypeInfo target) => Supported(target).Where(m => IsCounter(m.MemberType)).ToList();

    /// <summary>Integral or decimal; not an enum (its TypeCode is its underlying integer's), not float/double (decimal arithmetic
    /// would round them: 12345678f + 1 came back as 12345681).</summary>
    public static bool IsCounter(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return !t.IsEnum && (IsIntegral(t) || t == typeof(decimal));
    }

    /// <summary>A member that takes whole steps only.</summary>
    public static bool IsIntegral(Type t) => Type.GetTypeCode(Nullable.GetUnderlyingType(t) ?? t) is >= TypeCode.SByte and <= TypeCode.UInt64;
}
