using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SmartBuy.Domain.Entities;
using SmartBuy.Infrastructure.Persistence;
using SmartBuy.Infrastructure.Services;

namespace SmartBuy.Api.Infrastructure;

public sealed class JwtOptions
{
    public string Key { get; set; } = "";
    public string Issuer { get; set; } = "smartbuy-api";
    public string Audience { get; set; } = "smartbuy-web";
    public int AccessTokenMinutes { get; set; } = 30;
    public int RefreshTokenDays { get; set; } = 14;

    public const string DevelopmentKey = "dev-only-signing-key-change-me-0123456789abcdef";
    public SymmetricSecurityKey SigningKey => new(Encoding.UTF8.GetBytes(Key));
}

public sealed class GoogleOptions
{
    /// <summary>OAuth client ID. Google sign-in stays disabled until this is set.</summary>
    public string? ClientId { get; set; }
}

public sealed class TokenService(IOptions<JwtOptions> options, TimeProvider time)
{
    public const string OrganizationClaim = "org";

    public (string Token, DateTime ExpiresAt) CreateAccessToken(Guid userId, Guid organizationId)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(options.Value.AccessTokenMinutes);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Value.Issuer,
            Audience = options.Value.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(OrganizationClaim, organizationId.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            }),
            SigningCredentials = new SigningCredentials(options.Value.SigningKey, SecurityAlgorithms.HmacSha256),
        });
        return (token, expires);
    }
}

/// <summary>
/// Turns a validated token into a tenant context. The token only carries identifiers; the user's status, membership
/// and role are re-read on every request so that removing a member or deactivating a user takes effect immediately.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TenantContext tenant, AppDbContext db, AuditService audit)
    {
        audit.IpAddress = context.Connection.RemoteIpAddress?.ToString();
        if (context.User.Identity?.IsAuthenticated == true
            && Guid.TryParse(context.User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId)
            && Guid.TryParse(context.User.FindFirstValue(TokenService.OrganizationClaim), out var organizationId))
        {
            OrganizationMember? membership;
            using (tenant.Elevate())
            {
                membership = await db.OrganizationMembers.AsNoTracking()
                    .Include(m => m.User)
                    .FirstOrDefaultAsync(m => m.UserId == userId && m.OrganizationId == organizationId && !m.Organization!.IsDeleted, context.RequestAborted);
            }
            if (membership?.User is not { IsActive: true, IsDeleted: false })
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new ProblemDetails { Status = 401, Title = "Your access to this organization has ended. Sign in again." });
                return;
            }
            tenant.UserId = userId;
            tenant.OrganizationId = organizationId;
            tenant.Role = membership.Role;
            tenant.IsPlatformAdmin = membership.User.IsPlatformAdmin;
        }
        await next(context);
    }
}

/// <summary>Requires a signed-in member of the active organization with at least the given role.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireRoleAttribute(OrgRole minimum = OrgRole.Viewer) : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        // Lets a single action opt out of a controller-level requirement (for example a signed webhook).
        if (context.ActionDescriptor.EndpointMetadata.OfType<Microsoft.AspNetCore.Authorization.IAllowAnonymous>().Any()) return;
        var tenant = context.HttpContext.RequestServices.GetRequiredService<TenantContext>();
        if (tenant.UserId is null || tenant.Role is null)
            context.Result = new ObjectResult(new ProblemDetails { Status = 401, Title = "Sign in to continue." }) { StatusCode = 401 };
        else if (tenant.Role < minimum)
            context.Result = new ObjectResult(new ProblemDetails { Status = 403, Title = $"This action needs the {Describe(minimum)} role or higher." }) { StatusCode = 403 };
    }

    public static string Describe(OrgRole role) => role switch
    {
        OrgRole.PurchasingManager => "Purchasing Manager",
        OrgRole.Owner => "Organization Owner",
        _ => role.ToString()
    };
}

