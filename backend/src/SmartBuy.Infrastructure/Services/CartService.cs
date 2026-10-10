using Microsoft.EntityFrameworkCore;
using SmartBuy.Domain.Entities;
using SmartBuy.Domain.Optimization;
using SmartBuy.Domain.Pricing;
using SmartBuy.Domain.Suppliers;
using SmartBuy.Infrastructure.Persistence;

namespace SmartBuy.Infrastructure.Services;

public sealed record CartLineDto(
    Guid Id,
    Guid? ShoppingListItemId,
    string? ListItemDescription,
    Guid SupplierProductId,
    string ProductTitle,
    string? Sku,
    decimal PackQuantity,
    string PackUnit,
    int Packs,
    decimal? CurrentPackPrice,
    decimal SnapshotPackPrice,
    bool PriceChanged,
    decimal LineCost,
    string DataStatus);

public sealed record CartSupplierDto(
    Guid SupplierId,
    string SupplierName,
    string SupplierKind,
    IReadOnlyList<CartLineDto> Lines,
    SupplierTotals Totals,
    string CheckoutMode,
    string CheckoutNote);

public sealed record CartDto(Guid ProjectId, IReadOnlyList<CartSupplierDto> Suppliers, TotalsDto Totals, int LineCount, bool ContainsDemoData);

public sealed record PurchaseLinkDto(string Url, string Kind, bool IsAffiliate, string Note);

public static class SafeLink
{
    /// <summary>
    /// An outbound link is only handed to the browser when it is HTTPS and points at the supplier's own registered site.
    /// This stops a compromised or buggy adapter from turning SmartBuy into an open redirect.
    /// </summary>
    public static bool IsAllowed(string? url, string? supplierWebsite)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target) || target.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(target.UserInfo)) return false;
        if (!Uri.TryCreate(supplierWebsite, UriKind.Absolute, out var site)) return false;
        var allowed = site.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? site.Host[4..] : site.Host;
        return target.Host.Equals(allowed, StringComparison.OrdinalIgnoreCase)
               || target.Host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>One project cart spanning many suppliers. It only ever prepares purchases; it cannot place an order.</summary>
