using System.Text.Json;
using System.Xml;
using Moonlark.Native.Engineering.Chdman;
using Moonlark.Native.Engineering.Core;
using Moonlark.Native.Engineering.Fixtures;
using Moonlark.Native.Engineering.Generation;
using Moonlark.Native.Engineering.Native;
using Moonlark.Native.Engineering.Upstream;

namespace Moonlark.Native.Engineering.Cli;

internal static class CommandRouter
{
    private static readonly Dictionary<string, Func<IReadOnlyList<string>, CommandContext, int>> Commands = new(StringComparer.Ordinal)
    {
        ["native build"] = NativeCommand.Build,
        ["native verify"] = NativeCommand.Verify,
        ["generate"] = GenerationCommand.Generate,
        ["upstream update"] = UpstreamCommand.Update,
        ["chdman build"] = ChdmanCommand.Build,
        ["fixtures generate"] = FixturesCommand.Generate,
        ["fixtures verify"] = FixturesCommand.Verify,
    };

    internal static int Run(IReadOnlyList<string> arguments, TextWriter output, TextWriter error)
    {
        (string Name, int Length)? command = arguments.Count >= 2 && Commands.ContainsKey(arguments[0] + " " + arguments[1])
            ? (arguments[0] + " " + arguments[1], 2)
            : arguments.Count >= 1 && Commands.ContainsKey(arguments[0]) ? (arguments[0], 1) : null;
        if (command is not { } selected)
        {
            error.WriteLine("Usage: <" + string.Join(" | ", Commands.Keys) + "> [options]");
            return 2;
        }
        Result<string> root = RepositoryRoot.Find(AppContext.BaseDirectory);
        if (!root.Succeeded)
        {
            error.WriteLine("FAIL: " + root.Failure.Message);
            return 1;
        }
        var context = new CommandContext(root.Value, output, error, BuildEnvironment.Current());
        try
        {
            return Commands[selected.Name](arguments.Skip(selected.Length).ToArray(), context);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or XmlException or InvalidDataException or FormatException or KeyNotFoundException)
        {
            // Environment failures are reported like checks; programming errors still crash loudly.
            error.WriteLine("FAIL: " + exception.Message);
            return 1;
        }
    }
}
