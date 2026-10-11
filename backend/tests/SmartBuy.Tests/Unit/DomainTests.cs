using SmartBuy.Domain.Entities;
using SmartBuy.Domain.Matching;
using SmartBuy.Domain.Optimization;
using SmartBuy.Domain.Pricing;
using SmartBuy.Domain.Rules;
using SmartBuy.Domain.Units;

namespace SmartBuy.Tests.Unit;

public class UnitConversionTests
{
    [Theory]
    [InlineData("Feet", "ft")]
    [InlineData("pcs", "each")]
    [InlineData("EA", "each")]
    [InlineData("sq. ft.", "sqft")]
    [InlineData("gallons", "gal")]
    [InlineData("lbs", "lb")]
    [InlineData("cubic yards", "cuyd")]
    public void Normalizes_common_spellings(string input, string expected) => Assert.Equal(expected, UnitConverter.Normalize(input));

    [Theory]
    [InlineData("widgets")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_units_are_not_guessed(string? input) => Assert.Null(UnitConverter.Normalize(input));

    [Fact]
    public void Converts_within_a_dimension()
    {
        Assert.Equal(4m, UnitConverter.Convert(48m, "in", "ft"));
        Assert.Equal(1m, UnitConverter.Convert(4m, "qt", "gal"));
        Assert.Equal(2m, UnitConverter.Convert(32m, "oz", "lb"));
        Assert.Equal(81m, decimal.Round(UnitConverter.Convert(3m, "cuyd", "cuft")!.Value, 2));
        Assert.Equal(24m, UnitConverter.Convert(2m, "dozen", "each"));
    }

    [Fact]
    public void Refuses_to_convert_across_dimensions()
    {
        Assert.Null(UnitConverter.Convert(10m, "ft", "lb"));
        Assert.Null(UnitConverter.Convert(10m, "each", "gal"));
        Assert.False(UnitConverter.AreCompatible("sqft", "ft"));
    }
}

public class PackNormalizationTests
{
    [Fact]
    public void A_bag_of_ten_covers_ten_pieces_with_one_package()
    {
        var fit = PackMath.Fit(10m, "each", 10m, "each", 22.00m)!;
        Assert.Equal(1, fit.Packs);
        Assert.Equal(22.00m, fit.LineCost);
        Assert.Equal(2.20m, fit.UnitPrice);
    }

    [Fact]
    public void Rounds_up_to_whole_packages_and_reports_surplus()
    {
        var fit = PackMath.Fit(20m, "each", 25m, "each", 47.34m)!;
        Assert.Equal(1, fit.Packs);
        Assert.Equal(5m, fit.Surplus);

        var twelve = PackMath.Fit(12m, "each", 10m, "each", 15.00m)!;
        Assert.Equal(2, twelve.Packs);
        Assert.Equal(30.00m, twelve.LineCost);
        Assert.Equal(8m, twelve.Surplus);
    }

    [Fact]
    public void Converts_requirement_units_into_package_units()
    {
        // 50 ft needed, sold in 10 ft sticks.
        var sticks = PackMath.Fit(50m, "ft", 10m, "ft", 50.38m)!;
        Assert.Equal(5, sticks.Packs);
        Assert.Equal(251.90m, sticks.LineCost);
        Assert.Equal(5.038m, sticks.UnitPrice);

        // 3 cubic yards needed, sold in 2 cu ft bags: 81 cu ft => 41 bags.
        var bags = PackMath.Fit(3m, "cuyd", 2m, "cuft", 3.90m)!;
        Assert.Equal(41, bags.Packs);

        // 5 gallons needed, sold by the quart.
        var quarts = PackMath.Fit(5m, "gal", 1m, "qt", 9.00m)!;
        Assert.Equal(20, quarts.Packs);
        Assert.Equal(36.00m, quarts.UnitPrice);
    }

    [Fact]
    public void Exact_fits_are_not_rounded_up_by_conversion_noise()
    {
        var fit = PackMath.Fit(1m, "cuyd", 27m, "cuft", 40m)!;
        Assert.Equal(1, fit.Packs);
    }

    [Fact]
    public void Minimum_order_quantity_raises_the_package_count()
    {
        var fit = PackMath.Fit(1m, "each", 1m, "each", 4.00m, minOrderPacks: 2)!;
        Assert.Equal(2, fit.Packs);
        Assert.Equal(8.00m, fit.LineCost);
    }

