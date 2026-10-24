using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Sequfloh.Api.Auth;
using Sequfloh.Api.Infrastructure;
using Sequfloh.Application.Abstractions;
using Sequfloh.Application.Records;
using Sequfloh.Application.Security;
using Sequfloh.Infrastructure.Identity;
using Sequfloh.Infrastructure.Modules.MasterData;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Infrastructure.Security;
using Sequfloh.Infrastructure.Services;
using Sequfloh.Platform;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;
// hosting platforms read the console; keep it on in every environment with UTC timestamps
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.TimestampFormat = "yyyy-MM-dd HH:mm:ss 'UTC' "; o.UseUtcTimestamp = true; o.IncludeScopes = false; });
builder.Logging.AddFilter("Sequfloh", LogLevel.Information);
builder.Logging.AddFilter("Sequfloh.Api.Infrastructure.ErrorHandler", LogLevel.Information);

// Hosting platforms pass settings as plain environment variables; map them onto the configuration keys the app reads.
static void Map(IConfiguration c, Dictionary<string, string?> into, string env, string key) { if (c[env] is { Length: > 0 } v) into[key] = v; }
var mapped = new Dictionary<string, string?>();
Map(cfg, mapped, "DATABASE_URL", "ConnectionStrings:Default");
Map(cfg, mapped, "JWT_SECRET", "Auth:JwtSecret");
Map(cfg, mapped, "STORAGE_PROVIDER", "Storage:Provider");
Map(cfg, mapped, "STORAGE_LOCAL_PATH", "Storage:LocalPath");
Map(cfg, mapped, "STORAGE_SERVICE_URL", "Storage:ServiceUrl");
Map(cfg, mapped, "STORAGE_BUCKET", "Storage:Bucket");
Map(cfg, mapped, "STORAGE_ACCESS_KEY", "Storage:AccessKey");
Map(cfg, mapped, "STORAGE_SECRET_KEY", "Storage:SecretKey");
if (mapped.Count > 0) cfg.AddInMemoryCollection(mapped);
if (cfg["PORT"] is { Length: > 0 } port && cfg["ASPNETCORE_URLS"] is null) builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var connection = cfg.GetConnectionString("Default") ?? throw new InvalidOperationException("Set DATABASE_URL (or ConnectionStrings:Default) to the PostgreSQL connection string.");
var auth = cfg.GetSection("Auth").Get<AuthSettings>() ?? new AuthSettings();
if (auth.JwtSecret.Length < 32)
{
    if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
        throw new InvalidOperationException("Set JWT_SECRET to a random value of at least 32 characters.");
    auth.JwtSecret = "local-development-only-signing-secret-0123456789";
}
var storage = S3FileStorage.Clean(cfg.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions());

builder.Services.AddSingleton(auth);
builder.Services.AddSingleton(storage);
builder.Services.AddSingleton<IFileStorage>(sp => storage.Provider.Equals("s3", StringComparison.OrdinalIgnoreCase)
    ? new S3FileStorage(storage, sp.GetRequiredService<ILoggerFactory>().CreateLogger<S3FileStorage>()) : new LocalFileStorage(storage));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddScoped<AuditInterceptor>();
builder.Services.AddDbContext<AppDbContext>((sp, o) => o.UseNpgsql(ConnectionStrings.Normalize(connection), n => n.MigrationsHistoryTable("__ef_migrations", AppDbContext.Schema))
    .AddInterceptors(sp.GetRequiredService<AuditInterceptor>()));
