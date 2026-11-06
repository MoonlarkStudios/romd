using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Romd.Admin.Application.Common.Persistence;
using Romd.Contracts.Management.Users;
using Romd.Persistence;
using Romd.Persistence.Identity;
using Serilog;

namespace Romd.Worker.Host;

/// <summary>Issues a recovery link using runtime credentials without starting worker services.</summary>
internal static class AdminRecoveryCommand
{
    internal const string Flag = "--recover-admin";
    internal const int Success = 0;
    internal const int Refused = 1;
    internal const int InvalidArguments = 2;
    internal const int Cancelled = 130;

    internal static bool IsRequested(string[] args) =>
        args.Any(argument => argument.StartsWith(Flag, StringComparison.OrdinalIgnoreCase));

    internal static bool TryParse(string[] args, out string email)
    {
        email = "";
        if (args.Length != 2 || args[0] != Flag || args[1].Length is < 3 or > 320 ||
            args[1].StartsWith('-') || args[1].Any(char.IsWhiteSpace) || args[1].Any(char.IsControl) ||
            !args[1].Contains('@'))
            return false;
        email = args[1];
        return true;
    }

    internal static Task<int> RunAsync(string[] args) =>
        RunAsync(args, Program.CreateAdminRecoveryHostBuilder, Console.Out, Console.Error);

    internal static async Task<int> RunAsync(string[] args, Func<IHostBuilder> hostBuilder,
        TextWriter output, TextWriter errors)
    {
        if (!TryParse(args, out string email))
        {
            await errors.WriteLineAsync($"Usage: {Flag} <existing-admin-email>");
            return InvalidArguments;
        }

        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            budget.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            using var host = hostBuilder().Build();
            await using var scope = host.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            bool production = services.GetRequiredService<IHostEnvironment>().IsProduction();
            string? origin = services.GetRequiredService<IConfiguration>()["Romd:Auth:AdminSpa:PostLogoutRedirectUris:0"];
            if (!TryGetOrigin(origin, production, out var adminOrigin))
            {
                await errors.WriteLineAsync("Configure Romd:Auth:AdminSpa:PostLogoutRedirectUris:0 as the exact admin portal origin (HTTPS in Production).");
                return Refused;
            }

            var recovery = services.GetRequiredService<LocalAdminRecovery>();
            return await RunCoreAsync(email, adminOrigin!, services.GetRequiredService<IRomdSchemaProbe>(),
                services.GetRequiredService<IUnitOfWork>(), production,
                recovery.GetRuntimeRoleAsync, recovery.IssueAsync,
                output, errors, budget.Token);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            await errors.WriteLineAsync("Administrator recovery cancelled or exceeded its one-minute deadline.");
            return Cancelled;
        }
        catch (Exception failure)
        {
            // Exception messages and configuration can contain database credentials or tokens.
            Log.Error("Administrator recovery failed ({FailureType})", failure.GetType().Name);
            await errors.WriteLineAsync($"Administrator recovery failed ({failure.GetType().Name}).");
            return Refused;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    internal static bool TryGetOrigin(string? value, bool production, out Uri? origin)
    {
        origin = null;
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || !Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != "https" && (production || parsed.Scheme != "http" || !parsed.IsLoopback)) ||
            parsed.UserInfo.Length != 0 || parsed.Query.Length != 0 || parsed.Fragment.Length != 0 ||
            parsed.AbsolutePath != "/")
            return false;
        origin = parsed;
        return true;
    }

    internal static async Task<int> RunCoreAsync(string email, Uri adminOrigin, IRomdSchemaProbe schema,
        IUnitOfWork unitOfWork, bool requireWorkerRole, Func<CancellationToken, Task<string>> runtimeRole,
        Func<string, CancellationToken, Task<ErrorOr<IssuedAccountLinkDto>>> issue,
        TextWriter output, TextWriter errors, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (requireWorkerRole && await runtimeRole(ct) != "romd_worker")
        {
            await errors.WriteLineAsync("Administrator recovery requires the romd_worker runtime database role in Production.");
            return Refused;
        }
        if (await schema.GetPublishedVersionAsync(ct) != PostgreSqlConfiguration.ExpectedSchemaVersion)
        {
            await errors.WriteLineAsync("Administrator recovery requires the worker-published application schema version.");
            return Refused;
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        var result = await issue(email, ct);
        ct.ThrowIfCancellationRequested();
        if (result.IsError)
        {
            foreach (var error in result.Errors)
                await errors.WriteLineAsync($"{error.Code}: {error.Description}");
            return Refused;
        }

        await transaction.CommitAsync(ct);
        // Only this explicit operator output receives the secret, after durable commit.
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            recoveryUrl = new Uri(adminOrigin, $"/activate#{result.Value.Token}").AbsoluteUri,
            expiresAt = result.Value.ExpiresAt
        }));
        return Success;
    }
}
