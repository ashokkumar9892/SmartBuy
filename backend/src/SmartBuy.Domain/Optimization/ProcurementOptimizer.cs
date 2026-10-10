using SmartBuy.Domain.Pricing;

namespace SmartBuy.Domain.Optimization;

public enum PurchasingStrategy { LowestCost = 0, FastestDelivery = 1, Balanced = 2 }

public sealed record ItemRequirement(Guid ItemId, string Description, decimal Quantity, string Unit);

/// <summary>One way to fill one list item: a specific supplier offer with packs already fitted to the requirement.</summary>
/// <param name="DeliveryDays">Null when the supplier gave no delivery estimate.</param>
/// <param name="StockUnknown">Supplier did not report inventory; the option is allowed but flagged.</param>
public sealed record OfferOption(
    Guid ItemId,
    Guid OfferId,
    Guid SupplierId,
    Guid SupplierProductId,
    int Packs,
    decimal LineCost,
    decimal UnitPrice,
    int? DeliveryDays,
    bool StockUnknown,
    bool IsSubstitute);

public sealed class OptimizationProblem
{
    public required IReadOnlyList<ItemRequirement> Items { get; init; }
    /// <summary>Feasible options per item (insufficient known stock must already be excluded).</summary>
    public required IReadOnlyDictionary<Guid, IReadOnlyList<OfferOption>> Options { get; init; }
    public required IReadOnlyDictionary<Guid, SupplierTerms> Suppliers { get; init; }
    public decimal? TaxRatePercent { get; init; }
    /// <summary>Days from today by which everything must arrive. Null when the project has no deadline.</summary>
    public int? DeadlineDays { get; init; }
}

public sealed record PlanLine(ItemRequirement Item, OfferOption Option);

public sealed record PurchasingPlan(
    PurchasingStrategy Strategy,
    IReadOnlyList<PlanLine> Lines,
    IReadOnlyList<ItemRequirement> UnfilledItems,
    OrderTotals Totals,
    int? MaxDeliveryDays,
    bool DeliveryUnknown,
    bool? MeetsDeadline,
    string Method,
    IReadOnlyList<string> Uncertainties);

public sealed record Baseline(
    string Label,
    string Explanation,
    IReadOnlyList<PlanLine> Lines,
    OrderTotals Totals);

/// <summary>
/// Deterministic purchasing optimizer. All money math is exact decimal arithmetic on supplied offers; nothing here is
/// estimated by AI. Small problems are solved exhaustively (provably optimal for the stated objective); larger ones
/// enumerate supplier subsets and then apply a deterministic single-move local search.
/// </summary>
public static class ProcurementOptimizer
{
    /// <summary>Delivery time assumed for ranking when a supplier gives no estimate. Never shown as a fact.</summary>
    public const int UnknownDeliveryDays = 30;
    public const long ExhaustiveLimit = 60_000;
    public const int MaxSubsetSuppliers = 12;
    /// <summary>Balanced strategy: each delivery day is weighted as this fraction of the line cost.</summary>
    public const decimal BalancedDayWeight = 0.03m;
    /// <summary>Balanced strategy: preferred suppliers receive this fractional credit on line cost.</summary>
    public const decimal BalancedPreferenceCredit = 0.03m;
    private const decimal InfeasiblePenalty = 10_000_000m;

    private readonly record struct Key(decimal A, decimal B, decimal C) : IComparable<Key>
    {
        public int CompareTo(Key other)
        {
            var c = A.CompareTo(other.A);
            if (c != 0) return c;
            c = B.CompareTo(other.B);
            return c != 0 ? c : C.CompareTo(other.C);
        }
    }

    private sealed class Model
    {
        public required ItemRequirement[] Items;
        public required OfferOption[][] Options;
        public required int[][] SupplierIndex;
        public required SupplierTerms[] Suppliers;
        public required int? Deadline;
        public required PurchasingStrategy Strategy;
    }

