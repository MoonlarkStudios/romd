namespace Romd.Application.Common.Security;

public interface IAuditContext
{
    Guid ActorId { get; }
    bool IsSystem { get; }
}
