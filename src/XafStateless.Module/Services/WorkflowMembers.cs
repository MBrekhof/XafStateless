using DevExpress.ExpressApp.DataLocking;
using DevExpress.ExpressApp.DC;
using DevExpress.ExpressApp.Security;
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

    /// <summary>The members a state may lock: the <see cref="Supported"/> scalars plus references (CARD-2735): public, persistent,
    /// writable, browsable members holding one persistent object, whose key this row holds (never the inverse side of a one-to-one).
    /// A reference changes when its key does. Never <c>Workflow</c>.</summary>
    public static IReadOnlyList<IMemberInfo> Lockable(ITypeInfo target) =>
    [
        .. Supported(target),
        .. target.Members.Where(m => m.IsPublic && m.IsProperty && m.IsPersistent && !m.IsReadOnly && m.IsVisible && !m.IsKey && !m.IsList
                                     && m.Name != nameof(IWorkflowSubject.Workflow) && m.MemberTypeInfo is { IsPersistent: true }
                                     && m.AssociatedMemberInfo is not { IsList: false }),
    ];

    /// <summary>The user members (CARD-2615): public persistent references to a Guid-keyed class implementing XAF's
    /// <see cref="ISecurityUser"/>. A Task state or a Notify addresses one as <c>@Member</c>.</summary>
    public static IReadOnlyList<IMemberInfo> Users(ITypeInfo target) => target.Members
        .Where(m => m.IsPublic && m.IsProperty && m.IsPersistent && !m.IsList && typeof(ISecurityUser).IsAssignableFrom(m.MemberType)
                    && m.MemberTypeInfo.KeyMember?.MemberType == typeof(Guid))
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
