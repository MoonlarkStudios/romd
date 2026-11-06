using System.Diagnostics;
using Romd.Mount.Mounting;
using Shouldly;
using Xunit;

namespace Romd.Mount.Tests;

public sealed class TokenCommandTests
{
    [UnixFact]
    public async Task ChildCommand_ReceivesTokenOverPrivateSocket_WithoutSecretArguments()
    {
        await using var server = new TokenCommandServer(_ => Task.FromResult("synthetic-access"));
        var start = Start(server);
        start.ArgumentList.ShouldNotContain("synthetic-access");
        var result = await Run(start);
        result.Code.ShouldBe(0);
        result.Output.Trim().ShouldBe("synthetic-access");
        result.Error.ShouldBeEmpty();
    }

    [UnixFact]
    public async Task WrongNonce_DoesNotInvokeBrokerOrReturnToken()
    {
        int calls = 0;
        await using var server = new TokenCommandServer(_ => { calls++; return Task.FromResult("synthetic-access"); });
        var start = Start(server);
        string nonce = start.Environment["ROMD_MOUNT_TOKEN_NONCE"]!;
        start.Environment["ROMD_MOUNT_TOKEN_NONCE"] = (nonce[0] == '0' ? "1" : "0") + nonce[1..];
        var result = await Run(start);
        result.Code.ShouldBe(1);
        result.Output.ShouldBeEmpty();
        result.Error.Trim().ShouldBe("Token command failed.");
        calls.ShouldBe(0);
    }

    [UnixFact]
    public async Task BrokerFailure_ReturnsNoCredentialOrDetailedError()
    {
        await using var server = new TokenCommandServer(_ => throw new MountIdentityException(IdentityFailure.SignInRequired));
        var result = await Run(Start(server));
        result.Code.ShouldBe(1);
        result.Output.ShouldBeEmpty();
        result.Error.Trim().ShouldBe("Token command failed.");
    }

    private static ProcessStartInfo Start(TokenCommandServer server)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("_token");
        server.Configure(start);
        return start;
    }

    private static async Task<(int Code, string Output, string Error)> Run(ProcessStartInfo start)
    {
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        return (process.ExitCode, await output, await error);
    }

    public sealed class UnixFactAttribute : FactAttribute
    {
        public UnixFactAttribute()
        {
            if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
                Skip = "Unix-domain token IPC requires macOS or Linux.";
        }
    }
}
