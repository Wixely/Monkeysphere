using System.Net;
using System.Threading.RateLimiting;
using DnaX.Data.Migrations;
using DnaX.Hosting;
using DnaX.RemoteAccess;
using DnaX.RemoteAccess.Mcp;
using DnaX.RemoteAccess.Sqlite;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Monkeysphere.Data;
using Monkeysphere.Web.Components;
using Monkeysphere.Web.Remote;
using Monkeysphere.Web.Security;
using Monkeysphere.Web;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService(options => options.ServiceName = "Monkeysphere");
builder.Host.UseSystemd();

builder.Services.AddDnaXHosting();
builder.Services.AddOptions<DnaXPathOptions>()
    .Configure<IConfiguration>((options, configuration) =>
        options.WritableDataRoot = configuration["MONKEYSPHERE_DATA_ROOT"] ?? "data");
builder.Services.AddDataProtection()
    .SetApplicationName("Monkeysphere");
builder.Services.AddOptions<KeyManagementOptions>()
    .Configure<IConfiguration, IHostEnvironment, ILoggerFactory>((options, configuration, environment, loggerFactory) =>
    {
        string dataRoot = ResolveDataRoot(configuration, environment);
        DirectoryInfo keyDirectory = Directory.CreateDirectory(Path.Combine(dataRoot, "keys"));
        options.XmlRepository = new FileSystemXmlRepository(keyDirectory, loggerFactory);
    });
builder.Services.AddMonkeysphereData();

builder.Services.AddSingleton(provider => AdministratorCredential.Load(provider.GetRequiredService<IConfiguration>()));
builder.Services.AddScoped<AbsoluteSessionCookieEvents>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        // A __Host- prefixed cookie is always required to be Secure, while trusted
        // local deployments deliberately support plain HTTP. SameAsRequest makes
        // the cookie Secure as soon as the trusted proxy reports HTTPS.
        options.Cookie.Name = "Monkeysphere.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.IsEssential = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
        options.SlidingExpiration = true;
        options.EventsType = typeof(AbsoluteSessionCookieEvents);
    });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<MonkeysphereRemoteQueries>();
builder.Services.AddDnaXRemoteAccess(builder.Configuration.GetSection("DnaX:RemoteAccess"));
builder.Services.AddDnaXRemoteAccessSqlite("RemoteAccess", provider =>
{
    string path = provider.GetRequiredService<IDnaXPaths>().ResolveWritable("remote-access.db");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    SqliteConnectionStringBuilder connectionString = new()
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = true,
        ForeignKeys = true,
        DefaultTimeout = 30,
    };
    return new SqliteConnection(connectionString.ConnectionString);
});
builder.Services.AddDnaXRemoteMcp().WithTools<MonkeysphereRemoteTools>();

string[] trustedProxyValues = (builder.Configuration["MONKEYSPHERE_TRUSTED_PROXIES"] ?? string.Empty)
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
IPAddress[] trustedProxies = trustedProxyValues.Select(value =>
    IPAddress.TryParse(value, out IPAddress? address)
        ? address
        : throw new InvalidOperationException("MONKEYSPHERE_TRUSTED_PROXIES contains an invalid IP address."))
    .ToArray();
if (trustedProxies.Length > 0)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.RequireHeaderSymmetry = true;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (IPAddress address in trustedProxies)
        {
            options.KnownProxies.Add(address);
        }
    });
}

WebApplication app = builder.Build();

_ = app.Services.GetRequiredService<AdministratorCredential>();
await app.Services.MigrateDnaXDatabaseAsync(MonkeysphereDataExtensions.DatabaseName);
await app.Services.MigrateDnaXDatabaseAsync("RemoteAccess");

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

if (trustedProxies.Length > 0)
{
    app.UseForwardedHeaders();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAuthentication();
app.UseDnaXRemoteAccess();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" })).AllowAnonymous();
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" })).AllowAnonymous();
app.MapAdministratorAuthentication();
app.MapRecordImages();
app.MapMonkeysphereRemoteApi();
app.MapDnaXRemoteMcp();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static string ResolveDataRoot(IConfiguration configuration, IHostEnvironment environment)
{
    string configured = configuration["MONKEYSPHERE_DATA_ROOT"] ?? "data";
    return Path.IsPathRooted(configured)
        ? Path.GetFullPath(configured)
        : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configured));
}

public partial class Program;
