namespace SessionMemory.Commands
{
    /// <summary>status: the watcher, manifest and index states and the excluded sessions, as JSON.</summary>
    sealed internal class StatusCommand : ICommand
    {
        private readonly StatusReport _status;

        public StatusCommand(StatusReport status)
        {
            _status = status;
        }

        public string Verb => "status";

        public bool RequiresArguments => false;

        public int Execute(CommandArguments arguments)
        {
            Console.WriteLine(_status.Read().ToJsonString(Store.Options));
            return 0;
        }
    }
}