public sealed class CartService(
    AppDbContext db,
    ITenantContext tenant,
    ComparisonService comparison,
    ISupplierProviderRegistry registry,
    AuditService audit,
    TimeProvider time)
{
    public async Task<CartDto> GetAsync(Guid projectId, CancellationToken ct)
    {
        await EnsureProjectAsync(projectId, ct);
        var now = time.GetUtcNow().UtcDateTime;
        var lines = await db.CartItems.AsNoTracking()
            .Where(c => c.ProjectId == projectId)
            .Include(c => c.Supplier)
            .Include(c => c.SupplierProduct!).ThenInclude(sp => sp.Offers.Where(o => o.IsCurrent))
            .OrderBy(c => c.CreatedAt)
            .AsSplitQuery()
            .ToListAsync(ct);
        var itemIds = lines.Where(l => l.ShoppingListItemId is not null).Select(l => l.ShoppingListItemId!.Value).ToList();
        var descriptions = await db.ShoppingListItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Description, ct);
        var organization = await db.Organizations.AsNoTracking().FirstAsync(o => o.Id == tenant.OrganizationId, ct);
        var terms = lines.Select(l => l.Supplier!).DistinctBy(s => s.Id)
            .ToDictionary(s => s.Id, s => new SupplierTerms(s.Id, s.Name, s.ShippingFlatFee, s.FreeShippingThreshold, s.MinimumOrderAmount));

        var priced = lines.Select(l =>
        {
            var offer = l.SupplierProduct!.Offers.FirstOrDefault(o => o.IsCurrent);
            var price = offer?.Price ?? l.PackPriceSnapshot;
            var status = offer is null ? "STALE" : OfferFreshness.Status(offer.DataSource, offer.FetchedAt, l.Supplier!.CacheTtlMinutes, now);
            return (Line: l, Dto: new CartLineDto(l.Id, l.ShoppingListItemId,
                l.ShoppingListItemId is { } id && descriptions.TryGetValue(id, out var d) ? d : null,
                l.SupplierProductId, l.SupplierProduct.Title, l.SupplierProduct.ExternalId, l.SupplierProduct.PackQuantity, l.SupplierProduct.PackUnit,
                l.Packs, offer?.Price, l.PackPriceSnapshot, offer is not null && offer.Price != l.PackPriceSnapshot,
                decimal.Round(l.Packs * price, 2), status));
        }).ToList();

        var totals = OrderCalculator.Calculate(priced.Select(p => (p.Line.SupplierId, p.Dto.LineCost)), terms, organization.EstimatedTaxRatePercent);
        var groups = new List<CartSupplierDto>();
        foreach (var supplierTotals in totals.Suppliers)
        {
            var supplier = lines.First(l => l.SupplierId == supplierTotals.SupplierId).Supplier!;
            var status = registry.Get(supplier.ProviderKey) is { } provider ? await provider.GetProviderStatusAsync(ct) : null;
            var (mode, note) = status switch
            {
                { Capabilities.CartBuilding: true } => ("cart", "Opens a pre-filled cart on the supplier's site. You complete checkout there."),
                { Capabilities.PurchaseLinks: true, Mode: SupplierMode.Mock } => ("link", "Demo data. Links open the retailer's own search page; you place the order on their site."),
                { Capabilities.PurchaseLinks: true } => ("link", "Opens each product on the supplier's site. You place the order there."),
                _ => ("export", "This supplier has no approved purchase link. Create a purchase order or export the list to order directly."),
            };
            groups.Add(new CartSupplierDto(supplier.Id, supplier.Name, supplier.Kind.ToString(),
                priced.Where(p => p.Line.SupplierId == supplier.Id).Select(p => p.Dto).ToList(), supplierTotals, mode, note));
        }
        return new CartDto(projectId, groups, TotalsDto.From(totals), lines.Count, priced.Any(p => p.Dto.DataStatus == "DEMO"));
    }

    /// <summary>Replaces the project cart with the lines of one computed purchasing plan.</summary>
    public async Task<CartDto> ApplyPlanAsync(Guid projectId, PurchasingStrategy strategy, CancellationToken ct)
    {
        var result = await comparison.CompareAsync(projectId, forceRefresh: false, record: false, ct);
        var plan = result.Plans.FirstOrDefault(p => p.Strategy == strategy.ToString())
                   ?? throw new EntitlementException(result.LockedStrategies.FirstOrDefault(l => l.Strategy == strategy.ToString())?.Reason ?? "That strategy is not available.");
        if (plan.Lines.Count == 0) throw new BusinessRuleException("This plan has no priced lines to add to the cart.");

        var existing = await db.CartItems.Where(c => c.ProjectId == projectId).ToListAsync(ct);
        db.CartItems.RemoveRange(existing);
        foreach (var line in plan.Lines)
        {
            db.CartItems.Add(new CartItem
            {
                ProjectId = projectId,
                ShoppingListItemId = line.ItemId,
                SupplierId = line.SupplierId,
                SupplierProductId = line.SupplierProductId,
                Packs = line.Packs,
                PackPriceSnapshot = line.PackPrice,
                DataSource = line.DataStatus == "LIVE" ? OfferDataSource.Live : OfferDataSource.Demo,
                AddedByUserId = tenant.UserId ?? Guid.Empty,
            });
        }
        audit.Record("cart.plan_applied", nameof(PurchasingProject), projectId, $"{plan.Title}: {plan.Lines.Count} line(s)");
        await db.SaveChangesAsync(ct);
        return await GetAsync(projectId, ct);
    }

    public async Task<CartDto> AddAsync(Guid projectId, Guid offerId, int? packs, Guid? shoppingListItemId, CancellationToken ct)
    {
        await EnsureProjectAsync(projectId, ct);
        var offer = await db.ProductOffers.AsNoTracking().Include(o => o.SupplierProduct).FirstOrDefaultAsync(o => o.Id == offerId && o.IsCurrent, ct)
                    ?? throw new NotFoundException("That offer is no longer available. Refresh the comparison.");
        if (shoppingListItemId is { } itemId && !await db.ShoppingListItems.AnyAsync(i => i.Id == itemId && i.ShoppingList!.ProjectId == projectId, ct))
            throw new NotFoundException("List item not found in this project.");

        var quantity = Math.Max(packs ?? 1, offer.MinOrderPacks);
        if (quantity is < 1 or > 100_000) throw new ValidationException("Packs must be between 1 and 100,000.");

        // One line per list item: choosing a different supplier for the same item replaces the earlier choice.
        if (shoppingListItemId is not null)
            db.CartItems.RemoveRange(await db.CartItems.Where(c => c.ProjectId == projectId && c.ShoppingListItemId == shoppingListItemId).ToListAsync(ct));
        var same = await db.CartItems.FirstOrDefaultAsync(c => c.ProjectId == projectId && c.SupplierProductId == offer.SupplierProductId && c.ShoppingListItemId == null && shoppingListItemId == null, ct);
        if (same is not null) same.Packs += quantity;
        else
        {
            db.CartItems.Add(new CartItem
            {
                ProjectId = projectId,
                ShoppingListItemId = shoppingListItemId,
                SupplierId = offer.SupplierId,
                SupplierProductId = offer.SupplierProductId,
                Packs = quantity,
                PackPriceSnapshot = offer.Price,
                DataSource = offer.DataSource,
                AddedByUserId = tenant.UserId ?? Guid.Empty,
            });
        }
        await db.SaveChangesAsync(ct);
        return await GetAsync(projectId, ct);
    }

    public async Task<CartDto> UpdateAsync(Guid cartItemId, int packs, CancellationToken ct)
    {
        if (packs is < 1 or > 100_000) throw new ValidationException("Packs must be between 1 and 100,000.");
        var line = await db.CartItems.FirstOrDefaultAsync(c => c.Id == cartItemId, ct) ?? throw new NotFoundException("Cart line not found.");
        line.Packs = packs;
        await db.SaveChangesAsync(ct);
        return await GetAsync(line.ProjectId, ct);
    }

    public async Task<CartDto> RemoveAsync(Guid cartItemId, CancellationToken ct)
    {
        var line = await db.CartItems.FirstOrDefaultAsync(c => c.Id == cartItemId, ct) ?? throw new NotFoundException("Cart line not found.");
        db.CartItems.Remove(line);
        await db.SaveChangesAsync(ct);
        return await GetAsync(line.ProjectId, ct);
    }

    public async Task<CartDto> ClearAsync(Guid projectId, Guid? supplierId, CancellationToken ct)
    {
        await EnsureProjectAsync(projectId, ct);
        db.CartItems.RemoveRange(await db.CartItems.Where(c => c.ProjectId == projectId && (supplierId == null || c.SupplierId == supplierId)).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        return await GetAsync(projectId, ct);
    }

    /// <summary>Returns a validated outbound link for one cart line and records the click. Null when none is permitted.</summary>
    public async Task<PurchaseLinkDto?> BuildPurchaseLinkAsync(Guid cartItemId, CancellationToken ct)
    {
        var line = await db.CartItems.AsNoTracking().Include(c => c.Supplier).Include(c => c.SupplierProduct).FirstOrDefaultAsync(c => c.Id == cartItemId, ct)
                   ?? throw new NotFoundException("Cart line not found.");
        var provider = registry.Get(line.Supplier!.ProviderKey);
        if (provider is null) return null;
        var link = await provider.BuildPurchaseLinkAsync(line.SupplierProduct!.ExternalId, line.Packs, ct);
        if (link is null || !SafeLink.IsAllowed(link.Url, line.Supplier.WebsiteUrl)) return null;

        db.AffiliateClicks.Add(new AffiliateClick
        {
            OrganizationId = tenant.OrganizationId,
            UserId = tenant.UserId,
            SupplierId = line.SupplierId,
            SupplierProductId = line.SupplierProductId,
            TargetUrl = link.Url,
            IsAffiliateLink = link.IsAffiliate,
        });
        await db.SaveChangesAsync(ct);
        return new PurchaseLinkDto(link.Url, link.Kind.ToString(), link.IsAffiliate, link.Note);
    }

    private async Task EnsureProjectAsync(Guid projectId, CancellationToken ct)
    {
        if (!await db.PurchasingProjects.AnyAsync(p => p.Id == projectId, ct)) throw new NotFoundException("Project not found.");
    }
}
