using SmartBuy.Domain.Entities;

namespace SmartBuy.Domain.Suppliers;

public sealed record ProviderSearchQuery(
    string Text,
    string? Gtin = null,
    string? Mpn = null,
    string? Brand = null,
    string? PostalCode = null,
    int MaxResults = 10);

/// <param name="StockPacks">Null when the supplier does not report inventory. Never coerce to zero.</param>
public sealed record ProviderOffer(
    string ExternalId,
    decimal Price,
    string Currency,
    int MinOrderPacks,
    int? StockPacks,
    bool? InStock,
    int? DeliveryDaysMin,
    int? DeliveryDaysMax,
    OfferDataSource DataSource,
    DateTime FetchedAt);

public sealed record ProviderProduct(
    string ExternalId,
    string Title,
    string? Description,
    string? Brand,
    string? Model,
    string? Mpn,
    string? Gtin,
    decimal PackQuantity,
    string PackUnit,
    IReadOnlyDictionary<string, string> Specs,
    string? ProductUrl,
    ProviderOffer? Offer);

public sealed record ProviderAvailability(string ExternalId, bool? InStock, int? StockPacks, int? DeliveryDaysMin, int? DeliveryDaysMax, DateTime CheckedAt);

public enum PurchaseLinkKind
{
    /// <summary>Direct link to the exact product page supplied by the provider.</summary>
    ProductPage = 0,
    /// <summary>Link that opens a pre-filled cart via an approved supplier cart API.</summary>
    Cart = 1,
    /// <summary>Plain link to the supplier's own public search page; the user must find the exact item.</summary>
    SupplierSearch = 2
}

/// <param name="IsAffiliate">True only when the URL carries a real affiliate tag from configuration.</param>
public sealed record PurchaseLink(string Url, PurchaseLinkKind Kind, bool IsAffiliate, string Note);

public sealed record ProviderCapabilities(bool ProductSearch, bool Inventory, bool PurchaseLinks, bool CartBuilding, bool OrderPlacement);

public sealed record ProviderStatus(
    string ProviderKey,
    SupplierMode Mode,
    bool IsConfigured,
    bool IsHealthy,
    string Message,
    ProviderCapabilities Capabilities,
    DateTime CheckedAt);

/// <summary>
/// Adapter contract for one supplier. Implementations must only use officially permitted access (approved APIs or
/// merchant-owned stores). No scraping, and no order is ever placed unless <see cref="ProviderCapabilities.OrderPlacement"/>
/// is backed by a supplier API that returns a confirmation.
/// </summary>
public interface ISupplierProvider
{
    /// <summary>Matches <see cref="Supplier.ProviderKey"/>.</summary>
    string Key { get; }

    Task<IReadOnlyList<ProviderProduct>> SearchProductsAsync(ProviderSearchQuery query, CancellationToken cancellationToken);

    Task<ProviderProduct?> GetProductDetailsAsync(string externalId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProviderOffer>> GetOffersAsync(IReadOnlyCollection<string> externalIds, CancellationToken cancellationToken);

    Task<ProviderAvailability?> GetAvailabilityAsync(string externalId, string? postalCode, CancellationToken cancellationToken);

    /// <summary>Returns null when no permitted link can be built; callers then offer an exportable list instead.</summary>
    Task<PurchaseLink?> BuildPurchaseLinkAsync(string externalId, int packs, CancellationToken cancellationToken);

    Task<ProviderStatus> GetProviderStatusAsync(CancellationToken cancellationToken);
}

public interface ISupplierProviderRegistry
{
    ISupplierProvider? Get(string providerKey);
    IReadOnlyCollection<ISupplierProvider> All { get; }
}
