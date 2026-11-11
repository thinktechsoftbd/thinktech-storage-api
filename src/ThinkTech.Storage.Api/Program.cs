using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ThinkTech.Storage.Api.Authentication;
using ThinkTech.Storage.Api.Configuration;
using ThinkTech.Storage.Api.Storage;
using ThinkTech.Storage.Api.Tools;

// Admin commands (generate-client, generate-signing-key) run and exit without starting the web server.
if (CommandLineTools.TryRun(args, out var toolExitCode))
{
    return toolExitCode;
}

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

// appsettings.json holds local development settings and is not published. On the server, the complete
// configuration (signing key, client secret hashes, storage path, limits) lives in a file outside the
// deployment folder, e.g. /etc/thinktech-storage/appsettings.json. It is reloaded on change, so clients
// can be added or disabled without a restart.
var externalSettingsPath = Environment.GetEnvironmentVariable("THINKTECH_STORAGE_SETTINGS");
if (!string.IsNullOrWhiteSpace(externalSettingsPath))
{
    builder.Configuration.AddJsonFile(externalSettingsPath, optional: false, reloadOnChange: true);
    builder.Configuration.AddEnvironmentVariables(); // environment variables still win
    builder.Configuration.AddCommandLine(args);
}

builder.Services.AddOptions<JwtOptions>()
    .BindConfiguration(JwtOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<StorageOptions>()
    .BindConfiguration(StorageOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<StorageClientsOptions>()
    .BindConfiguration(StorageClientsOptions.SectionName)
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<StorageClientsOptions>, StorageClientsOptionsValidator>();

// ---------------------------------------------------------------------------
// Upload size limits (Kestrel + multipart), with headroom for multipart boundaries and form fields
// ---------------------------------------------------------------------------

const long multipartOverheadBytes = 1024 * 1024;
var maxFileSize = builder.Configuration
    .GetSection(StorageOptions.SectionName)
    .GetValue(nameof(StorageOptions.MaxFileSizeBytes), StorageOptions.DefaultMaxFileSizeBytes);
var maxRequestSize = maxFileSize + multipartOverheadBytes;

builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = maxRequestSize);
builder.Services.Configure<FormOptions>(form => form.MultipartBodyLengthLimit = maxRequestSize);

// ---------------------------------------------------------------------------
// Authentication: client id + client secret  ->  short-lived JWT bearer token
// ---------------------------------------------------------------------------

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ClientCredentialValidator>();
builder.Services.AddSingleton<TokenService>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((bearer, jwtOptions) =>
    {
        var jwt = jwtOptions.Value;

        bearer.MapInboundClaims = false;
        bearer.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = JwtKeys.CreateSigningKey(jwt.SigningKey),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = StorageClaimTypes.ClientId,
        };

        bearer.Events = new JwtBearerEvents
        {
            // A client that is removed or disabled in settings loses access immediately,
            // even if it still holds an unexpired token.
            OnTokenValidated = context =>
            {
                var clientId = context.Principal?.FindFirstValue(StorageClaimTypes.ClientId);
                var validator = context.HttpContext.RequestServices.GetRequiredService<ClientCredentialValidator>();
                if (clientId is null || validator.FindActiveClient(clientId) is null)
                {
                    context.Fail("The client is unknown or disabled.");
                }

                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorizationBuilder()
    // Secure by default: every endpoint needs a valid token unless it is explicitly [AllowAnonymous].
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build())
    .AddPolicy(StoragePolicies.Read, p => p.RequireAuthenticatedUser().RequireAssertion(c => c.User.HasScope(StorageScopes.Read)))
    .AddPolicy(StoragePolicies.Write, p => p.RequireAuthenticatedUser().RequireAssertion(c => c.User.HasScope(StorageScopes.Write)))
    .AddPolicy(StoragePolicies.Delete, p => p.RequireAuthenticatedUser().RequireAssertion(c => c.User.HasScope(StorageScopes.Delete)));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(RateLimitPolicies.Token, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = RateLimitPolicies.TokenRequestsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

// ---------------------------------------------------------------------------
// Storage, API and infrastructure
// ---------------------------------------------------------------------------

builder.Services.AddSingleton<LocalFileStorage>();
builder.Services.AddSingleton<IFileStorage>(sp => sp.GetRequiredService<LocalFileStorage>());

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddCheck<StorageHealthCheck>("storage");

// Behind nginx: trust X-Forwarded-For / X-Forwarded-Proto from the local reverse proxy only (loopback by default).
builder.Services.Configure<ForwardedHeadersOptions>(options =>
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

var app = builder.Build();

// Fail fast if the storage folder cannot be created, and warn when no client can log in yet.
var storage = app.Services.GetRequiredService<LocalFileStorage>();
app.Logger.LogInformation("Storing documents in {RootPath}", storage.RootPath);
if (app.Services.GetRequiredService<IOptionsMonitor<StorageClientsOptions>>().CurrentValue.Clients.Count == 0)
{
    app.Logger.LogWarning("No storage clients are configured. Create one with 'generate-client <client-id>'.");
}

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();

app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    await next(context);
});

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.MapControllers();
app.MapHealthChecks("/health").AllowAnonymous();

app.Run();
return 0;
