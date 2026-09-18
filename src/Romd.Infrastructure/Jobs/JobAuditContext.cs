using Romd.Application.Common.Security;
using Romd.Domain.Identity;

namespace Romd.Infrastructure.Jobs;

public sealed class JobAuditContext : IAuditContext
{
    private Guid _actorId = SystemActor.UserId;

    public Guid ActorId => _actorId;
    public bool IsSystem => _actorId == SystemActor.UserId;

    public void SetActor(Guid? userId) => _actorId = userId ?? SystemActor.UserId;
}
