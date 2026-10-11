using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SmartBuy.Domain.Entities;
using SmartBuy.Domain.Units;

namespace SmartBuy.Domain.Matching;

public sealed record CanonicalCandidate(
    Guid Id,
    string Name,
    string? Brand,
    string? Model,
    string? Mpn,
    string? Gtin,
    string? Material,
    string BaseUnit,
    IReadOnlyDictionary<string, string> Specs);

public sealed record SupplierListing(
    string Title,
    string? Brand,
    string? Model,
    string? Mpn,
    string? Gtin,
    IReadOnlyDictionary<string, string> Specs,
    decimal PackQuantity,
    string PackUnit);

public sealed record MatchResult(
    Guid CanonicalProductId,
    MatchMethod Method,
    double Confidence,
    MatchStatus Status,
    IReadOnlyList<string> Reasons);

/// <summary>
/// Deterministic product matching. Evidence is tried strongest-first (GTIN → MPN → brand+model → verified
/// specifications → title similarity). Name similarity alone can never produce an automatic match, and any conflict in
/// a critical specification vetoes the weaker methods.
/// </summary>
public static partial class ProductMatcher
{
    public const double AutoMatchThreshold = 0.85;
    public const double ReviewThreshold = 0.35;
    private const double TitleOnlyCap = 0.60;

