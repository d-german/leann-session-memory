using System.Collections.Immutable;
using CSharpFunctionalExtensions;

namespace SessionMemory.Commands
{
    /// <summary>
    /// Runs the command the verb names. An unknown or missing verb, or a verb that requires arguments given without any, prints the usage and
    /// exits with 2.
    /// </summary>
    sealed internal class CommandDispatcher
    {
        private readonly ImmutableArray<ICommand> _commands;

        public CommandDispatcher(IEnumerable<ICommand> commands)
        {
            _commands = [.. commands];
        }

        public int Execute(CommandArguments arguments) =>
            _commands.TryFirst(command => command.Verb == arguments.Verb && (!command.RequiresArguments || !arguments.Operands.IsEmpty))
                .Match(command => command.Execute(arguments), () => CommandLine.ShowUsage(2));
    }
}
