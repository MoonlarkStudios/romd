using Moonlark.Native.Engineering.Core;

namespace Moonlark.Native.Engineering.Cli;

/// <summary>Per-invocation inputs. Commands receive the environment explicitly instead of reading process state.</summary>
internal sealed record CommandContext(string Root, TextWriter Out, TextWriter Error, IReadOnlyDictionary<string, string> Environment)
{
    /// <summary>Prints PASS or FAIL and returns the process exit code.</summary>
    internal int Report(Failure? failure, string success)
    {
        if (failure is not null)
        {
            Error.WriteLine("FAIL: " + failure.Message);
            return 1;
        }
        Out.WriteLine("PASS: " + success);
        return 0;
    }
}