    /// <summary>Specifications that define what a part physically is. A disagreement here means a different product.</summary>
    public static readonly IReadOnlySet<string> CriticalSpecKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "material", "size", "diameter", "length", "width", "thickness", "height", "voltage", "amperage", "wattage",
        "capacity", "grade", "gauge", "type", "connection", "thread", "phase", "poles", "btu", "tonnage", "seer",
        "refrigerant", "conductors", "schedule", "rating", "color_temperature", "finish_type", "angle"
    };

    public static MatchResult? FindBestMatch(SupplierListing listing, IEnumerable<CanonicalCandidate> candidates)
    {
        MatchResult? best = null;
        foreach (var candidate in candidates)
        {
            var result = Evaluate(listing, candidate);
            if (result is null) continue;
            if (best is null
                || result.Confidence > best.Confidence
                || (result.Confidence == best.Confidence && result.Method < best.Method)
                || (result.Confidence == best.Confidence && result.Method == best.Method && result.CanonicalProductId.CompareTo(best.CanonicalProductId) < 0))
            {
                best = result;
            }
        }
        return best;
    }

    /// <summary>Scores one listing against one canonical product. Null means "not the same product".</summary>
    public static MatchResult? Evaluate(SupplierListing listing, CanonicalCandidate candidate)
    {
        var reasons = new List<string>();
        var specs = CompareSpecs(listing, candidate);
        var unitCompatible = UnitConverter.AreCompatible(listing.PackUnit, candidate.BaseUnit);
        var brandConflict = !string.IsNullOrWhiteSpace(candidate.Brand)
                            && !string.IsNullOrWhiteSpace(listing.Brand)
                            && NormalizeToken(candidate.Brand) != NormalizeToken(listing.Brand);

        // 1. GTIN / UPC / EAN — a globally unique trade identifier.
        var listingGtin = NormalizeGtin(listing.Gtin);
        var candidateGtin = NormalizeGtin(candidate.Gtin);
        if (listingGtin is not null && candidateGtin is not null)
        {
            if (listingGtin == candidateGtin)
            {
                reasons.Add($"GTIN {listingGtin} matches");
                return Finish(candidate, MatchMethod.Gtin, 0.99, specs, unitCompatible, reasons);
            }
            // Two valid but different GTINs identify different trade items (possibly a different pack size).
            return null;
        }

        // 2. Manufacturer part number.
        var listingMpn = NormalizeToken(listing.Mpn);
        var candidateMpn = NormalizeToken(candidate.Mpn);
        if (listingMpn.Length >= 3 && listingMpn == candidateMpn && !brandConflict)
        {
            var brandsAgree = !string.IsNullOrWhiteSpace(candidate.Brand) && !string.IsNullOrWhiteSpace(listing.Brand);
            reasons.Add(brandsAgree
                ? $"Manufacturer part number {listing.Mpn} and brand match"
                : $"Manufacturer part number {listing.Mpn} matches (brand not confirmed on both sides)");
            return Finish(candidate, MatchMethod.Mpn, brandsAgree ? 0.95 : 0.86, specs, unitCompatible, reasons);
        }

        // 3. Manufacturer + model.
        var listingModel = NormalizeToken(listing.Model);
        var candidateModel = NormalizeToken(candidate.Model);
        if (listingModel.Length >= 2 && listingModel == candidateModel
            && !string.IsNullOrWhiteSpace(candidate.Brand) && !string.IsNullOrWhiteSpace(listing.Brand) && !brandConflict)
        {
            reasons.Add($"Brand {listing.Brand} and model {listing.Model} match");
            return Finish(candidate, MatchMethod.BrandModel, 0.90, specs, unitCompatible, reasons);
        }

        // Weaker evidence from here on: a branded canonical product only accepts its own brand,
        // and any critical-spec conflict means this is a different product.
        if (brandConflict || specs.Conflicts.Count > 0) return null;
        if (!string.IsNullOrWhiteSpace(candidate.Brand) && string.IsNullOrWhiteSpace(listing.Brand)) return null;

        var similarity = TitleSimilarity(listing.Title, candidate.Name);

        // 4. Verified technical specifications (needs several agreeing critical specs plus a plausible title).
        if (specs.Agreements.Count >= 2 && specs.MissingOnListing.Count == 0 && similarity >= 0.25)
        {
            var confidence = Math.Min(0.90, 0.78 + 0.04 * Math.Min(specs.Agreements.Count, 3));
            // Agreeing specifications with a weak title is not enough to skip review.
            if (similarity < 0.5) confidence = Math.Min(confidence, 0.80);
            reasons.Add($"{specs.Agreements.Count} critical specifications agree ({string.Join(", ", specs.Agreements)})");
            reasons.Add($"Title similarity {similarity * 100:0}%");
            return Finish(candidate, MatchMethod.Specifications, confidence, specs, unitCompatible, reasons);
        }

        // 5. Normalized title similarity — capped so it always lands in manual review.
        if (similarity >= 0.5)
        {
            var confidence = Math.Min(TitleOnlyCap, similarity * TitleOnlyCap);
            if (confidence < ReviewThreshold) return null;
            reasons.Add($"Title similarity {similarity * 100:0}% only — names alone are not proof of equivalence");
            if (specs.MissingOnListing.Count > 0)
                reasons.Add($"Supplier listing does not state: {string.Join(", ", specs.MissingOnListing)}");
            return Finish(candidate, MatchMethod.TitleSimilarity, confidence, specs, unitCompatible, reasons);
        }

        return null;
    }

    private static MatchResult Finish(CanonicalCandidate candidate, MatchMethod method, double confidence, SpecComparison specs, bool unitCompatible, List<string> reasons)
    {
        // Even identifier matches are downgraded when the data disagrees: bad supplier data is common.
        if (specs.Conflicts.Count > 0)
        {
            confidence = Math.Min(confidence, 0.70);
            reasons.Add($"Specification conflict: {string.Join("; ", specs.Conflicts)}");
        }
        if (!unitCompatible)
        {
            confidence = Math.Min(confidence, 0.50);
            reasons.Add("Package unit cannot be converted to the catalog unit — quantity normalization needs review");
        }
        var status = confidence >= AutoMatchThreshold ? MatchStatus.AutoMatched : MatchStatus.NeedsReview;
        return new MatchResult(candidate.Id, method, Math.Round(confidence, 2), status, reasons);
    }

    private sealed record SpecComparison(List<string> Agreements, List<string> Conflicts, List<string> MissingOnListing);

    private static SpecComparison CompareSpecs(SupplierListing listing, CanonicalCandidate candidate)
    {
        var agreements = new List<string>();
        var conflicts = new List<string>();
        var missing = new List<string>();

        var canonicalSpecs = new Dictionary<string, string>(candidate.Specs, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(candidate.Material) && !canonicalSpecs.ContainsKey("material"))
            canonicalSpecs["material"] = candidate.Material;
        var listingSpecs = new Dictionary<string, string>(listing.Specs, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, expected) in canonicalSpecs.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!CriticalSpecKeys.Contains(key)) continue;
            if (!listingSpecs.TryGetValue(key, out var actual) || string.IsNullOrWhiteSpace(actual))
            {
                missing.Add(key);
                continue;
            }
            if (NormalizeSpecValue(expected) == NormalizeSpecValue(actual)) agreements.Add(key);
            else conflicts.Add($"{key}: catalog \"{expected}\" vs supplier \"{actual}\"");
        }
        return new SpecComparison(agreements, conflicts, missing);
    }

    /// <summary>Digits only, validated check digit, padded to GTIN-14. Null when not a valid GTIN.</summary>
    public static string? NormalizeGtin(string? gtin)
    {
        if (string.IsNullOrWhiteSpace(gtin)) return null;
        var digits = new string(gtin.Where(char.IsDigit).ToArray());
        if (digits.Length is not (8 or 12 or 13 or 14)) return null;
        var sum = 0;
        for (var i = digits.Length - 2; i >= 0; i--)
        {
            var positionFromRight = digits.Length - 1 - i;
            sum += (digits[i] - '0') * (positionFromRight % 2 == 1 ? 3 : 1);
        }
        var check = (10 - sum % 10) % 10;
        return check == digits[^1] - '0' ? digits.PadLeft(14, '0') : null;
    }

    public static string NormalizeToken(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    /// <summary>Normalizes "3/4 in.", "3/4\"", "0.75 inch" to the same string so they compare equal.</summary>
    public static string NormalizeSpecValue(string value)
    {
        var s = value.Trim().ToLowerInvariant().Replace("-", " ");
        s = s.Replace("\"", " in ").Replace("''", " in ").Replace("'", " ft ");
        s = MixedFraction().Replace(s, ReplaceMixedFraction);
        s = Fraction().Replace(s, ReplaceFraction);
        s = Number().Replace(s, m => TryNumber(m.Value, out var n) ? ToDecimal(n) : m.Value);
        s = UnitWords().Replace(s, m => m.Groups[1].Value switch
        {
            "inches" or "inch" or "in" => "in",
            "feet" or "foot" or "ft" => "ft",
            "volts" or "volt" or "v" => "v",
            "amps" or "amp" or "a" => "a",
            "watts" or "watt" or "w" => "w",
            "gallons" or "gallon" or "gal" => "gal",
            "awg" or "gauge" or "ga" => "ga",
            _ => m.Groups[1].Value
        });
        var sb = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            // Keep a period only when it is a decimal point ("0.75"), not an abbreviation mark ("in.").
            var isDecimalPoint = ch == '.' && i > 0 && i + 1 < s.Length && char.IsDigit(s[i - 1]) && char.IsDigit(s[i + 1]);
            if (char.IsLetterOrDigit(ch) || isDecimalPoint) sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string ToDecimal(decimal d) => decimal.Round(d, 4).ToString("0.####", CultureInfo.InvariantCulture);

    private static bool TryNumber(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    private static string ReplaceMixedFraction(Match m) =>
        TryNumber(m.Groups[1].Value, out var whole) && TryNumber(m.Groups[2].Value, out var num)
        && TryNumber(m.Groups[3].Value, out var den) && den != 0
            ? ToDecimal(whole + num / den)
            : m.Value;

    private static string ReplaceFraction(Match m) =>
        TryNumber(m.Groups[1].Value, out var num) && TryNumber(m.Groups[2].Value, out var den) && den != 0
            ? ToDecimal(num / den)
            : m.Value;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "of", "for", "with", "and", "in", "x", "by", "pack", "pk", "each", "ea", "pc", "pcs", "piece", "new"
    };

    public static string NormalizeTitle(string title)
    {
        var tokens = Tokenize(title);
        return string.Join(' ', tokens.OrderBy(t => t, StringComparer.Ordinal));
    }

    /// <summary>Token-set (Jaccard) similarity over normalized titles, 0..1.</summary>
    public static double TitleSimilarity(string a, string b)
    {
        var ta = Tokenize(a);
        var tb = Tokenize(b);
        if (ta.Count == 0 || tb.Count == 0) return 0;
        var intersection = ta.Intersect(tb).Count();
        var union = ta.Union(tb).Count();
        return (double)intersection / union;
    }

    /// <summary>Normalized, singularized, stop-word-free tokens (fractions become decimals).</summary>
    public static IReadOnlySet<string> Tokens(string text) => Tokenize(text);

    private static HashSet<string> Tokenize(string text)
    {
        var s = NormalizeSpecValueForTitle(text);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim('.');
            if (token.Length == 0 || StopWords.Contains(token)) continue;
            // Cheap singularization so "elbows" and "elbow" agree.
            if (token.Length > 3 && token.EndsWith('s') && !token.EndsWith("ss") && !char.IsDigit(token[0])) token = token[..^1];
            set.Add(token);
        }
        return set;
    }

    private static string NormalizeSpecValueForTitle(string text)
    {
        var s = text.ToLowerInvariant().Replace("\"", " in ").Replace("-", " ");
        s = MixedFraction().Replace(s, ReplaceMixedFraction);
        s = Fraction().Replace(s, ReplaceFraction);
        s = s.Replace("inches", "in").Replace("inch", "in").Replace("feet", "ft").Replace("foot", "ft");
        var sb = new StringBuilder();
        foreach (var ch in s) sb.Append(char.IsLetterOrDigit(ch) || ch == '.' ? ch : ' ');
        return sb.ToString();
    }

    [GeneratedRegex(@"(\d+)\s+(\d+)\s*/\s*(\d+)")]
    private static partial Regex MixedFraction();

    [GeneratedRegex(@"(\d+)\s*/\s*(\d+)")]
    private static partial Regex Fraction();

    [GeneratedRegex(@"\d+(\.\d+)?")]
    private static partial Regex Number();

    [GeneratedRegex(@"(?<![a-z])(inches|inch|in|feet|foot|ft|volts|volt|v|amps|amp|a|watts|watt|w|gallons|gallon|gal|awg|gauge|ga)(?![a-z])")]
    private static partial Regex UnitWords();
}
