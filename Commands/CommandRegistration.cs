using Microsoft.Extensions.DependencyInjection;

namespace SessionMemory.Commands
{
    /// <summary>Registers one command per verb and the dispatcher that selects among them.</summary>
    internal static class CommandRegistration
    {
        extension(IServiceCollection services)
        {
            /// <summary>Adds every command as a singleton ICommand and the CommandDispatcher that receives them all.</summary>
            public IServiceCollection AddCommands() =>
                services
                    .AddSingleton<ICommand, RunCommand>()
                    .AddSingleton<ICommand, SearchCommand>()
                    .AddSingleton<ICommand, FindCommand>()
                    .AddSingleton<ICommand, ShowCommand>()
                    .AddSingleton<ICommand, ExcludeCommand>()
                    .AddSingleton<ICommand, IncludeCommand>()
                    .AddSingleton<ICommand, CaptureCommand>()
                    .AddSingleton<ICommand, StatusCommand>()
                    .AddSingleton<ICommand, SelfTestCommand>()
                    .AddSingleton<CommandDispatcher>();
        }
    }
}
