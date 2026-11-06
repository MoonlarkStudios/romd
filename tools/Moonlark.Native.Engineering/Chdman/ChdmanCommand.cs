using System.Globalization;
using System.Text.Json.Nodes;
using Moonlark.Native.Engineering.Cli;
using Moonlark.Native.Engineering.Core;

namespace Moonlark.Native.Engineering.Chdman;

internal static class ChdmanCommand
{
    internal const int DefaultJobs = 4;

    /// <summary>The host RID's default output directory below the repository root.</summary>
    internal static string DefaultOutput(string rid) => "artifacts/tools/chdman/" + rid;

    /// <summary><c>chdman build --archive PATH --log PATH [--output PATH] [--jobs N] [--sdl-include PATH]</c>: builds only the pinned chdman and prints its receipt.</summary>
    internal static int Build(IReadOnlyList<string> arguments, CommandContext context)
    {
        Result<ParsedArguments> parsed = Arguments.Parse(arguments, ["--archive", "--log", "--output", "--jobs", "--sdl-include"], required: ["--archive", "--log"]);
        if (!parsed.Succeeded)
            return context.Report(parsed.Failure, "");
        string? jobsText = parsed.Value.Option("--jobs");
        int jobs = DefaultJobs;
        if (jobsText is not null && !int.TryParse(jobsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out jobs))
            return context.Report(new Failure("Jobs must be an integer: " + jobsText), "");
        string? output = parsed.Value.Option("--output");
        if (output is null)
        {
            Result<string> rid = ChdmanBuild.HostRid();
            if (!rid.Succeeded)
                return context.Report(rid.Failure, "");
            output = Path.Combine(context.Root, DefaultOutput(rid.Value));
        }
        var request = new ChdmanBuildRequest(Path.GetFullPath(parsed.Value.Option("--archive")!), Path.GetFullPath(output),
            jobs, Path.GetFullPath(parsed.Value.Option("--log")!), SdlIncludeRoot: parsed.Value.Option("--sdl-include"));
        Result<JsonObject> receipt = ChdmanBuild.Run(context.Root, context.Environment, request);
        if (!receipt.Succeeded)
            return context.Report(receipt.Failure, "");
        context.Out.Write(ReceiptJson.Serialize(receipt.Value));
        return context.Report(null, "pinned chdman built with receipt " + Path.Combine(request.Output, ChdmanBuild.ManifestName));
    }
}
