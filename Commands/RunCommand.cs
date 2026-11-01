using CSharpFunctionalExtensions;

namespace SessionMemory.Commands
{
    /// <summary>run [--once] [--force] [--progress]: the watcher, or with --once a single refresh.</summary>
    sealed internal class RunCommand : ICommand
    {
        private readonly Watcher _watcher;

        public RunCommand(Watcher watcher)
        {
            _watcher = watcher;
        }

        public string Verb => "run";

        public bool RequiresArguments => false;

        public int Execute(CommandArguments arguments) =>
            _watcher.RunExclusive(arguments.Flag("--once"), arguments.Flag("--force"), arguments.Flag("--progress")).Match(exitCode => exitCode, CommandLine.Fail);
    }
}
