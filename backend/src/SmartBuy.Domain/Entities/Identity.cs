namespace SmartBuy.Domain.Entities;

public class User : Entity, ISoftDelete
{
    public string Email { get; set; } = "";
    public string NormalizedEmail { get; set; } = "";
    public string FullName { get; set; } = "";
    /// <summary>Null for accounts that only sign in through an external identity provider.</summary>
    public string? PasswordHash { get; set; }
    public string? GoogleSubject { get; set; }
    /// <summary>Platform operator flag. Deliberately independent of any organization role.</summary>
    public bool IsPlatformAdmin { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDemo { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTime? LockoutUntil { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    public List<OrganizationMember> Memberships { get; set; } = new();
}

public class RefreshToken : Entity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public Guid OrganizationId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

public class Organization : Entity, ISoftDelete, IVersioned
{
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public Industry Industry { get; set; }
    public bool IsDemo { get; set; }
    /// <summary>User-supplied sales-tax estimate. Null means tax is unknown, never zero.</summary>
    public decimal? EstimatedTaxRatePercent { get; set; }
    public string? PostalCode { get; set; }
    public string? ShippingAddress { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();

    public List<OrganizationMember> Members { get; set; } = new();
    public Subscription? Subscription { get; set; }
}

public class OrganizationMember : Entity, ITenantOwned
{
    public Guid OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public OrgRole Role { get; set; }
}

public class Plan : Entity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int MonthlyPriceCents { get; set; }
    public string? StripePriceId { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    // Limits: null means unlimited.
    public int? MaxProjectsPerMonth { get; set; }
    public int? MaxMembers { get; set; }

    // Feature entitlements.
    public bool AiMaterialLists { get; set; }
    public bool AdvancedOptimization { get; set; }
    public bool PriceAlerts { get; set; }
    public bool PurchaseOrders { get; set; }
    public bool AdvancedReporting { get; set; }
    public bool ApprovalWorkflows { get; set; }
}

public class Subscription : Entity, ITenantOwned
{
    public Guid OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    public Guid PlanId { get; set; }
    public Plan? Plan { get; set; }
    public SubscriptionStatus Status { get; set; }
    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public DateTime? CurrentPeriodEnd { get; set; }
}

public class AuditLog : Entity
{
    public Guid? OrganizationId { get; set; }
    public Guid? UserId { get; set; }
    public string Action { get; set; } = "";
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
}

public class FeatureFlag : Entity
{
    public string Key { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsEnabled { get; set; }
}

public class ErrorLog : Entity
{
    public string Message { get; set; } = "";
    public string? Path { get; set; }
    public string? Method { get; set; }
    public string? TraceId { get; set; }
    public string? Detail { get; set; }
    public Guid? UserId { get; set; }
}

public class AiUsageLog : Entity
{
    public Guid? OrganizationId { get; set; }
    public Guid? UserId { get; set; }
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string Feature { get; set; } = "";
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    /// <summary>Estimate from configured per-token rates; zero for the mock provider.</summary>
    public decimal EstimatedCostUsd { get; set; }
    public bool Success { get; set; }
    public int DurationMs { get; set; }
    public string? Error { get; set; }
}
