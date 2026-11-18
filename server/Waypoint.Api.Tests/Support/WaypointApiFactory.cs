using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.MySql;
using Waypoint.Api.Auth;
using Waypoint.Api.Coach;

namespace Waypoint.Api.Tests.Support;

/// <summary>The API in-process against MySQL in Testcontainers, with a fake clock and a fake Google verifier.</summary>
public sealed class WaypointApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MySqlContainer _mysql = new MySqlBuilder("mysql:8.0").Build();

    public FakeCoachModelClient Coach { get; } = new();
    public CapturedLogs Logs { get; } = new();

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero));

    public async Task InitializeAsync() => await _mysql.StartAsync();

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync().AsTask();
        await _mysql.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Waypoint", _mysql.GetConnectionString());
        builder.UseSetting("Jwt:Key", "test-signing-key-test-signing-key-0123456789");
        builder.UseSetting("Google:ClientIds:0", "test-client-id");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IGoogleTokenVerifier>();
            services.AddSingleton<IGoogleTokenVerifier, FakeGoogleVerifier>();
            services.RemoveAll<ICoachModelClient>();
            services.AddSingleton<ICoachModelClient>(Coach);
            services.AddSingleton<ILoggerProvider>(Logs);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }
}
