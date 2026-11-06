using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Romd.Application.Common.Security;
using Romd.Domain.Identity;

namespace Romd.Infrastructure.Identity;

/// <summary>
///     Reads the actor ID directly from HttpContext claims to avoid a circular
///     dependency through ICurrentUser → RomdDbContext → AuditInterceptor → IAuditContext.
/// </summary>
public sealed class HttpAuditContext(IHttpContextAccessor httpContextAccessor) : IAuditContext
{
    public Guid ActorId
    {
        get
        {
            var claim = httpContextAccessor.HttpContext?.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? httpContextAccessor.HttpContext?.User.FindFirst("sub")?.Value;
            return Guid.TryParse(claim, out var userId) ? userId : SystemActor.UserId;
        }
    }

    public bool IsSystem => ActorId == SystemActor.UserId;
}
