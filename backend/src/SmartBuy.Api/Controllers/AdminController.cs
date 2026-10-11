using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using SmartBuy.Api.Infrastructure;
using SmartBuy.Domain.Entities;
using SmartBuy.Domain.Matching;
using SmartBuy.Domain.Suppliers;
using SmartBuy.Domain.Units;
using SmartBuy.Infrastructure;
using SmartBuy.Infrastructure.Ai;
using SmartBuy.Infrastructure.Persistence;
using SmartBuy.Infrastructure.Services;
using SmartBuy.Infrastructure.Suppliers.Mock;
using ValidationException = SmartBuy.Infrastructure.Services.ValidationException;

namespace SmartBuy.Api.Controllers;

/// <summary>Lifts tenant isolation for the duration of a platform-administration action.</summary>
public sealed class CrossTenantAttribute : ActionFilterAttribute
{
    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        using var elevated = context.HttpContext.RequestServices.GetRequiredService<TenantContext>().Elevate();
        await next();
    }
}

public sealed record AdminOverview(int Users, int Organizations, int DemoOrganizations, int Projects, int PurchaseOrders, int ActiveAlerts, int MatchesNeedingReview,
    int SupplierCallsLast24h, int SupplierFailuresLast24h, long AiTokensLast30d, decimal AiCostLast30d, int ErrorsLast24h, IReadOnlyDictionary<string, int> OrganizationsByPlan, string AiProvider, string DatabaseProvider);
public sealed record AdminUser(Guid Id, string Email, string FullName, bool IsActive, bool IsPlatformAdmin, bool IsDemo, DateTime CreatedAt, DateTime? LastLoginAt, IReadOnlyList<string> Organizations);
public sealed record AdminUserUpdate(bool IsActive, bool IsPlatformAdmin);
public sealed record AdminOrganization(Guid Id, string Name, string Slug, string Industry, bool IsDemo, string Plan, string PlanCode, string SubscriptionStatus, bool StripeLinked, int Members, int Projects, DateTime CreatedAt);
public sealed record AdminSupplier(Guid Id, string Code, string Name, string Kind, string Mode, string ProviderKey, bool IsEnabled, decimal? ShippingFlatFee, decimal? FreeShippingThreshold,
    decimal? MinimumOrderAmount, int CacheTtlMinutes, bool AffiliateProgramEnabled, string? IntegrationNotes, bool AdapterRegistered, bool IsConfigured, bool IsHealthy, string StatusMessage,
    ProviderCapabilities? Capabilities, string? SimulatedFault, int Listings, DateTime? LastSyncAt, bool? LastSyncSucceeded);
public sealed record AdminSupplierUpdate(bool IsEnabled, [Range(0, 100000)] decimal? ShippingFlatFee, [Range(0, 1000000)] decimal? FreeShippingThreshold, [Range(0, 1000000)] decimal? MinimumOrderAmount,
    [Range(1, 10080)] int CacheTtlMinutes, bool AffiliateProgramEnabled);
public sealed record FaultRequest([RegularExpression("none|error|timeout")] string Fault);
public sealed record AdminMatch(Guid Id, string Status, string Method, double Confidence, IReadOnlyList<string> Reasons, string SupplierName, string ListingTitle, string? ListingBrand, string? ListingModel,
    string? ListingGtin, decimal PackQuantity, string PackUnit, IReadOnlyDictionary<string, string> ListingSpecs, decimal? Price, Guid CanonicalProductId, string CanonicalName, string CanonicalUnit,
    IReadOnlyDictionary<string, string> CanonicalSpecs, DateTime UpdatedAt);
public sealed record CatalogInput([Required, MaxLength(300)] string Name, Industry Industry, [Required, MaxLength(100)] string Category, [MaxLength(100)] string? Brand, [MaxLength(100)] string? Model,
    [MaxLength(100)] string? Mpn, [MaxLength(20)] string? Gtin, [MaxLength(100)] string? Material, [Required, MaxLength(20)] string BaseUnit, bool IsSafetyCritical, [MaxLength(500)] string? SafetyNote,
    Dictionary<string, string>? Specifications);
public sealed record UsageBucket(string Key, int Calls, int Failures, long InputTokens, long OutputTokens, decimal Cost, double AverageMs);
public sealed record FlagDto(string Key, string Description, bool IsEnabled, DateTime UpdatedAt);
public sealed record FlagUpdate(bool IsEnabled);

