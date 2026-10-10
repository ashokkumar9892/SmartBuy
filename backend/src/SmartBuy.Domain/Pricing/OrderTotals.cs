namespace SmartBuy.Domain.Pricing;

/// <summary>Commercial terms that affect what an order from one supplier costs beyond the line items.</summary>
/// <param name="ShippingFlatFee">Null means the supplier's shipping charge is unknown (never assumed free).</param>
public sealed record SupplierTerms(
    Guid SupplierId,
    string Name,
    decimal? ShippingFlatFee,
    decimal? FreeShippingThreshold,
    decimal? MinimumOrderAmount,
    bool IsPreferred = false);

public sealed record SupplierTotals(
    Guid SupplierId,
    string SupplierName,
    int LineCount,
    decimal Subtotal,
    decimal? Shipping,
    decimal? Tax,
    decimal KnownTotal,
    bool BelowMinimumOrder,
    decimal? AmountToFreeShipping);

/// <summary>
/// Totals for a multi-supplier order. <see cref="KnownTotal"/> only ever sums figures that are actually known;
/// the Unknown flags say what is missing so the UI can show "+ shipping (unknown)" instead of a false total.
/// </summary>
public sealed record OrderTotals(
    IReadOnlyList<SupplierTotals> Suppliers,
    decimal Subtotal,
    decimal KnownShipping,
    bool ShippingUnknown,
    decimal? Tax,
    bool TaxUnknown,
    decimal KnownTotal)
{
    public bool IsComplete => !ShippingUnknown && !TaxUnknown;
}

public static class OrderCalculator
{
    /// <summary>Shipping for one supplier's subtotal. Null when the supplier does not publish a rate.</summary>
    public static decimal? Shipping(SupplierTerms terms, decimal subtotal)
    {
        if (subtotal <= 0) return 0m;
        if (terms.ShippingFlatFee is null) return null;
        if (terms.FreeShippingThreshold is { } threshold && subtotal >= threshold) return 0m;
        return terms.ShippingFlatFee.Value;
    }

    /// <summary>Estimated sales tax on merchandise only. Null when the organization has not provided a rate.</summary>
    public static decimal? Tax(decimal subtotal, decimal? taxRatePercent) =>
        taxRatePercent is null ? null : decimal.Round(subtotal * taxRatePercent.Value / 100m, 2, MidpointRounding.AwayFromZero);

    public static OrderTotals Calculate(
        IEnumerable<(Guid SupplierId, decimal LineCost)> lines,
        IReadOnlyDictionary<Guid, SupplierTerms> terms,
        decimal? taxRatePercent)
    {
        var suppliers = new List<SupplierTotals>();
        foreach (var group in lines.GroupBy(l => l.SupplierId))
        {
            var t = terms.TryGetValue(group.Key, out var found) ? found : new SupplierTerms(group.Key, "Unknown supplier", null, null, null);
            var subtotal = decimal.Round(group.Sum(l => l.LineCost), 2);
            var shipping = Shipping(t, subtotal);
            var tax = Tax(subtotal, taxRatePercent);
            decimal? toFree = t.ShippingFlatFee is > 0 && t.FreeShippingThreshold is { } th && subtotal < th ? th - subtotal : null;
            suppliers.Add(new SupplierTotals(
                t.SupplierId, t.Name, group.Count(), subtotal, shipping, tax,
                subtotal + (shipping ?? 0) + (tax ?? 0),
                t.MinimumOrderAmount is { } min && subtotal < min,
                toFree));
        }

        suppliers = suppliers.OrderBy(s => s.SupplierName, StringComparer.Ordinal).ThenBy(s => s.SupplierId).ToList();
        var sub = suppliers.Sum(s => s.Subtotal);
        var knownShipping = suppliers.Sum(s => s.Shipping ?? 0);
        var shippingUnknown = suppliers.Any(s => s.Shipping is null);
        var totalTax = Tax(sub, taxRatePercent);
        return new OrderTotals(suppliers, sub, knownShipping, shippingUnknown, totalTax, totalTax is null && sub > 0, sub + knownShipping + (totalTax ?? 0));
    }
}
