using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartBuy.Api.Infrastructure;
using SmartBuy.Domain.Entities;
using SmartBuy.Domain.Rules;
using SmartBuy.Domain.Suppliers;
using SmartBuy.Infrastructure;
using SmartBuy.Infrastructure.Persistence;
using SmartBuy.Infrastructure.Services;
using ValidationException = SmartBuy.Infrastructure.Services.ValidationException;

namespace SmartBuy.Api.Controllers;

public sealed record AlertDto(Guid Id, string Type, Guid ProductId, string ProductName, string Unit, Guid? SupplierId, string? SupplierName, decimal? TargetPrice,
    bool IsActive, DateTime? LastCheckedAt, DateTime? LastTriggeredAt, decimal? LastTriggeredPrice, decimal? CurrentBestUnitPrice, string? CurrentBestSupplier, DateTime CreatedAt);

public sealed record AlertInput(PriceAlertType Type, Guid ProductId, Guid? SupplierId, [Range(0.0001, 10_000_000)] decimal? TargetPrice, bool IsActive = true);

public sealed record NotificationDto(Guid Id, string Title, string Body, DateTime CreatedAt, bool IsRead, Guid? PriceAlertId);

public sealed record SupplierDto(Guid Id, string Code, string Name, string Kind, string Mode, bool IsEnabled, string? WebsiteUrl, decimal? ShippingFlatFee, decimal? FreeShippingThreshold,
    decimal? MinimumOrderAmount, string? IntegrationNotes, bool IsHealthy, string StatusMessage, ProviderCapabilities? Capabilities,
    bool IsSaved, bool IsPreferred, string? AccountNumber, string? OrderEmail, string? Notes);

public sealed record SavedSupplierInput(bool IsPreferred, [MaxLength(100)] string? AccountNumber, [EmailAddress, MaxLength(320)] string? OrderEmail, [MaxLength(1000)] string? Notes);

public sealed record DashboardComparison(Guid ProjectId, string ProjectName, DateTime RanAt, int ItemCount, int MatchedItemCount, int SupplierCount, decimal? BaselineCost, string? BaselineLabel, decimal? OptimizedCost, bool HasUnknowns);
public sealed record DashboardProject(Guid Id, string Name, string Status, DateTime UpdatedAt, int ItemCount, bool NeedsReview);
public sealed record DashboardOrder(Guid Id, string PoNumber, string Status, string SupplierName, decimal Total, DateTime CreatedAt);
public sealed record SupplierStatusSummary(Guid Id, string Name, string Mode, bool IsHealthy, string Message);
public sealed record Dashboard(
    int TotalProjects, int ActiveProjects, int ShoppingLists, int ListsAwaitingReview, int PendingPurchaseOrders, int ActivePriceAlerts, int UnreadNotifications,
    decimal SavingsOpportunity, decimal SavingsBaselineTotal, decimal SavingsOptimizedTotal, int SavingsProjectCount, string SavingsBasis, bool SavingsContainDemoData,
    IReadOnlyList<DashboardProject> RecentProjects, IReadOnlyList<DashboardComparison> RecentComparisons, IReadOnlyList<DashboardOrder> PendingOrders,
    IReadOnlyList<DashboardOrder> PurchaseHistory, IReadOnlyList<SupplierStatusSummary> Suppliers, int ProjectsThisMonth, int? ProjectQuota);

public sealed record BillingOverview(PlanInfo CurrentPlan, string SubscriptionStatus, IReadOnlyList<PlanInfo> Plans, BillingStatus Billing, int ProjectsThisMonth, int Members, string PricingNote);
public sealed record PlanRequest([Required] string PlanCode);
public sealed record CheckoutResponse(string Url);
public sealed record SpendRow(string Label, int Orders, decimal Total);
public sealed record SpendReport(decimal TotalCommitted, int OrderCount, IReadOnlyList<SpendRow> BySupplier, IReadOnlyList<SpendRow> ByMonth, IReadOnlyList<SpendRow> ByProject, string Basis);