[ApiController]
[Route("api/admin")]
[PlatformAdmin]
[CrossTenant]
public sealed class AdminController(
    AppDbContext db,
    ISupplierProviderRegistry registry,
    MockSupplierControl mockControl,
    AuditService audit,
    AiGateway ai,
    FeatureFlagService flags,
    IServiceScopeFactory scopes) : ApiController
{
    [HttpGet("overview")]
    public async Task<AdminOverview> Overview(CancellationToken ct)
    {
        var day = DateTime.UtcNow.AddDays(-1);
        var month = DateTime.UtcNow.AddDays(-30);
        var aiLogs = await db.AiUsageLogs.AsNoTracking().Where(l => l.CreatedAt >= month).Select(l => new { l.InputTokens, l.OutputTokens, l.EstimatedCostUsd }).ToListAsync(ct);
        var subscriptions = await db.Subscriptions.AsNoTracking().Include(s => s.Plan).Select(s => s.Plan!.Name).ToListAsync(ct);
        return new AdminOverview(
            await db.Users.CountAsync(ct), await db.Organizations.CountAsync(ct), await db.Organizations.CountAsync(o => o.IsDemo, ct),
            await db.PurchasingProjects.CountAsync(ct), await db.PurchaseOrders.CountAsync(ct), await db.PriceAlerts.CountAsync(a => a.IsActive, ct),
            await db.ProductMatches.CountAsync(m => m.Status == MatchStatus.NeedsReview, ct),
            await db.ProviderSyncLogs.CountAsync(l => l.CreatedAt >= day, ct), await db.ProviderSyncLogs.CountAsync(l => l.CreatedAt >= day && !l.Success, ct),
            aiLogs.Sum(l => (long)l.InputTokens + l.OutputTokens), aiLogs.Sum(l => l.EstimatedCostUsd), await db.ErrorLogs.CountAsync(l => l.CreatedAt >= day, ct),
            subscriptions.GroupBy(n => n).ToDictionary(g => g.Key, g => g.Count()), ai.ActiveProvider.Name, db.Database.IsNpgsql() ? "PostgreSQL" : "SQLite (development)");
    }

    // ---------- Users and organizations ----------

    [HttpGet("users")]
    public async Task<List<AdminUser>> Users([FromQuery] string? search, CancellationToken ct)
    {
        var query = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(u => u.Email.ToLower().Contains(term) || u.FullName.ToLower().Contains(term));
        }
        var users = await query.OrderByDescending(u => u.CreatedAt).Take(300).Include(u => u.Memberships).ThenInclude(m => m.Organization).AsSplitQuery().ToListAsync(ct);
        return users.Select(u => new AdminUser(u.Id, u.Email, u.FullName, u.IsActive, u.IsPlatformAdmin, u.IsDemo, u.CreatedAt, u.LastLoginAt,
            u.Memberships.Where(m => m.Organization is not null).Select(m => $"{m.Organization!.Name} ({m.Role})").ToList())).ToList();
    }

    [HttpPut("users/{id:guid}")]
    public async Task<IActionResult> UpdateUser(Guid id, AdminUserUpdate request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new NotFoundException("User not found.");
        if (user.Id == UserId && (!request.IsActive || !request.IsPlatformAdmin))
            throw new BusinessRuleException("You cannot deactivate yourself or remove your own administrator access.");
        if (user.IsDemo && request.IsPlatformAdmin) throw new BusinessRuleException("Demo accounts cannot be platform administrators.");
        user.IsActive = request.IsActive;
        user.IsPlatformAdmin = request.IsPlatformAdmin;
        if (!request.IsActive)
            await db.RefreshTokens.Where(t => t.UserId == id && t.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow), ct);
        audit.Record("admin.user_updated", nameof(User), id, $"active={request.IsActive}, platformAdmin={request.IsPlatformAdmin}");
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("organizations")]
    public async Task<List<AdminOrganization>> Organizations(CancellationToken ct)
    {
        var organizations = await db.Organizations.AsNoTracking().Include(o => o.Subscription!).ThenInclude(s => s.Plan).OrderByDescending(o => o.CreatedAt).Take(500).ToListAsync(ct);
        var members = await db.OrganizationMembers.AsNoTracking().GroupBy(m => m.OrganizationId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var projects = await db.PurchasingProjects.AsNoTracking().GroupBy(p => p.OrganizationId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return organizations.Select(o => new AdminOrganization(o.Id, o.Name, o.Slug, o.Industry.ToString(), o.IsDemo, o.Subscription?.Plan?.Name ?? "Free", o.Subscription?.Plan?.Code ?? "free",
            (o.Subscription?.Status ?? SubscriptionStatus.Active).ToString(), o.Subscription?.StripeSubscriptionId is not null, members.GetValueOrDefault(o.Id), projects.GetValueOrDefault(o.Id), o.CreatedAt)).ToList();
    }

    /// <summary>Operator override of an organization's plan (support, comps, pilots). Recorded in the audit log.</summary>
    [HttpPut("organizations/{id:guid}/plan")]
    public async Task<IActionResult> SetPlan(Guid id, PlanRequest request, CancellationToken ct)
    {
        if (!await db.Organizations.AnyAsync(o => o.Id == id, ct)) throw new NotFoundException("Organization not found.");
        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Code == request.PlanCode, ct) ?? throw new NotFoundException("Plan not found.");
        var subscription = await db.Subscriptions.FirstOrDefaultAsync(s => s.OrganizationId == id, ct);
        if (subscription is null) db.Subscriptions.Add(new Subscription { OrganizationId = id, PlanId = plan.Id, Status = SubscriptionStatus.Active });
        else
        {
            subscription.PlanId = plan.Id;
            subscription.Status = SubscriptionStatus.Active;
        }
        audit.Record("admin.plan_overridden", nameof(Organization), id, plan.Code, organizationId: id);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---------- Suppliers and providers ----------

    [HttpGet("suppliers")]
    public async Task<List<AdminSupplier>> Suppliers(CancellationToken ct)
    {
        var suppliers = await db.Suppliers.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
        var listings = await db.SupplierProducts.AsNoTracking().GroupBy(p => p.SupplierId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var lastSync = (await db.ProviderSyncLogs.AsNoTracking().OrderByDescending(l => l.CreatedAt).Take(500).ToListAsync(ct)).GroupBy(l => l.SupplierId).ToDictionary(g => g.Key, g => g.First());
        var result = new List<AdminSupplier>();
        foreach (var s in suppliers)
        {
            var provider = registry.Get(s.ProviderKey);
            var status = provider is null ? null : await provider.GetProviderStatusAsync(ct);
            lastSync.TryGetValue(s.Id, out var sync);
            result.Add(new AdminSupplier(s.Id, s.Code, s.Name, s.Kind.ToString(), s.Mode.ToString(), s.ProviderKey, s.IsEnabled, s.ShippingFlatFee, s.FreeShippingThreshold, s.MinimumOrderAmount,
                s.CacheTtlMinutes, s.AffiliateProgramEnabled, s.IntegrationNotes, provider is not null, status?.IsConfigured ?? false, status?.IsHealthy ?? false,
                status?.Message ?? "No adapter registered for this provider key.", status?.Capabilities, mockControl.GetFault(s.Code), listings.GetValueOrDefault(s.Id), sync?.CreatedAt, sync?.Success));
        }
        return result;
    }

    [HttpPut("suppliers/{id:guid}")]
    public async Task<IActionResult> UpdateSupplier(Guid id, AdminSupplierUpdate request, CancellationToken ct)
    {
        var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException("Supplier not found.");
        supplier.IsEnabled = request.IsEnabled;
        supplier.ShippingFlatFee = request.ShippingFlatFee;
        supplier.FreeShippingThreshold = request.FreeShippingThreshold;
        supplier.MinimumOrderAmount = request.MinimumOrderAmount;
        supplier.CacheTtlMinutes = request.CacheTtlMinutes;
        supplier.AffiliateProgramEnabled = request.AffiliateProgramEnabled;
        audit.Record("admin.supplier_updated", nameof(Supplier), id, supplier.Code);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Simulates an outage or timeout on a mock supplier to demonstrate failure handling. Held in memory only.</summary>
    [HttpPost("suppliers/{id:guid}/fault")]
    public async Task<IActionResult> SetFault(Guid id, FaultRequest request, CancellationToken ct)
    {
        var supplier = await db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException("Supplier not found.");
        if (supplier.Mode != SupplierMode.Mock) throw new BusinessRuleException("Faults can only be simulated on mock suppliers.");
        mockControl.SetFault(supplier.Code, request.Fault);
        audit.Record("admin.supplier_fault", nameof(Supplier), id, request.Fault);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---------- Catalog and match review ----------

    [HttpGet("catalog")]
    public async Task<List<CatalogProductDto>> Catalog([FromQuery] string? query, CancellationToken ct)
    {
        var products = db.CanonicalProducts.AsNoTracking().Include(p => p.Specifications).AsQueryable();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim().ToLower();
            products = products.Where(p => p.Name.ToLower().Contains(term) || p.Category.ToLower().Contains(term));
        }
        return (await products.OrderBy(p => p.Industry).ThenBy(p => p.Category).ThenBy(p => p.Name).Take(300).ToListAsync(ct)).Select(ProjectsController.ToCatalogDto).ToList();
    }

    [HttpPost("catalog")]
    public async Task<CatalogProductDto> CreateProduct(CatalogInput request, CancellationToken ct)
    {
        var product = new CanonicalProduct();
        ApplyCatalog(product, request);
        db.CanonicalProducts.Add(product);
        audit.Record("admin.catalog_created", nameof(CanonicalProduct), product.Id, product.Name);
        await db.SaveChangesAsync(ct);
        return ProjectsController.ToCatalogDto(product);
    }

    [HttpPut("catalog/{id:guid}")]
    public async Task<CatalogProductDto> UpdateProduct(Guid id, CatalogInput request, CancellationToken ct)
    {
        var product = await db.CanonicalProducts.Include(p => p.Specifications).FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Product not found.");
        db.ProductSpecifications.RemoveRange(product.Specifications);
        product.Specifications.Clear();
        ApplyCatalog(product, request);
        audit.Record("admin.catalog_updated", nameof(CanonicalProduct), id, product.Name);
        await db.SaveChangesAsync(ct);
        return ProjectsController.ToCatalogDto(product);
    }

    private static void ApplyCatalog(CanonicalProduct product, CatalogInput request)
    {
        var unit = UnitConverter.Resolve(request.BaseUnit) ?? throw new ValidationException($"\"{request.BaseUnit}\" is not a recognized unit.");
        string? gtin = null;
        if (!string.IsNullOrWhiteSpace(request.Gtin))
            gtin = ProductMatcher.NormalizeGtin(request.Gtin) ?? throw new ValidationException("The GTIN is not valid (wrong length or check digit).");
        product.Name = request.Name.Trim();
        product.NormalizedName = ProductMatcher.NormalizeTitle(request.Name);
        product.Industry = request.Industry;
        product.Category = request.Category.Trim();
        product.Brand = Clean(request.Brand);
        product.Model = Clean(request.Model);
        product.Mpn = Clean(request.Mpn);
        product.Gtin = gtin;
        product.Material = Clean(request.Material);
        product.BaseUnit = unit.Code;
        product.IsSafetyCritical = request.IsSafetyCritical;
        product.SafetyNote = Clean(request.SafetyNote);
        foreach (var (key, value) in request.Specifications ?? new())
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                product.Specifications.Add(new ProductSpecification { Key = key.Trim().ToLowerInvariant().Replace(' ', '_'), Value = value.Trim() });
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [HttpGet("matches")]
    public async Task<List<AdminMatch>> Matches([FromQuery] MatchStatus status = MatchStatus.NeedsReview, CancellationToken ct = default)
    {
        var matches = await db.ProductMatches.AsNoTracking().Where(m => m.Status == status)
            .Include(m => m.SupplierProduct!).ThenInclude(p => p.Supplier)
            .Include(m => m.SupplierProduct!).ThenInclude(p => p.Offers.Where(o => o.IsCurrent))
            .Include(m => m.CanonicalProduct!).ThenInclude(p => p.Specifications)
            .AsSplitQuery().OrderBy(m => m.Confidence).Take(300).ToListAsync(ct);
        return matches.Select(m =>
        {
            var listing = m.SupplierProduct!;
            Dictionary<string, string> specs;
            try { specs = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(listing.SpecsJson) ?? new(); }
            catch (System.Text.Json.JsonException) { specs = new(); }
            return new AdminMatch(m.Id, m.Status.ToString(), m.Method.ToString(), m.Confidence, m.Reasons.Split(" | ", StringSplitOptions.RemoveEmptyEntries), listing.Supplier!.Name,
                listing.Title, listing.Brand, listing.Model, listing.Gtin, listing.PackQuantity, listing.PackUnit, specs, listing.Offers.FirstOrDefault(o => o.IsCurrent)?.Price,
                m.CanonicalProductId, m.CanonicalProduct!.Name, m.CanonicalProduct.BaseUnit,
                m.CanonicalProduct.Specifications.GroupBy(s => s.Key).ToDictionary(g => g.Key, g => g.First().Value), m.UpdatedAt);
        }).ToList();
    }

    [HttpPost("matches/{id:guid}/confirm")]
    public Task<IActionResult> ConfirmMatch(Guid id, CancellationToken ct) => ReviewMatchAsync(id, MatchStatus.Confirmed, ct);

    [HttpPost("matches/{id:guid}/reject")]
    public Task<IActionResult> RejectMatch(Guid id, CancellationToken ct) => ReviewMatchAsync(id, MatchStatus.Rejected, ct);

    private async Task<IActionResult> ReviewMatchAsync(Guid id, MatchStatus decision, CancellationToken ct)
    {
        var match = await db.ProductMatches.FirstOrDefaultAsync(m => m.Id == id, ct) ?? throw new NotFoundException("Match not found.");
        match.Status = decision;
        match.Method = decision == MatchStatus.Confirmed ? MatchMethod.Manual : match.Method;
        match.ReviewedByUserId = UserId;
        match.ReviewedAt = DateTime.UtcNow;
        audit.Record(decision == MatchStatus.Confirmed ? "admin.match_confirmed" : "admin.match_rejected", nameof(ProductMatch), id);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---------- Usage and logs ----------

    [HttpGet("usage/providers")]
    public async Task<object> ProviderUsage(CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-7);
        var logs = await db.ProviderSyncLogs.AsNoTracking().Include(l => l.Supplier).Where(l => l.CreatedAt >= since).OrderByDescending(l => l.CreatedAt).Take(5000).ToListAsync(ct);
        return new
        {
            WindowDays = 7,
            BySupplier = logs.GroupBy(l => l.Supplier!.Name).Select(g => new UsageBucket(g.Key, g.Count(), g.Count(l => !l.Success), 0, 0, 0, Math.Round(g.Average(l => l.DurationMs), 1))).OrderBy(b => b.Key).ToList(),
            Recent = logs.Take(50).Select(l => new { l.Id, Supplier = l.Supplier!.Name, l.Operation, l.Success, l.DurationMs, l.ItemCount, l.Error, l.CreatedAt }).ToList(),
        };
    }

    [HttpGet("usage/ai")]
    public async Task<object> AiUsage(CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-30);
        var logs = await db.AiUsageLogs.AsNoTracking().Where(l => l.CreatedAt >= since).OrderByDescending(l => l.CreatedAt).Take(10000).ToListAsync(ct);
        static UsageBucket Bucket(string key, IEnumerable<AiUsageLog> source)
        {
            var list = source.ToList();
            return new UsageBucket(key, list.Count, list.Count(l => !l.Success), list.Sum(l => (long)l.InputTokens), list.Sum(l => (long)l.OutputTokens), list.Sum(l => l.EstimatedCostUsd), Math.Round(list.Average(l => l.DurationMs), 1));
        }
        return new
        {
            WindowDays = 30,
            ActiveProvider = ai.ActiveProvider.Name,
            ActiveModel = ai.ActiveProvider.Model,
            CostNote = "Cost is an estimate from the per-token rates in server configuration. The mock provider is free.",
            Total = logs.Count == 0 ? new UsageBucket("All", 0, 0, 0, 0, 0, 0) : Bucket("All", logs),
            ByFeature = logs.GroupBy(l => l.Feature).Select(g => Bucket(g.Key, g)).OrderBy(b => b.Key).ToList(),
            ByProvider = logs.GroupBy(l => $"{l.Provider} / {l.Model}").Select(g => Bucket(g.Key, g)).OrderBy(b => b.Key).ToList(),
            Recent = logs.Take(50).Select(l => new { l.Id, l.Provider, l.Model, l.Feature, l.InputTokens, l.OutputTokens, l.EstimatedCostUsd, l.Success, l.DurationMs, l.Error, l.CreatedAt }).ToList(),
        };
    }

    [HttpGet("analytics/projects")]
    public async Task<object> ProjectAnalytics(CancellationToken ct)
    {
        var projects = await db.PurchasingProjects.AsNoTracking().Select(p => new { p.Status, p.Industry, p.CreatedAt }).ToListAsync(ct);
        var runs = await db.ComparisonRuns.AsNoTracking().Where(r => r.BaselineCost != null && r.OptimizedCost != null).Select(r => new { r.ProjectId, r.BaselineCost, r.OptimizedCost, r.CreatedAt }).ToListAsync(ct);
        var latest = runs.GroupBy(r => r.ProjectId).Select(g => g.OrderByDescending(r => r.CreatedAt).First()).ToList();
        var baseline = latest.Sum(r => r.BaselineCost!.Value);
        var optimized = latest.Sum(r => r.OptimizedCost!.Value);
        return new
        {
            TotalProjects = projects.Count,
            ByStatus = projects.GroupBy(p => p.Status.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            ByIndustry = projects.GroupBy(p => ProjectsController.IndustryLabel(p.Industry)).ToDictionary(g => g.Key, g => g.Count()),
            CreatedLast30Days = projects.Count(p => p.CreatedAt >= DateTime.UtcNow.AddDays(-30)),
            ComparisonRuns = await db.ComparisonRuns.CountAsync(ct),
            ProjectsWithPricedPlan = latest.Count,
            BaselineTotal = baseline,
            OptimizedTotal = optimized,
            AverageSavingsPercent = baseline > 0 ? Math.Round((baseline - optimized) / baseline * 100m, 1) : 0m,
            Note = "Savings compare each project's latest single-supplier baseline with its lowest-cost plan. Demo tenants are included and use demo prices.",
        };
    }

    [HttpGet("affiliate-clicks")]
    public async Task<object> AffiliateClicks(CancellationToken ct)
    {
        var clicks = await db.AffiliateClicks.AsNoTracking().Include(c => c.Supplier).OrderByDescending(c => c.CreatedAt).Take(2000).ToListAsync(ct);
        return new
        {
            Total = clicks.Count,
            TaggedAffiliateClicks = clicks.Count(c => c.IsAffiliateLink),
            Note = "Outbound clicks are logged for every supplier link. A click only counts as affiliate when a real, configured affiliate tag was attached; none are configured by default.",
            BySupplier = clicks.GroupBy(c => c.Supplier!.Name).Select(g => new { Supplier = g.Key, Clicks = g.Count(), Affiliate = g.Count(c => c.IsAffiliateLink) }).OrderByDescending(x => x.Clicks).ToList(),
            Recent = clicks.Take(50).Select(c => new { c.Id, Supplier = c.Supplier!.Name, c.TargetUrl, c.IsAffiliateLink, c.CreatedAt }).ToList(),
        };
    }

    [HttpGet("errors")]
    public async Task<object> Errors(CancellationToken ct) =>
        await db.ErrorLogs.AsNoTracking().OrderByDescending(e => e.CreatedAt).Take(100).Select(e => new { e.Id, e.Message, e.Path, e.Method, e.TraceId, e.Detail, e.UserId, e.CreatedAt }).ToListAsync(ct);

    [HttpGet("audit")]
    public async Task<object> Audit([FromQuery] Guid? organizationId, CancellationToken ct) =>
        await db.AuditLogs.AsNoTracking().Where(a => organizationId == null || a.OrganizationId == organizationId).OrderByDescending(a => a.CreatedAt).Take(200)
            .Select(a => new { a.Id, a.Action, a.EntityType, a.EntityId, a.Details, a.OrganizationId, a.UserId, a.IpAddress, a.CreatedAt }).ToListAsync(ct);

    // ---------- Feature flags and operations ----------

    [HttpGet("flags")]
    public async Task<List<FlagDto>> Flags(CancellationToken ct) =>
        await db.FeatureFlags.AsNoTracking().OrderBy(f => f.Key).Select(f => new FlagDto(f.Key, f.Description, f.IsEnabled, f.UpdatedAt)).ToListAsync(ct);

    [HttpPut("flags/{key}")]
    public async Task<IActionResult> SetFlag(string key, FlagUpdate request, CancellationToken ct)
    {
        var flag = await db.FeatureFlags.FirstOrDefaultAsync(f => f.Key == key, ct) ?? throw new NotFoundException("Feature flag not found.");
        flag.IsEnabled = request.IsEnabled;
        audit.Record("admin.flag_changed", nameof(FeatureFlag), key, request.IsEnabled ? "enabled" : "disabled");
        await db.SaveChangesAsync(ct);
        flags.Invalidate();
        return NoContent();
    }

    /// <summary>Deletes the three sample businesses and recreates them from scratch.</summary>
    [HttpPost("demo/reset")]
    public async Task<IActionResult> ResetDemo(CancellationToken ct)
    {
        // Runs in its own scope so the seeder's tenant switching cannot leak into this request.
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DataSeeder>().ResetDemoAsync(ct);
        audit.Record("admin.demo_reset");
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("jobs/price-alerts/run")]
    public Task<AlertJobResult> RunPriceAlerts(CancellationToken ct) => PriceAlertScheduler.RunOnceAsync(scopes, null, ct);
}
