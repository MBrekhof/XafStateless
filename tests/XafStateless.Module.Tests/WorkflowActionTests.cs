using Microsoft.Extensions.DependencyInjection;
using XafStateless.Module.Services;

namespace XafStateless.Module.Tests;

/// <summary>The action catalog through DI, and the lookup interface's two forms; no database (XSM-032).</summary>
public class WorkflowActionTests
{
    sealed class Named(string name) : IWorkflowAction
    {
        public string Name => name;
        public void Run(WorkflowActionContext context) { }
    }

    [Fact] public void Catalog_collects_actions_registered_before_and_after_AddXafStateless()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkflowAction>(new Named("before"));
        services.AddXafStateless();
        services.AddSingleton<IWorkflowAction>(new Named("after"));
        using var provider = services.BuildServiceProvider();

        var catalog = provider.GetRequiredService<WorkflowActionCatalog>();
        Assert.Equal(["after", "before"], catalog.Names);
        Assert.IsType<Named>(catalog.Find("before"));
        Assert.Null(catalog.Find("missing"));
        Assert.NotNull(provider.GetRequiredService<WorkflowRuntime>());    // both consumers take the registered catalog
        Assert.NotNull(provider.GetRequiredService<DefinitionService>());
    }

    [Fact] public void Host_without_actions_has_an_empty_catalog()
    {
        var services = new ServiceCollection();
        services.AddXafStateless();
        using var provider = services.BuildServiceProvider();
        Assert.Empty(provider.GetRequiredService<WorkflowActionCatalog>().Names);
        Assert.Empty(WorkflowActionCatalog.Empty.Names);
    }

    [Fact] public void Duplicate_or_empty_names_fail_when_the_catalog_is_resolved()
    {
        var services = new ServiceCollection();
        services.AddXafStateless();
        services.AddSingleton<IWorkflowAction>(new Named("x"));
        services.AddSingleton<IWorkflowAction>(new Named("x"));
        using var provider = services.BuildServiceProvider();
        Assert.Contains("named 'x'", Assert.ThrowsAny<Exception>(() => provider.GetRequiredService<WorkflowActionCatalog>()).ToString());

        Assert.Contains("has no name", Assert.Throws<ArgumentException>(() => new WorkflowActionCatalog([new Named("")])).Message);
    }

    sealed class LegacySource : IStringLookupSource   // a source written before actions existed: only the two-argument form
    {
        public IEnumerable<string> LookupValues(string memberName, Func<IReadOnlyList<string>> roleNames) => ["a", .. roleNames()];
    }

    [Fact] public void Two_argument_lookup_sources_answer_the_three_argument_call()
    {
        IStringLookupSource s = new LegacySource();
        Assert.Equal(["a", "r"], s.LookupValues("X", () => ["r"], () => ["ignored"]));
    }
}
