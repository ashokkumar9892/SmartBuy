using System.Globalization;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using SmartBuy.Api.Infrastructure;
using SmartBuy.Infrastructure;
using SmartBuy.Infrastructure.Persistence;

// Money and dates are formatted explicitly where shown to users; everything else must not depend on the server's locale.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

// One-shot tool: `--export-demo-data <file>` writes the demo catalog for the Netlify-hosted search function and exits.
var exportIndex = Array.IndexOf(args, "--export-demo-data");
if (exportIndex >= 0 && exportIndex + 1 < args.Length)
{
    await SmartBuy.Infrastructure.Services.DemoDataExport.WriteAsync(args[exportIndex + 1]);
    Console.WriteLine($"Demo data written to {args[exportIndex + 1]}");
    return 0;
}

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

builder.Services.Configure<JwtOptions>(configuration.GetSection("Jwt"));
builder.Services.Configure<GoogleOptions>(configuration.GetSection("Google"));
builder.Services.Configure<FacebookOptions>(configuration.GetSection("Facebook"));
var jwt = configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwt.Key) || jwt.Key.Length < 32)
    throw new InvalidOperationException("Jwt:Key must be set to a random secret of at least 32 characters (environment variable Jwt__Key).");
if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing") && jwt.Key == JwtOptions.DevelopmentKey)
    throw new InvalidOperationException("The development JWT signing key must not be used outside Development. Set Jwt__Key.");

builder.Services.AddSmartBuyInfrastructure(configuration);
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<FeatureFlagService>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = jwt.Issuer,
        ValidAudience = jwt.Audience,
        IssuerSigningKey = jwt.SigningKey,
        ValidateIssuerSigningKey = true,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = JwtRegisteredClaimNames.Sub,
    };
});
builder.Services.AddAuthorization();

var allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    // Explicit origins only. Tokens travel in the Authorization header, so credentials (cookies) are never allowed.
    policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("Content-Disposition");
}));

var rateLimits = configuration.GetSection("RateLimits");
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await context.HttpContext.Response.WriteAsJsonAsync(new ProblemDetails { Status = 429, Title = "Too many requests. Wait a minute and try again." }, ct);
    };
    // Keyed by user and address together: demo accounts are shared by many visitors, and one of them must not be able
    // to exhaust the allowance for the rest.
    static string Client(HttpContext http) => $"{http.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value}|{http.Connection.RemoteIpAddress}";
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http => RateLimitPartition.GetFixedWindowLimiter(Client(http),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = rateLimits.GetValue("GlobalPerMinute", 300), Window = TimeSpan.FromMinutes(1) }));
    // Sign-in endpoints are limited per IP address, independent of any identity the caller claims.
    options.AddPolicy("auth", http => RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = rateLimits.GetValue("AuthPerMinute", 10), Window = TimeSpan.FromMinutes(1) }));
    options.AddPolicy("ai", http => RateLimitPartition.GetFixedWindowLimiter(Client(http),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = rateLimits.GetValue("AiPerMinute", 20), Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
    })
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
        {
            var first = context.ModelState.Where(e => e.Value?.Errors.Count > 0).Select(e => e.Value!.Errors[0].ErrorMessage).FirstOrDefault();
            var problem = new ValidationProblemDetails(context.ModelState) { Status = 400, Title = string.IsNullOrWhiteSpace(first) ? "The request is not valid." : first };
            problem.Extensions["code"] = "validation";
            return new BadRequestObjectResult(problem);
        };
    });
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 6 * 1024 * 1024);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 6 * 1024 * 1024);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "FindMyPrice API",
        Version = "v1",
        Description = "Procurement comparison and optimization API. Send the access token from /api/auth/login as a Bearer token.",
    });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", Description = "Access token from /api/auth/login" });
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = new List<string>() });
    options.CustomSchemaIds(type => type.FullName?.Replace("+", ".").Replace("FindMyPrice.", ""));
});

builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database", tags: new[] { "ready" });
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Behind IIS or a reverse proxy on the same machine. List other trusted proxies in configuration when needed.
});

var app = builder.Build();

await app.Services.InitializeDatabaseAsync();

// One-shot job mode for Windows Task Scheduler / cron: `SmartBuy.Api.exe --run-job price-alerts`
var jobIndex = Array.IndexOf(args, "--run-job");
if (jobIndex >= 0)
{
    var job = jobIndex + 1 < args.Length ? args[jobIndex + 1] : "";
    if (job != "price-alerts")
    {
        Console.Error.WriteLine($"Unknown job '{job}'. Available jobs: price-alerts");
        return 2;
    }
    var result = await PriceAlertScheduler.RunOnceAsync(app.Services.GetRequiredService<IServiceScopeFactory>(), null, CancellationToken.None);
    Console.WriteLine($"price-alerts: checked {result.AlertsChecked}, triggered {result.AlertsTriggered}, supplier issues {result.ProviderIssues}");
    return 0;
}

app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers.XFrameOptions = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
    if (!context.Request.Path.StartsWithSegments("/swagger"))
        headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
    await next();
});
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
    app.UseHsts();

app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment() || configuration.GetValue("Swagger:Enabled", false))
{
    app.UseSwagger();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "FindMyPrice API v1"));
}

app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new() { Predicate = _ => false }).DisableRateLimiting();
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") }).DisableRateLimiting();
app.MapGet("/", () => Results.Json(new { name = "FindMyPrice API", status = "ok", docs = "/swagger" })).ExcludeFromDescription();
app.MapControllers();

await app.RunAsync();
return 0;

public partial class Program;
