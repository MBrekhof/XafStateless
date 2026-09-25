using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XafStateless.Module.Services;

namespace XafStateless.Module;

public static class ServiceCollectionExtensions
{
    /// <summary>The only registration call a host makes for the module's services. Hosted services (timer, notification
    /// dispatcher) stay host-owned. TimeProvider is TryAdd'ed so a test or host can pre-register a fake clock.</summary>
    public static IServiceCollection AddXafStateless(this IServiceCollection s)
    {
        s.AddSingleton<DefinitionStore>();
        s.AddSingleton<DefinitionService>();
        s.TryAddSingleton(TimeProvider.System);
        return s;
    }
}