    [Fact]
    public void Incompatible_units_return_null_instead_of_a_guess()
    {
        Assert.Null(PackMath.Fit(10m, "ft", 5m, "lb", 10m));
        Assert.Null(PackMath.Fit(10m, "widgets", 5m, "each", 10m));
        Assert.Null(PackMath.UnitPrice(10m, 5m, "lb", "ft"));
    }
}

public class PriceCalculationTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private static Dictionary<Guid, SupplierTerms> Terms() => new()
    {
        [A] = new SupplierTerms(A, "Alpha", 8.99m, 45m, null),
        [B] = new SupplierTerms(B, "Beta", null, null, 100m),
    };

    [Fact]
    public void Shipping_is_flat_below_the_threshold_and_free_at_or_above_it()
    {
        var terms = Terms()[A];
        Assert.Equal(8.99m, OrderCalculator.Shipping(terms, 44.99m));
        Assert.Equal(0m, OrderCalculator.Shipping(terms, 45m));
    }

    [Fact]
    public void Unknown_shipping_stays_unknown_and_is_never_zero()
    {
        Assert.Null(OrderCalculator.Shipping(Terms()[B], 500m));
        var totals = OrderCalculator.Calculate(new[] { (B, 60m) }, Terms(), 8m);
        Assert.True(totals.ShippingUnknown);
        Assert.False(totals.IsComplete);
        Assert.Null(totals.Suppliers.Single().Shipping);
        Assert.Equal(64.80m, totals.KnownTotal); // merchandise + tax only
    }

    [Fact]
    public void Unknown_tax_stays_unknown()
    {
        var totals = OrderCalculator.Calculate(new[] { (A, 100m) }, Terms(), null);
        Assert.Null(totals.Tax);
        Assert.True(totals.TaxUnknown);
        Assert.Equal(100m, totals.KnownTotal);
    }

    [Fact]
    public void Totals_group_by_supplier_and_flag_minimum_order_shortfalls()
    {
        var totals = OrderCalculator.Calculate(new[] { (A, 20m), (A, 10m), (B, 60m) }, Terms(), 10m);
        var alpha = totals.Suppliers.Single(s => s.SupplierId == A);
        var beta = totals.Suppliers.Single(s => s.SupplierId == B);
        Assert.Equal(30m, alpha.Subtotal);
        Assert.Equal(2, alpha.LineCount);
        Assert.Equal(8.99m, alpha.Shipping);
        Assert.Equal(15m, alpha.AmountToFreeShipping);
        Assert.True(beta.BelowMinimumOrder);
        Assert.Equal(90m, totals.Subtotal);
        Assert.Equal(9m, totals.Tax);
        Assert.Equal(107.99m, totals.KnownTotal);
    }

    [Fact]
    public void Tax_rounds_half_away_from_zero_to_cents() => Assert.Equal(0.83m, OrderCalculator.Tax(10.05m, 8.25m));
}

public class ProductMatchingTests
{
    private static readonly Guid PipeL = Guid.NewGuid();
    private static readonly Guid PipeM = Guid.NewGuid();
    private static readonly Guid Faucet = Guid.NewGuid();

    private static readonly CanonicalCandidate[] Catalog =
    {
        new(PipeL, "3/4 in. Type L Copper Pipe", null, null, null, null, "Copper", "ft", new Dictionary<string, string> { ["size"] = "3/4 in", ["type"] = "L" }),
        new(PipeM, "3/4 in. Type M Copper Pipe", null, null, null, null, "Copper", "ft", new Dictionary<string, string> { ["size"] = "3/4 in", ["type"] = "M" }),
        new(Faucet, "Aquaforge AF-2100 Bathroom Faucet", "Aquaforge", "AF-2100-BN", "AF-2100-BN", "400000000015", "Brass", "each", new Dictionary<string, string> { ["finish_type"] = "Brushed nickel" }),
    };

    private static SupplierListing Listing(string title, string? brand = null, string? model = null, string? mpn = null, string? gtin = null, Dictionary<string, string>? specs = null, decimal pack = 1, string unit = "each") =>
        new(title, brand, model, mpn, gtin, specs ?? new Dictionary<string, string>(), pack, unit);

    [Fact]
    public void Gtin_is_the_strongest_evidence()
    {
        var result = ProductMatcher.FindBestMatch(Listing("Totally different title", gtin: "0400000000015"), Catalog)!;
        Assert.Equal(Faucet, result.CanonicalProductId);
        Assert.Equal(MatchMethod.Gtin, result.Method);
        Assert.Equal(MatchStatus.AutoMatched, result.Status);
        Assert.True(result.Confidence >= 0.99);
    }

