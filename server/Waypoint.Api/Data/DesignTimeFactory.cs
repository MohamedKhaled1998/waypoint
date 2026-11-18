using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Waypoint.Api.Auth;

namespace Waypoint.Api.Data;

/// <summary>Lets `dotnet ef migrations add` build the model without a database or the running host.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<WaypointDb>
{
    public WaypointDb CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<WaypointDb>()
            .UseMySql("Server=localhost;Database=waypoint", new MySqlServerVersion(new Version(8, 0, 36))).Options,
        new CurrentUser(new HttpContextAccessor()));
}