    public static PurchasingPlan Optimize(OptimizationProblem problem, PurchasingStrategy strategy, IReadOnlyDictionary<Guid, OfferOption>? seed = null)
    {
        var fillable = problem.Items.Where(i => problem.Options.TryGetValue(i.ItemId, out var o) && o.Count > 0).ToArray();
        var unfilled = problem.Items.Where(i => !fillable.Contains(i)).ToList();
        if (fillable.Length == 0)
            return BuildPlan(problem, strategy, new Dictionary<Guid, OfferOption>(), unfilled, "none");

        var supplierIds = fillable.SelectMany(i => problem.Options[i.ItemId]).Select(o => o.SupplierId).Distinct().OrderBy(id => id).ToArray();
        var supplierPos = supplierIds.Select((id, idx) => (id, idx)).ToDictionary(x => x.id, x => x.idx);
        var options = fillable
            .Select(i => problem.Options[i.ItemId].OrderBy(o => o.LineCost).ThenBy(o => o.OfferId).ToArray())
            .ToArray();
        var model = new Model
        {
            Items = fillable,
            Options = options,
            SupplierIndex = options.Select(row => row.Select(o => supplierPos[o.SupplierId]).ToArray()).ToArray(),
            Suppliers = supplierIds.Select(id => problem.Suppliers.TryGetValue(id, out var t) ? t : new SupplierTerms(id, "Unknown supplier", null, null, null)).ToArray(),
            Deadline = problem.DeadlineDays,
            Strategy = strategy
        };

        long combinations = 1;
        foreach (var row in options)
        {
            combinations *= row.Length;
            if (combinations > ExhaustiveLimit) break;
        }

        int[] best;
        string method;
        if (combinations <= ExhaustiveLimit)
        {
            best = Exhaustive(model);
            method = "exhaustive";
        }
        else
        {
            best = SubsetSearch(model, SeedToIndices(model, seed));
            method = model.Suppliers.Length <= MaxSubsetSuppliers ? "supplier-subset + local-search" : "greedy + local-search";
        }

        // A caller-provided plan (the baseline) is always a candidate, so the result can never be worse than it.
        var seedIndices = SeedToIndices(model, seed);
        if (seedIndices is not null && Score(model, seedIndices).CompareTo(Score(model, best)) < 0) best = seedIndices;

        var assignment = new Dictionary<Guid, OfferOption>();
        for (var i = 0; i < fillable.Length; i++) assignment[fillable[i].ItemId] = options[i][best[i]];
        return BuildPlan(problem, strategy, assignment, unfilled, method);
    }

    /// <summary>
    /// Baseline = one-stop shopping: the single supplier that covers the most items (cheapest on ties). Items that supplier
    /// does not carry are priced at the cheapest offer elsewhere. This is the reference every "savings" figure is measured against.
    /// </summary>
    public static Baseline? ComputeBaseline(OptimizationProblem problem)
    {
        var fillable = problem.Items.Where(i => problem.Options.TryGetValue(i.ItemId, out var o) && o.Count > 0).ToList();
        if (fillable.Count == 0) return null;

        (Guid SupplierId, int Coverage, decimal Total, Dictionary<Guid, OfferOption> Assignment, int Filled)? best = null;
        var supplierIds = fillable.SelectMany(i => problem.Options[i.ItemId]).Select(o => o.SupplierId).Distinct().OrderBy(id => id);
        foreach (var supplierId in supplierIds)
        {
            var assignment = new Dictionary<Guid, OfferOption>();
            var coverage = 0;
            foreach (var item in fillable)
            {
                var all = problem.Options[item.ItemId];
                var own = all.Where(o => o.SupplierId == supplierId && !o.IsSubstitute).OrderBy(o => o.LineCost).ThenBy(o => o.OfferId).FirstOrDefault();
                if (own is not null) coverage++;
                assignment[item.ItemId] = own ?? all.OrderBy(o => o.LineCost).ThenBy(o => o.OfferId).First();
            }
            if (coverage == 0) continue;
            var total = Totals(problem, assignment).KnownTotal;
            if (best is null || coverage > best.Value.Coverage || (coverage == best.Value.Coverage && total < best.Value.Total))
                best = (supplierId, coverage, total, assignment, fillable.Count - coverage);
        }
        if (best is null) return null;

        var name = problem.Suppliers.TryGetValue(best.Value.SupplierId, out var terms) ? terms.Name : "one supplier";
        var lines = fillable.Select(i => new PlanLine(i, best.Value.Assignment[i.ItemId])).ToList();
        var explanation = best.Value.Filled == 0
            ? $"Buying every priced item from {name}, the single supplier that carries the whole list at the lowest total."
            : $"Buying from {name}, the single supplier that carries the most items ({best.Value.Coverage} of {fillable.Count}); the remaining {best.Value.Filled} item(s) are priced at the cheapest offer elsewhere.";
        return new Baseline($"Single supplier: {name}", explanation, lines, Totals(problem, best.Value.Assignment));
    }