    [Fact]
    public void Invalid_gtin_check_digit_is_ignored()
    {
        Assert.Null(ProductMatcher.NormalizeGtin("400000000018"));
        Assert.Equal("00400000000015", ProductMatcher.NormalizeGtin("4 00000 00001 5"));
    }

    [Fact]
    public void Different_valid_gtins_never_match_even_with_identical_titles()
    {
        var other = Listing("Aquaforge AF-2100 Bathroom Faucet", "Aquaforge", "AF-2100-BN", "AF-2100-BN", "400000000022");
        Assert.NotNull(ProductMatcher.NormalizeGtin("400000000022"));
        Assert.Null(ProductMatcher.Evaluate(other, Catalog[2]));
    }

    [Fact]
    public void Mpn_with_brand_matches_automatically()
    {
        var result = ProductMatcher.FindBestMatch(Listing("AF2100 lav faucet BN", "AQUAFORGE", mpn: "af-2100 bn"), Catalog)!;
        Assert.Equal(MatchMethod.Mpn, result.Method);
        Assert.Equal(MatchStatus.AutoMatched, result.Status);
    }

    [Fact]
    public void Brand_and_model_match_when_no_identifiers_are_present()
    {
        var result = ProductMatcher.FindBestMatch(Listing("Bath faucet", "Aquaforge", model: "AF-2100-BN"), Catalog)!;
        Assert.Equal(MatchMethod.BrandModel, result.Method);
    }

    [Fact]
    public void Similar_names_alone_never_auto_match()
    {
        var result = ProductMatcher.FindBestMatch(Listing("3/4\" Type L Copper Pipe 10ft - contractor lot"), Catalog);
        Assert.NotNull(result);
        Assert.Equal(MatchMethod.TitleSimilarity, result!.Method);
        Assert.Equal(MatchStatus.NeedsReview, result.Status);
        Assert.True(result.Confidence <= 0.60);
    }

    [Fact]
    public void A_conflicting_critical_spec_selects_the_right_product_not_the_similar_one()
    {
        var typeM = Listing("3/4 in. Type M Copper Pipe (10 ft)", specs: new() { ["size"] = "3/4 in", ["type"] = "M", ["material"] = "Copper" }, pack: 10, unit: "ft");
        Assert.Null(ProductMatcher.Evaluate(typeM, Catalog[0]));
        Assert.Equal(PipeM, ProductMatcher.FindBestMatch(typeM, Catalog)!.CanonicalProductId);
    }

    [Fact]
    public void Verified_specifications_match_commodity_items_across_brands()
    {
        var listing = Listing("3/4 in. Type L Copper Pipe (10 ft)", "Mueller", specs: new() { ["size"] = "0.75 inch", ["type"] = "L", ["material"] = "copper" }, pack: 10, unit: "ft");
        var result = ProductMatcher.FindBestMatch(listing, Catalog)!;
        Assert.Equal(PipeL, result.CanonicalProductId);
        Assert.Equal(MatchMethod.Specifications, result.Method);
        Assert.Equal(MatchStatus.AutoMatched, result.Status);
    }

    [Fact]
    public void A_branded_catalog_product_rejects_other_brands()
    {
        var other = Listing("Bathroom Faucet Brushed Nickel", "Rivermark", specs: new() { ["finish_type"] = "Brushed nickel", ["material"] = "Brass" });
        Assert.Null(ProductMatcher.Evaluate(other, Catalog[2]));
    }

