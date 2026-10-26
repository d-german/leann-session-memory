using CSharpFunctionalExtensions;

namespace SessionMemory.Commands
{
    /// <summary>exclude: drops a session and its subagents from the searches now and from the indexes at the next rebuild.</summary>
    sealed internal class ExcludeCommand : ICommand
    {
        private const string Hint = "Search hides it now; the watcher removes it from both indexes within about 20 minutes.";

        private readonly ExclusionService _exclusions;

        public ExcludeCommand(ExclusionService exclusions)
        {
            _exclusions = exclusions;
        }

        public string Verb => "exclude";

        public bool RequiresArguments => true;

        public int Execute(CommandArguments arguments) =>
            _exclusions.Exclude(arguments.Operands[0])
                .Tap(update => CommandLine.Print(update.Report))
                .Tap(update => _exclusions.Save(update))
                .Tap(() => Console.WriteLine(Hint))
                .Match(_ => 0, CommandLine.Fail);
    }
}
