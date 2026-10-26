using Microsoft.Extensions.DependencyInjection;
using SessionMemory.Clients;

namespace SessionMemory
{
    /// <summary>Registers the data paths and the services the commands use. The clients are registered on their own, so a test can supply others.</summary>
    internal static class ServiceRegistration
    {
        extension(IServiceCollection services)
        {
            /// <summary>
            /// Adds the data paths and every service as a singleton, except the watcher: its build and failure state belongs to one run.
            /// </summary>
            public IServiceCollection AddSessionMemory(ClientContext paths) =>
                services
                    .AddSingleton(paths)
                    .AddSingleton<LeannInstallation>()
                    .AddSingleton<CurrentSession>()
                    .AddSingleton<ExclusionService>()
                    .AddSingleton<Corpus>()
                    .AddSingleton<Finder>()
                    .AddSingleton<SearchServer>()
                    .AddSingleton<Indexer>()
                    .AddSingleton<SessionReader>()
                    .AddSingleton<StatusReport>()
                    .AddTransient<Watcher>();
        }
    }
}
