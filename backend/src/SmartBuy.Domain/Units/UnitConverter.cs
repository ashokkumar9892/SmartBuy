namespace SmartBuy.Domain.Units;

public enum UnitDimension { Count, Length, Volume, Weight, Area }

/// <param name="Code">Canonical code (each, ft, in, gal, ...).</param>
/// <param name="ToBase">Multiplier to the dimension's base unit (each, ft, gal, lb, sqft).</param>
public sealed record UnitDefinition(string Code, UnitDimension Dimension, decimal ToBase, string Label);

/// <summary>Deterministic unit normalization. No unit is ever guessed: unknown text returns null.</summary>
public static class UnitConverter
{
    private static readonly UnitDefinition[] Definitions =
    {
        new("each", UnitDimension.Count, 1m, "each"),
        new("pair", UnitDimension.Count, 2m, "pair"),
        new("dozen", UnitDimension.Count, 12m, "dozen"),
        new("ft", UnitDimension.Length, 1m, "ft"),
        new("in", UnitDimension.Length, 1m / 12m, "in"),
        new("yd", UnitDimension.Length, 3m, "yd"),
        new("m", UnitDimension.Length, 3.280839895m, "m"),
        new("cm", UnitDimension.Length, 0.03280839895m, "cm"),
        new("mm", UnitDimension.Length, 0.003280839895m, "mm"),
        new("gal", UnitDimension.Volume, 1m, "gal"),
        new("qt", UnitDimension.Volume, 0.25m, "qt"),
        new("pt", UnitDimension.Volume, 0.125m, "pt"),
        new("floz", UnitDimension.Volume, 1m / 128m, "fl oz"),
        new("l", UnitDimension.Volume, 0.2641720524m, "L"),
        new("ml", UnitDimension.Volume, 0.0002641720524m, "mL"),
        new("lb", UnitDimension.Weight, 1m, "lb"),
        new("oz", UnitDimension.Weight, 1m / 16m, "oz"),
        new("kg", UnitDimension.Weight, 2.2046226218m, "kg"),
        new("g", UnitDimension.Weight, 0.0022046226218m, "g"),
        new("ton", UnitDimension.Weight, 2000m, "ton"),
        new("sqft", UnitDimension.Area, 1m, "sq ft"),
        new("sqyd", UnitDimension.Area, 9m, "sq yd"),
        new("sqm", UnitDimension.Area, 10.76391042m, "sq m"),
        new("cuft", UnitDimension.Volume, 7.480519481m, "cu ft"),
        new("cuyd", UnitDimension.Volume, 201.974026m, "cu yd"),
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ea"] = "each", ["each"] = "each", ["pc"] = "each", ["pcs"] = "each", ["piece"] = "each", ["pieces"] = "each",
        ["unit"] = "each", ["units"] = "each", ["item"] = "each", ["items"] = "each", ["ct"] = "each", ["count"] = "each",
        ["x"] = "each", ["qty"] = "each", ["nos"] = "each", ["no"] = "each",
        ["pair"] = "pair", ["pairs"] = "pair", ["pr"] = "pair",
        ["dozen"] = "dozen", ["doz"] = "dozen", ["dz"] = "dozen",
        ["ft"] = "ft", ["foot"] = "ft", ["feet"] = "ft", ["'"] = "ft", ["lf"] = "ft", ["linft"] = "ft", ["linearfeet"] = "ft", ["linearfoot"] = "ft", ["linearft"] = "ft",
        ["in"] = "in", ["inch"] = "in", ["inches"] = "in", ["\""] = "in",
        ["yd"] = "yd", ["yard"] = "yd", ["yards"] = "yd", ["yds"] = "yd",
        ["m"] = "m", ["meter"] = "m", ["meters"] = "m", ["metre"] = "m", ["metres"] = "m",
        ["cm"] = "cm", ["centimeter"] = "cm", ["centimeters"] = "cm",
        ["mm"] = "mm", ["millimeter"] = "mm", ["millimeters"] = "mm",
        ["gal"] = "gal", ["gallon"] = "gal", ["gallons"] = "gal", ["gals"] = "gal",
        ["qt"] = "qt", ["quart"] = "qt", ["quarts"] = "qt", ["qts"] = "qt",
        ["pt"] = "pt", ["pint"] = "pt", ["pints"] = "pt",
        ["floz"] = "floz", ["fluidounce"] = "floz", ["fluidounces"] = "floz",
        ["l"] = "l", ["liter"] = "l", ["liters"] = "l", ["litre"] = "l", ["litres"] = "l",
        ["ml"] = "ml", ["milliliter"] = "ml", ["milliliters"] = "ml",
        ["lb"] = "lb", ["lbs"] = "lb", ["pound"] = "lb", ["pounds"] = "lb", ["#"] = "lb",
        ["oz"] = "oz", ["ounce"] = "oz", ["ounces"] = "oz",
        ["kg"] = "kg", ["kilogram"] = "kg", ["kilograms"] = "kg", ["kgs"] = "kg",
        ["g"] = "g", ["gram"] = "g", ["grams"] = "g",
        ["ton"] = "ton", ["tons"] = "ton",
        ["sqft"] = "sqft", ["sf"] = "sqft", ["squarefeet"] = "sqft", ["squarefoot"] = "sqft", ["ft2"] = "sqft", ["sq.ft"] = "sqft",
        ["sqyd"] = "sqyd", ["squareyard"] = "sqyd", ["squareyards"] = "sqyd", ["yd2"] = "sqyd",
        ["sqm"] = "sqm", ["squaremeter"] = "sqm", ["squaremeters"] = "sqm", ["m2"] = "sqm",
        ["cuft"] = "cuft", ["cubicfeet"] = "cuft", ["cubicfoot"] = "cuft", ["ft3"] = "cuft", ["cf"] = "cuft",
        ["cuyd"] = "cuyd", ["cubicyard"] = "cuyd", ["cubicyards"] = "cuyd", ["yd3"] = "cuyd", ["cy"] = "cuyd",
    };

