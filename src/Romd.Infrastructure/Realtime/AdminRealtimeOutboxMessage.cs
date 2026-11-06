namespace Romd.Infrastructure.Realtime;

public sealed record AdminRealtimeOutboxMessage(
    int Id,
    string EventType,
    string PayloadJson,
    int SchemaVersion,
    int Attempts);
