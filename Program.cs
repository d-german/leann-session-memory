using CSharpFunctionalExtensions;
using Microsoft.Extensions.DependencyInjection;
using SessionMemory.Clients;
using SessionMemory.Commands;

namespace SessionMemory
{
    /// <summary>The composition root: resolves the data folders, builds the services and runs the command the arguments name.</summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var arguments = new CommandArguments(args);
            if (arguments.AsksForHelp) return CommandLine.ShowUsage(0);
            var root = arguments.Option("--root").Match(Path.GetFullPath, DefaultRoot);
            var paths = new ClientContext(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), root);
            using var services = new ServiceCollection()
                .AddSessionClients()
                .AddSessionMemory(paths)
                .AddCommands()
                .BuildServiceProvider();
            return services.GetRequiredService<CommandDispatcher>().Execute(arguments);
        }

        private static string DefaultRoot() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeannSessionMemory");
    }
}
