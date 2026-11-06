using System.Globalization;
using Moonlark.Native.Engineering.Cli;
using Moonlark.Native.Engineering.Core;
using Moonlark.Native.Engineering.Generation;

namespace Moonlark.Native.Engineering.Upstream;

internal static class UpstreamCommand
{
    /// <summary>
    /// <c>upstream update --commit SHA [--dotnet PATH]</c>: moves the libchdr pin to a commit already fetched into the
    /// submodule and regenerates every derived file with the pinned generator, or changes nothing.
    /// </summary>
    internal static int Update(IReadOnlyList<string> arguments, CommandContext context) =>
        Update(arguments, context, dotnet => root =>
        {
            Result<GenerationResult> generated = BindingGenerator.Run(root, dotnet, check: false, context.Environment,
                BindingGenerator.PinnedTool, BindingGenerator.HostPlatformArguments);
            return generated.Succeeded ? null : generated.Failure;
        });

    internal static int Update(IReadOnlyList<string> arguments, CommandContext context, Func<string, Regenerate> regeneratorFor)
    {
        Result<ParsedArguments> parsed = Arguments.Parse(arguments, ["--commit", "--dotnet"], required: ["--commit"]);
        if (!parsed.Succeeded)
            return context.Report(parsed.Failure, "");
        Result<UpstreamUpdateResult> result = UpstreamUpdate.Run(context.Root, parsed.Value.Option("--commit")!, context.Environment,
            regeneratorFor(parsed.Value.Option("--dotnet") ?? "dotnet"));
        if (!result.Succeeded)
            return context.Report(result.Failure, "");
        UpstreamUpdateResult update = result.Value;
        context.Out.WriteLine($"Moved libchdr {update.PreviousCommit} -> {update.Commit} ({update.UpstreamVersion}).");
        context.Out.WriteLine("Remaining manual steps:");
        string[] steps = RemainingSteps(update);
        for (int index = 0; index < steps.Length; index++)
            context.Out.WriteLine($"  {(index + 1).ToString(CultureInfo.InvariantCulture)}. {steps[index]}");
        return context.Report(null, "pin, props, exports.txt, bindings and contract regenerated for " + update.UpstreamVersion);
    }

    internal static string[] RemainingSteps(UpstreamUpdateResult update) =>
    [
        "Review the derived diff: git diff -- " + string.Join(' ', UpstreamUpdate.TrackedPaths),
        "Update the handwritten safe API in src/Moonlark.Libchdr if the bindings changed.",
        .. update.ExportsChanged
            ? (string[])["The export list changed: native build regenerates the platform export files from exports.txt; review each added or removed function."]
            : [],
        "Describe the reviewed upstream diff, including bundled codec changes, in the change description. Start from: "
            + $"git -C {GenerationConfiguration.SourcePath} log --oneline {update.PreviousCommit}..{update.Commit}",
        $"Stage the new submodule commit with the derived files: git add {GenerationConfiguration.SourcePath} " + string.Join(' ', UpstreamUpdate.TrackedPaths),
        "Rebuild and verify the native library, then run the tests: native build --rid <rid>, native verify, dotnet build Romd.sln -c Release and dotnet test tests/Moonlark.Libchdr.Tests -c Release",
    ];
}