/// <summary>Platform operator access. Independent of organization roles: an organization owner is not a platform admin.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class PlatformAdminAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var tenant = context.HttpContext.RequestServices.GetRequiredService<TenantContext>();
        if (tenant.UserId is null)
            context.Result = new ObjectResult(new ProblemDetails { Status = 401, Title = "Sign in to continue." }) { StatusCode = 401 };
        else if (!tenant.IsPlatformAdmin)
            context.Result = new ObjectResult(new ProblemDetails { Status = 403, Title = "Platform administrator access is required." }) { StatusCode = 403 };
    }
}

public sealed class FeatureFlagService(AppDbContext db, IMemoryCache cache)
{
    public async Task<bool> IsEnabledAsync(string key, CancellationToken ct)
    {
        var flags = await cache.GetOrCreateAsync("feature-flags", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            return await db.FeatureFlags.AsNoTracking().ToDictionaryAsync(f => f.Key, f => f.IsEnabled, ct);
        });
        return flags is not null && flags.TryGetValue(key, out var enabled) && enabled;
    }

    public void Invalidate() => cache.Remove("feature-flags");
}

/// <summary>Maps domain exceptions to RFC 7807 responses and records unexpected failures without leaking details.</summary>
public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger, IServiceScopeFactory scopes)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client went away; nothing to send.
        }
        catch (Exception ex)
        {
            var (status, title, code) = ex switch
            {
                NotFoundException => (404, ex.Message, "not_found"),
                ValidationException => (400, ex.Message, "validation"),
                ForbiddenException => (403, ex.Message, "forbidden"),
                EntitlementException => (402, ex.Message, "plan_limit"),
                BusinessRuleException rule => (409, ex.Message, rule.Code),
                TenantIsolationException => (403, "That resource does not belong to your organization.", "tenant_isolation"),
                DbUpdateConcurrencyException => (409, "Someone else changed this record. Reload and try again.", "concurrency"),
                SmartBuy.Infrastructure.Ai.AiProviderException => (502, "The AI service could not complete the request. Try again, or add items manually.", "ai_unavailable"),
                _ => (500, "Something went wrong on our side. The error has been logged.", "server_error"),
            };

            if (status == 500 || ex is TenantIsolationException)
            {
                logger.LogError(ex, "Unhandled error on {Method} {Path}", context.Request.Method, context.Request.Path);
                await RecordAsync(context, ex);
            }
            if (context.Response.HasStarted) throw;

            context.Response.Clear();
            context.Response.StatusCode = status;
            var problem = new ProblemDetails { Status = status, Title = title, Instance = context.Request.Path };
            problem.Extensions["code"] = code;
            problem.Extensions["traceId"] = context.TraceIdentifier;
            await context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
        }
    }

    private async Task RecordAsync(HttpContext context, Exception ex)
    {
        try
        {
            // A fresh scope: the request's DbContext may be the thing that failed.
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var requestTenant = context.RequestServices.GetRequiredService<TenantContext>();
            var detail = ex.ToString();
            db.ErrorLogs.Add(new ErrorLog
            {
                Message = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message,
                Path = context.Request.Path,
                Method = context.Request.Method,
                TraceId = context.TraceIdentifier,
                Detail = detail.Length > 6000 ? detail[..6000] : detail,
                UserId = requestTenant.UserId,
            });
            await db.SaveChangesAsync();
        }
        catch (Exception logFailure)
        {
            logger.LogError(logFailure, "Could not persist error log");
        }
    }
}

public abstract class ApiController : ControllerBase
{
    protected TenantContext Tenant => HttpContext.RequestServices.GetRequiredService<TenantContext>();
    protected OrgRole Role => Tenant.Role ?? OrgRole.Viewer;
    protected Guid UserId => Tenant.UserId ?? throw new ForbiddenException("Sign in to continue.");
    protected Guid OrganizationId => Tenant.OrganizationId ?? throw new ForbiddenException("No organization is active.");
}