builder.Services.AddScoped<IAccessService, AccessService>();
builder.Services.AddScoped<INumberingService, NumberingService>();
builder.Services.AddScoped<INotifier, Notifier>();
builder.Services.AddScoped<ModuleSupport>();
builder.Services.AddScoped<MasterDataService>();
builder.Services.AddScoped<SequflohFoundationService>();
builder.Services.AddScoped<SequflohPartsService>();
builder.Services.AddScoped<SequflohPartsImport>();
builder.Services.AddScoped<SequflohInspectionService>();
builder.Services.AddScoped<SequflohInstructionService>();
builder.Services.AddScoped<SequflohTemplateService>();
builder.Services.AddScoped<SequflohPrintService>();
builder.Services.AddScoped<SequflohJobService>();
builder.Services.AddScoped<SequflohTrackingService>();
builder.Services.AddScoped<SequflohNotifications>();
builder.Services.AddScoped<SequflohReportService>();
builder.Services.AddScoped<Seeder>();
builder.Services.AddSingleton<Sequfloh.Imports.ILegacyOfficeConverter, Sequfloh.Imports.HeadlessOfficeConverter>();
builder.Services.AddScoped<Sequfloh.Imports.ImportedFileService>();
builder.Services.AddScoped<Sequfloh.Drawings.DrawingService>();
builder.Services.AddScoped<Sequfloh.Drawings.DrawingImportService>();
builder.Services.AddScoped<Sequfloh.Drawings.DrawingPartMatcher>();
builder.Services.AddScoped<Sequfloh.Deletion.DeletionService>();
builder.Services.AddScoped<Sequfloh.Tracking.SequflohTimeService>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<IPasswordHasher<ApplicationUser>, PasswordHasher<ApplicationUser>>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = AuthSettings.Issuer, ValidAudience = AuthSettings.Issuer, IssuerSigningKey = auth.Key, ClockSkew = TimeSpan.FromMinutes(1),
        NameClaimType = ClaimTypes.Name,
    };
    o.Events = new JwtBearerEvents
    {
        // browsers send the session in an HttpOnly cookie; API clients may send "Authorization: Bearer <token>"
        OnMessageReceived = ctx => { if (string.IsNullOrEmpty(ctx.Token) && ctx.Request.Cookies.TryGetValue(AuthSettings.CookieName, out var t)) ctx.Token = t; return Task.CompletedTask; },
        // a deactivated account or a changed password ends every earlier session
        OnTokenValidated = async ctx =>
        {
            var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
            if (!Guid.TryParse(ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) { ctx.Fail("bad token"); return; }
            var u = await db.Users.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.IsActive, x.TokenVersion }).FirstOrDefaultAsync();
            if (u is null || !u.IsActive || u.TokenVersion.ToString() != ctx.Principal!.FindFirstValue("tv")) ctx.Fail("session ended");
        },
    };
});
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionHandler>();
builder.Services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = cfg.GetValue("RateLimits:LoginPerMinute", 10), Window = TimeSpan.FromMinutes(1) }));
});
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // the hosting platform's load balancer is the only thing in front of the app
    o.KnownIPNetworks.Clear(); o.KnownProxies.Clear();
});
builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ErrorHandler>();

var app = builder.Build();

if (args.Length > 0 && args[0] == "migrate") { await MigrateAsync(app); return; }
if (cfg.GetValue("Database:MigrateOnStartup", true)) await MigrateAsync(app);
await app.Services.GetRequiredService<IFileStorage>().EnsureReadyAsync(CancellationToken.None);

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "same-origin";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
// health check for the hosting platform; it does not touch the database so a sleeping database is not woken for nothing
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapControllers();
app.MapFallback("/api/{**rest}", () => Results.NotFound()).AllowAnonymous();
app.MapFallbackToFile("index.html").AllowAnonymous();
app.Run();

static async Task MigrateAsync(WebApplication app)
{
    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<Seeder>().SeedAsync();
            return;
        }
        catch (Exception e) when (attempt < 6 && e is Npgsql.NpgsqlException or TimeoutException or System.Net.Sockets.SocketException)
        {
            // a free database can take a few seconds to wake up after a quiet period
            log.LogWarning("Database not reachable yet (attempt {Attempt}): {Message}", attempt, e.Message);
            await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
        }
    }
}

public partial class Program;
