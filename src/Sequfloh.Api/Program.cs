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
using Sequfloh.Infrastructure.Modules.Messages;
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
builder.Services.AddScoped<RecordNumberService>();
builder.Services.AddScoped<INotifier, Notifier>();
builder.Services.AddScoped<ModuleSupport>();
builder.Services.AddScoped<MasterDataService>();
builder.Services.AddScoped<SequflohFoundationService>();
builder.Services.AddScoped<SequflohPartsService>();
builder.Services.AddScoped<SequflohPartsImport>();
builder.Services.AddScoped<PartFamilyChangeService>();
builder.Services.AddScoped<PartCustomerMoveService>();
builder.Services.AddScoped<CustomerAssignmentService>();
builder.Services.AddScoped<CustomDropdownService>();
builder.Services.AddScoped<SequflohInspectionService>();
builder.Services.AddScoped<SequflohProjectService>();
builder.Services.AddScoped<SequflohEightDService>();
builder.Services.AddScoped<SequflohInstructionService>();
builder.Services.AddScoped<SequflohTemplateService>();
builder.Services.AddScoped<SequflohPrintService>();
builder.Services.AddScoped<SequflohJobService>();
builder.Services.AddScoped<SequflohTestResultService>();
builder.Services.AddScoped<SequflohCrewService>();
builder.Services.AddScoped<SequflohTimeCorrectionService>();
builder.Services.AddScoped<SequflohChargebackService>();
builder.Services.AddScoped<SequflohTrackingService>();
builder.Services.AddScoped<SequflohNotifications>();
builder.Services.AddScoped<SequflohReportService>();
builder.Services.AddScoped<MonthlyReportService>();
builder.Services.AddScoped<MessageService>();
builder.Services.AddScoped<Seeder>();
builder.Services.AddSingleton<Sequfloh.Imports.ILegacyOfficeConverter, Sequfloh.Imports.HeadlessOfficeConverter>();
builder.Services.AddScoped<Sequfloh.Imports.ImportedFileService>();
builder.Services.AddScoped<Sequfloh.Drawings.DrawingService>();
builder.Services.AddScoped<Sequfloh.Drawings.DrawingImportService>();
builder.Services.AddScoped<Sequfloh.Drawings.DrawingPartMatcher>();
builder.Services.AddScoped<Sequfloh.Deletion.DeletionService>();
builder.Services.AddScoped<Sequfloh.Tracking.SequflohDayClockService>();
builder.Services.AddScoped<Sequfloh.Tracking.SequflohTimeService>();
builder.Services.AddHostedService<DayClockSweepHostedService>();
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
            var u = await db.Users.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.IsActive, x.TokenVersion, x.MustChangePassword }).FirstOrDefaultAsync();
            if (u is null || !u.IsActive || u.TokenVersion.ToString() != ctx.Principal!.FindFirstValue("tv")) ctx.Fail("session ended");
            else if (u.MustChangePassword) ctx.HttpContext.Items[PasswordChange.Flag] = true;
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
// API answers (a filled form's grid can be over a megabyte) are compressed; files and pages are left as they are
builder.Services.AddResponseCompression(o => { o.EnableForHttps = true; o.MimeTypes = ["application/json"]; o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>(); o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>(); });
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ErrorHandler>();

var app = builder.Build();

if (args.Length > 0 && args[0] == "migrate") { await MigrateAsync(app); return; }
if (cfg.GetValue("Database:MigrateOnStartup", true)) await MigrateAsync(app);
await app.Services.GetRequiredService<IFileStorage>().EnsureReadyAsync(CancellationToken.None);
// forms saved before their first sheet was stored get it now, in the background, so their first open is quick
if (!app.Environment.IsEnvironment("Testing"))
    app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), app.Lifetime.ApplicationStopping); await Sequfloh.Imports.ImportedFileService.PrepareStoredAsync(app.Services, app.Lifetime.ApplicationStopping); }
        catch (Exception e) when (e is not OperationCanceledException) { app.Logger.LogError(e, "Preparing stored sheets stopped"); }
    }));

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing")) app.UseHsts();
app.UseExceptionHandler();
app.UseResponseCompression();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "same-origin";
    await next();
});
// index.html is checked with the server on every load so a normal refresh picks up the new hashed files after an update
var pageFiles = new StaticFileOptions { OnPrepareResponse = c => { if (c.File.Name == "index.html") c.Context.Response.Headers.CacheControl = "no-cache"; } };
app.UseDefaultFiles();
app.UseStaticFiles(pageFiles);
app.UseRateLimiter();
app.UseAuthentication();
// a person told to change their password can only use the sign-in endpoints, and the open ones the sign-in
// and change-password screens use (such as the organization branding), until they do
app.Use(async (ctx, next) =>
{
    if (ctx.Items.ContainsKey(PasswordChange.Flag) && ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.StartsWithSegments("/api/auth")
        && ctx.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is null)
    {
        ctx.Response.StatusCode = 403;
        await ctx.Response.WriteAsJsonAsync(new { title = "Change your password to continue.", code = PasswordChange.Code });
        return;
    }
    await next();
});
app.UseAuthorization();
// a signed-in person's activity updates last-seen, at most once a minute, so avatar dots stay honest without a write on every call
app.Use(async (ctx, next) =>
{
    if (ctx.User.Identity?.IsAuthenticated == true
        && Guid.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        && ctx.Request.Path.StartsWithSegments("/api")
        && !(HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Path.StartsWithSegments("/api/presence"))
        && Sequfloh.Platform.Presence.Due(id))
    {
        var db = ctx.RequestServices.GetRequiredService<AppDbContext>();
        await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenAt, DateTimeOffset.UtcNow));
    }
    await next();
});
// health check for the hosting platform; it does not touch the database so a sleeping database is not woken for nothing
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
// the running build, so signed-in pages can offer a refresh after an update; open and light like the health check
var build = BuildVersion.Current(app.Configuration);
app.MapGet("/api/version", (HttpContext c) => { c.Response.Headers.CacheControl = "no-store"; return Results.Ok(new { version = build }); }).AllowAnonymous();
app.MapControllers();
app.MapFallback("/api/{**rest}", () => Results.NotFound()).AllowAnonymous();
app.MapFallbackToFile("index.html", pageFiles).AllowAnonymous();
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

static class PasswordChange
{
    public const string Flag = "sqf.mustChangePassword";
    public const string Code = "PasswordChangeRequired";
}
