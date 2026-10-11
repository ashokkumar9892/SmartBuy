using Microsoft.EntityFrameworkCore;
using SmartBuy.Domain.Entities;
using SmartBuy.Domain.Optimization;
using SmartBuy.Domain.Pricing;
using SmartBuy.Domain.Rules;
using SmartBuy.Domain.Units;
using SmartBuy.Infrastructure.Persistence;

namespace SmartBuy.Infrastructure.Services;

public sealed record OfferRow(
    Guid OfferId,
    Guid SupplierId,
    string SupplierName,
    Guid SupplierProductId,
    string ProductTitle,
    string? Brand,
    string? Model,
    decimal PackQuantity,
    string PackUnit,
    decimal PackPrice,
    decimal? UnitPrice,
    string UnitPriceUnit,
    int? PacksRequired,
    decimal? PurchasedQuantity,
    decimal? Subtotal,
    decimal? Shipping,
    bool ShippingUnknown,
    decimal? Tax,
    bool TaxUnknown,
    decimal? EstimatedTotal,
    int? StockPacks,
    string StockStatus,
    int? DeliveryDaysMin,
    int? DeliveryDaysMax,
    string DataStatus,
    DateTime FetchedAt,
    bool IsSubstitute,
    bool Usable,
    string? UnusableReason,
    double MatchConfidence,
    string MatchMethod,
    bool IsLowestTotal);

public sealed record ItemComparison(
    Guid ItemId,
    int LineNumber,
    string Description,
    decimal Quantity,
    string Unit,
    Guid? CanonicalProductId,
    string? CanonicalProductName,
    bool IsSafetyCritical,
    Guid? SubstituteProductId,
    string? SubstituteProductName,
    IReadOnlyList<OfferRow> Offers,
    int ListingsNeedingReview,
    string? Problem);

public sealed record PlanLineDto(
    Guid ItemId,
    string Description,
    decimal Quantity,
    string Unit,
    Guid SupplierId,
    string SupplierName,
    Guid OfferId,
    Guid SupplierProductId,
    string ProductTitle,
    int Packs,
    decimal PackPrice,
    decimal LineCost,
    decimal UnitPrice,
    int? DeliveryDays,
    bool IsSubstitute,
    string DataStatus);

public sealed record TotalsDto(
    decimal Subtotal,
    decimal KnownShipping,
    bool ShippingUnknown,
    decimal? Tax,
    bool TaxUnknown,
    decimal KnownTotal,
    bool IsComplete,
    IReadOnlyList<SupplierTotals> Suppliers)
{
    public static TotalsDto From(OrderTotals t) => new(t.Subtotal, t.KnownShipping, t.ShippingUnknown, t.Tax, t.TaxUnknown, t.KnownTotal, t.IsComplete, t.Suppliers);
}

public sealed record PlanDto(
    string Strategy,
    string Title,
    string Summary,
    IReadOnlyList<PlanLineDto> Lines,
    IReadOnlyList<string> UnfilledItems,
    TotalsDto Totals,
    int? MaxDeliveryDays,
    bool DeliveryUnknown,
    bool? MeetsDeadline,
    decimal? Savings,
    decimal? SavingsPercent,
    IReadOnlyList<string> Uncertainties,
    string Method);

public sealed record BaselineDto(string Label, string Explanation, TotalsDto Totals, IReadOnlyList<PlanLineDto> Lines);

public sealed record LockedStrategy(string Strategy, string Title, string Reason);

public sealed record ComparisonResult(
    Guid ProjectId,
    string ProjectName,
    DateTime GeneratedAt,
    IReadOnlyList<ItemComparison> Items,
    BaselineDto? Baseline,
    IReadOnlyList<PlanDto> Plans,
    IReadOnlyList<LockedStrategy> LockedStrategies,
    IReadOnlyList<ProviderIssue> ProviderIssues,
    IReadOnlyList<string> Warnings,
    decimal? TaxRatePercent,
    int? DeadlineDays,
    bool ContainsDemoData,
    bool ContainsStaleData);

