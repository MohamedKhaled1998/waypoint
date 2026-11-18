using System.Security.Claims;

namespace Waypoint.Api.Auth;

/// <summary>The signed-in user of the current request; null id outside an authenticated request.</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor)
{
    public Guid? Id =>
        Guid.TryParse(accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
