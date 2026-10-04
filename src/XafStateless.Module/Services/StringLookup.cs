using DevExpress.ExpressApp;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using Microsoft.Extensions.DependencyInjection;

namespace XafStateless.Module.Services;

/// <summary>An entity whose string members are picked from a list (the lookup editors ask it for the values).</summary>
public interface IStringLookupSource
{
    /// <param name="roleNames">Supplied by the editor: role names read through a non-secured object space
    /// (<see cref="StringLookup.RoleNames"/>), so the entity never queries roles through the user's secured space.
    /// Called only when a member lists roles.</param>
    IEnumerable<string> LookupValues(string memberName, Func<IReadOnlyList<string>> roleNames);

    /// <param name="actionNames">Supplied by the editor: the host's workflow action names (<see cref="StringLookup.ActionNames"/>).
    /// Called only when a member lists actions. Sources without such a member keep the two-argument form.</param>
    IEnumerable<string> LookupValues(string memberName, Func<IReadOnlyList<string>> roleNames, Func<IReadOnlyList<string>> actionNames) =>
        LookupValues(memberName, roleNames);

    /// <summary>The text shown for <paramref name="value"/> in the list and the field; the value itself unless a member
    /// stores something that reads badly (XSM-025: a full class name shows as its caption).</summary>
    string LookupText(string memberName, string value) => value;
}

public static class StringLookup
{
    /// <summary>Editor alias of the single-select string lookup.</summary>
    public const string Single = "XafStateless.StringLookup";
    /// <summary>Editor alias of the multi-select string lookup (values joined with <see cref="Separator"/>).</summary>
    public const string Multi = "XafStateless.StringMultiLookup";
    public const char Separator = ';';

    /// <summary>Role names through a non-secured object space from <paramref name="services"/> (designers cannot read roles).</summary>
    public static Func<IReadOnlyList<string>> RoleNames(IServiceProvider services) => () =>
    {
        using var os = services.GetRequiredService<INonSecuredObjectSpaceFactory>().CreateNonSecuredObjectSpace(typeof(PermissionPolicyRole));
        return WorkflowRoleCatalog.Names(os);
    };

    /// <summary>The host's registered workflow action names (<see cref="WorkflowActionCatalog"/>).</summary>
    public static Func<IReadOnlyList<string>> ActionNames(IServiceProvider services) => () => services.GetRequiredService<WorkflowActionCatalog>().Names;
}