    private static readonly Dictionary<string, UnitDefinition> ByCode =
        Definitions.ToDictionary(d => d.Code, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<UnitDefinition> All => Definitions;

    /// <summary>Resolves free text ("Feet", "pcs", "sq. ft.") to a unit definition, or null when unrecognized.</summary>
    public static UnitDefinition? Resolve(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var key = text.Trim().TrimEnd('.').Replace(" ", "").Replace(".", "").ToLowerInvariant();
        if (key.Length == 0) return null;
        if (Aliases.TryGetValue(key, out var code)) return ByCode[code];
        // "sq.ft" collapses to "sqft" above; keep the dotted alias reachable too.
        return Aliases.TryGetValue(text.Trim().ToLowerInvariant(), out code) ? ByCode[code] : null;
    }

    /// <summary>Canonical code for the text, or null when unrecognized.</summary>
    public static string? Normalize(string? text) => Resolve(text)?.Code;

    public static bool AreCompatible(string? a, string? b)
    {
        var ua = Resolve(a);
        var ub = Resolve(b);
        return ua is not null && ub is not null && ua.Dimension == ub.Dimension;
    }

    /// <summary>Converts a quantity between units of the same dimension. Returns null when units are unknown or incompatible.</summary>
    public static decimal? Convert(decimal quantity, string? fromUnit, string? toUnit)
    {
        var from = Resolve(fromUnit);
        var to = Resolve(toUnit);
        if (from is null || to is null || from.Dimension != to.Dimension) return null;
        if (from.Code == to.Code) return quantity;
        return quantity * from.ToBase / to.ToBase;
    }
}

/// <summary>Result of fitting a required quantity into whole supplier packages.</summary>
/// <param name="Packs">Whole packages to buy (after minimum-order rounding).</param>
/// <param name="RequiredInPackUnit">Required quantity expressed in the package's unit.</param>
/// <param name="PurchasedQuantity">Total quantity received, in the requirement's unit.</param>
/// <param name="Surplus">Quantity bought beyond what was required, in the requirement's unit.</param>
/// <param name="UnitPrice">Price per one requirement unit (pack price ÷ normalized pack quantity).</param>
public sealed record PackFit(int Packs, decimal RequiredInPackUnit, decimal PurchasedQuantity, decimal Surplus, decimal UnitPrice, decimal LineCost);

public static class PackMath
{
    // Guards against 4.0000000001 packs caused by unit-conversion rounding.
    private const decimal Epsilon = 0.000001m;

    /// <summary>
    /// How many whole packages cover <paramref name="requiredQuantity"/>. A "bag of 10" with a need of 10 pieces is one
    /// package, not ten. Returns null when units cannot be reconciled so the caller can route the line to manual review.
    /// </summary>
    public static PackFit? Fit(decimal requiredQuantity, string requiredUnit, decimal packQuantity, string packUnit, decimal packPrice, int minOrderPacks = 1)
    {
        if (requiredQuantity <= 0 || packQuantity <= 0 || packPrice < 0) return null;
        var requiredInPackUnit = UnitConverter.Convert(requiredQuantity, requiredUnit, packUnit);
        if (requiredInPackUnit is null) return null;

        var exact = requiredInPackUnit.Value / packQuantity;
        var packs = (int)Math.Ceiling(exact - Epsilon);
        packs = Math.Max(packs, Math.Max(1, minOrderPacks));

        var purchasedInPackUnit = packs * packQuantity;
        var purchased = UnitConverter.Convert(purchasedInPackUnit, packUnit, requiredUnit)!.Value;
        var perRequirementUnit = UnitConverter.Convert(1m, requiredUnit, packUnit)!.Value; // pack units per requirement unit
        var unitPrice = packPrice / packQuantity * perRequirementUnit;

        return new PackFit(
            packs,
            decimal.Round(requiredInPackUnit.Value, 6),
            decimal.Round(purchased, 4),
            decimal.Round(Math.Max(0, purchased - requiredQuantity), 4),
            decimal.Round(unitPrice, 4),
            decimal.Round(packs * packPrice, 2));
    }

    /// <summary>Price per one <paramref name="targetUnit"/>, or null when units are incompatible.</summary>
    public static decimal? UnitPrice(decimal packPrice, decimal packQuantity, string packUnit, string targetUnit)
    {
        if (packQuantity <= 0) return null;
        var packUnitsPerTarget = UnitConverter.Convert(1m, targetUnit, packUnit);
        if (packUnitsPerTarget is null) return null;
        return decimal.Round(packPrice / packQuantity * packUnitsPerTarget.Value, 4);
    }
}
