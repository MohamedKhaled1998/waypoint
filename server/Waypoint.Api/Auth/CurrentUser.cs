using System.Security.Claims;

namespace Waypoint.Api.Auth;

/// <summary>The signed-in user of the current request; null id outside an authenticated request.</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor)
{
    /// <summary>Set by the job runner, which acts for the job's user outside any request.</summary>
    public Guid? Override { get; set; }

    public Guid? Id => Override ?? FromRequest;

    private Guid? FromRequest =>
        Guid.TryParse(accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
