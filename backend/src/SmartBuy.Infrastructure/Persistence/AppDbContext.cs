using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SmartBuy.Domain.Entities;

namespace SmartBuy.Infrastructure.Persistence;

/// <summary>Who is making the current request. Supplied per scope; background jobs set it explicitly.</summary>
public interface ITenantContext
{
    Guid? OrganizationId { get; }
    Guid? UserId { get; }
    /// <summary>True only for platform administration and system jobs that must read across organizations.</summary>
    bool BypassTenantFilter { get; }
}

public class TenantIsolationException(string message) : InvalidOperationException(message);

public class AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenant) : DbContext(options)
{
    // Referenced by the global query filters; EF re-evaluates them per context instance.
    private Guid? CurrentOrganizationId => tenant.OrganizationId;
    private bool BypassTenant => tenant.BypassTenantFilter;

    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationMember> OrganizationMembers => Set<OrganizationMember>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<PurchasingProject> PurchasingProjects => Set<PurchasingProject>();
    public DbSet<ShoppingList> ShoppingLists => Set<ShoppingList>();
    public DbSet<ShoppingListItem> ShoppingListItems => Set<ShoppingListItem>();
    public DbSet<ComparisonRun> ComparisonRuns => Set<ComparisonRun>();
    public DbSet<CartItem> CartItems => Set<CartItem>();
    public DbSet<CanonicalProduct> CanonicalProducts => Set<CanonicalProduct>();
    public DbSet<ProductSpecification> ProductSpecifications => Set<ProductSpecification>();
    public DbSet<ProductAlternative> ProductAlternatives => Set<ProductAlternative>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<SavedSupplier> SavedSuppliers => Set<SavedSupplier>();
    public DbSet<SupplierProduct> SupplierProducts => Set<SupplierProduct>();
    public DbSet<ProductOffer> ProductOffers => Set<ProductOffer>();
    public DbSet<ProductMatch> ProductMatches => Set<ProductMatch>();
    public DbSet<PriceHistory> PriceHistory => Set<PriceHistory>();
    public DbSet<PriceAlert> PriceAlerts => Set<PriceAlert>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseOrderItem> PurchaseOrderItems => Set<PurchaseOrderItem>();
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();
    public DbSet<AffiliateClick> AffiliateClicks => Set<AffiliateClick>();
    public DbSet<AiUsageLog> AiUsageLogs => Set<AiUsageLog>();
    public DbSet<ProviderSyncLog> ProviderSyncLogs => Set<ProviderSyncLog>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<FeatureFlag> FeatureFlags => Set<FeatureFlag>();
    public DbSet<ErrorLog> ErrorLogs => Set<ErrorLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.NormalizedEmail).IsUnique();
            e.HasIndex(x => x.GoogleSubject);
            e.HasIndex(x => x.FacebookSubject);
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.NormalizedEmail).HasMaxLength(320);
            e.Property(x => x.FullName).HasMaxLength(200);
        });
        b.Entity<RefreshToken>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Organization>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Slug).HasMaxLength(80);
            e.Property(x => x.EstimatedTaxRatePercent).HasPrecision(6, 3);
        });
        b.Entity<OrganizationMember>(e =>
        {
            e.HasIndex(x => new { x.OrganizationId, x.UserId }).IsUnique();
            e.HasOne(x => x.Organization).WithMany(o => o.Members).HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany(u => u.Memberships).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Plan>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(40);
        });
        b.Entity<Subscription>(e =>
        {
            e.HasIndex(x => x.OrganizationId).IsUnique();
            e.HasIndex(x => x.StripeCustomerId);
            e.HasOne(x => x.Organization).WithOne(o => o.Subscription).HasForeignKey<Subscription>(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Plan).WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<PurchasingProject>(e =>
        {
            e.HasIndex(x => new { x.OrganizationId, x.Status });
            e.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ShoppingList>(e =>
        {
            e.HasIndex(x => new { x.OrganizationId, x.ProjectId });
            e.HasOne(x => x.Project).WithMany(p => p.ShoppingLists).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ShoppingListItem>(e =>
        {
            e.HasIndex(x => new { x.OrganizationId, x.ShoppingListId });
            e.Property(x => x.Quantity).HasPrecision(18, 4);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.Unit).HasMaxLength(20);
            e.HasOne(x => x.ShoppingList).WithMany(l => l.Items).HasForeignKey(x => x.ShoppingListId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.CanonicalProduct).WithMany().HasForeignKey(x => x.CanonicalProductId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.ApprovedSubstituteProduct).WithMany().HasForeignKey(x => x.ApprovedSubstituteProductId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<ComparisonRun>(e =>
        {
            e.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
            e.Property(x => x.BaselineCost).HasPrecision(18, 2);
            e.Property(x => x.OptimizedCost).HasPrecision(18, 2);
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<CartItem>(e =>
        {
            e.HasIndex(x => new { x.OrganizationId, x.ProjectId });
            e.Property(x => x.PackPriceSnapshot).HasPrecision(18, 2);
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SupplierProduct).WithMany().HasForeignKey(x => x.SupplierProductId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<CanonicalProduct>(e =>
        {
            e.HasIndex(x => x.Gtin);
            e.HasIndex(x => x.Mpn);
            e.HasIndex(x => new { x.Industry, x.Category });
            e.HasIndex(x => x.NormalizedName);
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.NormalizedName).HasMaxLength(300);
            e.Property(x => x.Gtin).HasMaxLength(14);
            e.Property(x => x.BaseUnit).HasMaxLength(20);
        });
        b.Entity<ProductSpecification>(e =>
        {
            e.HasIndex(x => new { x.CanonicalProductId, x.Key }).IsUnique();
            e.Property(x => x.Key).HasMaxLength(80);
            e.Property(x => x.Value).HasMaxLength(300);
            e.HasOne(x => x.CanonicalProduct).WithMany(p => p.Specifications).HasForeignKey(x => x.CanonicalProductId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ProductAlternative>(e =>
        {
            e.HasIndex(x => new { x.ProductId, x.AlternativeProductId }).IsUnique();
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.AlternativeProduct).WithMany().HasForeignKey(x => x.AlternativeProductId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Supplier>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(60);
            e.Property(x => x.ShippingFlatFee).HasPrecision(18, 2);
            e.Property(x => x.FreeShippingThreshold).HasPrecision(18, 2);
            e.Property(x => x.MinimumOrderAmount).HasPrecision(18, 2);
        });
        b.Entity<SavedSupplier>(e =>
        {
            e.HasIndex(x => new { x.OrganizationId, x.SupplierId }).IsUnique();
            e.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<SupplierProduct>(e =>
        {
            e.HasIndex(x => new { x.SupplierId, x.ExternalId }).IsUnique();
            e.HasIndex(x => x.Gtin);
            e.Property(x => x.ExternalId).HasMaxLength(200);
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.PackQuantity).HasPrecision(18, 4);
            e.Property(x => x.PackUnit).HasMaxLength(20);
            e.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ProductOffer>(e =>
        {
            e.HasIndex(x => new { x.SupplierProductId, x.IsCurrent });
            e.HasIndex(x => x.FetchedAt);
            e.Property(x => x.Price).HasPrecision(18, 2);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.HasOne(x => x.SupplierProduct).WithMany(p => p.Offers).HasForeignKey(x => x.SupplierProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<ProductMatch>(e =>
        {
            e.HasIndex(x => new { x.SupplierProductId, x.CanonicalProductId }).IsUnique();
            e.HasIndex(x => new { x.CanonicalProductId, x.Status });
            e.HasIndex(x => x.Status);
            e.HasOne(x => x.SupplierProduct).WithMany(p => p.Matches).HasForeignKey(x => x.SupplierProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.CanonicalProduct).WithMany().HasForeignKey(x => x.CanonicalProductId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<PriceHistory>(e =>
        {
            e.HasIndex(x => new { x.SupplierProductId, x.RecordedAt });
            e.Property(x => x.Price).HasPrecision(18, 2);
            e.HasOne(x => x.SupplierProduct).WithMany().HasForeignKey(x => x.SupplierProductId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ProviderSyncLog>(e =>
        {
            e.HasIndex(x => new { x.SupplierId, x.CreatedAt });
            e.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<AffiliateClick>(e =>
        {
            e.HasIndex(x => x.CreatedAt);
            e.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PriceAlert>(e =>
        {
            e.HasIndex(x => new { x.OrganizationId, x.IsActive });
            e.Property(x => x.TargetPrice).HasPrecision(18, 4);
            e.Property(x => x.LastTriggeredPrice).HasPrecision(18, 4);
            e.HasOne(x => x.CanonicalProduct).WithMany().HasForeignKey(x => x.CanonicalProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Notification>(e =>
        {
            e.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<PurchaseOrder>(e =>
        {
            e.HasIndex(x => new { x.OrganizationId, x.PoNumber }).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.Status });
            e.Property(x => x.PoNumber).HasMaxLength(40);
            e.Property(x => x.Subtotal).HasPrecision(18, 2);
            e.Property(x => x.ShippingEstimate).HasPrecision(18, 2);
            e.Property(x => x.TaxEstimate).HasPrecision(18, 2);
            e.Property(x => x.Total).HasPrecision(18, 2);
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<PurchaseOrderItem>(e =>
        {
            e.HasIndex(x => new { x.OrganizationId, x.PurchaseOrderId });
            e.Property(x => x.Quantity).HasPrecision(18, 4);
            e.Property(x => x.PackQuantity).HasPrecision(18, 4);
            e.Property(x => x.UnitPrice).HasPrecision(18, 2);
            e.Property(x => x.LineTotal).HasPrecision(18, 2);
            e.Property(x => x.QuantityReceived).HasPrecision(18, 4);
            e.HasOne(x => x.PurchaseOrder).WithMany(p => p.Items).HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ApprovalRequest>(e =>
        {
            e.HasIndex(x => new { x.OrganizationId, x.Status });
            e.HasOne(x => x.PurchaseOrder).WithMany(p => p.Approvals).HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AiUsageLog>(e =>
        {
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.OrganizationId);
            e.Property(x => x.EstimatedCostUsd).HasPrecision(18, 6);
        });
        b.Entity<AuditLog>(e =>
        {
            e.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
            e.HasIndex(x => x.CreatedAt);
            e.Property(x => x.Action).HasMaxLength(120);
        });
        b.Entity<FeatureFlag>(e => e.HasIndex(x => x.Key).IsUnique());
        b.Entity<ErrorLog>(e => e.HasIndex(x => x.CreatedAt));

        var utc = new ValueConverter<DateTime, DateTime>(v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var utcNullable = new ValueConverter<DateTime?, DateTime?>(
            v => v == null ? v : (v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime()),
            v => v == null ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));
        var isSqlite = Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;

        foreach (var entityType in b.Model.GetEntityTypes())
        {
            var clr = entityType.ClrType;
            // Keys are created in the Entity constructor. Declaring them as never store-generated makes EF treat a new
            // child added to a tracked parent's collection as an insert instead of mistaking it for an existing row.
            if (typeof(Entity).IsAssignableFrom(clr))
                b.Entity(clr).Property(nameof(Entity.Id)).ValueGeneratedNever();
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime)) property.SetValueConverter(utc);
                else if (property.ClrType == typeof(DateTime?)) property.SetValueConverter(utcNullable);
                // SQLite has no decimal type; store as REAL so ordering and aggregates work in local development.
                else if (isSqlite && (property.ClrType == typeof(decimal) || property.ClrType == typeof(decimal?)))
                    property.SetProviderClrType(typeof(double));
            }

            if (typeof(IVersioned).IsAssignableFrom(clr))
                b.Entity(clr).Property(nameof(IVersioned.Version)).IsConcurrencyToken();

            var isTenant = typeof(ITenantOwned).IsAssignableFrom(clr);
            var isSoft = typeof(ISoftDelete).IsAssignableFrom(clr);
            if (!isTenant && !isSoft) continue;

            var parameter = Expression.Parameter(clr, "e");
            Expression? body = null;
            if (isSoft)
                body = Expression.Not(Expression.Property(parameter, nameof(ISoftDelete.IsDeleted)));
            if (isTenant)
            {
                var self = Expression.Constant(this);
                var bypass = Expression.Property(self, typeof(AppDbContext).GetProperty(nameof(BypassTenant), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!);
                var current = Expression.Property(self, typeof(AppDbContext).GetProperty(nameof(CurrentOrganizationId), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!);
                var owner = Expression.Convert(Expression.Property(parameter, nameof(ITenantOwned.OrganizationId)), typeof(Guid?));
                var tenantFilter = Expression.OrElse(bypass, Expression.Equal(owner, current));
                body = body is null ? tenantFilter : Expression.AndAlso(body, tenantFilter);
            }
            b.Entity(clr).HasQueryFilter(Expression.Lambda(body!, parameter));
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyConventions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ApplyConventions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ApplyConventions()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;

            if (entry.Entity is ITenantOwned owned && !BypassTenant)
            {
                // Writes are checked as strictly as reads: a request can only ever touch its own organization's rows.
                if (CurrentOrganizationId is not { } orgId)
                    throw new TenantIsolationException("No organization is active for this request.");
                if (entry.State == EntityState.Added && owned.OrganizationId == Guid.Empty) owned.OrganizationId = orgId;
                if (owned.OrganizationId != orgId)
                    throw new TenantIsolationException($"Attempt to write {entry.Entity.GetType().Name} outside the active organization.");
                if (entry.State == EntityState.Modified && entry.Property(nameof(ITenantOwned.OrganizationId)).IsModified)
                    throw new TenantIsolationException("OrganizationId cannot be changed.");
            }

            if (entry.State == EntityState.Deleted && entry.Entity is ISoftDelete soft)
            {
                entry.State = EntityState.Modified;
                soft.IsDeleted = true;
                soft.DeletedAt = now;
            }

            if (entry.Entity is Entity entity)
            {
                if (entry.State == EntityState.Added && entity.CreatedAt == default) entity.CreatedAt = now;
                if (entry.State != EntityState.Deleted) entity.UpdatedAt = now;
            }

            if (entry.Entity is IVersioned versioned && entry.State is EntityState.Added or EntityState.Modified)
                versioned.Version = Guid.NewGuid();
        }
    }
}