    [Fact]
    public void Identifier_matches_with_conflicting_specs_are_sent_to_review()
    {
        var conflict = Listing("Aquaforge AF-2100", "Aquaforge", gtin: "400000000015", specs: new() { ["finish_type"] = "Chrome" });
        var result = ProductMatcher.Evaluate(conflict, Catalog[2])!;
        Assert.Equal(MatchStatus.NeedsReview, result.Status);
        Assert.Contains(result.Reasons, r => r.Contains("conflict", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_package_unit_that_cannot_be_converted_forces_review()
    {
        var byWeight = Listing("3/4 in. Type L Copper Pipe", specs: new() { ["size"] = "3/4 in", ["type"] = "L", ["material"] = "Copper" }, pack: 5, unit: "lb");
        var result = ProductMatcher.Evaluate(byWeight, Catalog[0])!;
        Assert.Equal(MatchStatus.NeedsReview, result.Status);
        Assert.True(result.Confidence <= 0.5);
    }

    [Theory]
    [InlineData("3/4 in.", "0.75 inch")]
    [InlineData("3/4\"", "3/4 in")]
    [InlineData("1-1/2 in", "1.5 inches")]
    [InlineData("120 V", "120 volts")]
    [InlineData("12 AWG", "12 gauge")]
    public void Spec_values_normalize_to_the_same_form(string a, string b) =>
        Assert.Equal(ProductMatcher.NormalizeSpecValue(a), ProductMatcher.NormalizeSpecValue(b));

    [Fact]
    public void Malformed_fractions_do_not_throw() => Assert.NotNull(ProductMatcher.NormalizeSpecValue("1/0 in 99999999999999999999999999999999/3"));
}

public class OptimizerTests
{
    private static Guid G(int n) => new($"00000000-0000-0000-0000-{n:000000000000}");

    private static OptimizationProblem Problem(IEnumerable<SupplierTerms> suppliers, int itemCount, Func<int, IEnumerable<(Guid Supplier, decimal Cost, int? Days)>> offers, int? deadline = null, decimal? tax = null)
    {
        var items = Enumerable.Range(1, itemCount).Select(i => new ItemRequirement(G(i), $"Item {i}", 1, "each")).ToList();
        var options = new Dictionary<Guid, IReadOnlyList<OfferOption>>();
        var offerId = 1000;
        for (var i = 0; i < itemCount; i++)
        {
            options[items[i].ItemId] = offers(i).Select(o => new OfferOption(items[i].ItemId, G(offerId++), o.Supplier, G(offerId++), 1, o.Cost, o.Cost, o.Days, false, false)).ToList();
        }
        return new OptimizationProblem { Items = items, Options = options, Suppliers = suppliers.ToDictionary(s => s.SupplierId), DeadlineDays = deadline, TaxRatePercent = tax };
    }

    [Fact]
    public void Lowest_cost_accounts_for_shipping_not_just_item_prices()
    {
        var a = new SupplierTerms(G(1), "A", 30m, null, null);
        var b = new SupplierTerms(G(2), "B", 0m, null, null);
        // A is $1 cheaper on one item, but buying from A adds $30 shipping.
        var problem = Problem(new[] { a, b }, 2, i => i == 0
            ? new[] { (a.SupplierId, 9m, (int?)2), (b.SupplierId, 10m, (int?)2) }
            : new[] { (b.SupplierId, 10m, (int?)2) });
        var plan = ProcurementOptimizer.Optimize(problem, PurchasingStrategy.LowestCost);
        Assert.All(plan.Lines, l => Assert.Equal(b.SupplierId, l.Option.SupplierId));
        Assert.Equal(20m, plan.Totals.KnownTotal);
        Assert.Equal("exhaustive", plan.Method);
    }

    [Fact]
    public void Lowest_cost_consolidates_to_reach_a_free_shipping_threshold()
    {
        var a = new SupplierTerms(G(1), "A", 12m, 50m, null);
        var b = new SupplierTerms(G(2), "B", 12m, 50m, null);
        // Splitting pays shipping twice; consolidating on A reaches the threshold.
        var problem = Problem(new[] { a, b }, 2, i => i == 0
            ? new[] { (a.SupplierId, 30m, (int?)2), (b.SupplierId, 29m, (int?)2) }
            : new[] { (a.SupplierId, 25m, (int?)2), (b.SupplierId, 26m, (int?)2) });
        var plan = ProcurementOptimizer.Optimize(problem, PurchasingStrategy.LowestCost);
        Assert.Equal(55m, plan.Totals.KnownTotal);
        Assert.Single(plan.Totals.Suppliers);
    }

    [Fact]
    public void Unknown_shipping_is_not_ranked_as_free()
    {
        var known = new SupplierTerms(G(1), "Known", 5m, null, null);
        var unknown = new SupplierTerms(G(2), "Unknown", null, null, null);
        var problem = Problem(new[] { known, unknown }, 1, _ => new[] { (known.SupplierId, 100m, (int?)2), (unknown.SupplierId, 102m, (int?)2) });
        var plan = ProcurementOptimizer.Optimize(problem, PurchasingStrategy.LowestCost);
        Assert.Equal(known.SupplierId, plan.Lines.Single().Option.SupplierId);
        Assert.Equal(105m, plan.Totals.KnownTotal);
    }

    [Fact]
    public void Unknown_shipping_is_flagged_and_excluded_from_the_total_when_chosen()
    {
        var unknown = new SupplierTerms(G(2), "Unknown", null, null, null);
        var problem = Problem(new[] { unknown }, 1, _ => new[] { (unknown.SupplierId, 99m, (int?)null) });
        var plan = ProcurementOptimizer.Optimize(problem, PurchasingStrategy.LowestCost);
        Assert.Equal(99m, plan.Totals.KnownTotal);
        Assert.True(plan.Totals.ShippingUnknown);
        Assert.True(plan.DeliveryUnknown);
        Assert.Contains(plan.Uncertainties, n => n.Contains("Shipping from Unknown is unknown"));
        Assert.Contains(plan.Uncertainties, n => n.Contains("Delivery time is unknown"));
    }

    [Fact]
    public void Fastest_delivery_prefers_speed_then_cost()
    {
        var slow = new SupplierTerms(G(1), "Slow", 0m, null, null);
        var fast = new SupplierTerms(G(2), "Fast", 0m, null, null);
        var problem = Problem(new[] { slow, fast }, 2, _ => new[] { (slow.SupplierId, 10m, (int?)6), (fast.SupplierId, 14m, (int?)1) });
        var fastest = ProcurementOptimizer.Optimize(problem, PurchasingStrategy.FastestDelivery);
        var cheapest = ProcurementOptimizer.Optimize(problem, PurchasingStrategy.LowestCost);
        Assert.Equal(1, fastest.MaxDeliveryDays);
        Assert.Equal(28m, fastest.Totals.KnownTotal);
        Assert.Equal(20m, cheapest.Totals.KnownTotal);
    }

    [Fact]
    public void A_deadline_overrides_price_in_every_strategy()
    {
        var slow = new SupplierTerms(G(1), "Slow", 0m, null, null);
        var fast = new SupplierTerms(G(2), "Fast", 0m, null, null);
        var problem = Problem(new[] { slow, fast }, 1, _ => new[] { (slow.SupplierId, 10m, (int?)9), (fast.SupplierId, 40m, (int?)2) }, deadline: 3);
        foreach (var strategy in Enum.GetValues<PurchasingStrategy>())
        {
            var plan = ProcurementOptimizer.Optimize(problem, strategy);
            Assert.Equal(fast.SupplierId, plan.Lines.Single().Option.SupplierId);
            Assert.True(plan.MeetsDeadline);
        }
    }

    [Fact]
    public void Balanced_gives_preferred_suppliers_a_small_credit()
    {
        var plain = new SupplierTerms(G(1), "Plain", 0m, null, null);
        var preferred = new SupplierTerms(G(2), "Preferred", 0m, null, null, IsPreferred: true);
        var problem = Problem(new[] { plain, preferred }, 1, _ => new[] { (plain.SupplierId, 100m, (int?)2), (preferred.SupplierId, 101m, (int?)2) });
        Assert.Equal(preferred.SupplierId, ProcurementOptimizer.Optimize(problem, PurchasingStrategy.Balanced).Lines.Single().Option.SupplierId);
        Assert.Equal(plain.SupplierId, ProcurementOptimizer.Optimize(problem, PurchasingStrategy.LowestCost).Lines.Single().Option.SupplierId);
    }

    [Fact]
    public void Minimum_order_amounts_are_respected_when_an_alternative_exists()
    {
        var wholesale = new SupplierTerms(G(1), "Wholesale", 0m, null, 100m);
        var retail = new SupplierTerms(G(2), "Retail", 0m, null, null);
        var problem = Problem(new[] { wholesale, retail }, 1, _ => new[] { (wholesale.SupplierId, 20m, (int?)2), (retail.SupplierId, 25m, (int?)2) });
        var plan = ProcurementOptimizer.Optimize(problem, PurchasingStrategy.LowestCost);
        Assert.Equal(retail.SupplierId, plan.Lines.Single().Option.SupplierId);
    }

    [Fact]
    public void Items_without_offers_are_reported_not_priced()
    {
        var a = new SupplierTerms(G(1), "A", 0m, null, null);
        var problem = Problem(new[] { a }, 2, i => i == 0 ? new[] { (a.SupplierId, 10m, (int?)1) } : Array.Empty<(Guid, decimal, int?)>());
        var plan = ProcurementOptimizer.Optimize(problem, PurchasingStrategy.LowestCost);
        Assert.Single(plan.Lines);
        Assert.Single(plan.UnfilledItems);
        Assert.Equal(10m, plan.Totals.KnownTotal);
    }

    [Fact]
    public void Baseline_is_the_best_single_supplier_and_the_plan_is_never_worse()
    {
        var a = new SupplierTerms(G(1), "A", 5m, null, null);
        var b = new SupplierTerms(G(2), "B", 5m, null, null);
        var problem = Problem(new[] { a, b }, 3, i => new[] { (a.SupplierId, 10m + i, (int?)2), (b.SupplierId, 12m - i * 3, (int?)2) }, tax: 10m);
        var baseline = ProcurementOptimizer.ComputeBaseline(problem)!;
        var seed = baseline.Lines.ToDictionary(l => l.Item.ItemId, l => l.Option);
        var plan = ProcurementOptimizer.Optimize(problem, PurchasingStrategy.LowestCost, seed);
        Assert.Single(baseline.Totals.Suppliers);
        Assert.StartsWith("Single supplier:", baseline.Label);
        Assert.True(plan.Totals.KnownTotal <= baseline.Totals.KnownTotal);
    }

    [Fact]
    public void Results_are_deterministic()
    {
        var problem = RandomProblem(seed: 7, items: 6, suppliers: 5);
        var first = ProcurementOptimizer.Optimize(problem, PurchasingStrategy.LowestCost);
        var second = ProcurementOptimizer.Optimize(problem, PurchasingStrategy.LowestCost);
        Assert.Equal(first.Lines.Select(l => l.Option.OfferId), second.Lines.Select(l => l.Option.OfferId));
    }

    private static OptimizationProblem RandomProblem(int seed, int items, int suppliers)
    {
        var random = new Random(seed);
        var terms = Enumerable.Range(1, suppliers).Select(s => new SupplierTerms(G(s), $"S{s}",
            random.Next(4) == 0 ? null : random.Next(0, 30), random.Next(2) == 0 ? random.Next(40, 200) : null, null)).ToList();
        return Problem(terms, items, _ => terms.Where(_ => random.Next(10) < 7).DefaultIfEmpty(terms[0])
            .Select(t => (t.SupplierId, (decimal)random.Next(500, 9000) / 100m, (int?)random.Next(1, 8))).ToList());
    }

    /// <summary>The heuristic path (used for large lists) is checked against provably optimal exhaustive search.</summary>
    [Fact]
    public void Heuristic_search_matches_exhaustive_search_on_random_problems()
    {
        var exact = 0;
        for (var seed = 1; seed <= 60; seed++)
        {
            var problem = RandomProblem(seed, items: 2 + seed % 6, suppliers: 3 + seed % 4);
            foreach (var strategy in Enum.GetValues<PurchasingStrategy>())
            {
                var optimal = ProcurementOptimizer.Optimize(problem, strategy, mode: SearchMode.Exhaustive);
                var heuristic = ProcurementOptimizer.Optimize(problem, strategy, mode: SearchMode.Heuristic);
                if (strategy == PurchasingStrategy.LowestCost)
                {
                    // Compare on the ranking objective, which includes the stand-in for unknown shipping.
                    var assumed = ProcurementOptimizer.UnknownShippingAssumption(problem);
                    decimal Objective(PurchasingPlan p) => p.Totals.Subtotal + p.Totals.KnownShipping + p.Totals.Suppliers.Count(s => s.Shipping is null) * assumed;
                    Assert.True(Objective(heuristic) <= Objective(optimal) * 1.03m, $"seed {seed}: heuristic {Objective(heuristic)} vs optimal {Objective(optimal)}");
                    if (Objective(heuristic) == Objective(optimal)) exact++;
                }
                else if (strategy == PurchasingStrategy.FastestDelivery)
                    Assert.Equal(optimal.MaxDeliveryDays, heuristic.MaxDeliveryDays);
            }
        }
        Assert.True(exact >= 54, $"Heuristic found the exact optimum in only {exact} of 60 lowest-cost problems.");
    }

    [Fact]
    public void Large_problems_use_the_heuristic_and_still_beat_or_equal_the_baseline()
    {
        var problem = RandomProblem(seed: 99, items: 40, suppliers: 9);
        var baseline = ProcurementOptimizer.ComputeBaseline(problem)!;
        var plan = ProcurementOptimizer.Optimize(problem, PurchasingStrategy.LowestCost, baseline.Lines.ToDictionary(l => l.Item.ItemId, l => l.Option));
        Assert.Contains("local-search", plan.Method);
        Assert.Equal(40, plan.Lines.Count);
        var assumed = ProcurementOptimizer.UnknownShippingAssumption(problem);
        decimal Objective(OrderTotals t) => t.Subtotal + t.KnownShipping + t.Suppliers.Count(s => s.Shipping is null) * assumed;
        Assert.True(Objective(plan.Totals) <= Objective(baseline.Totals));
    }
}

public class PriceAlertRuleTests
{
    private static readonly Guid S1 = Guid.NewGuid();
    private static readonly Guid S2 = Guid.NewGuid();

    private static AlertOffer[] Offers(decimal p1, decimal p2, bool? stock1 = true, bool? stock2 = true) =>
        new[] { new AlertOffer(S1, "Alpha", p1, stock1), new AlertOffer(S2, "Beta", p2, stock2) };

    [Fact]
    public void Price_below_fires_at_or_under_target_only()
    {
        var alert = new PriceAlert { Type = PriceAlertType.PriceBelow, TargetPrice = 4.50m };
        Assert.False(PriceAlertRules.Evaluate(alert, "Pipe", "ft", Offers(4.80m, 4.51m)).Triggered);
        var hit = PriceAlertRules.Evaluate(alert, "Pipe", "ft", Offers(4.80m, 4.50m));
        Assert.True(hit.Triggered);
        Assert.Equal(4.50m, hit.Price);
        Assert.Contains("Beta", hit.Message);
    }

    [Fact]
    public void Price_below_does_not_repeat_for_the_same_price()
    {
        var alert = new PriceAlert { Type = PriceAlertType.PriceBelow, TargetPrice = 4.50m, LastTriggeredPrice = 4.40m };
        Assert.False(PriceAlertRules.Evaluate(alert, "Pipe", "ft", Offers(4.40m, 5m)).Triggered);
        Assert.True(PriceAlertRules.Evaluate(alert, "Pipe", "ft", Offers(4.30m, 5m)).Triggered);
    }

    [Fact]
    public void Price_below_can_be_limited_to_one_supplier()
    {
        var alert = new PriceAlert { Type = PriceAlertType.PriceBelow, TargetPrice = 4.50m, SupplierId = S1 };
        Assert.False(PriceAlertRules.Evaluate(alert, "Pipe", "ft", Offers(4.80m, 4.00m)).Triggered);
    }

    [Fact]
    public void Back_in_stock_needs_a_confirmed_out_then_in_transition()
    {
        var neverSeen = new PriceAlert { Type = PriceAlertType.BackInStock, LastKnownInStock = null };
        Assert.False(PriceAlertRules.Evaluate(neverSeen, "Valve", "each", Offers(9m, 9m)).Triggered);

        var wasOut = new PriceAlert { Type = PriceAlertType.BackInStock, LastKnownInStock = false };
        Assert.True(PriceAlertRules.Evaluate(wasOut, "Valve", "each", Offers(9m, 9m, stock1: false, stock2: true)).Triggered);
        Assert.False(PriceAlertRules.Evaluate(wasOut, "Valve", "each", Offers(9m, 9m, stock1: false, stock2: false)).Triggered);
        Assert.False(PriceAlertRules.Evaluate(wasOut, "Valve", "each", Offers(9m, 9m, stock1: null, stock2: null)).Triggered);
    }

    [Fact]
    public void Better_offer_compares_other_suppliers_with_the_current_one()
    {
        var alert = new PriceAlert { Type = PriceAlertType.BetterOffer, SupplierId = S1 };
        Assert.False(PriceAlertRules.Evaluate(alert, "Faucet", "each", Offers(80m, 85m)).Triggered);
        var hit = PriceAlertRules.Evaluate(alert, "Faucet", "each", Offers(100m, 80m));
        Assert.True(hit.Triggered);
        Assert.Contains("20%", hit.Message);
        Assert.False(PriceAlertRules.Evaluate(alert, "Faucet", "each", Offers(100m, 80m, stock2: false)).Triggered);
    }

    [Fact]
    public void No_offers_means_no_alert()
    {
        var alert = new PriceAlert { Type = PriceAlertType.PriceBelow, TargetPrice = 100m };
        Assert.False(PriceAlertRules.Evaluate(alert, "Pipe", "ft", Array.Empty<AlertOffer>()).Triggered);
    }
}

public class PurchaseOrderRuleTests
{
    [Fact]
    public void Totals_sum_lines_and_only_known_extras()
    {
        var po = new PurchaseOrder { ShippingEstimate = null, TaxEstimate = 10.50m };
        po.Items.Add(new PurchaseOrderItem { Quantity = 5, UnitPrice = 50.38m });
        po.Items.Add(new PurchaseOrderItem { Quantity = 2.5m, UnitPrice = 3.33m });
        PurchaseOrderRules.Recalculate(po);
        Assert.Equal(251.90m, po.Items[0].LineTotal);
        Assert.Equal(8.33m, po.Items[1].LineTotal);
        Assert.Equal(260.23m, po.Subtotal);
        Assert.Equal(270.73m, po.Total);
    }

    [Theory]
    [InlineData(PurchaseOrderStatus.Draft, PurchaseOrderStatus.PendingApproval, true)]
    [InlineData(PurchaseOrderStatus.Draft, PurchaseOrderStatus.Sent, false)]
    [InlineData(PurchaseOrderStatus.PendingApproval, PurchaseOrderStatus.Approved, true)]
    [InlineData(PurchaseOrderStatus.PendingApproval, PurchaseOrderStatus.Draft, true)]
    [InlineData(PurchaseOrderStatus.Approved, PurchaseOrderStatus.Sent, true)]
    [InlineData(PurchaseOrderStatus.Sent, PurchaseOrderStatus.Received, true)]
    [InlineData(PurchaseOrderStatus.Received, PurchaseOrderStatus.Cancelled, false)]
    [InlineData(PurchaseOrderStatus.Cancelled, PurchaseOrderStatus.Draft, false)]
    [InlineData(PurchaseOrderStatus.PartiallyReceived, PurchaseOrderStatus.Cancelled, false)]
    public void Status_transitions_follow_the_workflow(PurchaseOrderStatus from, PurchaseOrderStatus to, bool allowed) =>
        Assert.Equal(allowed, PurchaseOrderRules.CanTransition(from, to));

    [Fact]
    public void Receipt_status_reflects_received_quantities()
    {
        var po = new PurchaseOrder();
        po.Items.Add(new PurchaseOrderItem { Quantity = 5 });
        po.Items.Add(new PurchaseOrderItem { Quantity = 2 });
        Assert.Equal(PurchaseOrderStatus.Sent, PurchaseOrderRules.ReceiptStatus(po));
        po.Items[0].QuantityReceived = 5;
        Assert.Equal(PurchaseOrderStatus.PartiallyReceived, PurchaseOrderRules.ReceiptStatus(po));
        po.Items[1].QuantityReceived = 2;
        Assert.Equal(PurchaseOrderStatus.Received, PurchaseOrderRules.ReceiptStatus(po));
    }

    [Fact]
    public void Po_numbers_are_formatted_per_year() => Assert.Equal("PO-2026-0042", PurchaseOrderRules.FormatNumber(2026, 42));
}

public class EntitlementAndRoleTests
{
    private static readonly Plan Free = new() { Name = "Free", MaxProjectsPerMonth = 3, MaxMembers = 1 };
    private static readonly Plan Business = new() { Name = "Business", PurchaseOrders = true, PriceAlerts = true, AiMaterialLists = true, AdvancedOptimization = true, ApprovalWorkflows = true, AdvancedReporting = true, MaxMembers = 25 };

    [Fact]
    public void Features_follow_the_plan()
    {
        Assert.False(Entitlements.CheckFeature(Free, PlanFeature.PurchaseOrders).Allowed);
        Assert.Contains("Free plan", Entitlements.CheckFeature(Free, PlanFeature.PriceAlerts).Reason);
        Assert.All(Enum.GetValues<PlanFeature>(), f => Assert.True(Entitlements.CheckFeature(Business, f).Allowed));
    }

    [Fact]
    public void Quotas_block_at_the_limit_and_null_means_unlimited()
    {
        Assert.True(Entitlements.CheckProjectQuota(Free, 2).Allowed);
        Assert.False(Entitlements.CheckProjectQuota(Free, 3).Allowed);
        Assert.True(Entitlements.CheckProjectQuota(Business, 10_000).Allowed);
        Assert.False(Entitlements.CheckMemberQuota(Free, 1).Allowed);
    }

    [Fact]
    public void Roles_are_ordered_and_cannot_escalate()
    {
        Assert.False(RolePermissions.CanEditLists(OrgRole.Viewer));
        Assert.True(RolePermissions.CanEditLists(OrgRole.Employee));
        Assert.False(RolePermissions.CanApprovePurchaseOrders(OrgRole.Employee));
        Assert.True(RolePermissions.CanApprovePurchaseOrders(OrgRole.PurchasingManager));
        Assert.False(RolePermissions.CanManageBilling(OrgRole.Administrator));
        Assert.False(RolePermissions.CanAssignRole(OrgRole.Administrator, OrgRole.Owner));
        Assert.True(RolePermissions.CanAssignRole(OrgRole.Administrator, OrgRole.Administrator));
        Assert.False(RolePermissions.CanAssignRole(OrgRole.PurchasingManager, OrgRole.Viewer));
    }
}
