using DevExpress.ExpressApp;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;

namespace XafStateless.Module.Services;

/// <summary>Role NAMES only, read through a non-secured object space the caller passes (standard XAF roles deny role reads).</summary>
public static class WorkflowRoleCatalog
{
    // ponytail: PermissionPolicyRole (and subclasses) only; take the role type as a parameter if a host uses another.
    public static IReadOnlyList<string> Names(IObjectSpace nonSecured) =>
        nonSecured.GetObjectsQuery<PermissionPolicyRole>().Select(r => r.Name).OrderBy(n => n).ToList();
}
