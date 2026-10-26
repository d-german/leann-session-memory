using System.Collections.Immutable;

namespace SessionMemory.Commands
{
    /// <summary>The usage text and the way commands write their results and failures.</summary>
    internal static class CommandLine
    {
        public const string Usage = "Usage: SessionMemory [--root <path>] run [--once] [--force] | search <query> [--top-k <n>] [--json] [--include-current] [--current] | " +
                                    "find <term> [<term> ...] [--top <n>] [--session <id>] [--partial] [--json] [--include-current] [--current] [--since <time>] [--outputs] | " +
                                    "show <session-id>|--current [--last <n>] [--around <regex>] [--context <n>] [--max <n>] [--chars <n>] | " +
                                    "exclude <session-id> | include <session-id> | capture | status | self-test";

        // A failure goes to standard error and its exit code becomes the process's.
        public static int Fail(CliError error)
        {
            Console.Error.WriteLine(error.Message);
            return error.ExitCode;
        }

        public static int ShowUsage(int exitCode) => Fail(new CliError(Usage, exitCode));

        // JSON is ASCII and goes through Console.Out; compact text is written as UTF-8 whatever the console code page is.
        public static int Write(string output, bool json)
        {
            if (json) Console.Out.WriteLine(output);
            else Compact.Write(output);
            return 0;
        }

        public static void Print(ImmutableArray<string> lines)
        {
            foreach (var line in lines) Console.WriteLine(line);
        }
    }
}
