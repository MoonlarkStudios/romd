using Moonlark.Native.Engineering.Core;

namespace Moonlark.Native.Engineering.Cli;

internal sealed record ParsedArguments(IReadOnlyDictionary<string, string> Options, IReadOnlySet<string> Flags)
{
    internal string? Option(string name) => Options.GetValueOrDefault(name);

    internal bool Flag(string name) => Flags.Contains(name);
}

/// <summary>Closed <c>--name value</c> and <c>--flag</c> grammar; unknown, repeated or positional arguments fail.</summary>
internal static class Arguments
{
    internal static Result<ParsedArguments> Parse(IReadOnlyList<string> arguments, IReadOnlyCollection<string> options,
        IReadOnlyCollection<string>? flags = null, IReadOnlyCollection<string>? required = null)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < arguments.Count; index++)
        {
            string name = arguments[index];
            if (flags?.Contains(name) == true)
            {
                if (!set.Add(name))
                    return new Failure("Repeated flag " + name);
            }
            else if (options.Contains(name))
            {
                if (index + 1 >= arguments.Count)
                    return new Failure("Missing value for " + name);
                if (!values.TryAdd(name, arguments[++index]))
                    return new Failure("Repeated option " + name);
            }
            else
            {
                return new Failure("Unrecognized argument " + name);
            }
        }
        foreach (string name in required ?? [])
            if (!values.ContainsKey(name))
                return new Failure("Missing required option " + name);
        return new ParsedArguments(values, set);
    }
}
