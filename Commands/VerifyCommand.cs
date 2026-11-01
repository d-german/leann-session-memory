using CSharpFunctionalExtensions;

namespace SessionMemory.Commands
{
    /// <summary>Retrieves source context for proposed claims.</summary>
    sealed internal class VerifyCommand(ClaimVerifier verifier) : ICommand
    {
        public string Verb => "verify";
        public bool RequiresArguments => true;

        public int Execute(CommandArguments arguments)
        {
            var operands = arguments.Operands;
            if (operands.Length != 1 || operands[0].StartsWith("--", StringComparison.Ordinal))
                return CommandLine.Fail(new CliError(CommandLine.Usage, 2));
            return verifier.Verify(operands[0]).Tap(Compact.Write).Match(_ => 0, CommandLine.Fail);
        }
    }
}
