using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SessionMemory.Clients;
using SessionMemory.Commands;

namespace SessionMemory
{
    internal static partial class SelfTest
    {
        // The services as Program composes them, for the given folders.
        private static ServiceProvider Services(string home, string root) =>
            new ServiceCollection().AddSessionClients().AddSessionMemory(new ClientContext(home, root)).AddCommands().BuildServiceProvider();

        // The same services with these clients registered instead of the real ones.
        private static ServiceProvider Services(string home, string root, ImmutableArray<ISessionClient> clients)
        {
            var services = new ServiceCollection().AddSessionMemory(new ClientContext(home, root)).AddCommands();
            foreach (var client in clients) services.AddSingleton(client);
            return services.BuildServiceProvider();
        }

        // The registered clients, in the order they resolve.
        private static ImmutableArray<ISessionClient> RegisteredClients()
        {
            using var services = new ServiceCollection().AddSessionClients().BuildServiceProvider();
            return [.. services.GetServices<ISessionClient>()];
        }

        // One capture of the root with exactly these clients.
        private static CaptureReport CaptureWith(string root, ImmutableArray<ISessionClient> clients)
        {
            using var services = Services(Path.Combine(root, "home"), root, clients);
            return services.GetRequiredService<Corpus>().Capture();
        }

        // The hosts' variables are read in the clients' registration order: Codex, Claude Code, then GitHub Copilot CLI.
        private static void CurrentSessionChecks()
        {
            var missingRoot = Path.Combine(Path.GetTempPath(), "session-memory-no-such-root");
            using var services = Services(Path.Combine(missingRoot, "home"), missingRoot);
            var current = services.GetRequiredService<CurrentSession>();
            var saved = current.Variables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
            try
            {
                foreach (var name in current.Variables) Environment.SetEnvironmentVariable(name, null);
                Check(current.Detect() is null && current.Require().Error == new CliError("The current session could not be detected; give its session ID instead.", 2),
                    "detects no current session when no host sets one");
                Environment.SetEnvironmentVariable("COPILOT_AGENT_SESSION_ID", "abcdef12-3456");
                Check(current.Detect() == "abcdef12-3456", "detects the current Copilot session");
                Environment.SetEnvironmentVariable("COPILOT_AGENT_SESSION_ID", "abc");
                Check(current.Detect() is null, "ignores a detected session ID too short to be safe");
                Environment.SetEnvironmentVariable("CLAUDE_CODE_SESSION_ID", " 0f1e2d3c-4b5a ");
                Check(current.Detect() == "0f1e2d3c-4b5a", "detects the current Claude Code session");
                Environment.SetEnvironmentVariable("CODEX_THREAD_ID", "9a8b7c6d-5e4f");
                Check(current.Detect() == "9a8b7c6d-5e4f", "detects the current Codex thread");
                Environment.SetEnvironmentVariable("COPILOT_AGENT_SESSION_ID", "abcdef12-3456");
                Check(current.Detect() == "9a8b7c6d-5e4f", "prefers Codex when every host announces a session");
                Environment.SetEnvironmentVariable("CODEX_THREAD_ID", null);
                Check(current.Detect() == "0f1e2d3c-4b5a", "prefers Claude Code to GitHub Copilot CLI");
                Check(current.Scope(true, null).Value == "0f1e2d3c-4b5a" && current.Scope(false, "abcdef12").Value == "abcdef12" && current.Scope(false, null).Value is null,
                    "scopes to the current session only with --current");
            }
            finally
            {
                foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
            }
        }

        private static void CommandChecks()
        {
            DispatcherChecks();
            var temporary = Directory.CreateTempSubdirectory("session-memory-command-test-");
            using var cleanup = new TemporaryDirectory(temporary.FullName);
            CommandWiringChecks(temporary.FullName);
        }

        // The dispatcher runs the command its verb names, and only with the arguments that command requires.
        private static void DispatcherChecks()
        {
            var find = new RecordingCommand("find", true, 7);
            var status = new RecordingCommand("status", false, 5);
            var dispatcher = new CommandDispatcher([find, status]);
            var found = Dispatch(dispatcher, ["find", "zebra", "--top", "3"]);
            Check(found == (7, "", "") && find is { Runs: 1, Operands: ["zebra", "--top", "3"] } && status.Runs == 0, "runs the command its verb names");
            Check(Dispatch(dispatcher, ["status"]) == (5, "", "") && status.Runs == 1, "runs a command that needs no arguments without any");
            var usage = (2, "", CommandLine.Usage + Environment.NewLine);
            Check(Dispatch(dispatcher, ["find"]) == usage && Dispatch(dispatcher, ["bogus", "x"]) == usage && Dispatch(dispatcher, []) == usage
                  && Dispatch(dispatcher, ["FIND", "x"]) == usage && find.Runs == 1, "prints the usage for an unknown or missing verb or missing arguments");
        }

        // Every verb resolves to its command, and a command's failure reaches standard error with its exit code.
        private static void CommandWiringChecks(string root)
        {
            using var services = Services(Path.Combine(root, "home"), root);
            Check(services.GetServices<ICommand>().Select(command => command.Verb).SequenceEqual(["run", "search", "find", "show", "exclude", "include", "capture", "status", "self-test"]),
                "registers one command per verb");
            var dispatcher = services.GetRequiredService<CommandDispatcher>();
            Check(Dispatch(dispatcher, ["include", "zzzzzzzz-none"]) == (1, "", "'zzzzzzzz-none' is not excluded." + Environment.NewLine)
                  && Dispatch(dispatcher, ["exclude", " abc "]) == (2, "", "Give at least 8 characters of the session ID." + Environment.NewLine),
                "reports a failed command on standard error with its exit code");
            Check(Dispatch(dispatcher, ["find", "x", "--current", "--session", "abcdefgh"]) == (2, "", "Use either --current or --session, not both." + Environment.NewLine),
                "rejects --current with --session");
            var (exitCode, output, error) = Dispatch(dispatcher, ["status"]);
            Check(exitCode == 0 && error == "" && JsonNode.Parse(output) is JsonObject { Count: 5 } report && report["manifest"] is null
                  && report["excluded"] is JsonArray { Count: 0 }, "reports the status of an empty root");
        }

        // Runs the dispatcher and returns the exit code with what the command wrote to standard output and standard error.
        private static (int ExitCode, string Output, string Error) Dispatch(CommandDispatcher dispatcher, ImmutableArray<string> arguments)
        {
            var (standardOutput, standardError) = (Console.Out, Console.Error);
            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);
            try
            {
                var exitCode = dispatcher.Execute(new CommandArguments(arguments));
                return (exitCode, output.ToString(), error.ToString());
            }
            finally
            {
                Console.SetOut(standardOutput);
                Console.SetError(standardError);
            }
        }

        // A command that counts its runs, keeps the operands of the last one and exits with a fixed code.
        sealed private class RecordingCommand : ICommand
        {
            private readonly int _exitCode;

            public RecordingCommand(string verb, bool requiresArguments, int exitCode)
            {
                Verb = verb;
                RequiresArguments = requiresArguments;
                _exitCode = exitCode;
            }

            public string Verb { get; }

            public bool RequiresArguments { get; }

            public int Runs { get; private set; }

            public ImmutableArray<string> Operands { get; private set; } = [];

            public int Execute(CommandArguments arguments)
            {
                Runs++;
                Operands = arguments.Operands;
                return _exitCode;
            }
        }
    }
}