    public static OrderTotals Totals(OptimizationProblem problem, IReadOnlyDictionary<Guid, OfferOption> assignment) =>
        OrderCalculator.Calculate(assignment.Values.Select(o => (o.SupplierId, o.LineCost)), problem.Suppliers, problem.TaxRatePercent);

    private static PurchasingPlan BuildPlan(OptimizationProblem problem, PurchasingStrategy strategy, Dictionary<Guid, OfferOption> assignment, List<ItemRequirement> unfilled, string method)
    {
        var lines = problem.Items.Where(i => assignment.ContainsKey(i.ItemId)).Select(i => new PlanLine(i, assignment[i.ItemId])).ToList();
        var totals = Totals(problem, assignment);
        var deliveryUnknown = lines.Any(l => l.Option.DeliveryDays is null);
        int? maxDays = lines.Count == 0 || lines.All(l => l.Option.DeliveryDays is null) ? null : lines.Max(l => l.Option.DeliveryDays ?? 0);
        bool? meets = problem.DeadlineDays is null || lines.Count == 0
            ? null
            : lines.All(l => l.Option.DeliveryDays is { } d && d <= problem.DeadlineDays);

        var notes = new List<string>();
        if (unfilled.Count > 0) notes.Add($"{unfilled.Count} item(s) have no usable supplier offer and are not included in any total.");
        foreach (var s in totals.Suppliers.Where(s => s.Shipping is null))
            notes.Add($"Shipping from {s.SupplierName} is unknown and is not included in the total.");
        if (totals.TaxUnknown) notes.Add("No tax rate is set for this organization, so tax is not included in the total.");
        foreach (var s in totals.Suppliers.Where(s => s.BelowMinimumOrder))
            notes.Add($"The order from {s.SupplierName} is below that supplier's minimum order amount.");
        var stockUnknown = lines.Count(l => l.Option.StockUnknown);
        if (stockUnknown > 0) notes.Add($"Stock is unconfirmed for {stockUnknown} line(s).");
        if (deliveryUnknown) notes.Add("Delivery time is unknown for at least one line.");
        if (meets == false) notes.Add("At least one line cannot be confirmed to arrive by the required date.");
        var subs = lines.Count(l => l.Option.IsSubstitute);
        if (subs > 0) notes.Add($"{subs} line(s) use a substitution you approved.");

        return new PurchasingPlan(strategy, lines, unfilled, totals, maxDays, deliveryUnknown, meets, method, notes);
    }

    private static int[]? SeedToIndices(Model model, IReadOnlyDictionary<Guid, OfferOption>? seed)
    {
        if (seed is null) return null;
        var indices = new int[model.Items.Length];
        for (var i = 0; i < model.Items.Length; i++)
        {
            if (!seed.TryGetValue(model.Items[i].ItemId, out var chosen)) return null;
            var idx = Array.FindIndex(model.Options[i], o => o.OfferId == chosen.OfferId);
            if (idx < 0) return null;
            indices[i] = idx;
        }
        return indices;
    }

    private static Key Score(Model m, int[] pick)
    {
        Span<decimal> subtotals = m.Suppliers.Length <= 64 ? stackalloc decimal[m.Suppliers.Length] : new decimal[m.Suppliers.Length];
        subtotals.Clear();
        decimal late = 0, weighted = 0;
        var maxDays = 0;
        for (var i = 0; i < pick.Length; i++)
        {
            var o = m.Options[i][pick[i]];
            var s = m.SupplierIndex[i][pick[i]];
            subtotals[s] += o.LineCost;
            var days = o.DeliveryDays ?? UnknownDeliveryDays;
            if (days > maxDays) maxDays = days;
            if (m.Deadline is { } deadline && days > deadline) late++;
            if (m.Strategy == PurchasingStrategy.Balanced)
                weighted += o.LineCost * (1 + BalancedDayWeight * Math.Min(days, UnknownDeliveryDays)) * (m.Suppliers[s].IsPreferred ? 1 - BalancedPreferenceCredit : 1);
        }

        decimal cost = 0, extras = 0;
        for (var s = 0; s < subtotals.Length; s++)
        {
            if (subtotals[s] <= 0) continue;
            cost += subtotals[s];
            var terms = m.Suppliers[s];
            extras += OrderCalculator.Shipping(terms, subtotals[s]) ?? 0;
            if (terms.MinimumOrderAmount is { } min && subtotals[s] < min) extras += InfeasiblePenalty;
        }

        return m.Strategy switch
        {
            PurchasingStrategy.FastestDelivery => new Key(late, maxDays, cost + extras),
            PurchasingStrategy.Balanced => new Key(late, weighted + extras, maxDays),
            _ => new Key(late, cost + extras, maxDays)
        };
    }

