using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartBuy.Domain.Entities;
using SmartBuy.Domain.Matching;
using SmartBuy.Domain.Optimization;
using SmartBuy.Domain.Pricing;
using SmartBuy.Domain.Rules;
using SmartBuy.Infrastructure.Services;
using SmartBuy.Infrastructure.Suppliers.Mock;

namespace SmartBuy.Infrastructure.Persistence;

public sealed class SeedOptions
{
    /// <summary>Creates the three sample businesses and their projects.</summary>
    public bool DemoData { get; set; } = true;
    /// <summary>Platform administrator created at first start. Leave empty to create none.</summary>
    public string? AdminEmail { get; set; }
    public string? AdminPassword { get; set; }
}

public sealed record DemoBusiness(string Slug, string Name, Industry Industry, string PlanCode, decimal? TaxRate, string OwnerName, string Tagline);

/// <summary>Idempotent reference and demo data. Safe to run at every start.</summary>
public sealed class DataSeeder(AppDbContext db, TenantContext tenant, ComparisonService comparison, IOptions<SeedOptions> options, ILogger<DataSeeder> logger)
{
    public static readonly IReadOnlyList<DemoBusiness> DemoBusinesses = new[]
    {
        new DemoBusiness("abc-plumbing", "ABC Plumbing", Industry.Plumbing, "business", 8.25m, "Alex Carter", "Residential service and repipe contractor"),
        new DemoBusiness("premier-renovations", "Premier Renovations", Industry.HomeRenovation, "pro", 7.00m, "Jordan Blake", "Kitchen, bath and rental-turnover remodeler"),
        new DemoBusiness("green-valley-landscaping", "Green Valley Landscaping", Industry.Landscaping, "free", null, "Priya Nair", "Commercial grounds and HOA maintenance"),
    };

    public static string DemoOwnerEmail(string slug) => $"owner@{slug}.demo.smartbuy.local";
    public static Guid SupplierId(string code) => DemoCatalog.StableGuid("supplier:" + code);

    public async Task SeedAsync(CancellationToken ct)
    {
        using (tenant.Elevate())
        {
            await SeedPlansAsync(ct);
            await db.SaveChangesAsync(ct);
            await SeedSuppliersAsync(ct);
            await SeedCatalogAsync(ct);
            await SeedFlagsAsync(ct);
            await SeedAdminAsync(ct);
            await db.SaveChangesAsync(ct);
        }
        // Demo tenants are seeded under normal tenant isolation, one organization at a time.
        if (options.Value.DemoData) await SeedDemoAsync(ct);
    }

    /// <summary>Deletes the sample businesses and recreates them from scratch.</summary>
    public async Task ResetDemoAsync(CancellationToken ct)
    {
        using (tenant.Elevate())
        {
            var demoOrgIds = await db.Organizations.IgnoreQueryFilters().Where(o => o.IsDemo).Select(o => o.Id).ToListAsync(ct);
            // Purchase orders reference suppliers with a restricting key, so they go first; the rest cascades from the organization.
            await db.PurchaseOrders.IgnoreQueryFilters().Where(p => demoOrgIds.Contains(p.OrganizationId)).ExecuteDeleteAsync(ct);
            await db.CartItems.IgnoreQueryFilters().Where(c => demoOrgIds.Contains(c.OrganizationId)).ExecuteDeleteAsync(ct);
            await db.Organizations.IgnoreQueryFilters().Where(o => o.IsDemo).ExecuteDeleteAsync(ct);
            await db.Users.IgnoreQueryFilters().Where(u => u.IsDemo).ExecuteDeleteAsync(ct);
            db.ChangeTracker.Clear();
        }
        await SeedDemoAsync(ct);
    }

