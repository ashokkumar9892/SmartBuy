using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Google.Apis.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartBuy.Api.Infrastructure;
using SmartBuy.Domain.Entities;
using SmartBuy.Domain.Rules;
using SmartBuy.Infrastructure.Ai;
using SmartBuy.Infrastructure.Persistence;
using SmartBuy.Infrastructure.Services;
using ValidationException = SmartBuy.Infrastructure.Services.ValidationException;

namespace SmartBuy.Api.Controllers;

public sealed record RegisterRequest(
    [Required, EmailAddress, MaxLength(320)] string Email,
    [Required, MinLength(10, ErrorMessage = "Use a password of at least 10 characters."), MaxLength(200)] string Password,
    [Required, MaxLength(200)] string FullName,
    [Required, MaxLength(200)] string OrganizationName,
    Industry Industry = Industry.General);

public sealed record LoginRequest([Required, MaxLength(320)] string Email, [Required, MaxLength(200)] string Password);
public sealed record RefreshRequest([Required, MaxLength(200)] string RefreshToken);
public sealed record DemoLoginRequest([Required, MaxLength(80)] string Business);
public sealed record GoogleLoginRequest([Required] string IdToken);
public sealed record SwitchOrganizationRequest(Guid OrganizationId);

public sealed record UserDto(Guid Id, string Email, string FullName, bool IsPlatformAdmin, bool IsDemo);
public sealed record OrganizationSummary(Guid Id, string Name, string Slug, string Industry, bool IsDemo, string Role);
public sealed record PlanInfo(string Code, string Name, string Description, int MonthlyPriceCents, int? MaxProjectsPerMonth, int? MaxMembers,
    bool AiMaterialLists, bool AdvancedOptimization, bool PriceAlerts, bool PurchaseOrders, bool AdvancedReporting, bool ApprovalWorkflows)
{
    public static PlanInfo From(Plan p) => new(p.Code, p.Name, p.Description, p.MonthlyPriceCents, p.MaxProjectsPerMonth, p.MaxMembers,
        p.AiMaterialLists, p.AdvancedOptimization, p.PriceAlerts, p.PurchaseOrders, p.AdvancedReporting, p.ApprovalWorkflows);
}
public sealed record PermissionsDto(bool CanEditLists, bool CanManagePurchasing, bool CanApprovePurchaseOrders, bool CanManageOrganization, bool CanManageBilling);
public sealed record SessionDto(UserDto User, OrganizationSummary Organization, IReadOnlyList<OrganizationSummary> Organizations, PlanInfo Plan, PermissionsDto Permissions, string AiProvider);
public sealed record AuthResponse(string AccessToken, DateTime ExpiresAt, string RefreshToken, SessionDto Session);
public sealed record DemoBusinessDto(string Slug, string Name, string Industry, string Tagline, string Plan);
public sealed record PublicConfig(bool DemoEnabled, bool SelfSignupEnabled, bool GoogleEnabled, string? GoogleClientId, IReadOnlyList<DemoBusinessDto> DemoBusinesses, IReadOnlyList<PlanInfo> Plans);

