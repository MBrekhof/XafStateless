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
}
