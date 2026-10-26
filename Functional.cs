using System.Text.Json;
using CSharpFunctionalExtensions;

namespace SessionMemory
{
    /// <summary>A command-line failure: the message for standard error and the process exit code.</summary>
    sealed internal record CliError
    {
        public CliError(string message, int exitCode)
        {
            Message = message;
            ExitCode = exitCode;
        }

        public string Message { get; init; }

        public int ExitCode { get; init; }
    }

    /// <summary>
    /// Boundary adapters that turn the exceptions of APIs without a Try variant into a Result. Each catches only the exception types the
    /// calling code expects, so programming errors still surface. Failure text is "{ExceptionType}: {Message}", the format already persisted
    /// in the manifest and watcher status.
    /// </summary>
    internal static class Attempt
    {
        public static string Describe(Exception error) => $"{error.GetType().Name}: {error.Message}";

        public static Result<T, string> Io<T>(Func<T> action)
        {
            try
            {
                return Result.Success<T, string>(action());
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return Result.Failure<T, string>(Describe(error));
            }
        }

        public static UnitResult<string> Io(Action action)
        {
            try
            {
                action();
                return UnitResult.Success<string>();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return UnitResult.Failure(Describe(error));
            }
        }

        public static Result<T, string> Json<T>(Func<T> action)
        {
            try
            {
                return Result.Success<T, string>(action());
            }
            catch (JsonException error)
            {
                return Result.Failure<T, string>(Describe(error));
            }
        }
    }
}