    private static int[] Exhaustive(Model m)
    {
        var n = m.Items.Length;
        var pick = new int[n];
        var best = (int[])pick.Clone();
        var bestKey = Score(m, pick);
        while (true)
        {
            // Odometer increment over every combination, in a fixed order.
            var pos = n - 1;
            while (pos >= 0 && ++pick[pos] == m.Options[pos].Length) pick[pos--] = 0;
            if (pos < 0) break;
            var key = Score(m, pick);
            if (key.CompareTo(bestKey) < 0)
            {
                bestKey = key;
                Array.Copy(pick, best, n);
            }
        }
        return best;
    }

    private static int[] SubsetSearch(Model m, int[]? seed)
    {
        var n = m.Items.Length;
        var candidates = new List<(Key Key, int[] Pick)>();

        void Offer(int[] pick)
        {
            var key = Score(m, pick);
            candidates.Add((key, (int[])pick.Clone()));
            if (candidates.Count > 64)
            {
                candidates.Sort((x, y) => x.Key.CompareTo(y.Key));
                candidates.RemoveRange(8, candidates.Count - 8);
            }
        }

        // Greedy start: best option per item ignoring order-level effects.
        var greedy = new int[n];
        for (var i = 0; i < n; i++) greedy[i] = BestInMask(m, i, ulong.MaxValue);
        Offer(greedy);
        if (seed is not null) Offer(seed);

        if (m.Suppliers.Length <= MaxSubsetSuppliers)
        {
            var pick = new int[n];
            for (ulong mask = 1; mask < (1UL << m.Suppliers.Length); mask++)
            {
                var covered = true;
                for (var i = 0; i < n && covered; i++)
                {
                    var idx = BestInMask(m, i, mask);
                    if (idx < 0) covered = false;
                    else pick[i] = idx;
                }
                if (covered) Offer(pick);
            }
        }

        candidates.Sort((x, y) => x.Key.CompareTo(y.Key));
        var bestPick = candidates[0].Pick;
        var bestKey = candidates[0].Key;
        foreach (var (_, start) in candidates.Take(8))
        {
            var improved = LocalSearch(m, start, out var key);
            if (key.CompareTo(bestKey) < 0)
            {
                bestKey = key;
                bestPick = improved;
            }
        }
        return bestPick;
    }

    private static int BestInMask(Model m, int item, ulong mask)
    {
        var best = -1;
        Key bestKey = default;
        for (var j = 0; j < m.Options[item].Length; j++)
        {
            var s = m.SupplierIndex[item][j];
            if (s < 64 && (mask & (1UL << s)) == 0) continue;
            var o = m.Options[item][j];
            var days = o.DeliveryDays ?? UnknownDeliveryDays;
            decimal late = m.Deadline is { } d && days > d ? 1 : 0;
            var key = m.Strategy switch
            {
                PurchasingStrategy.FastestDelivery => new Key(late, days, o.LineCost),
                PurchasingStrategy.Balanced => new Key(late, o.LineCost * (1 + BalancedDayWeight * Math.Min(days, UnknownDeliveryDays)) * (m.Suppliers[s].IsPreferred ? 1 - BalancedPreferenceCredit : 1), days),
                _ => new Key(late, o.LineCost, days)
            };
            if (best < 0 || key.CompareTo(bestKey) < 0)
            {
                best = j;
                bestKey = key;
            }
        }
        return best;
    }

    private static int[] LocalSearch(Model m, int[] start, out Key key)
    {
        var pick = (int[])start.Clone();
        key = Score(m, pick);
        for (var round = 0; round < 200; round++)
        {
            var improved = false;
            for (var i = 0; i < pick.Length; i++)
            {
                var original = pick[i];
                var bestJ = original;
                for (var j = 0; j < m.Options[i].Length; j++)
                {
                    if (j == original) continue;
                    pick[i] = j;
                    var candidate = Score(m, pick);
                    if (candidate.CompareTo(key) < 0)
                    {
                        key = candidate;
                        bestJ = j;
                        improved = true;
                    }
                }
                pick[i] = bestJ;
            }
            if (!improved) break;
        }
        return pick;
    }
}