    private async Task SeedPlansAsync(CancellationToken ct)
    {
        if (await db.Plans.AnyAsync(ct)) return;
        db.Plans.AddRange(
            new Plan { Code = "free", Name = "Free", SortOrder = 1, MonthlyPriceCents = 0, Description = "Try SmartBuy on a few projects each month.", MaxProjectsPerMonth = 3, MaxMembers = 1 },
            new Plan { Code = "pro", Name = "Pro", SortOrder = 2, MonthlyPriceCents = 2900, Description = "For owner-operators who buy materials every week.", MaxProjectsPerMonth = 50, MaxMembers = 3, AiMaterialLists = true, AdvancedOptimization = true, PriceAlerts = true },
            new Plan { Code = "business", Name = "Business", SortOrder = 3, MonthlyPriceCents = 7900, Description = "For teams that need purchase orders, approvals and reporting.", MaxProjectsPerMonth = null, MaxMembers = 25, AiMaterialLists = true, AdvancedOptimization = true, PriceAlerts = true, PurchaseOrders = true, AdvancedReporting = true, ApprovalWorkflows = true });
    }

    private async Task SeedSuppliersAsync(CancellationToken ct)
    {
        var existing = (await db.Suppliers.Select(s => s.Code).ToListAsync(ct)).ToHashSet();
        foreach (var profile in MockSupplierProfiles.All.Where(p => !existing.Contains(p.Code)))
        {
            db.Suppliers.Add(new Supplier
            {
                Id = SupplierId(profile.Code),
                Code = profile.Code,
                Name = profile.Name,
                ProviderKey = profile.ProviderKey,
                Kind = profile.Kind,
                Mode = SupplierMode.Mock,
                WebsiteUrl = profile.Website,
                ShippingFlatFee = profile.ShippingFlatFee,
                FreeShippingThreshold = profile.FreeShippingThreshold,
                MinimumOrderAmount = profile.MinimumOrderAmount,
                CacheTtlMinutes = 60,
                IntegrationNotes = profile.IntegrationNotes,
            });
        }
    }

    private async Task SeedCatalogAsync(CancellationToken ct)
    {
        var existing = (await db.CanonicalProducts.IgnoreQueryFilters().Select(p => p.Id).ToListAsync(ct)).ToHashSet();
        foreach (var item in DemoCatalog.Products.Where(p => !existing.Contains(p.Id)))
        {
            var product = new CanonicalProduct
            {
                Id = item.Id,
                Name = item.Name,
                NormalizedName = ProductMatcher.NormalizeTitle(item.Name),
                Description = $"{item.Category} · demo catalog entry",
                Industry = item.Industry,
                Category = item.Category,
                Brand = item.Brand,
                Model = item.Model,
                Mpn = item.Model,
                Gtin = ProductMatcher.NormalizeGtin(item.Gtin),
                Material = item.Material,
                BaseUnit = item.BaseUnit,
                IsSafetyCritical = item.SafetyCritical,
                SafetyNote = item.SafetyNote,
            };
            foreach (var (key, value) in item.Specs)
                product.Specifications.Add(new ProductSpecification { Key = key, Value = value });
            db.CanonicalProducts.Add(product);
        }

        if (!await db.ProductAlternatives.AnyAsync(ct))
        {
            var byKey = DemoCatalog.Products.ToDictionary(p => p.Key);
            foreach (var alt in DemoCatalog.Alternatives)
            {
                db.ProductAlternatives.Add(new ProductAlternative
                {
                    ProductId = byKey[alt.ProductKey].Id,
                    AlternativeProductId = byKey[alt.AlternativeKey].Id,
                    SameCertification = alt.SameCertification,
                    SpecDifferences = alt.SpecDifferences,
                    Compatibility = alt.Compatibility,
                    QualityNotes = alt.QualityNotes,
                    Rationale = alt.Rationale,
                });
            }
        }
    }

