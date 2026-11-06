using System.Text;
using Moonlark.Native.Engineering.Cli;
using Moonlark.Native.Engineering.Core;

namespace Moonlark.Native.Engineering.Native;

internal static class NativeCommand
{
    private static readonly UTF8Encoding LogEncoding = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary><c>native build --rid RID [--source PATH] [--output PATH] [--compiler PATH] [--log PATH]</c>; never installs or publishes.</summary>
    internal static int Build(IReadOnlyList<string> arguments, CommandContext context)
    {
        Result<ParsedArguments> parsed = Arguments.Parse(arguments, ["--rid", "--source", "--output", "--compiler", "--log"], required: ["--rid"]);
        if (!parsed.Succeeded)
            return context.Report(parsed.Failure, "");
        string rid = parsed.Value.Option("--rid")!;
        if (!NativeRids.IsSupported(rid))
            return context.Report(new Failure("Unsupported RID: " + rid), "");
        string root = ArtifactsPath.Resolve(context.Root);
        string defaultOutput = Path.Combine(root, "artifacts", "native", "libchdr", rid);
        // An explicit log may be evidence outside artifacts; the default one must stay inside the ignored tree.
        Result<string> log = parsed.Value.Option("--log") is { } explicitLog
            ? Path.GetFullPath(explicitLog)
            : DefaultLog(Path.Combine(defaultOutput, "build.log"), root);
        if (!log.Succeeded)
            return context.Report(log.Failure, "");
        var request = new NativeBuildRequest(root, rid,
            Path.GetFullPath(parsed.Value.Option("--source") ?? Path.Combine(root, "native", "libchdr", "upstream")),
            Path.GetFullPath(parsed.Value.Option("--output") ?? defaultOutput),
            parsed.Value.Option("--compiler") ?? "clang",
            context.Environment);
        Result<NativeBuildResult> result = Logged(log.Value, writer => NativeBuild.Run(request, writer));
        int code = context.Report(result.Succeeded ? null : result.Failure,
            $"local {rid} build and inspection; unqualified manifest: {(result.Succeeded ? result.Value.Manifest : "")}");
        TextWriter output = code == 0 ? context.Out : context.Error;
        if (result.Succeeded)
            output.WriteLine("Layout probe: " + result.Value.ProbeReceipt);
        output.WriteLine("Log: " + log.Value);
        return code;
    }

    /// <summary><c>native verify --manifest PATH [--source PATH] [--log PATH]</c>; re-checks a manifest and its actual binary.</summary>
    internal static int Verify(IReadOnlyList<string> arguments, CommandContext context)
    {
        Result<ParsedArguments> parsed = Arguments.Parse(arguments, ["--manifest", "--source", "--log"], required: ["--manifest"]);
        if (!parsed.Succeeded)
            return context.Report(parsed.Failure, "");
        string root = ArtifactsPath.Resolve(context.Root);
        string manifest = Path.GetFullPath(parsed.Value.Option("--manifest")!);
        string source = ArtifactsPath.Resolve(Path.GetFullPath(parsed.Value.Option("--source") ?? Path.Combine(root, "native", "libchdr", "upstream")));
        string log = Path.GetFullPath(parsed.Value.Option("--log") ?? Path.Combine(Path.GetDirectoryName(manifest)!, "verify.log"));
        if (parsed.Value.Option("--log") is null && ArtifactsPath.ValidateLogFile(log) is { } unsafeLog)
            return context.Report(unsafeLog, "");
        Result<string> binary = Logged(log, writer => NativeVerify.Run(root, manifest, source, context.Environment, writer));
        int code = context.Report(binary.Succeeded ? null : binary.Failure,
            "digest, recipe, exports, dependencies, architecture and native build-info: " + (binary.Succeeded ? binary.Value : ""));
        if (code == 0)
            context.Out.WriteLine("Qualification: local-unqualified; no release attestation");
        return code;
    }

    private static Result<string> DefaultLog(string path, string root)
    {
        Result<string> directory = ArtifactsPath.ValidateDirectory(Path.GetDirectoryName(path)!, root);
        if (!directory.Succeeded)
            return directory.Failure;
        string resolved = Path.Combine(directory.Value, Path.GetFileName(path));
        return ArtifactsPath.ValidateLogFile(resolved) is { } invalid ? invalid : resolved;
    }

    /// <summary>Appends commands and output, preserving failed attempts; a failure is recorded before it is reported.</summary>
    private static Result<T> Logged<T>(string path, Func<TextWriter, Result<T>> action)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var log = new StreamWriter(path, append: true, LogEncoding);
        try
        {
            Result<T> result = action(log);
            if (!result.Succeeded)
                log.Write("FAIL: " + result.Failure.Message + "\n");
            return result;
        }
        catch (Exception exception)
        {
            log.Write("FAIL: " + exception.Message + "\n");
            throw;
        }
    }
}
