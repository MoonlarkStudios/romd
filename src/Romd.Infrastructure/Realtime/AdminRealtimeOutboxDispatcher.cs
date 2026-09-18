using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Realtime;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Models;
using Romd.Contracts.Management.Realtime;
using Romd.Domain.Libraries;
using Romd.Persistence.Realtime;

namespace Romd.Infrastructure.Realtime;

public sealed class AdminRealtimeOutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    ILogger<AdminRealtimeOutboxDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ActiveDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);
    private const int BatchSize = 50;

    public async Task<int> DispatchPendingAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IAdminRealtimeOutbox>();
        var sink = scope.ServiceProvider.GetRequiredService<IAdminRealtimeEventSink>();
        var messages = await outbox.ClaimPendingAsync(BatchSize, LeaseDuration, ct);

        foreach (var message in messages)
        {
            try
            {
                await DispatchAsync(sink, message, ct);
                await outbox.MarkProcessedAsync(message.Id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to dispatch admin realtime outbox event {EventId} ({EventType})",
                    message.Id,
                    message.EventType);

                await outbox.MarkFailedAsync(message.Id, ex.Message, RetryDelay, CancellationToken.None);
            }
        }

        return messages.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = IdleDelay;

            try
            {
                int dispatched = await DispatchPendingAsync(stoppingToken);
                delay = dispatched > 0 ? ActiveDelay : IdleDelay;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Admin realtime outbox dispatcher failed");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }

    private static Task DispatchAsync(
        IAdminRealtimeEventSink sink,
        AdminRealtimeOutboxMessage message,
        CancellationToken ct) =>
        message.EventType switch
        {
            AdminRealtimeEventTypes.JobUpdated => sink.SendJobUpdatedAsync(
                AdminRealtimePayloadSerializer.Deserialize<JobDto>(message.PayloadJson),
                message.SchemaVersion,
                ct),
            AdminRealtimeEventTypes.TitleEnriched => sink.SendTitleEnrichedAsync(
                AdminRealtimePayloadSerializer.Deserialize<AdminRealtimeTitleEnrichedOutboxPayload>(
                    message.PayloadJson),
                message.SchemaVersion,
                ct),
            AdminRealtimeEventTypes.LibraryUpdated => DispatchLibraryUpdatedAsync(sink, message, ct),
            AdminRealtimeEventTypes.StorageStatsChanged => sink.SendStorageChangedAsync(message.SchemaVersion, ct),
            AdminRealtimeEventTypes.CoverageStatsChanged => sink.SendCoverageChangedAsync(message.SchemaVersion, ct),
            AdminRealtimeEventTypes.HealthStatsChanged => sink.SendHealthChangedAsync(message.SchemaVersion, ct),
            _ => throw new InvalidOperationException($"Unknown admin realtime event type '{message.EventType}'.")
        };

    private static Task DispatchLibraryUpdatedAsync(
        IAdminRealtimeEventSink sink,
        AdminRealtimeOutboxMessage message,
        CancellationToken ct)
    {
        AdminRealtimeLibraryUpdatedPayload payload = message.SchemaVersion switch
        {
            AdminRealtimeSchemaVersions.Initial => TranslateLibraryUpdatedV1(message.PayloadJson),
            AdminRealtimeSchemaVersions.LibraryUpdatedOpaqueIdentity =>
                ValidateLibraryUpdatedV2(
                    AdminRealtimePayloadSerializer.Deserialize<AdminRealtimeLibraryUpdatedPayload>(
                        message.PayloadJson)),
            _ => throw new InvalidOperationException(
                $"Unsupported LibraryUpdated schema version '{message.SchemaVersion}'.")
        };

        // Version-1 rows are upgraded at the delivery boundary, so raw database
        // identity never crosses SignalR. A failed send remains unprocessed and
        // retries through the same deterministic translation.
        return sink.SendLibraryUpdatedAsync(
            payload,
            AdminRealtimeSchemaVersions.LibraryUpdatedOpaqueIdentity,
            ct);
    }

    private static AdminRealtimeLibraryUpdatedPayload TranslateLibraryUpdatedV1(string payloadJson)
    {
        var legacy = AdminRealtimePayloadSerializer.Deserialize<AdminRealtimeLibraryUpdatedV1Payload>(
            payloadJson);
        ValidateLibraryUpdatedV1(legacy);
        return new AdminRealtimeLibraryUpdatedPayload(
            IdCoder.Encode(legacy.LibraryId),
            legacy.Name,
            legacy.NeedsMaterialization,
            legacy.ItemCount,
            legacy.ConfigurationState);
    }

    private static void ValidateLibraryUpdatedV1(AdminRealtimeLibraryUpdatedV1Payload payload)
    {
        if (payload.LibraryId <= 0)
        {
            throw new JsonException("LibraryUpdated version-1 LibraryId must be positive.");
        }

        ValidateLibraryUpdatedFacts(payload.Name, payload.ItemCount, payload.ConfigurationState);
    }

    private static AdminRealtimeLibraryUpdatedPayload ValidateLibraryUpdatedV2(
        AdminRealtimeLibraryUpdatedPayload payload)
    {
        if (!IdCoder.TryDecode(payload.LibraryId, out int libraryId)
            || !string.Equals(payload.LibraryId, IdCoder.Encode(libraryId), StringComparison.Ordinal))
        {
            throw new JsonException("LibraryUpdated version-2 LibraryId must be a canonical Sqid.");
        }

        ValidateLibraryUpdatedFacts(payload.Name, payload.ItemCount, payload.ConfigurationState);
        return payload;
    }

    private static void ValidateLibraryUpdatedFacts(string name, int itemCount, string configurationState)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new JsonException("LibraryUpdated Name must not be blank.");
        }

        if (itemCount < 0)
        {
            throw new JsonException("LibraryUpdated ItemCount must not be negative.");
        }

        if (configurationState is not (nameof(LibraryConfigurationState.Valid)
            or nameof(LibraryConfigurationState.Invalid)
            or nameof(LibraryConfigurationState.RequiresMigration)))
        {
            throw new JsonException(
                "LibraryUpdated ConfigurationState must be Valid, Invalid, or RequiresMigration.");
        }
    }
}