    private async Task SeedFlagsAsync(CancellationToken ct)
    {
        var existing = (await db.FeatureFlags.Select(f => f.Key).ToListAsync(ct)).ToHashSet();
        var flags = new (string Key, string Description, bool Enabled)[]
        {
            ("demo_login", "Allow visitors to open the sample businesses from the Try Demo button.", true),
            ("ai_material_lists", "Allow natural-language material list generation.", true),
            ("self_signup", "Allow new organizations to register themselves.", true),
            ("affiliate_links", "Attach configured affiliate tags to outbound supplier links (requires each supplier's program approval).", false),
        };
        foreach (var flag in flags.Where(f => !existing.Contains(f.Key)))
            db.FeatureFlags.Add(new FeatureFlag { Key = flag.Key, Description = flag.Description, IsEnabled = flag.Enabled });
    }

    private async Task SeedAdminAsync(CancellationToken ct)
    {
        var email = options.Value.AdminEmail?.Trim();
        var password = options.Value.AdminPassword;
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password)) return;
        var normalized = email.ToUpperInvariant();
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.NormalizedEmail == normalized, ct)) return;

        var admin = new User { Email = email, NormalizedEmail = normalized, FullName = "Platform Administrator", PasswordHash = PasswordHasher.Hash(password), IsPlatformAdmin = true };
        var org = new Organization { Name = "SmartBuy Operations", Slug = "smartbuy-operations", Industry = Industry.General };
        var business = await db.Plans.FirstAsync(p => p.Code == "business", ct);
        db.Users.Add(admin);
        db.Organizations.Add(org);
        db.OrganizationMembers.Add(new OrganizationMember { OrganizationId = org.Id, UserId = admin.Id, Role = OrgRole.Owner });
        db.Subscriptions.Add(new Subscription { OrganizationId = org.Id, Plan = business, Status = SubscriptionStatus.Active });
        logger.LogInformation("Created platform administrator {Email}", email);
    }

    private async Task SeedDemoAsync(CancellationToken ct)
    {
        List<(Organization Org, User Owner)> created = new();
        using (tenant.Elevate())
        {
            var plans = await db.Plans.ToDictionaryAsync(p => p.Code, ct);
            foreach (var business in DemoBusinesses)
            {
                if (await db.Organizations.IgnoreQueryFilters().AnyAsync(o => o.Slug == business.Slug, ct)) continue;
                var org = new Organization
                {
                    Name = business.Name, Slug = business.Slug, Industry = business.Industry, IsDemo = true,
                    EstimatedTaxRatePercent = business.TaxRate, PostalCode = "78701", ShippingAddress = $"{business.Name}, 100 Demo Way, Austin, TX 78701",
                };
                var owner = NewDemoUser(DemoOwnerEmail(business.Slug), business.OwnerName);
                db.Organizations.Add(org);
                db.Users.Add(owner);
                db.OrganizationMembers.Add(new OrganizationMember { OrganizationId = org.Id, UserId = owner.Id, Role = OrgRole.Owner });
                db.Subscriptions.Add(new Subscription { OrganizationId = org.Id, PlanId = plans[business.PlanCode].Id, Status = SubscriptionStatus.Active });
                created.Add((org, owner));
            }
            await db.SaveChangesAsync(ct);
        }

        foreach (var (org, owner) in created)
        {
            tenant.OrganizationId = org.Id;
            tenant.UserId = owner.Id;
            tenant.Role = OrgRole.Owner;
            try
            {
                switch (org.Slug)
                {
                    case "abc-plumbing": await SeedAbcPlumbingAsync(org, owner, ct); break;
                    case "premier-renovations": await SeedPremierAsync(owner, ct); break;
                    case "green-valley-landscaping": await SeedGreenValleyAsync(owner, ct); break;
                }
            }
            finally
            {
                tenant.OrganizationId = null;
                tenant.UserId = null;
                tenant.Role = null;
            }
            logger.LogInformation("Seeded demo business {Name}", org.Name);
        }
    }

    private static User NewDemoUser(string email, string name) => new() { Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = name, IsDemo = true };

    private static ShoppingListItem Line(int n, string key, decimal quantity, string? unit = null, ListItemSource source = ListItemSource.Manual, string? notes = null)
    {
        var product = DemoCatalog.Products.First(p => p.Key == key);
        return new ShoppingListItem
        {
            LineNumber = n, Description = product.Name, Quantity = quantity, Unit = unit ?? product.BaseUnit, Material = product.Material,
            Specifications = string.Join(", ", product.Specs.Values), Source = source, CanonicalProductId = product.Id, MatchConfidence = 0.99, Notes = notes,
        };
    }

    private PurchasingProject AddProject(User owner, string name, string description, Industry industry, int? dueInDays, string listName, bool approved, string? sourceText, string? assumptions, params ShoppingListItem[] items)
    {
        var project = new PurchasingProject
        {
            Name = name, Description = description, Industry = industry, Status = approved ? ProjectStatus.Active : ProjectStatus.Draft,
            RequiredByDate = dueInDays is { } d ? DateTime.UtcNow.Date.AddDays(d) : null, DeliveryPostalCode = "78701", CreatedByUserId = owner.Id,
        };
        var list = new ShoppingList
        {
            Name = listName, IsApproved = approved, ApprovedAt = approved ? DateTime.UtcNow : null, ApprovedByUserId = approved ? owner.Id : null,
            SourceText = sourceText, Assumptions = assumptions,
        };
        list.Items.AddRange(items);
        project.ShoppingLists.Add(list);
        db.PurchasingProjects.Add(project);
        return project;
    }

    private async Task SeedAbcPlumbingAsync(Organization org, User owner, CancellationToken ct)
    {
        User manager, employee;
        using (tenant.Elevate())
        {
            manager = NewDemoUser($"maria@{org.Slug}.demo.smartbuy.local", "Maria Lopez");
            employee = NewDemoUser($"sam@{org.Slug}.demo.smartbuy.local", "Sam Patel");
            db.Users.AddRange(manager, employee);
            await db.SaveChangesAsync(ct);
        }
        db.OrganizationMembers.Add(new OrganizationMember { UserId = manager.Id, Role = OrgRole.PurchasingManager });
        db.OrganizationMembers.Add(new OrganizationMember { UserId = employee.Id, Role = OrgRole.Employee });

        db.SavedSuppliers.Add(new SavedSupplier { SupplierId = SupplierId("local-riverside"), IsPreferred = true, AccountNumber = "ABC-10442", OrderEmail = "orders@riverside-supply.example", Notes = "Counter pickup available. Net 30 terms." });
        db.SavedSuppliers.Add(new SavedSupplier { SupplierId = SupplierId("homedepot"), IsPreferred = false, Notes = "Pro account for jobsite delivery." });

        var repipe = AddProject(owner, "Henderson Residence Repipe", "Replace galvanized supply lines with copper in a two-bath single-family home.", Industry.Plumbing, 10,
            "Rough-in materials", true,
            "I need 50 feet of 3/4-inch copper pipe, 20 elbows, 10 shutoff valves, and 5 faucets for a residential plumbing project.", null,
            Line(1, "copper-pipe-l-34", 50), Line(2, "copper-elbow-90-34", 20), Line(3, "angle-stop-12", 10), Line(4, "faucet-bath-af2100", 5),
            Line(5, "copper-coupling-34", 12), Line(6, "ball-valve-34", 2), Line(7, "solder-lead-free", 1), Line(8, "ptfe-tape", 5));

        AddProject(owner, "Maple Court Bathroom Fixtures", "Replace plumbing fixtures in three bathrooms.", Industry.Plumbing, 21,
            "AI draft: fixture replacement", false,
            "Build a materials list for replacing plumbing fixtures in three bathrooms.",
            "Draft produced by the built-in rule-based parser (demo mode), not by a language model. Review every line before searching suppliers.\nAssumed 3 bathroom(s), each with one toilet and one single-handle lavatory faucet being replaced. Tubs, showers and valves behind walls are not included.\nAssumed existing drains and supply stubs are reusable. Quantities are a planning draft, not a field-verified take-off.",
            Line(1, "toilet-af-e128", 3, source: ListItemSource.Ai), Line(2, "wax-ring", 3, source: ListItemSource.Ai), Line(3, "faucet-bath-af2100", 3, source: ListItemSource.Ai),
            Line(4, "angle-stop-12", 9, source: ListItemSource.Ai, notes: "Two per faucet and one per toilet."), Line(5, "supply-line-12", 6, source: ListItemSource.Ai),
            Line(6, "p-trap-112", 3, source: ListItemSource.Ai), Line(7, "ptfe-tape", 1, source: ListItemSource.Ai), Line(8, "caulk-kitchen-bath", 3, source: ListItemSource.Ai));

        var restock = AddProject(owner, "Service Van Restock", "Monthly consumables and common repair parts for two service vans.", Industry.Plumbing, null,
            "Van stock", true, null, null,
            Line(1, "ptfe-tape", 20), Line(2, "supply-line-12", 12), Line(3, "wax-ring", 6), Line(4, "p-trap-112", 6), Line(5, "copper-elbow-90-12", 30), Line(6, "nitrile-gloves-l", 400));

        db.PriceAlerts.Add(new PriceAlert { CanonicalProductId = DemoCatalog.Products.First(p => p.Key == "copper-pipe-l-34").Id, Type = PriceAlertType.PriceBelow, TargetPrice = 4.60m, CreatedByUserId = owner.Id });
        db.PriceAlerts.Add(new PriceAlert { CanonicalProductId = DemoCatalog.Products.First(p => p.Key == "angle-stop-12").Id, Type = PriceAlertType.BackInStock, CreatedByUserId = owner.Id });
        db.PriceAlerts.Add(new PriceAlert { CanonicalProductId = DemoCatalog.Products.First(p => p.Key == "faucet-bath-af2100").Id, SupplierId = SupplierId("homedepot"), Type = PriceAlertType.BetterOffer, CreatedByUserId = owner.Id });
        await db.SaveChangesAsync(ct);

        var result = await comparison.CompareAsync(repipe.Id, forceRefresh: true, record: true, ct);
        await comparison.CompareAsync(restock.Id, forceRefresh: false, record: true, ct);
        await SeedPurchaseOrdersAsync(org, repipe, result, owner, employee, ct);
    }

    /// <summary>Builds two sample purchase orders from the computed lowest-cost plan so their prices match the comparison.</summary>
    private async Task SeedPurchaseOrdersAsync(Organization org, PurchasingProject project, ComparisonResult result, User owner, User employee, CancellationToken ct)
    {
        var plan = result.Plans.FirstOrDefault(p => p.Strategy == nameof(PurchasingStrategy.LowestCost));
        if (plan is null) return;
        var suppliers = await db.Suppliers.AsNoTracking().ToDictionaryAsync(s => s.Id, ct);
        var sequence = 1;
        foreach (var group in plan.Lines.GroupBy(l => l.SupplierId).OrderByDescending(g => g.Sum(l => l.LineCost)).Take(2))
        {
            var supplier = suppliers[group.Key];
            var pending = sequence == 1;
            var po = new PurchaseOrder
            {
                ProjectId = project.Id, SupplierId = supplier.Id, PoNumber = PurchaseOrderRules.FormatNumber(DateTime.UtcNow.Year, sequence++),
                Status = pending ? PurchaseOrderStatus.PendingApproval : PurchaseOrderStatus.Draft,
                RequestedDeliveryDate = project.RequiredByDate, ShipTo = org.ShippingAddress, CreatedByUserId = pending ? employee.Id : owner.Id,
                InternalNotes = "Prices on this PO came from DEMO supplier data. Confirm pricing with the supplier before sending.",
            };
            var n = 1;
            foreach (var line in group)
                po.Items.Add(new PurchaseOrderItem { LineNumber = n++, Description = line.ProductTitle, Quantity = line.Packs, PackQuantity = 1, Unit = "pkg", UnitPrice = line.PackPrice });
            PurchaseOrderRules.Recalculate(po);
            po.ShippingEstimate = OrderCalculator.Shipping(new SupplierTerms(supplier.Id, supplier.Name, supplier.ShippingFlatFee, supplier.FreeShippingThreshold, supplier.MinimumOrderAmount), po.Subtotal);
            po.TaxEstimate = OrderCalculator.Tax(po.Subtotal, org.EstimatedTaxRatePercent);
            PurchaseOrderRules.Recalculate(po);
            if (pending) po.Approvals.Add(new ApprovalRequest { RequestedByUserId = employee.Id, Status = ApprovalStatus.Pending });
            db.PurchaseOrders.Add(po);
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedPremierAsync(User owner, CancellationToken ct)
    {
        db.SavedSuppliers.Add(new SavedSupplier { SupplierId = SupplierId("lowes"), IsPreferred = true, Notes = "Preferred for flooring and tile." });
        var kitchen = AddProject(owner, "Oak Street Kitchen Remodel", "Full kitchen refresh: flooring, backsplash, drywall repair, paint and lighting.", Industry.HomeRenovation, 14,
            "Finish materials", true, null, null,
            Line(1, "paint-int-eggshell", 6), Line(2, "primer-int", 3), Line(3, "painters-tape", 6), Line(4, "roller-cover-9", 6), Line(5, "lvp-oak", 420),
            Line(6, "subway-tile-3x6", 60), Line(7, "thinset", 50), Line(8, "joint-compound", 5), Line(9, "drywall-12-4x8", 12), Line(10, "drywall-screw-158", 5),
            Line(11, "gfci-20a", 4), Line(12, "led-recessed-6", 8));
        var turnover = AddProject(owner, "Unit 4B Turnover", "Make-ready for a rental unit between tenants.", Industry.PropertyManagement, 5,
            "Turnover checklist", true, null, null,
            Line(1, "led-a19-2700", 24), Line(2, "smoke-alarm-sa10", 4), Line(3, "deadbolt-lr-d100", 2), Line(4, "caulk-kitchen-bath", 4), Line(5, "paint-int-eggshell", 4), Line(6, "filter-16x25x1-m8", 2));
        db.PriceAlerts.Add(new PriceAlert { CanonicalProductId = DemoCatalog.Products.First(p => p.Key == "lvp-oak").Id, Type = PriceAlertType.PriceBelow, TargetPrice = 2.75m, CreatedByUserId = owner.Id });
        await db.SaveChangesAsync(ct);
        await comparison.CompareAsync(kitchen.Id, forceRefresh: true, record: true, ct);
        await comparison.CompareAsync(turnover.Id, forceRefresh: false, record: true, ct);
    }

    private async Task SeedGreenValleyAsync(User owner, CancellationToken ct)
    {
        var refresh = AddProject(owner, "Willow Park HOA Spring Refresh", "Mulch beds, repair turf and extend drip irrigation at the entrance and clubhouse.", Industry.Landscaping, 12,
            "Spring materials", true, null, null,
            Line(1, "mulch-brown", 3, "cuyd"), Line(2, "topsoil", 40), Line(3, "landscape-fabric", 900), Line(4, "paver-12x12", 60),
            Line(5, "drip-tubing-12", 400), Line(6, "grass-seed-fescue", 50), Line(7, "lawn-fertilizer", 90), Line(8, "trash-bags-42", 100));
        await db.SaveChangesAsync(ct);
        await comparison.CompareAsync(refresh.Id, forceRefresh: true, record: true, ct);
    }
}
