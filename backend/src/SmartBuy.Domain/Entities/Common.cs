namespace SmartBuy.Domain.Entities;

public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Rows are never physically removed; a global query filter hides them.</summary>
public interface ISoftDelete
{
    bool IsDeleted { get; set; }
    DateTime? DeletedAt { get; set; }
}

/// <summary>Row belongs to exactly one organization; a global query filter enforces isolation.</summary>
public interface ITenantOwned
{
    Guid OrganizationId { get; set; }
}

/// <summary>Optimistic concurrency token, regenerated on every save (provider independent).</summary>
public interface IVersioned
{
    Guid Version { get; set; }
}

public enum OrgRole
{
    Viewer = 0,
    Employee = 1,
    PurchasingManager = 2,
    Administrator = 3,
    Owner = 4
}

public enum Industry
{
    General = 0,
    Plumbing = 1,
    Electrical = 2,
    Hvac = 3,
    Construction = 4,
    HomeRenovation = 5,
    Landscaping = 6,
    PropertyManagement = 7
}

public enum ProjectStatus { Draft = 0, Active = 1, Ordered = 2, Completed = 3, Archived = 4 }

public enum ListItemSource { Manual = 0, Ai = 1, Upload = 2, Reuse = 3 }

/// <summary>Where a price came from. STALE is derived from age, never stored.</summary>
public enum OfferDataSource { Demo = 0, Live = 1 }

public enum MatchMethod { Gtin = 0, Mpn = 1, BrandModel = 2, Specifications = 3, TitleSimilarity = 4, Manual = 5 }

public enum MatchStatus { AutoMatched = 0, NeedsReview = 1, Confirmed = 2, Rejected = 3 }

public enum PurchaseOrderStatus
{
    Draft = 0,
    PendingApproval = 1,
    Approved = 2,
    Sent = 3,
    PartiallyReceived = 4,
    Received = 5,
    Cancelled = 6
}

public enum ApprovalStatus { Pending = 0, Approved = 1, Rejected = 2 }

public enum PriceAlertType { PriceBelow = 0, BackInStock = 1, BetterOffer = 2 }

public enum SubscriptionStatus { Active = 0, Trialing = 1, PastDue = 2, Canceled = 3 }

public enum SupplierKind { Retailer = 0, Marketplace = 1, LocalSupplier = 2, ShopifyMerchant = 3, WooCommerceMerchant = 4 }

public enum SupplierMode { Mock = 0, Live = 1 }
