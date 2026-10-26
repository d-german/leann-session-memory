using CSharpFunctionalExtensions;

namespace SessionMemory.Commands
{
    /// <summary>include: takes an excluded session back into the searches and the indexes.</summary>
    sealed internal class IncludeCommand : ICommand
    {
        private const string Hint = "The watcher indexes it again within a few minutes.";

        private readonly ExclusionService _exclusions;

        public IncludeCommand(ExclusionService exclusions)
        {
            _exclusions = exclusions;
        }

        public string Verb => "include";

        public bool RequiresArguments => true;

        public int Execute(CommandArguments arguments) =>
            _exclusions.Include(arguments.Operands[0])
                .Tap(update => CommandLine.Print(update.Report))
                .Tap(update => _exclusions.Save(update))
                .Tap(() => Console.WriteLine(Hint))
                .Match(_ => 0, CommandLine.Fail);
    }
}
