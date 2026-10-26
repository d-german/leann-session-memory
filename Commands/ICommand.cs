namespace SessionMemory.Commands
{
    /// <summary>
    /// One verb of the command line. A command reads its arguments, calls the services and turns their result into output and an exit code;
    /// the rules themselves live in the services.
    /// </summary>
    internal interface ICommand
    {
        /// <summary>The first argument, which selects the command, such as "find".</summary>
        string Verb { get; }

        /// <summary>Whether the verb must be followed by at least one more argument; without one the dispatcher prints the usage instead.</summary>
        bool RequiresArguments { get; }

        /// <summary>Runs the command on the arguments still to be read, the verb first, and returns the process exit code.</summary>
        int Execute(CommandArguments arguments);
    }
}
