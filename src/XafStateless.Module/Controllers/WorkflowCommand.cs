using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Security;
using DevExpress.Persistent.Base;
using Microsoft.Extensions.DependencyInjection;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Services;

namespace XafStateless.Module.Controllers;

/// <summary>The command path every workflow action follows: save the view, act as the current user, refresh, report.</summary>
public static class WorkflowCommand
{
    /// <summary>The logged-on user's role names (none without security).</summary>
    public static IReadOnlyList<string> RoleNames(XafApplication app) => RoleNames(app.Security?.User);

    internal static IReadOnlyList<string> RoleNames(object? user) =>
        (user as IPermissionPolicyUser)?.Roles.Select(r => r.Name).ToList() ?? [];

    /// <summary>True when the logged-on user has a role with IsAdministrative set (never decided by a role name).</summary>
    public static bool IsAdministrator(XafApplication app) => IsAdministrator(app.Security?.User);

    internal static bool IsAdministrator(object? user) => (user as IPermissionPolicyUser)?.Roles.Any(r => r.IsAdministrative) == true;

    /// <summary>The current user; reads through <paramref name="secured"/>, writes through non-secured command spaces.</summary>
    public static WorkflowCaller Caller(XafApplication app, IObjectSpace secured)
    {
        var (roles, services) = (RoleNames(app), app.ServiceProvider);
        return new(app.Security?.UserName ?? "", roles.Contains, secured,
            () => services.GetRequiredService<INonSecuredObjectSpaceFactory>().CreateNonSecuredObjectSpace(typeof(WorkflowInstance)),
            app.Security as IRequestSecurityStrategy,
            app.Security?.UserId switch
            {
                null => null,
                Guid id => id,
                var other => throw new NotSupportedException($"XafStateless needs Guid-keyed users; the user key is a {other.GetType().Name}."),
            });
    }

    /// <summary>The caller for a command issued from an entry screen (<see cref="IEntryScreen"/>) or any other view whose object
    /// space is not the subject's: its Secured is the object space of <paramref name="os"/> that holds <paramref name="subjectType"/>
    /// (<see cref="SpaceFor"/>). With a non-persistent space as Secured the runtime's Write check would be skipped (it only
    /// knows ISecuredObjectSpace), so a user with Read only could start workflows from a screen; this overload makes that
    /// impossible: with security configured, a resolved space that is not secured is refused.</summary>
    public static WorkflowCaller Caller(XafApplication app, IObjectSpace os, Type subjectType) =>
        Caller(app, SpaceFor(os, subjectType, app.Security is IRequestSecurityStrategy));

    /// <summary>The object space of <paramref name="os"/> that holds <paramref name="subjectType"/>: <paramref name="os"/> itself when
    /// it knows the type by itself, else the first additional object space of the composite (an entry screen's non-persistent
    /// space, populated by the host) that knows it. Never CanInstantiate: on a composite it answers through the additional spaces
    /// and would hand back the composite. With <paramref name="securityConfigured"/> the result must be an
    /// <see cref="ISecuredObjectSpace"/>; a plain space is accepted only in an application without security.</summary>
    internal static IObjectSpace SpaceFor(IObjectSpace os, Type subjectType, bool securityConfigured)
    {
        var space = os is CompositeObjectSpace c && !c.IsKnownType(subjectType, false)
            ? c.AdditionalObjectSpaces.FirstOrDefault(a => a.IsKnownType(subjectType))
              ?? throw new InvalidOperationException($"No object space holds {subjectType.Name}: the host must populate the additional object spaces of non-persistent object spaces (integrating.md step 11).")
            : os;
        if (securityConfigured && space is not ISecuredObjectSpace)
            throw new InvalidOperationException($"The object space for {subjectType.Name} is not secured; a workflow command needs the user's secured object space.");
        return space;
    }

    /// <summary>Commits pending changes of <paramref name="os"/>. A refused save (validation, security, a guard) shows its
    /// message and returns false; the pending changes stay for the user to fix.</summary>
    public static bool Save(XafApplication app, IObjectSpace os) => Save(os, m => ShowError(app, m));

    static void ShowError(XafApplication app, string message) => app.ShowViewStrategy.ShowMessage(message, InformationType.Error);

    internal static bool Save(IObjectSpace os, Action<string> showError)
    {
        if (!os.IsModified) return true;
        try
        {
            os.CommitChanges();
            return true;
        }
        catch (Exception ex)
        {
            Tracing.Tracer.LogError(ex);
            showError(ex.Message);
            return false;
        }
    }

    /// <summary>Save, run <paramref name="command"/> as the current user, refresh the view to the committed result, and show
    /// the runtime's message when it refused.</summary>
    public static void Run(XafApplication app, ObjectView view, Func<WorkflowCaller, RunResult> command) =>
        Run(view.ObjectSpace, () => Caller(app, view.ObjectSpace), command, m => ShowError(app, m));

    /// <summary>The same path without a frame (tests): a refused save stops before <paramref name="command"/> runs.</summary>
    internal static void Run(IObjectSpace os, Func<WorkflowCaller> caller, Func<WorkflowCaller, RunResult> command, Action<string> showError)
    {
        if (!Save(os, showError)) return;
        var result = command(caller());
        os.Refresh();
        if (!result.Ok) showError(result.Message);
    }
}
