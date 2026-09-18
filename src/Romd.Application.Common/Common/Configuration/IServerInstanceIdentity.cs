namespace Romd.Application.Common.Configuration;

/// <summary>
///     Provides the stable public namespace owned by one ROMD data directory.
///     This identifier is not a secret and does not authenticate a server.
/// </summary>
public interface IServerInstanceIdentity
{
    Guid InstanceId { get; }
}
