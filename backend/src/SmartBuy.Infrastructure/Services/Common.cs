using Microsoft.EntityFrameworkCore;
using SmartBuy.Domain.Entities;
using SmartBuy.Domain.Rules;
using SmartBuy.Infrastructure.Persistence;

namespace SmartBuy.Infrastructure.Services;

/// <summary>Maps to HTTP 404.</summary>
public class NotFoundException(string message) : Exception(message);

/// <summary>Maps to HTTP 409: the request is valid but the current state does not allow it.</summary>
public class BusinessRuleException(string message, string code = "business_rule") : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Maps to HTTP 403.</summary>
public class ForbiddenException(string message) : Exception(message);

/// <summary>Maps to HTTP 402: the organization's plan does not include the feature or quota.</summary>
public class EntitlementException(string message) : Exception(message);

/// <summary>Maps to HTTP 400.</summary>
public class ValidationException(string message) : Exception(message);

public sealed class AuditService(AppDbContext db, ITenantContext tenant)
{
    public string? IpAddress { get; set; }

    /// <summary>Queues an audit row; it is written with the caller's next SaveChanges so it commits atomically with the action.</summary>
    public void Record(string action, string? entityType = null, object? entityId = null, string? details = null, Guid? organizationId = null, Guid? userId = null)
    {
        db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = organizationId ?? tenant.OrganizationId,
            UserId = userId ?? tenant.UserId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId?.ToString(),
            Details = details is { Length: > 2000 } ? details[..2000] : details,
            IpAddress = IpAddress,
        });
    }
}

public sealed class EntitlementService(AppDbContext db, ITenantContext tenant)
{
    private Plan? _plan;

    public async Task<Plan> GetPlanAsync(CancellationToken ct)
    {
        if (_plan is not null) return _plan;
        var subscription = await db.Subscriptions.AsNoTracking().Include(s => s.Plan).FirstOrDefaultAsync(ct);
        // A cancelled or missing subscription falls back to the free plan rather than locking the tenant out of its data.
        if (subscription?.Plan is not null && subscription.Status != SubscriptionStatus.Canceled)
            return _plan = subscription.Plan;
        return _plan = await db.Plans.AsNoTracking().FirstAsync(p => p.Code == "free", ct);
    }

    public async Task RequireAsync(PlanFeature feature, CancellationToken ct)
    {
        var result = Entitlements.CheckFeature(await GetPlanAsync(ct), feature);
        if (!result.Allowed) throw new EntitlementException(result.Reason!);
    }

    public async Task<bool> HasAsync(PlanFeature feature, CancellationToken ct) => Entitlements.HasFeature(await GetPlanAsync(ct), feature);

    public async Task<int> ProjectsThisMonthAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        // Deleted projects still count: otherwise the quota could be bypassed by deleting and recreating.
        return await db.PurchasingProjects.IgnoreQueryFilters()
            .CountAsync(p => p.OrganizationId == tenant.OrganizationId && p.CreatedAt >= monthStart, ct);
    }

    public async Task RequireProjectQuotaAsync(CancellationToken ct)
    {
        var result = Entitlements.CheckProjectQuota(await GetPlanAsync(ct), await ProjectsThisMonthAsync(ct));
        if (!result.Allowed) throw new EntitlementException(result.Reason!);
    }

    public async Task RequireMemberQuotaAsync(CancellationToken ct)
    {
        var members = await db.OrganizationMembers.CountAsync(ct);
        var result = Entitlements.CheckMemberQuota(await GetPlanAsync(ct), members);
        if (!result.Allowed) throw new EntitlementException(result.Reason!);
    }
}