[ApiController]
[Route("api/auth")]
public sealed partial class AuthController(
    AppDbContext db,
    TokenService tokens,
    FeatureFlagService flags,
    AuditService audit,
    AiGateway ai,
    IOptions<JwtOptions> jwt,
    IOptions<GoogleOptions> google,
    TimeProvider time) : ApiController
{
    private const int MaxFailedLogins = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    // Verifying against this keeps response time the same whether or not the account exists.
    private static readonly string DummyHash = PasswordHasher.Hash("not-a-real-password");

    [HttpGet("config"), AllowAnonymous]
    public async Task<PublicConfig> Config(CancellationToken ct)
    {
        var plans = await db.Plans.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.SortOrder).ToListAsync(ct);
        var demo = await flags.IsEnabledAsync("demo_login", ct);
        List<string> demoSlugs;
        using (Tenant.Elevate())
            demoSlugs = await db.Organizations.AsNoTracking().Where(o => o.IsDemo).Select(o => o.Slug).ToListAsync(ct);
        return new PublicConfig(
            demo && demoSlugs.Count > 0,
            await flags.IsEnabledAsync("self_signup", ct),
            !string.IsNullOrWhiteSpace(google.Value.ClientId),
            google.Value.ClientId,
            DataSeeder.DemoBusinesses.Where(b => demoSlugs.Contains(b.Slug)).Select(b => new DemoBusinessDto(b.Slug, b.Name, b.Industry.ToString(), b.Tagline, b.PlanCode)).ToList(),
            plans.Select(PlanInfo.From).ToList());
    }

    [HttpPost("register"), AllowAnonymous, EnableRateLimiting("auth")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request, CancellationToken ct)
    {
        if (!await flags.IsEnabledAsync("self_signup", ct)) throw new BusinessRuleException("New sign-ups are currently closed.", "signup_closed");
        ValidatePassword(request.Password);
        var email = request.Email.Trim();
        var normalized = email.ToUpperInvariant();

        using var elevated = Tenant.Elevate();
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.NormalizedEmail == normalized, ct))
            throw new BusinessRuleException("An account with this email already exists. Sign in instead.", "email_in_use");

        var user = new User { Email = email, NormalizedEmail = normalized, FullName = request.FullName.Trim(), PasswordHash = PasswordHasher.Hash(request.Password) };
        var organization = await CreateOrganizationAsync(user, request.OrganizationName, request.Industry, ct);
        db.Users.Add(user);
        audit.Record("auth.registered", nameof(User), user.Id, organizationId: organization.Id, userId: user.Id);
        await db.SaveChangesAsync(ct);
        return await IssueAsync(user, organization.Id, ct);
    }

    [HttpPost("login"), AllowAnonymous, EnableRateLimiting("auth")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var normalized = request.Email.Trim().ToUpperInvariant();
        var now = time.GetUtcNow().UtcDateTime;
        using var elevated = Tenant.Elevate();
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);

        if (user is null || user.PasswordHash is null || user.IsDemo)
        {
            PasswordHasher.Verify(request.Password, DummyHash);
            return InvalidCredentials();
        }
        if (user.LockoutUntil is { } until && until > now)
            return Problem(statusCode: 423, title: "Too many failed sign-in attempts. Try again in a few minutes.");

        if (!PasswordHasher.Verify(request.Password, user.PasswordHash))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= MaxFailedLogins)
            {
                user.LockoutUntil = now.Add(LockoutDuration);
                user.FailedLoginCount = 0;
                audit.Record("auth.locked_out", nameof(User), user.Id, userId: user.Id);
            }
            await db.SaveChangesAsync(ct);
            return InvalidCredentials();
        }
        if (!user.IsActive) return Problem(statusCode: 403, title: "This account has been deactivated. Contact your administrator.");

        user.FailedLoginCount = 0;
        user.LockoutUntil = null;
        user.LastLoginAt = now;
        if (PasswordHasher.NeedsRehash(user.PasswordHash)) user.PasswordHash = PasswordHasher.Hash(request.Password);

        var organizationId = await DefaultOrganizationAsync(user.Id, ct);
        if (organizationId is null) return Problem(statusCode: 403, title: "This account is not a member of any organization.");
        audit.Record("auth.login", nameof(User), user.Id, organizationId: organizationId, userId: user.Id);
        await db.SaveChangesAsync(ct);
        return await IssueAsync(user, organizationId.Value, ct);
    }

    /// <summary>Opens one of the seeded sample businesses. Demo accounts have no password and cannot be used any other way.</summary>
    [HttpPost("demo"), AllowAnonymous, EnableRateLimiting("auth")]
    public async Task<ActionResult<AuthResponse>> Demo(DemoLoginRequest request, CancellationToken ct)
    {
        if (!await flags.IsEnabledAsync("demo_login", ct)) throw new BusinessRuleException("The demo is currently switched off.", "demo_disabled");
        using var elevated = Tenant.Elevate();
        var organization = await db.Organizations.AsNoTracking().FirstOrDefaultAsync(o => o.IsDemo && o.Slug == request.Business, ct)
                           ?? throw new NotFoundException("That demo business does not exist.");
        var owner = await db.OrganizationMembers.AsNoTracking().Where(m => m.OrganizationId == organization.Id && m.Role == OrgRole.Owner && m.User!.IsDemo)
            .Select(m => m.User!).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("That demo business has no demo user.");
        return await IssueAsync(owner, organization.Id, ct);
    }

    /// <summary>Google sign-in. Disabled (501) until Google:ClientId is configured.</summary>
    [HttpPost("google"), AllowAnonymous, EnableRateLimiting("auth")]
    public async Task<ActionResult<AuthResponse>> Google(GoogleLoginRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(google.Value.ClientId))
            return Problem(statusCode: 501, title: "Google sign-in is not configured on this server.");
        GoogleJsonWebSignature.Payload payload;
        try
        {
            payload = await GoogleJsonWebSignature.ValidateAsync(request.IdToken, new GoogleJsonWebSignature.ValidationSettings { Audience = new[] { google.Value.ClientId } });
        }
        catch (InvalidJwtException)
        {
            return Problem(statusCode: 401, title: "Google sign-in could not be verified.");
        }
        if (!payload.EmailVerified) return Problem(statusCode: 401, title: "Your Google email address is not verified.");

        using var elevated = Tenant.Elevate();
        var normalized = payload.Email.ToUpperInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.GoogleSubject == payload.Subject || u.NormalizedEmail == normalized, ct);
        Guid? organizationId;
        if (user is null)
        {
            if (!await flags.IsEnabledAsync("self_signup", ct)) throw new BusinessRuleException("New sign-ups are currently closed.", "signup_closed");
            user = new User { Email = payload.Email, NormalizedEmail = normalized, FullName = payload.Name ?? payload.Email, GoogleSubject = payload.Subject };
            db.Users.Add(user);
            organizationId = (await CreateOrganizationAsync(user, $"{user.FullName}'s Business", Industry.General, ct)).Id;
        }
        else
        {
            if (!user.IsActive || user.IsDemo) return Problem(statusCode: 403, title: "This account cannot sign in.");
            user.GoogleSubject ??= payload.Subject;
            organizationId = await DefaultOrganizationAsync(user.Id, ct);
            if (organizationId is null) return Problem(statusCode: 403, title: "This account is not a member of any organization.");
        }
        user.LastLoginAt = time.GetUtcNow().UtcDateTime;
        audit.Record("auth.login_google", nameof(User), user.Id, organizationId: organizationId, userId: user.Id);
        await db.SaveChangesAsync(ct);
        return await IssueAsync(user, organizationId.Value, ct);
    }

    [HttpPost("refresh"), AllowAnonymous, EnableRateLimiting("auth")]
    public async Task<ActionResult<AuthResponse>> Refresh(RefreshRequest request, CancellationToken ct)
    {
        var hash = PasswordHasher.HashToken(request.RefreshToken);
        var now = time.GetUtcNow().UtcDateTime;
        using var elevated = Tenant.Elevate();
        var stored = await db.RefreshTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored?.User is null) return Problem(statusCode: 401, title: "Your session has expired. Sign in again.");
        if (stored.RevokedAt is not null)
        {
            // A rotated token was presented again: assume it was stolen and end every session for this user.
            await db.RefreshTokens.Where(t => t.UserId == stored.UserId && t.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
            audit.Record("auth.refresh_reuse_detected", nameof(User), stored.UserId, userId: stored.UserId);
            await db.SaveChangesAsync(ct);
            return Problem(statusCode: 401, title: "Your session has expired. Sign in again.");
        }
        if (stored.ExpiresAt <= now || !stored.User.IsActive || stored.User.IsDeleted)
            return Problem(statusCode: 401, title: "Your session has expired. Sign in again.");
        if (!await db.OrganizationMembers.AnyAsync(m => m.UserId == stored.UserId && m.OrganizationId == stored.OrganizationId, ct))
            return Problem(statusCode: 401, title: "Your session has expired. Sign in again.");

        stored.RevokedAt = now;
        await db.SaveChangesAsync(ct);
        return await IssueAsync(stored.User, stored.OrganizationId, ct);
    }

    [HttpPost("logout"), AllowAnonymous]
    public async Task<IActionResult> Logout(RefreshRequest request, CancellationToken ct)
    {
        var hash = PasswordHasher.HashToken(request.RefreshToken);
        using var elevated = Tenant.Elevate();
        await db.RefreshTokens.Where(t => t.TokenHash == hash && t.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, time.GetUtcNow().UtcDateTime), ct);
        return NoContent();
    }

    [HttpGet("me"), RequireRole]
    public async Task<SessionDto> Me(CancellationToken ct)
    {
        User user;
        using (Tenant.Elevate()) user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == UserId, ct);
        return await BuildSessionAsync(user, OrganizationId, ct);
    }

    [HttpPost("switch-organization"), RequireRole]
    public async Task<ActionResult<AuthResponse>> SwitchOrganization(SwitchOrganizationRequest request, CancellationToken ct)
    {
        using var elevated = Tenant.Elevate();
        if (!await db.OrganizationMembers.AnyAsync(m => m.UserId == UserId && m.OrganizationId == request.OrganizationId, ct))
            throw new ForbiddenException("You are not a member of that organization.");
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == UserId, ct);
        return await IssueAsync(user, request.OrganizationId, ct);
    }

    private ObjectResult InvalidCredentials() => Problem(statusCode: 401, title: "The email or password is incorrect.");

    public static void ValidatePassword(string password)
    {
        if (password.Length < 10) throw new ValidationException("Use a password of at least 10 characters.");
        if (!password.Any(char.IsLetter) || !password.Any(ch => !char.IsLetter(ch)))
            throw new ValidationException("Use a password that mixes letters with numbers or symbols.");
    }

    private async Task<Organization> CreateOrganizationAsync(User user, string name, Industry industry, CancellationToken ct)
    {
        var baseSlug = SlugPattern().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        if (baseSlug.Length == 0) baseSlug = "organization";
        if (baseSlug.Length > 60) baseSlug = baseSlug[..60];
        var slug = baseSlug;
        for (var n = 2; await db.Organizations.IgnoreQueryFilters().AnyAsync(o => o.Slug == slug, ct); n++) slug = $"{baseSlug}-{n}";

        var organization = new Organization { Name = name.Trim(), Slug = slug, Industry = industry };
        var free = await db.Plans.FirstAsync(p => p.Code == "free", ct);
        db.Organizations.Add(organization);
        db.OrganizationMembers.Add(new OrganizationMember { OrganizationId = organization.Id, UserId = user.Id, Role = OrgRole.Owner });
        db.Subscriptions.Add(new Subscription { OrganizationId = organization.Id, PlanId = free.Id, Status = SubscriptionStatus.Active });
        return organization;
    }

    private async Task<Guid?> DefaultOrganizationAsync(Guid userId, CancellationToken ct) =>
        await db.OrganizationMembers.AsNoTracking()
            .Where(m => m.UserId == userId && !m.Organization!.IsDeleted)
            .OrderBy(m => m.CreatedAt).Select(m => (Guid?)m.OrganizationId).FirstOrDefaultAsync(ct);

    /// <summary>Caller must hold an elevated tenant scope.</summary>
    private async Task<AuthResponse> IssueAsync(User user, Guid organizationId, CancellationToken ct)
    {
        var (access, expires) = tokens.CreateAccessToken(user.Id, organizationId);
        var refresh = PasswordHasher.NewToken();
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            OrganizationId = organizationId,
            TokenHash = PasswordHasher.HashToken(refresh),
            ExpiresAt = time.GetUtcNow().UtcDateTime.AddDays(jwt.Value.RefreshTokenDays),
        });
        await db.SaveChangesAsync(ct);
        return new AuthResponse(access, expires, refresh, await BuildSessionAsync(user, organizationId, ct));
    }

    private async Task<SessionDto> BuildSessionAsync(User user, Guid organizationId, CancellationToken ct)
    {
        using var elevated = Tenant.Elevate();
        var memberships = await db.OrganizationMembers.AsNoTracking().Include(m => m.Organization)
            .Where(m => m.UserId == user.Id && !m.Organization!.IsDeleted).OrderBy(m => m.Organization!.Name).ToListAsync(ct);
        var current = memberships.First(m => m.OrganizationId == organizationId);
        var subscription = await db.Subscriptions.AsNoTracking().Include(s => s.Plan).FirstOrDefaultAsync(s => s.OrganizationId == organizationId, ct);
        var plan = subscription?.Plan is not null && subscription.Status != SubscriptionStatus.Canceled
            ? subscription.Plan
            : await db.Plans.AsNoTracking().FirstAsync(p => p.Code == "free", ct);

        static OrganizationSummary Summary(OrganizationMember m) =>
            new(m.OrganizationId, m.Organization!.Name, m.Organization.Slug, m.Organization.Industry.ToString(), m.Organization.IsDemo, m.Role.ToString());

        return new SessionDto(
            new UserDto(user.Id, user.Email, user.FullName, user.IsPlatformAdmin, user.IsDemo),
            Summary(current),
            memberships.Select(Summary).ToList(),
            PlanInfo.From(plan),
            new PermissionsDto(RolePermissions.CanEditLists(current.Role), RolePermissions.CanManagePurchasing(current.Role),
                RolePermissions.CanApprovePurchaseOrders(current.Role), RolePermissions.CanManageOrganization(current.Role), RolePermissions.CanManageBilling(current.Role)),
            ai.ActiveProvider.Name);
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugPattern();
}