[ApiController]
[Route("api")]
[RequireRole]
public sealed class WorkspaceController(
    AppDbContext db,
    AuditService audit,
    EntitlementService entitlements,
    SupplierCatalogService catalog,
    ISupplierProviderRegistry registry,
    BillingService billing,
    IServiceScopeFactory scopes,
    TimeProvider time) : ApiController
{
    // ---------- Dashboard ----------

    [HttpGet("dashboard")]
    public async Task<Dashboard> Dashboard(CancellationToken ct)
    {
        var projects = await db.PurchasingProjects.AsNoTracking().OrderByDescending(p => p.UpdatedAt)
            .Select(p => new { p.Id, p.Name, p.Status, p.UpdatedAt, Items = p.ShoppingLists.SelectMany(l => l.Items).Count(), NeedsReview = p.ShoppingLists.Any(l => !l.IsApproved) })
            .ToListAsync(ct);
        var lists = await db.ShoppingLists.AsNoTracking().Select(l => l.IsApproved).ToListAsync(ct);
        var orders = await db.PurchaseOrders.AsNoTracking().Include(o => o.Supplier).OrderByDescending(o => o.CreatedAt).Take(200).ToListAsync(ct);
        var runs = await db.ComparisonRuns.AsNoTracking().Include(r => r.Project).OrderByDescending(r => r.CreatedAt).Take(200).ToListAsync(ct);
        var alerts = await db.PriceAlerts.CountAsync(a => a.IsActive, ct);
        var unread = await db.Notifications.CountAsync(n => n.ReadAt == null, ct);
        var plan = await entitlements.GetPlanAsync(ct);

        // Savings = latest comparison per live project, baseline minus optimized. Projects without a priced plan are excluded.
        var latest = runs.Where(r => r.Project is not null && r.BaselineCost is not null && r.OptimizedCost is not null)
            .GroupBy(r => r.ProjectId).Select(g => g.First()).ToList();
        var baselineTotal = latest.Sum(r => r.BaselineCost!.Value);
        var optimizedTotal = latest.Sum(r => r.OptimizedCost!.Value);

        static DashboardOrder Order(PurchaseOrder o) => new(o.Id, o.PoNumber, PurchaseOrderService.Label(o.Status), o.Supplier?.Name ?? "", o.Total, o.CreatedAt);
        var pendingStatuses = new[] { PurchaseOrderStatus.Draft, PurchaseOrderStatus.PendingApproval, PurchaseOrderStatus.Approved };
        var historyStatuses = new[] { PurchaseOrderStatus.Sent, PurchaseOrderStatus.PartiallyReceived, PurchaseOrderStatus.Received };

        var suppliers = await db.Suppliers.AsNoTracking().Where(s => s.IsEnabled).OrderBy(s => s.Name).ToListAsync(ct);
        var statuses = new List<SupplierStatusSummary>();
        foreach (var supplier in suppliers)
        {
            var status = registry.Get(supplier.ProviderKey) is { } provider ? await provider.GetProviderStatusAsync(ct) : null;
            statuses.Add(new SupplierStatusSummary(supplier.Id, supplier.Name, supplier.Mode.ToString(), status?.IsHealthy ?? false, status?.Message ?? "No adapter registered."));
        }

        var anyDemo = suppliers.Any(s => s.Mode == SupplierMode.Mock);
        return new Dashboard(
            projects.Count, projects.Count(p => p.Status is ProjectStatus.Active or ProjectStatus.Draft), lists.Count, lists.Count(approved => !approved),
            orders.Count(o => pendingStatuses.Contains(o.Status)), alerts, unread,
            Math.Max(0, baselineTotal - optimizedTotal), baselineTotal, optimizedTotal, latest.Count,
            "Latest comparison of each project: single-supplier baseline minus the lowest-cost plan, using known merchandise, shipping and tax only.",
            anyDemo,
            projects.Take(6).Select(p => new DashboardProject(p.Id, p.Name, p.Status.ToString(), p.UpdatedAt, p.Items, p.NeedsReview)).ToList(),
            runs.Where(r => r.Project is not null).Take(6).Select(r => new DashboardComparison(r.ProjectId, r.Project!.Name, r.CreatedAt, r.ItemCount, r.MatchedItemCount, r.SupplierCount, r.BaselineCost, r.BaselineLabel, r.OptimizedCost, r.HasUnknowns)).ToList(),
            orders.Where(o => pendingStatuses.Contains(o.Status)).Take(6).Select(Order).ToList(),
            orders.Where(o => historyStatuses.Contains(o.Status)).Take(6).Select(Order).ToList(),
            statuses, await entitlements.ProjectsThisMonthAsync(ct), plan.MaxProjectsPerMonth);
    }

    // ---------- Price alerts ----------

    [HttpGet("price-alerts")]
    public async Task<List<AlertDto>> Alerts(CancellationToken ct)
    {
        var alerts = await db.PriceAlerts.AsNoTracking().Include(a => a.CanonicalProduct).Include(a => a.Supplier).OrderByDescending(a => a.CreatedAt).ToListAsync(ct);
        var productIds = alerts.Select(a => a.CanonicalProductId).Distinct().ToList();
        var offers = await PriceAlertJob.LoadOffersAsync(db, productIds, time.GetUtcNow().UtcDateTime, ct);
        return alerts.Select(a =>
        {
            var best = offers.TryGetValue(a.CanonicalProductId, out var list)
                ? list.Where(o => a.Type == PriceAlertType.BetterOffer || a.SupplierId is null || o.SupplierId == a.SupplierId).OrderBy(o => o.UnitPrice).FirstOrDefault()
                : null;
            return new AlertDto(a.Id, a.Type.ToString(), a.CanonicalProductId, a.CanonicalProduct!.Name, a.CanonicalProduct.BaseUnit, a.SupplierId, a.Supplier?.Name, a.TargetPrice,
                a.IsActive, a.LastCheckedAt, a.LastTriggeredAt, a.LastTriggeredPrice, best?.UnitPrice, best?.SupplierName, a.CreatedAt);
        }).ToList();
    }

    [HttpPost("price-alerts"), RequireRole(OrgRole.Employee)]
    public async Task<IActionResult> CreateAlert(AlertInput request, CancellationToken ct)
    {
        await entitlements.RequireAsync(PlanFeature.PriceAlerts, ct);
        await ValidateAlertAsync(request, ct);
        if (await db.PriceAlerts.CountAsync(ct) >= 200) throw new BusinessRuleException("An organization can have at most 200 price alerts.");
        var alert = new PriceAlert { CanonicalProductId = request.ProductId, SupplierId = request.SupplierId, Type = request.Type, TargetPrice = request.TargetPrice, IsActive = request.IsActive, CreatedByUserId = UserId };
        db.PriceAlerts.Add(alert);
        // Make sure there is price data to evaluate against from the first run.
        await catalog.RefreshAsync(new[] { request.ProductId }, force: false, ct);
        audit.Record("price_alert.created", nameof(PriceAlert), alert.Id, request.Type.ToString());
        await db.SaveChangesAsync(ct);
        return Created($"/api/price-alerts/{alert.Id}", new { alert.Id });
    }

    [HttpPut("price-alerts/{id:guid}"), RequireRole(OrgRole.Employee)]
    public async Task<IActionResult> UpdateAlert(Guid id, AlertInput request, CancellationToken ct)
    {
        var alert = await db.PriceAlerts.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException("Price alert not found.");
        await ValidateAlertAsync(request, ct);
        if (request.IsActive && !alert.IsActive) await entitlements.RequireAsync(PlanFeature.PriceAlerts, ct);
        alert.Type = request.Type;
        alert.CanonicalProductId = request.ProductId;
        alert.SupplierId = request.SupplierId;
        alert.TargetPrice = request.TargetPrice;
        alert.IsActive = request.IsActive;
        alert.LastTriggeredPrice = null;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("price-alerts/{id:guid}"), RequireRole(OrgRole.Employee)]
    public async Task<IActionResult> DeleteAlert(Guid id, CancellationToken ct)
    {
        var alert = await db.PriceAlerts.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException("Price alert not found.");
        db.PriceAlerts.Remove(alert);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Runs the alert job now for this organization only (manual execution for development and demos).</summary>
    [HttpPost("price-alerts/run"), RequireRole(OrgRole.Employee)]
    public async Task<AlertJobResult> RunAlerts(CancellationToken ct)
    {
        await entitlements.RequireAsync(PlanFeature.PriceAlerts, ct);
        return await PriceAlertScheduler.RunOnceAsync(scopes, OrganizationId, ct);
    }

    private async Task ValidateAlertAsync(AlertInput request, CancellationToken ct)
    {
        if (!await db.CanonicalProducts.AnyAsync(p => p.Id == request.ProductId, ct)) throw new ValidationException("Choose a catalog product.");
        if (request.SupplierId is { } supplierId && !await db.Suppliers.AnyAsync(s => s.Id == supplierId, ct)) throw new ValidationException("Unknown supplier.");
        if (request.Type == PriceAlertType.PriceBelow && request.TargetPrice is null) throw new ValidationException("Enter the target price per unit.");
        if (request.Type == PriceAlertType.BetterOffer && request.SupplierId is null && request.TargetPrice is null)
            throw new ValidationException("Choose your current supplier or enter the price you pay now.");
    }

    [HttpGet("notifications")]
    public async Task<List<NotificationDto>> Notifications(CancellationToken ct) =>
        await db.Notifications.AsNoTracking().OrderByDescending(n => n.CreatedAt).Take(100)
            .Select(n => new NotificationDto(n.Id, n.Title, n.Body, n.CreatedAt, n.ReadAt != null, n.PriceAlertId)).ToListAsync(ct);

    [HttpPost("notifications/read-all")]
    public async Task<IActionResult> ReadAll(CancellationToken ct)
    {
        var unread = await db.Notifications.Where(n => n.ReadAt == null).ToListAsync(ct);
        foreach (var n in unread) n.ReadAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---------- Suppliers ----------

    [HttpGet("suppliers")]
    public async Task<List<SupplierDto>> Suppliers(CancellationToken ct)
    {
        var suppliers = await db.Suppliers.AsNoTracking().Where(s => s.IsEnabled).OrderBy(s => s.Name).ToListAsync(ct);
        var saved = await db.SavedSuppliers.AsNoTracking().ToDictionaryAsync(s => s.SupplierId, ct);
        var result = new List<SupplierDto>();
        foreach (var s in suppliers)
        {
            var status = registry.Get(s.ProviderKey) is { } provider ? await provider.GetProviderStatusAsync(ct) : null;
            saved.TryGetValue(s.Id, out var mine);
            result.Add(new SupplierDto(s.Id, s.Code, s.Name, s.Kind.ToString(), s.Mode.ToString(), s.IsEnabled, s.WebsiteUrl, s.ShippingFlatFee, s.FreeShippingThreshold, s.MinimumOrderAmount,
                s.IntegrationNotes, status?.IsHealthy ?? false, status?.Message ?? "No adapter registered.", status?.Capabilities,
                mine is not null, mine?.IsPreferred ?? false, mine?.AccountNumber, mine?.OrderEmail, mine?.Notes));
        }
        return result;
    }

    [HttpPut("suppliers/{supplierId:guid}/saved"), RequireRole(OrgRole.PurchasingManager)]
    public async Task<IActionResult> SaveSupplier(Guid supplierId, SavedSupplierInput request, CancellationToken ct)
    {
        if (!await db.Suppliers.AnyAsync(s => s.Id == supplierId, ct)) throw new NotFoundException("Supplier not found.");
        var saved = await db.SavedSuppliers.FirstOrDefaultAsync(s => s.SupplierId == supplierId, ct);
        if (saved is null)
        {
            saved = new SavedSupplier { SupplierId = supplierId };
            db.SavedSuppliers.Add(saved);
        }
        saved.IsPreferred = request.IsPreferred;
        saved.AccountNumber = string.IsNullOrWhiteSpace(request.AccountNumber) ? null : request.AccountNumber.Trim();
        saved.OrderEmail = string.IsNullOrWhiteSpace(request.OrderEmail) ? null : request.OrderEmail.Trim();
        saved.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        audit.Record("supplier.saved", nameof(Supplier), supplierId);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("suppliers/{supplierId:guid}/saved"), RequireRole(OrgRole.PurchasingManager)]
    public async Task<IActionResult> UnsaveSupplier(Guid supplierId, CancellationToken ct)
    {
        var saved = await db.SavedSuppliers.FirstOrDefaultAsync(s => s.SupplierId == supplierId, ct);
        if (saved is not null)
        {
            db.SavedSuppliers.Remove(saved);
            await db.SaveChangesAsync(ct);
        }
        return NoContent();
    }

    // ---------- Billing ----------

    [HttpGet("billing")]
    public async Task<BillingOverview> Billing(CancellationToken ct)
    {
        var plans = await db.Plans.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.SortOrder).ToListAsync(ct);
        var subscription = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(ct);
        return new BillingOverview(PlanInfo.From(await entitlements.GetPlanAsync(ct)), (subscription?.Status ?? SubscriptionStatus.Active).ToString(),
            plans.Select(PlanInfo.From).ToList(), await billing.StatusAsync(ct), await entitlements.ProjectsThisMonthAsync(ct), await db.OrganizationMembers.CountAsync(ct),
            "Prices shown are proposed test prices for this preview, not established market prices.");
    }

    [HttpPost("billing/checkout"), RequireRole(OrgRole.Owner)]
    public async Task<CheckoutResponse> Checkout(PlanRequest request, CancellationToken ct) => new(await billing.CreateCheckoutUrlAsync(request.PlanCode, ct));

    /// <summary>Switches plan without payment. Only available when Stripe is not configured and the server (or a demo tenant) allows it.</summary>
    [HttpPost("billing/change-plan"), RequireRole(OrgRole.Owner)]
    public async Task<IActionResult> ChangePlan(PlanRequest request, CancellationToken ct)
    {
        await billing.ChangePlanManuallyAsync(request.PlanCode, ct);
        return NoContent();
    }

    /// <summary>Stripe webhook. Authenticated by signature, not by a user token.</summary>
    [HttpPost("billing/webhook"), AllowAnonymous]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> StripeWebhook(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var payload = await reader.ReadToEndAsync(ct);
        using var elevated = Tenant.Elevate();
        return await billing.HandleWebhookAsync(payload, Request.Headers["Stripe-Signature"], ct) ? Ok() : BadRequest();
    }

    // ---------- Reports ----------

    private async Task<SpendReport> BuildSpendAsync(CancellationToken ct)
    {
        await entitlements.RequireAsync(PlanFeature.AdvancedReporting, ct);
        var counted = new[] { PurchaseOrderStatus.Approved, PurchaseOrderStatus.Sent, PurchaseOrderStatus.PartiallyReceived, PurchaseOrderStatus.Received };
        var orders = await db.PurchaseOrders.AsNoTracking().Include(o => o.Supplier).Include(o => o.Project).Where(o => counted.Contains(o.Status)).ToListAsync(ct);
        static List<SpendRow> Group(IEnumerable<PurchaseOrder> source, Func<PurchaseOrder, string> key) =>
            source.GroupBy(key).Select(g => new SpendRow(g.Key, g.Count(), g.Sum(o => o.Total))).OrderByDescending(r => r.Total).ToList();
        return new SpendReport(orders.Sum(o => o.Total), orders.Count,
            Group(orders, o => o.Supplier?.Name ?? "Unknown"),
            Group(orders, o => o.CreatedAt.ToString("yyyy-MM", CultureInfo.InvariantCulture)).OrderBy(r => r.Label, StringComparer.Ordinal).ToList(),
            Group(orders, o => o.Project?.Name ?? "No project"),
            "Committed spend: purchase orders that are Approved, Sent, Partially Received or Received. Totals use each order's known amounts.");
    }

    [HttpGet("reports/spend"), RequireRole(OrgRole.PurchasingManager)]
    public Task<SpendReport> Spend(CancellationToken ct) => BuildSpendAsync(ct);

    [HttpGet("reports/spend.csv"), RequireRole(OrgRole.PurchasingManager)]
    public async Task<IActionResult> SpendCsv(CancellationToken ct)
    {
        var report = await BuildSpendAsync(ct);
        var sb = new StringBuilder("Grouping,Label,Orders,Total\n");
        foreach (var (name, rows) in new[] { ("Supplier", report.BySupplier), ("Month", report.ByMonth), ("Project", report.ByProject) })
            foreach (var row in rows)
                sb.AppendLine($"{name},{PurchaseOrderExport.CsvCell(row.Label)},{row.Orders},{row.Total.ToString("0.00", CultureInfo.InvariantCulture)}");
        return File(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", "smartbuy-spend-report.csv");
    }
}
