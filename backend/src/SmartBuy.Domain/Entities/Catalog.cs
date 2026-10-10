namespace SmartBuy.Domain.Entities;

/// <summary>Supplier-independent product identity. One row per distinct purchasable thing, priced per <see cref="BaseUnit"/>.</summary>
public class CanonicalProduct : Entity, ISoftDelete
{
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public string? Description { get; set; }
    public Industry Industry { get; set; }
    public string Category { get; set; } = "";
    /// <summary>Null for commodity items that any brand can satisfy when specifications agree.</summary>
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string? Mpn { get; set; }
    public string? Gtin { get; set; }
    public string? Material { get; set; }
    /// <summary>Unit all offers are normalized to (each, ft, gal, lb, sqft).</summary>
    public string BaseUnit { get; set; } = "each";
    /// <summary>Safety-critical items are never suggested for substitution on price alone.</summary>
    public bool IsSafetyCritical { get; set; }
    public string? SafetyNote { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    public List<ProductSpecification> Specifications { get; set; } = new();
}

public class ProductSpecification : Entity
{
    public Guid CanonicalProductId { get; set; }
    public CanonicalProduct? CanonicalProduct { get; set; }
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>Curated substitution candidate. Suggestion only; a user must approve each use.</summary>
public class ProductAlternative : Entity
{
    public Guid ProductId { get; set; }
    public CanonicalProduct? Product { get; set; }
    public Guid AlternativeProductId { get; set; }
    public CanonicalProduct? AlternativeProduct { get; set; }
    public string SpecDifferences { get; set; } = "";
    public string Compatibility { get; set; } = "";
    public string QualityNotes { get; set; } = "";
    public string Rationale { get; set; } = "";
    /// <summary>True only when the alternative carries the same listing/certification for the application.</summary>
    public bool SameCertification { get; set; }
}

public class Supplier : Entity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Key of the <c>ISupplierProvider</c> adapter that serves this supplier.</summary>
    public string ProviderKey { get; set; } = "";
    public SupplierKind Kind { get; set; }
    public SupplierMode Mode { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string? WebsiteUrl { get; set; }
    /// <summary>Null means shipping cost is unknown, never free.</summary>
    public decimal? ShippingFlatFee { get; set; }
    public decimal? FreeShippingThreshold { get; set; }
    public decimal? MinimumOrderAmount { get; set; }
    public int CacheTtlMinutes { get; set; } = 60;
    public bool AffiliateProgramEnabled { get; set; }
    public string? IntegrationNotes { get; set; }
}

/// <summary>An organization's relationship with a supplier (preference, account number, PO contact).</summary>
public class SavedSupplier : Entity, ITenantOwned
{
    public Guid OrganizationId { get; set; }
    public Guid SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public bool IsPreferred { get; set; }
    public string? AccountNumber { get; set; }
    public string? OrderEmail { get; set; }
    public string? Notes { get; set; }
}

public class SupplierProduct : Entity
{
    public Guid SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public string ExternalId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string? Mpn { get; set; }
    public string? Gtin { get; set; }
    /// <summary>How many <see cref="PackUnit"/> one purchasable package contains (e.g. 10 ft stick, bag of 10).</summary>
    public decimal PackQuantity { get; set; } = 1;
    public string PackUnit { get; set; } = "each";
    public string SpecsJson { get; set; } = "{}";
    public string? ProductUrl { get; set; }

    public List<ProductOffer> Offers { get; set; } = new();
    public List<ProductMatch> Matches { get; set; } = new();
}

public class ProductOffer : Entity
{
    public Guid SupplierProductId { get; set; }
    public SupplierProduct? SupplierProduct { get; set; }
    public Guid SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    /// <summary>Price of one package.</summary>
    public decimal Price { get; set; }
    public string Currency { get; set; } = "USD";
    public int MinOrderPacks { get; set; } = 1;
    /// <summary>Packages on hand. Null means the supplier did not report inventory.</summary>
    public int? StockPacks { get; set; }
    public bool? InStock { get; set; }
    public int? DeliveryDaysMin { get; set; }
    public int? DeliveryDaysMax { get; set; }
    public OfferDataSource DataSource { get; set; }
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
    public bool IsCurrent { get; set; } = true;
}

public class ProductMatch : Entity
{
    public Guid SupplierProductId { get; set; }
    public SupplierProduct? SupplierProduct { get; set; }
    public Guid CanonicalProductId { get; set; }
    public CanonicalProduct? CanonicalProduct { get; set; }
    public MatchMethod Method { get; set; }
    public double Confidence { get; set; }
    public MatchStatus Status { get; set; }
    public string Reasons { get; set; } = "";
    public Guid? ReviewedByUserId { get; set; }
    public DateTime? ReviewedAt { get; set; }
}

public class PriceHistory : Entity
{
    public Guid SupplierProductId { get; set; }
    public SupplierProduct? SupplierProduct { get; set; }
    public decimal Price { get; set; }
    public OfferDataSource DataSource { get; set; }
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}

public class ProviderSyncLog : Entity
{
    public Guid SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public string Operation { get; set; } = "";
    public bool Success { get; set; }
    public int DurationMs { get; set; }
    public int ItemCount { get; set; }
    public string? Error { get; set; }
}

public class AffiliateClick : Entity
{
    public Guid? OrganizationId { get; set; }
    public Guid? UserId { get; set; }
    public Guid SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public Guid? SupplierProductId { get; set; }
    public string TargetUrl { get; set; } = "";
    /// <summary>True only when the link carried a real, configured affiliate tag.</summary>
    public bool IsAffiliateLink { get; set; }
}
