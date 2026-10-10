namespace SmartBuy.Domain.Entities;

public class PurchasingProject : Entity, ITenantOwned, ISoftDelete, IVersioned
{
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public Industry Industry { get; set; }
    public ProjectStatus Status { get; set; }
    public DateTime? RequiredByDate { get; set; }
    public string? DeliveryPostalCode { get; set; }
    public bool AllowSubstitutions { get; set; }
    public Guid CreatedByUserId { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();

    public List<ShoppingList> ShoppingLists { get; set; } = new();
}

public class ShoppingList : Entity, ITenantOwned, ISoftDelete, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid ProjectId { get; set; }
    public PurchasingProject? Project { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Original natural-language request or uploaded file name, kept for traceability.</summary>
    public string? SourceText { get; set; }
    public string? Assumptions { get; set; }
    /// <summary>Quantities must be reviewed by a person before supplier search is allowed.</summary>
    public bool IsApproved { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();

    public List<ShoppingListItem> Items { get; set; } = new();
}

public class ShoppingListItem : Entity, ITenantOwned
{
    public Guid OrganizationId { get; set; }
    public Guid ShoppingListId { get; set; }
    public ShoppingList? ShoppingList { get; set; }
    public int LineNumber { get; set; }
    public string Description { get; set; } = "";
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = "each";
    public string? Material { get; set; }
    public string? Specifications { get; set; }
    public string? Notes { get; set; }
    public ListItemSource Source { get; set; }
    public Guid? CanonicalProductId { get; set; }
    public CanonicalProduct? CanonicalProduct { get; set; }
    public double? MatchConfidence { get; set; }
    /// <summary>Set only by an explicit user approval; the optimizer may then price this product instead.</summary>
    public Guid? ApprovedSubstituteProductId { get; set; }
    public CanonicalProduct? ApprovedSubstituteProduct { get; set; }
    public Guid? SubstitutionApprovedByUserId { get; set; }
    public DateTime? SubstitutionApprovedAt { get; set; }
}

public class ComparisonRun : Entity, ITenantOwned
{
    public Guid OrganizationId { get; set; }
    public Guid ProjectId { get; set; }
    public PurchasingProject? Project { get; set; }
    public Guid UserId { get; set; }
    public int ItemCount { get; set; }
    public int MatchedItemCount { get; set; }
    public int SupplierCount { get; set; }
    public decimal? BaselineCost { get; set; }
    public string? BaselineLabel { get; set; }
    public decimal? OptimizedCost { get; set; }
    public bool HasUnknowns { get; set; }
}

public class CartItem : Entity, ITenantOwned
{
    public Guid OrganizationId { get; set; }
    public Guid ProjectId { get; set; }
    public PurchasingProject? Project { get; set; }
    public Guid? ShoppingListItemId { get; set; }
    public Guid SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public Guid SupplierProductId { get; set; }
    public SupplierProduct? SupplierProduct { get; set; }
    public Guid? CanonicalProductId { get; set; }
    public int Packs { get; set; }
    /// <summary>Price captured when the line was added; the cart re-reads the current offer for display.</summary>
    public decimal PackPriceSnapshot { get; set; }
    public OfferDataSource DataSource { get; set; }
    public Guid AddedByUserId { get; set; }
}

public class PurchaseOrder : Entity, ITenantOwned, ISoftDelete, IVersioned
{
    public Guid OrganizationId { get; set; }
    public Guid? ProjectId { get; set; }
    public PurchasingProject? Project { get; set; }
    public Guid SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public string PoNumber { get; set; } = "";
    public PurchaseOrderStatus Status { get; set; }
    public DateTime? RequestedDeliveryDate { get; set; }
    public string? ShipTo { get; set; }
    public string? InternalNotes { get; set; }
    public decimal Subtotal { get; set; }
    /// <summary>Null means unknown; the total then excludes it and says so.</summary>
    public decimal? ShippingEstimate { get; set; }
    public decimal? TaxEstimate { get; set; }
    public decimal Total { get; set; }
    public Guid CreatedByUserId { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public DateTime? ApprovedAt { get; set; }
    /// <summary>Populated only after a delivery channel confirms the hand-off.</summary>
    public DateTime? SentAt { get; set; }
    public string? SentVia { get; set; }
    public string? SentTo { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();

    public List<PurchaseOrderItem> Items { get; set; } = new();
    public List<ApprovalRequest> Approvals { get; set; } = new();
}

public class PurchaseOrderItem : Entity, ITenantOwned
{
    public Guid OrganizationId { get; set; }
    public Guid PurchaseOrderId { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }
    public int LineNumber { get; set; }
    public string Description { get; set; } = "";
    public string? SupplierSku { get; set; }
    public Guid? CanonicalProductId { get; set; }
    /// <summary>Number of packages ordered.</summary>
    public decimal Quantity { get; set; }
    public decimal PackQuantity { get; set; } = 1;
    public string Unit { get; set; } = "each";
    /// <summary>Price per package.</summary>
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
    public decimal QuantityReceived { get; set; }
}

public class ApprovalRequest : Entity, ITenantOwned
{
    public Guid OrganizationId { get; set; }
    public Guid PurchaseOrderId { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }
    public Guid RequestedByUserId { get; set; }
    public ApprovalStatus Status { get; set; }
    public Guid? DecidedByUserId { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? Comment { get; set; }
}

public class PriceAlert : Entity, ITenantOwned, ISoftDelete
{
    public Guid OrganizationId { get; set; }
    public Guid CanonicalProductId { get; set; }
    public CanonicalProduct? CanonicalProduct { get; set; }
    /// <summary>Optional: restrict the rule to (or compare against) one supplier.</summary>
    public Guid? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public PriceAlertType Type { get; set; }
    /// <summary>Target price per base unit (PriceBelow) or reference unit price (BetterOffer).</summary>
    public decimal? TargetPrice { get; set; }
    public bool IsActive { get; set; } = true;
    public bool? LastKnownInStock { get; set; }
    public decimal? LastTriggeredPrice { get; set; }
    public DateTime? LastCheckedAt { get; set; }
    public DateTime? LastTriggeredAt { get; set; }
    public Guid CreatedByUserId { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
}

public class Notification : Entity, ITenantOwned
{
    public Guid OrganizationId { get; set; }
    public Guid? PriceAlertId { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime? ReadAt { get; set; }
}