/// <summary>
/// Builds the supplier comparison and the three purchasing plans for a project. Every figure is computed
/// deterministically from stored offers; this class never calls an AI provider.
/// </summary>
public sealed class ComparisonService(
    AppDbContext db,
    ITenantContext tenant,
    SupplierCatalogService catalog,
    EntitlementService entitlements,
    TimeProvider time)
{
    public static string StrategyTitle(PurchasingStrategy s) => s switch
    {
        PurchasingStrategy.LowestCost => "Lowest Cost",
        PurchasingStrategy.FastestDelivery => "Fastest Delivery",
        _ => "Balanced"
    };

    private static string StrategySummary(PurchasingStrategy s) => s switch
    {
        PurchasingStrategy.LowestCost => "Minimizes the estimated total of merchandise plus known shipping across all suppliers.",
        PurchasingStrategy.FastestDelivery => "Chooses the offers that arrive soonest, then the cheapest among those.",
        _ => $"Weighs cost against speed: each delivery day counts as {ProcurementOptimizer.BalancedDayWeight * 100:0}% of a line's cost and preferred suppliers get a {ProcurementOptimizer.BalancedPreferenceCredit * 100:0}% credit."
    };

    public async Task<ComparisonResult> CompareAsync(Guid projectId, bool forceRefresh, bool record, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var project = await db.PurchasingProjects.AsNoTracking()
            .Include(p => p.ShoppingLists).ThenInclude(l => l.Items)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == projectId, ct) ?? throw new NotFoundException("Project not found.");

        var warnings = new List<string>();
        var approvedLists = project.ShoppingLists.Where(l => l.IsApproved).ToList();
        var pending = project.ShoppingLists.Count - approvedLists.Count;
        if (approvedLists.Count == 0)
            throw new BusinessRuleException("Review and approve the shopping list quantities before comparing suppliers.", "list_not_approved");
        if (pending > 0) warnings.Add($"{pending} shopping list(s) are not approved yet and were left out of this comparison.");

        var items = approvedLists.SelectMany(l => l.Items).OrderBy(i => i.LineNumber).ThenBy(i => i.CreatedAt).ToList();
        var productIds = items.SelectMany(i => new[] { i.CanonicalProductId, i.ApprovedSubstituteProductId }).Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();

        var report = await catalog.RefreshAsync(productIds, forceRefresh, ct);

        var products = await db.CanonicalProducts.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var matches = await db.ProductMatches.AsNoTracking()
            .Where(m => productIds.Contains(m.CanonicalProductId) && m.SupplierProduct!.Supplier!.IsEnabled)
            .Include(m => m.SupplierProduct!).ThenInclude(sp => sp.Offers.Where(o => o.IsCurrent))
            .Include(m => m.SupplierProduct!).ThenInclude(sp => sp.Supplier)
            .AsSplitQuery()
            .ToListAsync(ct);
        var matchesByProduct = matches.GroupBy(m => m.CanonicalProductId).ToDictionary(g => g.Key, g => g.ToList());

        var organization = await db.Organizations.AsNoTracking().FirstAsync(o => o.Id == tenant.OrganizationId, ct);
        var taxRate = organization.EstimatedTaxRatePercent;
        var preferred = (await db.SavedSuppliers.AsNoTracking().Where(s => s.IsPreferred).Select(s => s.SupplierId).ToListAsync(ct)).ToHashSet();
        var terms = matches.Select(m => m.SupplierProduct!.Supplier!).DistinctBy(s => s.Id)
            .ToDictionary(s => s.Id, s => new SupplierTerms(s.Id, s.Name, s.ShippingFlatFee, s.FreeShippingThreshold, s.MinimumOrderAmount, preferred.Contains(s.Id)));

        var requirements = new List<ItemRequirement>();
        var options = new Dictionary<Guid, IReadOnlyList<OfferOption>>();
        var comparisons = new List<ItemComparison>();
        var rowLookup = new Dictionary<Guid, (OfferRow Row, ShoppingListItem Item)>();
        bool anyDemo = false, anyStale = false;

        foreach (var item in items)
        {
            var requirement = new ItemRequirement(item.Id, item.Description, item.Quantity, item.Unit);
            requirements.Add(requirement);
            products.TryGetValue(item.CanonicalProductId ?? Guid.Empty, out var product);
            products.TryGetValue(item.ApprovedSubstituteProductId ?? Guid.Empty, out var substitute);

            if (product is null)
            {
                comparisons.Add(new ItemComparison(item.Id, item.LineNumber, item.Description, item.Quantity, item.Unit, null, null, false, null, null,
                    Array.Empty<OfferRow>(), 0, "Not linked to a catalog product yet. Choose a product on the list before comparing."));
                options[item.Id] = Array.Empty<OfferOption>();
                continue;
            }

            var rows = new List<OfferRow>();
            var review = 0;
            foreach (var (source, isSubstitute) in new[] { (product, false), (substitute, true) })
            {
                if (source is null || !matchesByProduct.TryGetValue(source.Id, out var productMatches)) continue;
                foreach (var match in productMatches)
                {
                    if (match.Status is MatchStatus.NeedsReview) { if (!isSubstitute) review++; continue; }
                    if (match.Status is MatchStatus.Rejected) continue;
                    var listing = match.SupplierProduct!;
                    var offer = listing.Offers.FirstOrDefault(o => o.IsCurrent);
                    if (offer is null) continue;
                    rows.Add(BuildRow(item, listing, offer, match, terms[listing.SupplierId], taxRate, isSubstitute, now));
                }
            }

            var usable = rows.Where(r => r.Usable).ToList();
            if (usable.Count > 0)
            {
                var lowest = usable.Min(r => r.EstimatedTotal);
                rows = rows.Select(r => r.Usable && r.EstimatedTotal == lowest ? r with { IsLowestTotal = true } : r).ToList();
            }
            rows = rows.OrderByDescending(r => r.Usable).ThenBy(r => r.EstimatedTotal ?? decimal.MaxValue).ThenBy(r => r.SupplierName, StringComparer.Ordinal).ToList();
            foreach (var r in rows) rowLookup[r.OfferId] = (r, item);
            anyDemo |= rows.Any(r => r.DataStatus == "DEMO");
            anyStale |= rows.Any(r => r.DataStatus == "STALE");

            options[item.Id] = rows.Where(r => r.Usable).Select(r => new OfferOption(
                item.Id, r.OfferId, r.SupplierId, r.SupplierProductId, r.PacksRequired!.Value, r.Subtotal!.Value, r.UnitPrice!.Value,
                r.DeliveryDaysMax ?? r.DeliveryDaysMin, r.StockStatus == "unknown", r.IsSubstitute)).ToList();

            string? problem = rows.Count == 0
                ? (review > 0 ? "Supplier listings were found but all need a manual match review before they can be priced." : "No supplier currently lists this product.")
                : usable.Count == 0 ? "Offers exist but none can fill this quantity right now." : null;
            comparisons.Add(new ItemComparison(item.Id, item.LineNumber, item.Description, item.Quantity, item.Unit, product.Id, product.Name, product.IsSafetyCritical,
                substitute?.Id, substitute?.Name, rows, review, problem));
        }

        int? deadlineDays = project.RequiredByDate is { } due ? Math.Max(0, (int)Math.Ceiling((due.Date - now.Date).TotalDays)) : null;
        var problemDefinition = new OptimizationProblem
        {
            Items = requirements,
            Options = options,
            Suppliers = terms,
            TaxRatePercent = taxRate,
            DeadlineDays = deadlineDays,
        };

        var baseline = ProcurementOptimizer.ComputeBaseline(problemDefinition);
        var baselineSeed = baseline?.Lines.ToDictionary(l => l.Item.ItemId, l => l.Option);
        var plan = await entitlements.GetPlanAsync(ct);
        var plans = new List<PlanDto>();
        var locked = new List<LockedStrategy>();
        foreach (var strategy in new[] { PurchasingStrategy.LowestCost, PurchasingStrategy.FastestDelivery, PurchasingStrategy.Balanced })
        {
            if (strategy != PurchasingStrategy.LowestCost && !Entitlements.HasFeature(plan, PlanFeature.AdvancedOptimization))
            {
                locked.Add(new LockedStrategy(strategy.ToString(), StrategyTitle(strategy), $"Advanced optimization is not included in the {plan.Name} plan."));
                continue;
            }
            // Only the cost objective is seeded with the baseline: that is the guarantee "never worse than one-stop shopping".
            var result = ProcurementOptimizer.Optimize(problemDefinition, strategy, strategy == PurchasingStrategy.LowestCost ? baselineSeed : null);
            plans.Add(ToDto(result, baseline, rowLookup, terms));
        }

        var baselineDto = baseline is null ? null : new BaselineDto(baseline.Label, baseline.Explanation, TotalsDto.From(baseline.Totals),
            baseline.Lines.Select(l => ToLine(l, rowLookup, terms)).ToList());

        if (anyStale) warnings.Add("Some prices are STALE: the supplier could not be refreshed, so an older price is shown.");
        if (taxRate is null) warnings.Add("No estimated tax rate is set for your organization, so tax is shown as unknown. Set one in Organization settings.");

        if (record)
        {
            var lowest = plans.FirstOrDefault(p => p.Strategy == nameof(PurchasingStrategy.LowestCost));
            db.ComparisonRuns.Add(new ComparisonRun
            {
                ProjectId = project.Id,
                UserId = tenant.UserId ?? Guid.Empty,
                ItemCount = items.Count,
                MatchedItemCount = comparisons.Count(c => c.Offers.Any(o => o.Usable)),
                SupplierCount = terms.Count,
                BaselineCost = baseline?.Totals.KnownTotal,
                BaselineLabel = baseline?.Label,
                OptimizedCost = lowest is { Lines.Count: > 0 } ? lowest.Totals.KnownTotal : null,
                HasUnknowns = lowest is not null && (!lowest.Totals.IsComplete || lowest.UnfilledItems.Count > 0),
            });
            await db.SaveChangesAsync(ct);
        }

        return new ComparisonResult(project.Id, project.Name, now, comparisons, baselineDto, plans, locked, report.Issues, warnings, taxRate, deadlineDays, anyDemo, anyStale);
    }

    private static OfferRow BuildRow(ShoppingListItem item, SupplierProduct listing, ProductOffer offer, ProductMatch match, SupplierTerms terms, decimal? taxRate, bool isSubstitute, DateTime now)
    {
        var supplier = listing.Supplier!;
        var fit = PackMath.Fit(item.Quantity, item.Unit, listing.PackQuantity, listing.PackUnit, offer.Price, offer.MinOrderPacks);
        var status = OfferFreshness.Status(offer.DataSource, offer.FetchedAt, supplier.CacheTtlMinutes, now);

        string stockStatus;
        string? unusable = null;
        if (fit is null)
        {
            stockStatus = "unknown";
            unusable = $"The list unit \"{item.Unit}\" cannot be converted to this package's unit \"{listing.PackUnit}\".";
        }
        else if (offer.InStock == false || offer.StockPacks == 0)
        {
            stockStatus = "out_of_stock";
            unusable = "Out of stock.";
        }
        else if (offer.StockPacks is { } stock && stock < fit.Packs)
        {
            stockStatus = "insufficient";
            unusable = $"Only {stock} package(s) in stock; {fit.Packs} needed.";
        }
        else stockStatus = offer.StockPacks is null ? "unknown" : "in_stock";

        decimal? shipping = fit is null ? null : OrderCalculator.Shipping(terms, fit.LineCost);
        decimal? tax = fit is null ? null : OrderCalculator.Tax(fit.LineCost, taxRate);
        decimal? total = fit is null ? null : fit.LineCost + (shipping ?? 0) + (tax ?? 0);

        return new OfferRow(
            offer.Id, supplier.Id, supplier.Name, listing.Id, listing.Title, listing.Brand, listing.Model,
            listing.PackQuantity, listing.PackUnit, offer.Price, fit?.UnitPrice, UnitConverter.Normalize(item.Unit) ?? item.Unit,
            fit?.Packs, fit?.PurchasedQuantity, fit?.LineCost,
            shipping, fit is not null && shipping is null, tax, fit is not null && tax is null, total,
            offer.StockPacks, stockStatus, offer.DeliveryDaysMin, offer.DeliveryDaysMax,
            status, offer.FetchedAt, isSubstitute, unusable is null, unusable, match.Confidence, match.Method.ToString(), false);
    }

    private static PlanLineDto ToLine(PlanLine line, Dictionary<Guid, (OfferRow Row, ShoppingListItem Item)> rows, Dictionary<Guid, SupplierTerms> terms)
    {
        var row = rows[line.Option.OfferId].Row;
        return new PlanLineDto(line.Item.ItemId, line.Item.Description, line.Item.Quantity, line.Item.Unit, line.Option.SupplierId,
            terms[line.Option.SupplierId].Name, line.Option.OfferId, line.Option.SupplierProductId, row.ProductTitle, line.Option.Packs,
            row.PackPrice, line.Option.LineCost, line.Option.UnitPrice, line.Option.DeliveryDays, line.Option.IsSubstitute, row.DataStatus);
    }

    private static PlanDto ToDto(PurchasingPlan plan, Baseline? baseline, Dictionary<Guid, (OfferRow Row, ShoppingListItem Item)> rows, Dictionary<Guid, SupplierTerms> terms)
    {
        decimal? savings = null, percent = null;
        if (baseline is not null && plan.Lines.Count > 0)
        {
            savings = decimal.Round(baseline.Totals.KnownTotal - plan.Totals.KnownTotal, 2);
            percent = baseline.Totals.KnownTotal > 0 ? decimal.Round(savings.Value / baseline.Totals.KnownTotal * 100m, 1) : 0m;
        }
        var notes = plan.Uncertainties.ToList();
        var stale = plan.Lines.Count(l => rows[l.Option.OfferId].Row.DataStatus == "STALE");
        if (stale > 0) notes.Add($"{stale} line(s) are priced from stale supplier data.");
        if (baseline is not null && plan.Lines.Count > 0 && baseline.Totals.ShippingUnknown != plan.Totals.ShippingUnknown)
            notes.Add("Shipping is unknown on one side of the savings comparison, so the savings figure may shift once shipping is confirmed.");

        return new PlanDto(plan.Strategy.ToString(), StrategyTitle(plan.Strategy), StrategySummary(plan.Strategy),
            plan.Lines.Select(l => ToLine(l, rows, terms)).ToList(),
            plan.UnfilledItems.Select(i => i.Description).ToList(),
            TotalsDto.From(plan.Totals), plan.MaxDeliveryDays, plan.DeliveryUnknown, plan.MeetsDeadline, savings, percent, notes, plan.Method);
    }
}
