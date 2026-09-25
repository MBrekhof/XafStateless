using DevExpress.ExpressApp;
using DevExpress.Persistent.Base;
using Microsoft.Extensions.DependencyInjection;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Services;

namespace XafStateless.Module.Controllers;

/// <summary>The command path every workflow action follows: save the view, act as the current user, refresh, report.</summary>
public static class WorkflowCommand
{
    /// <summary>The logged-on user's role names (none without security).</summary>
    public static IReadOnlyList<string> RoleNames(XafApplication app) =>
        (app.Security?.User as IPermissionPolicyUser)?.Roles.Select(r => r.Name).ToList() ?? [];

    /// <summary>The current user; reads through <paramref name="secured"/>, writes through non-secured command spaces.</summary>
    public static WorkflowCaller Caller(XafApplication app, IObjectSpace secured)
    {
        var (roles, services) = (RoleNames(app), app.ServiceProvider);
        return new(app.Security?.UserName ?? "", roles.Contains, secured,
            () => services.GetRequiredService<INonSecuredObjectSpaceFactory>().CreateNonSecuredObjectSpace(typeof(WorkflowInstance)));
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
