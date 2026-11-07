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

    /// <summary>The members of a user (a class implementing <see cref="ISecurityUser"/>) a Set field expression may not read
    /// (CARD-2895): its password, its lockout and login data, and its roles (who administers the application).</summary>
    public static readonly IReadOnlySet<string> UserSecrets = new HashSet<string>(StringComparer.Ordinal)
        { "StoredPassword", "ChangePasswordOnFirstLogon", "AccessFailedCount", "LockoutEnd", "Roles", "UserLogins" };

    /// <summary>A user class, or one of the XAF interfaces that declare a user's secrets; several of them do not extend
    /// <see cref="ISecurityUser"/>, so a member typed as one of them is a user too.</summary>
    public static bool IsUserType(Type t) =>
        new[] { typeof(ISecurityUser), typeof(ISecurityUserWithRoles), typeof(ISecurityUserLockout), typeof(IOAuthSecurityUser),
                typeof(DevExpress.Persistent.Base.Security.IAuthenticationStandardUser), typeof(DevExpress.Persistent.Base.IPermissionPolicyUser) }
            .Any(i => i.IsAssignableFrom(t));

    /// <summary>True when <paramref name="member"/>, read on <paramref name="owner"/>, is one of a user's <see cref="UserSecrets"/>.</summary>
    public static bool IsSecret(Type owner, string member) => IsUserType(owner) && UserSecrets.Contains(member);

    /// <summary>A member a workflow may not read (CARD-2895 expressions, CARD-2909 placeholders): hidden ([Browsable(false)]:
    /// XAF's type-level Read still allows it, and no member permission can be set on it in the UI), or a user's secret, judged
    /// on the type it is read on (<paramref name="receiver"/>) and on the type declaring it.</summary>
    public static bool IsUnreadable(IMemberInfo member, Type receiver) =>
        !member.IsVisible || IsSecret(receiver, member.Name) || IsSecret(member.Owner.Type, member.Name);

    /// <summary>The first hop of <paramref name="path"/> (a member path found on <paramref name="from"/>) a workflow may not
    /// read (<see cref="IsUnreadable"/>), each hop judged on the type it is read on; null when every hop is fine.</summary>
    public static IMemberInfo? FirstUnreadable(ITypeInfo from, IMemberInfo path)
    {
        var receiver = from;
        foreach (var hop in path.GetPath())
        {
            if (IsUnreadable(hop, receiver.Type)) return hop;
            receiver = hop.MemberTypeInfo;
        }
        return null;
    }

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
