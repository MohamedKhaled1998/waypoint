using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Waypoint.Api.Auth;
using Waypoint.Api.Coach;
using Waypoint.Api.Data;
using Waypoint.Api.Push;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<GoogleOptions>().Bind(builder.Configuration.GetSection("Google"))
    .PostConfigure(o => o.ClientIds = o.ClientIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray())
    .Validate(o => o.ClientIds.Length > 0, "Google:ClientIds must list at least one client ID.")
    .ValidateOnStart();
builder.Services.AddSingleton<IGoogleTokenVerifier, GoogleTokenVerifier>();

builder.Services.Configure<CoachOptions>(builder.Configuration.GetSection("Coach"));
builder.Services.Configure<AnthropicOptions>(builder.Configuration.GetSection("Anthropic"));
builder.Services.Configure<ClaudeCliOptions>(builder.Configuration.GetSection("ClaudeCli"));
// Development only: Coach calls go through the local `claude` CLI on the developer's subscription.
if (builder.Configuration.GetValue<bool>("ClaudeCli:Enabled"))
    builder.Services.AddSingleton<ICoachModelClient, ClaudeCliCoachModelClient>();
else
    builder.Services.AddHttpClient<ICoachModelClient, AnthropicCoachModelClient>(c => c.Timeout = TimeSpan.FromMinutes(2));

builder.Services.AddOptions<FirebaseOptions>().Bind(builder.Configuration.GetSection("Firebase"))
    .Validate(o => !string.IsNullOrWhiteSpace(o.ServiceAccountFile) && CanRead(o.ServiceAccountFile),
        "Firebase:ServiceAccountFile must name a readable service-account JSON file.")
    .ValidateOnStart();
builder.Services.AddSingleton<IPushSender, FirebasePushSender>();
builder.Services.AddScoped<PushNotifier>();
builder.Services.AddScoped<CoachMessenger>();
builder.Services.AddScoped<ContactGate>();

builder.Services.AddScoped<JobQueue>();
builder.Services.AddScoped<ProgressCalculator>();
builder.Services.AddScoped<PlanDrafter>();
builder.Services.AddScoped<ReminderWriter>();
builder.Services.AddScoped<GuideWriter>();
builder.Services.AddScoped<WeeklyRefreshRunner>();
builder.Services.AddScoped<RefreshApplier>();
builder.Services.AddScoped<CheckInReplies>();
builder.Services.AddScoped<Rethinks>();
builder.Services.AddScoped<WelcomeBackWriter>();
builder.Services.AddScoped<SilenceChecker>();
builder.Services.AddScoped<MilestoneService>();
builder.Services.AddScoped<MilestoneNotes>();
builder.Services.AddScoped<MeasureNotes>();
builder.Services.AddScoped<MeasureStore>();
builder.Services.AddSingleton<JobRunner>();
if (builder.Configuration.GetValue("Jobs:Worker:Enabled", true)) builder.Services.AddHostedService<JobWorker>();

builder.Services.AddDbContext<WaypointDb>(o => o.UseMySql(
    builder.Configuration.GetConnectionString("Waypoint"),
    new MySqlServerVersion(new Version(8, 0, 36))));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<TimeProvider, IConfiguration>((o, clock, config) =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            IssuerSigningKey = AuthDefaults.SigningKey(config["Jwt:Key"]
                ?? throw new InvalidOperationException("Jwt:Key is not configured.")),
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            LifetimeValidator = (nbf, exp, _, _) =>
                exp is not null && exp > clock.GetUtcNow().UtcDateTime && (nbf is null || nbf <= clock.GetUtcNow().UtcDateTime),
            NameClaimType = ClaimTypes.NameIdentifier,
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<WaypointDb>().Database.MigrateAsync();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapPost("/auth/google", async (SignInRequest req, IGoogleTokenVerifier google, TokenService tokens, WaypointDb db, CancellationToken ct) =>
{
    var identity = await google.VerifyAsync(req.IdToken ?? "", ct);
    if (identity is null) return Results.Unauthorized();
    var pair = await tokens.SignInAsync(identity, ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(pair);
});

app.MapPost("/auth/refresh", async (RefreshRequest req, TokenService tokens, WaypointDb db, CancellationToken ct) =>
{
    var pair = await tokens.RefreshAsync(req.RefreshToken ?? "", ct);
    if (pair is null) return Results.Unauthorized();
    await db.SaveChangesAsync(ct);
    return Results.Ok(pair);
});

app.MapPost("/auth/sign-out", async (RefreshRequest req, TokenService tokens, CancellationToken ct) =>
{
    await tokens.SignOutAsync(req.RefreshToken ?? "", ct);
    return Results.NoContent();
});

app.MapGet("/me", async (WaypointDb db, CancellationToken ct) =>
    (await db.Users.Select(u => new { u.Id }).SingleOrDefaultAsync(ct)) is { } me
        ? Results.Ok(me)
        : Results.Unauthorized()).RequireAuthorization();

app.MapGet("/me/sessions", async (TimeProvider clock, WaypointDb db, CancellationToken ct) =>
{
    var now = clock.GetUtcNow().UtcDateTime;
    return Results.Ok(await db.RefreshTokens
        .Where(t => t.RevokedAt == null && t.ExpiresAt > now)
        .Select(t => new { t.Id }).ToListAsync(ct));
}).RequireAuthorization();

app.MapCoachChat();
app.MapInterviews();
app.MapPlans();
app.MapWeeklyRefreshes();
app.MapProgress();
app.MapAspirationProgress();
app.MapAspirationLifecycle();
app.MapMilestones();
app.MapMeasures();
app.MapCheckIns();
app.MapMakeUps();
app.MapSync();
app.MapPushes();
app.MapPauses();
app.MapSettings();
app.MapMyData();

app.Run();

static bool CanRead(string path)
{
    try { using var f = File.OpenRead(path); return true; }
    catch (Exception) { return false; }
}

record SignInRequest(string? IdToken);
record RefreshRequest(string? RefreshToken);

public partial class Program;
