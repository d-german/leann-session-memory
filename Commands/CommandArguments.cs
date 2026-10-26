using System.Collections.Immutable;
using System.Globalization;
using CSharpFunctionalExtensions;

namespace SessionMemory.Commands
{
    /// <summary>
    /// The command-line arguments still to be read, the verb first. Reading a flag or an option takes its first occurrence out, so the operands are
    /// what remains once a command has read the options it knows.
    /// </summary>
    sealed internal class CommandArguments
    {
        private readonly List<string> _arguments;

        public CommandArguments(IEnumerable<string> arguments)
        {
            _arguments = [.. arguments];
        }

        // --help, -h, -? or /? anywhere asks for the usage.
        public bool AsksForHelp => _arguments.Any(argument => argument is "--help" or "-h" or "-?" or "/?");

        public string? Verb => _arguments.FirstOrDefault();

        // The arguments after the verb that no flag or option has taken.
        public ImmutableArray<string> Operands => [.. _arguments.Skip(1)];

        // Takes the flag out and tells whether it was given.
        public bool Flag(string name) => _arguments.Remove(name);

        // Takes "<name> <value>" out and returns the value. A name without a value stays where it is.
        public Maybe<string> Option(string name)
        {
            var index = _arguments.IndexOf(name);
            if (index < 0 || index + 1 >= _arguments.Count) return Maybe<string>.None;
            var value = _arguments[index + 1];
            _arguments.RemoveRange(index, 2);
            return Maybe.From(value);
        }

        // A whole-number option, or the fallback when it is absent.
        public Result<int, CliError> Number(string name, int fallback) =>
            Option(name).Match(
                value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                    ? Result.Success<int, CliError>(number)
                    : Result.Failure<int, CliError>(new CliError($"'{value}' is not a whole number for {name}.\n{CommandLine.Usage}", 2)),
                () => Result.Success<int, CliError>(fallback));
    }
}
