using System.Security.Cryptography;
using System.Text;
using SmartBuy.Domain.Entities;

namespace SmartBuy.Infrastructure.Suppliers.Mock;

/// <param name="Quantity">Units per package, in <paramref name="Unit"/>.</param>
/// <param name="Discount">Per-unit price multiplier for this pack size (bulk packs are cheaper per unit).</param>
public sealed record PackVariant(decimal Quantity, string Unit, decimal Discount = 1m);

public sealed record DemoProduct(
    string Key,
    Industry Industry,
    string Category,
    string Name,
    string BaseUnit,
    decimal BasePrice,
    string? Material,
    string? Brand,
    string? Model,
    bool SafetyCritical,
    string? SafetyNote,
    IReadOnlyDictionary<string, string> Specs,
    IReadOnlyList<PackVariant> Packs,
    IReadOnlyList<string> Keywords)
{
    public Guid Id => DemoCatalog.StableGuid("canonical:" + Key);
    /// <summary>Demo GTIN-12 in the "4" local-use number system, so it can never collide with a real product.</summary>
    public string? Gtin => Brand is null ? null : DemoCatalog.DemoGtin(Key);
}

public sealed record DemoAlternative(string ProductKey, string AlternativeKey, bool SameCertification, string SpecDifferences, string Compatibility, string QualityNotes, string Rationale);

/// <summary>
/// Fictional reference catalog used to seed canonical products and to drive the mock supplier adapters.
/// Brands, model numbers, GTINs and prices are invented for demonstration and are labelled DEMO everywhere they surface.
/// </summary>
public static class DemoCatalog
{
    private static Dictionary<string, string> S(params string[] kv)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
        return d;
    }

    private static PackVariant[] P(params PackVariant[] packs) => packs;
    private static PackVariant Pk(decimal qty, string unit, decimal discount = 1m) => new(qty, unit, discount);
    private static string[] K(params string[] words) => words;

    private static DemoProduct Item(string key, Industry industry, string category, string name, string baseUnit, decimal basePrice,
        Dictionary<string, string> specs, PackVariant[] packs, string[] keywords, string? material = null, string? brand = null,
        string? model = null, string? safety = null) =>
        new(key, industry, category, name, baseUnit, basePrice, material, brand, model, safety is not null, safety, specs, packs, keywords);

    public static readonly IReadOnlyList<DemoProduct> Products = new List<DemoProduct>
    {
        // ---------- Plumbing ----------
        Item("copper-pipe-l-34", Industry.Plumbing, "Pipe", "3/4 in. Type L Copper Pipe", "ft", 4.85m,
            S("size", "3/4 in", "type", "L"), P(Pk(10, "ft"), Pk(20, "ft", 0.97m), Pk(2, "ft", 1.18m)), K("copper", "pipe", "tube", "tubing", "hard"), material: "Copper"),
        Item("copper-pipe-m-34", Industry.Plumbing, "Pipe", "3/4 in. Type M Copper Pipe", "ft", 3.60m,
            S("size", "3/4 in", "type", "M"), P(Pk(10, "ft"), Pk(5, "ft", 1.08m)), K("copper", "pipe", "tube", "tubing"), material: "Copper"),
        Item("copper-pipe-l-12", Industry.Plumbing, "Pipe", "1/2 in. Type L Copper Pipe", "ft", 2.95m,
            S("size", "1/2 in", "type", "L"), P(Pk(10, "ft"), Pk(20, "ft", 0.97m)), K("copper", "pipe", "tube", "tubing"), material: "Copper"),
        Item("pex-a-34", Industry.Plumbing, "Pipe", "3/4 in. PEX-A Tubing", "ft", 0.92m,
            S("size", "3/4 in", "type", "PEX-A"), P(Pk(100, "ft"), Pk(300, "ft", 0.93m), Pk(20, "ft", 1.2m)), K("pex", "tubing", "pipe", "expansion"), material: "PEX"),
        Item("copper-elbow-90-34", Industry.Plumbing, "Fittings", "3/4 in. Copper 90-Degree Elbow (C x C)", "each", 2.48m,
            S("size", "3/4 in", "angle", "90 degree", "connection", "C x C"), P(Pk(1, "each"), Pk(10, "each", 0.88m), Pk(25, "each", 0.82m)), K("elbow", "elbows", "ell", "fitting", "copper", "sweat"), material: "Copper"),
        Item("copper-elbow-90-12", Industry.Plumbing, "Fittings", "1/2 in. Copper 90-Degree Elbow (C x C)", "each", 1.12m,
            S("size", "1/2 in", "angle", "90 degree", "connection", "C x C"), P(Pk(1, "each"), Pk(10, "each", 0.88m), Pk(25, "each", 0.82m)), K("elbow", "elbows", "ell", "fitting", "copper", "sweat"), material: "Copper"),
        Item("copper-coupling-34", Industry.Plumbing, "Fittings", "3/4 in. Copper Coupling with Stop (C x C)", "each", 1.64m,
            S("size", "3/4 in", "connection", "C x C"), P(Pk(1, "each"), Pk(10, "each", 0.9m)), K("coupling", "couplings", "fitting", "copper"), material: "Copper"),
        Item("copper-tee-34", Industry.Plumbing, "Fittings", "3/4 in. Copper Tee (C x C x C)", "each", 4.15m,
            S("size", "3/4 in", "connection", "C x C x C"), P(Pk(1, "each"), Pk(10, "each", 0.9m)), K("tee", "tees", "fitting", "copper"), material: "Copper"),
        Item("angle-stop-12", Industry.Plumbing, "Valves", "1/2 in. x 3/8 in. Quarter-Turn Angle Stop Shutoff Valve", "each", 9.75m,
            S("size", "1/2 in", "type", "Quarter-turn angle stop", "connection", "Compression"), P(Pk(1, "each"), Pk(5, "each", 0.93m)), K("shutoff", "shut-off", "stop", "valve", "valves", "angle", "supply"), material: "Brass"),
        Item("ball-valve-34", Industry.Plumbing, "Valves", "3/4 in. Lead-Free Brass Full-Port Ball Valve (Sweat)", "each", 14.20m,
            S("size", "3/4 in", "type", "Full-port ball valve", "connection", "Sweat", "rating", "Potable water 600 WOG"), P(Pk(1, "each")), K("ball", "valve", "valves", "shutoff", "main"), material: "Brass"),
        Item("gas-ball-valve-34", Industry.Plumbing, "Valves", "3/4 in. Gas Ball Valve (FIP x FIP)", "each", 17.60m,
            S("size", "3/4 in", "type", "Gas ball valve", "connection", "FIP x FIP", "rating", "CSA gas 5 psi"), P(Pk(1, "each")), K("gas", "ball", "valve", "appliance"), material: "Brass",
            safety: "Gas-rated component. Only a valve listed for fuel gas may be used on a gas line."),
        Item("faucet-bath-af2100", Industry.Plumbing, "Fixtures", "Aquaforge AF-2100 Single-Handle Bathroom Faucet, Brushed Nickel", "each", 89.00m,
            S("type", "Single-handle bathroom faucet", "finish_type", "Brushed nickel", "connection", "4 in centerset"), P(Pk(1, "each")), K("faucet", "faucets", "bathroom", "lavatory", "sink", "bath"), material: "Brass", brand: "Aquaforge", model: "AF-2100-BN"),
        Item("faucet-bath-af2000", Industry.Plumbing, "Fixtures", "Aquaforge AF-2000 Single-Handle Bathroom Faucet, Chrome", "each", 72.00m,
            S("type", "Single-handle bathroom faucet", "finish_type", "Chrome", "connection", "4 in centerset"), P(Pk(1, "each")), K("faucet", "faucets", "bathroom", "lavatory", "sink", "bath"), material: "Brass", brand: "Aquaforge", model: "AF-2000-CH"),
        Item("faucet-kitchen-af7400", Industry.Plumbing, "Fixtures", "Aquaforge AF-7400 Pull-Down Kitchen Faucet, Stainless", "each", 168.00m,
            S("type", "Pull-down kitchen faucet", "finish_type", "Stainless"), P(Pk(1, "each")), K("faucet", "faucets", "kitchen", "pull-down", "sink"), material: "Brass", brand: "Aquaforge", model: "AF-7400-SS"),
        Item("toilet-af-e128", Industry.Plumbing, "Fixtures", "Aquaforge E128 Two-Piece Elongated Toilet, 1.28 GPF, White", "each", 219.00m,
            S("type", "Two-piece elongated toilet", "capacity", "1.28 gpf"), P(Pk(1, "each")), K("toilet", "toilets", "commode", "wc"), material: "Vitreous china", brand: "Aquaforge", model: "E128-WH"),
        Item("wax-ring", Industry.Plumbing, "Fixtures", "Toilet Wax Ring with Flange and Bolts", "each", 6.40m,
            S("type", "Wax ring with flange", "size", "3 in"), P(Pk(1, "each"), Pk(6, "each", 0.9m)), K("wax", "ring", "toilet", "seal", "gasket")),
        Item("supply-line-12", Industry.Plumbing, "Fittings", "3/8 in. Comp x 1/2 in. FIP x 12 in. Braided Faucet Supply Line", "each", 7.10m,
            S("size", "3/8 in", "length", "12 in", "connection", "Comp x FIP"), P(Pk(1, "each"), Pk(2, "each", 0.94m)), K("supply", "line", "lines", "braided", "connector", "faucet", "hose"), material: "Stainless steel"),
        Item("ptfe-tape", Industry.Plumbing, "Consumables", "1/2 in. x 520 in. PTFE Thread Seal Tape", "each", 1.35m,
            S("width", "1/2 in", "length", "520 in"), P(Pk(1, "each"), Pk(10, "each", 0.8m)), K("teflon", "ptfe", "thread", "tape", "seal", "plumber")),
        Item("solder-lead-free", Industry.Plumbing, "Consumables", "Lead-Free Plumbing Solder", "lb", 31.00m,
            S("type", "Lead-free"), P(Pk(1, "lb"), Pk(8, "oz", 1.06m)), K("solder", "sweat", "soldering"), material: "Tin alloy"),
        Item("p-trap-112", Industry.Plumbing, "Drainage", "1-1/2 in. PVC P-Trap", "each", 5.90m,
            S("size", "1-1/2 in"), P(Pk(1, "each")), K("p-trap", "ptrap", "trap", "drain", "sink"), material: "PVC"),

        // ---------- Electrical ----------
        Item("nmb-12-2", Industry.Electrical, "Wire", "12/2 NM-B Wire with Ground", "ft", 0.78m,
            S("gauge", "12 AWG", "conductors", "2 with ground", "type", "NM-B", "rating", "20 A"), P(Pk(250, "ft"), Pk(50, "ft", 1.15m), Pk(25, "ft", 1.3m)), K("romex", "wire", "cable", "nm-b", "nm", "12-2", "12/2"), material: "Copper",
            safety: "Conductor size determines ampacity. A smaller gauge must never replace a larger one."),
        Item("nmb-14-2", Industry.Electrical, "Wire", "14/2 NM-B Wire with Ground", "ft", 0.52m,
            S("gauge", "14 AWG", "conductors", "2 with ground", "type", "NM-B", "rating", "15 A"), P(Pk(250, "ft"), Pk(50, "ft", 1.15m), Pk(25, "ft", 1.3m)), K("romex", "wire", "cable", "nm-b", "nm", "14-2", "14/2"), material: "Copper",
            safety: "Conductor size determines ampacity. 14 AWG is limited to 15 A circuits."),
        Item("receptacle-15a-tr", Industry.Electrical, "Devices", "15 A Tamper-Resistant Duplex Receptacle, White", "each", 1.95m,
            S("amperage", "15 A", "voltage", "125 V", "type", "Tamper-resistant duplex"), P(Pk(1, "each"), Pk(10, "each", 0.85m)), K("outlet", "outlets", "receptacle", "receptacles", "duplex", "plug")),
        Item("gfci-20a", Industry.Electrical, "Devices", "20 A Self-Test GFCI Receptacle, White", "each", 21.50m,
            S("amperage", "20 A", "voltage", "125 V", "type", "GFCI"), P(Pk(1, "each"), Pk(3, "each", 0.92m)), K("gfci", "gfi", "outlet", "receptacle", "ground", "fault"),
            safety: "Life-safety device. Substitutes must carry the same listing and amperage rating."),
        Item("switch-15a-sp", Industry.Electrical, "Devices", "15 A Single-Pole Toggle Switch, White", "each", 1.40m,
            S("amperage", "15 A", "voltage", "120 V", "poles", "Single-pole"), P(Pk(1, "each"), Pk(10, "each", 0.85m)), K("switch", "switches", "toggle", "light")),
        Item("breaker-vm120", Industry.Electrical, "Panels & Breakers", "Voltmark VM120 20 A Single-Pole Circuit Breaker", "each", 8.90m,
            S("amperage", "20 A", "voltage", "120 V", "poles", "Single-pole", "type", "Plug-in thermal-magnetic"), P(Pk(1, "each")), K("breaker", "breakers", "circuit", "panel"), brand: "Voltmark", model: "VM120",
            safety: "Breakers are listed for specific panels. Only the panel manufacturer's listed breaker may be used."),
        Item("box-1g-18", Industry.Electrical, "Boxes", "1-Gang 18 cu. in. New-Work Electrical Box", "each", 0.98m,
            S("capacity", "18 cu in", "type", "1-gang new work"), P(Pk(1, "each"), Pk(25, "each", 0.86m)), K("box", "boxes", "gang", "electrical", "outlet", "junction"), material: "PVC"),
        Item("led-recessed-6", Industry.Electrical, "Lighting", "6 in. LED Recessed Retrofit Downlight, 3000K", "each", 9.80m,
            S("size", "6 in", "color_temperature", "3000K", "wattage", "12 W"), P(Pk(1, "each"), Pk(6, "each", 0.84m), Pk(12, "each", 0.8m)), K("recessed", "can", "light", "lights", "downlight", "led", "retrofit")),
        Item("wire-nut-yellow", Industry.Electrical, "Consumables", "Yellow Twist-On Wire Connectors", "each", 0.11m,
            S("type", "Twist-on", "gauge", "18-12 AWG"), P(Pk(100, "each"), Pk(500, "each", 0.78m), Pk(25, "each", 1.4m)), K("wire", "nut", "nuts", "connector", "connectors", "twist")),
        Item("emt-12", Industry.Electrical, "Conduit", "1/2 in. EMT Conduit", "ft", 0.89m,
            S("size", "1/2 in", "type", "EMT"), P(Pk(10, "ft")), K("emt", "conduit", "tubing", "metallic"), material: "Galvanized steel"),

        // ---------- HVAC ----------
        Item("filter-16x25x1-m8", Industry.Hvac, "Filters", "16x25x1 MERV 8 Pleated Air Filter", "each", 6.20m,
            S("size", "16x25x1", "rating", "MERV 8"), P(Pk(1, "each"), Pk(6, "each", 0.82m), Pk(12, "each", 0.75m)), K("filter", "filters", "furnace", "air", "pleated", "hvac", "merv")),
        Item("filter-16x25x1-m11", Industry.Hvac, "Filters", "16x25x1 MERV 11 Pleated Air Filter", "each", 9.40m,
            S("size", "16x25x1", "rating", "MERV 11"), P(Pk(1, "each"), Pk(6, "each", 0.82m), Pk(12, "each", 0.75m)), K("filter", "filters", "furnace", "air", "pleated", "hvac", "merv")),
        Item("filter-20x20x1-m8", Industry.Hvac, "Filters", "20x20x1 MERV 8 Pleated Air Filter", "each", 6.60m,
            S("size", "20x20x1", "rating", "MERV 8"), P(Pk(1, "each"), Pk(6, "each", 0.82m), Pk(12, "each", 0.75m)), K("filter", "filters", "furnace", "air", "pleated", "hvac", "merv")),
        Item("thermostat-ct500", Industry.Hvac, "Controls", "Climara CT-500 7-Day Programmable Thermostat", "each", 54.00m,
            S("type", "Programmable thermostat", "voltage", "24 V"), P(Pk(1, "each")), K("thermostat", "thermostats", "programmable", "stat"), brand: "Climara", model: "CT-500"),
        Item("foil-tape", Industry.Hvac, "Consumables", "2.5 in. x 60 yd. Aluminum Foil HVAC Tape", "each", 13.80m,
            S("width", "2.5 in", "length", "60 yd"), P(Pk(1, "each")), K("foil", "tape", "duct", "aluminum", "hvac")),
        Item("flex-duct-6", Industry.Hvac, "Ductwork", "6 in. x 25 ft. Insulated Flexible Duct, R-6", "each", 39.00m,
            S("diameter", "6 in", "length", "25 ft", "rating", "R-6"), P(Pk(1, "each")), K("flex", "duct", "ductwork", "insulated", "flexible")),
        Item("condensate-pump", Industry.Hvac, "Equipment", "Climara CP-22 Condensate Removal Pump, 115 V", "each", 62.00m,
            S("voltage", "115 V", "capacity", "22 ft lift"), P(Pk(1, "each")), K("condensate", "pump", "removal"), brand: "Climara", model: "CP-22"),

        // ---------- Construction ----------
        Item("stud-2x4x8", Industry.Construction, "Lumber", "2 in. x 4 in. x 8 ft. SPF Stud", "each", 3.85m,
            S("size", "2x4", "length", "8 ft", "grade", "Stud"), P(Pk(1, "each")), K("stud", "studs", "2x4", "lumber", "framing"), material: "SPF"),
        Item("drywall-12-4x8", Industry.Construction, "Drywall", "1/2 in. x 4 ft. x 8 ft. Drywall Panel", "each", 13.40m,
            S("thickness", "1/2 in", "size", "4x8"), P(Pk(1, "each")), K("drywall", "sheetrock", "gypsum", "panel", "board", "sheet", "sheets"), material: "Gypsum"),
        Item("osb-716-4x8", Industry.Construction, "Sheathing", "7/16 in. x 4 ft. x 8 ft. OSB Sheathing", "each", 15.90m,
            S("thickness", "7/16 in", "size", "4x8"), P(Pk(1, "each")), K("osb", "sheathing", "panel", "plywood", "sheet"), material: "OSB"),
        Item("drywall-screw-158", Industry.Construction, "Fasteners", "#6 x 1-5/8 in. Coarse-Thread Drywall Screws", "lb", 7.10m,
            S("size", "#6", "length", "1-5/8 in", "thread", "Coarse"), P(Pk(1, "lb"), Pk(5, "lb", 0.82m), Pk(25, "lb", 0.7m)), K("drywall", "screw", "screws", "coarse", "fastener")),
        Item("joint-compound", Industry.Construction, "Drywall", "All-Purpose Pre-Mixed Joint Compound", "gal", 6.30m,
            S("type", "All-purpose pre-mixed"), P(Pk(4.5m, "gal", 0.72m), Pk(1, "gal"), Pk(1, "qt", 1.5m)), K("joint", "compound", "mud", "drywall", "spackle")),
        Item("concrete-mix", Industry.Construction, "Concrete", "High-Strength Concrete Mix", "lb", 0.082m,
            S("type", "High-strength 4000 psi"), P(Pk(80, "lb"), Pk(60, "lb", 1.06m), Pk(50, "lb", 1.1m)), K("concrete", "mix", "cement", "bag", "quikrete", "sakrete")),
        Item("construction-adhesive", Industry.Construction, "Adhesives", "10 oz. Heavy-Duty Construction Adhesive", "each", 5.20m,
            S("capacity", "10 oz", "type", "Heavy-duty"), P(Pk(1, "each"), Pk(12, "each", 0.86m)), K("construction", "adhesive", "glue", "liquid", "nails", "caulk")),

        // ---------- Home renovation ----------
        Item("paint-int-eggshell", Industry.HomeRenovation, "Paint", "Interior Eggshell Paint and Primer, White", "gal", 34.00m,
            S("finish_type", "Eggshell", "type", "Interior latex"), P(Pk(1, "gal"), Pk(5, "gal", 0.88m), Pk(1, "qt", 1.55m)), K("paint", "interior", "eggshell", "wall", "latex")),
        Item("primer-int", Industry.HomeRenovation, "Paint", "Interior Multi-Purpose Latex Primer", "gal", 22.00m,
            S("type", "Interior latex primer"), P(Pk(1, "gal"), Pk(5, "gal", 0.88m)), K("primer", "sealer", "undercoat", "prime")),
        Item("painters-tape", Industry.HomeRenovation, "Paint Supplies", "1.88 in. x 60 yd. Painter's Tape", "each", 6.95m,
            S("width", "1.88 in", "length", "60 yd"), P(Pk(1, "each"), Pk(3, "each", 0.9m), Pk(6, "each", 0.84m)), K("painter", "painters", "tape", "masking", "blue")),
        Item("roller-cover-9", Industry.HomeRenovation, "Paint Supplies", "9 in. x 3/8 in. Nap Roller Cover", "each", 4.60m,
            S("size", "9 in", "thickness", "3/8 in"), P(Pk(1, "each"), Pk(3, "each", 0.86m), Pk(6, "each", 0.8m)), K("roller", "cover", "covers", "nap", "sleeve")),
        Item("lvp-oak", Industry.HomeRenovation, "Flooring", "Luxury Vinyl Plank Flooring, Natural Oak, 6 mm", "sqft", 2.89m,
            S("thickness", "6 mm", "type", "Click-lock LVP"), P(Pk(23.64m, "sqft"), Pk(18.5m, "sqft", 1.04m)), K("lvp", "vinyl", "plank", "flooring", "floor", "luxury")),
        Item("subway-tile-3x6", Industry.HomeRenovation, "Tile", "3 in. x 6 in. White Ceramic Subway Tile", "sqft", 2.10m,
            S("size", "3x6", "type", "Glazed ceramic wall tile"), P(Pk(12.5m, "sqft"), Pk(10, "sqft", 1.05m)), K("subway", "tile", "tiles", "ceramic", "wall", "backsplash"), material: "Ceramic"),
        Item("thinset", Industry.HomeRenovation, "Tile", "Polymer-Modified Thinset Mortar, White", "lb", 0.46m,
            S("type", "Polymer-modified thinset"), P(Pk(50, "lb"), Pk(25, "lb", 1.15m)), K("thinset", "mortar", "tile", "adhesive")),

        // ---------- Landscaping ----------
        Item("mulch-brown", Industry.Landscaping, "Mulch & Soil", "Brown Hardwood Mulch", "cuft", 1.95m,
            S("type", "Dyed hardwood", "finish_type", "Brown"), P(Pk(2, "cuft"), Pk(1, "cuyd", 0.62m), Pk(1.5m, "cuft", 1.05m)), K("mulch", "bark", "hardwood", "brown", "landscape")),
        Item("mulch-black", Industry.Landscaping, "Mulch & Soil", "Black Hardwood Mulch", "cuft", 1.90m,
            S("type", "Dyed hardwood", "finish_type", "Black"), P(Pk(2, "cuft"), Pk(1, "cuyd", 0.62m)), K("mulch", "bark", "hardwood", "black", "landscape")),
        Item("topsoil", Industry.Landscaping, "Mulch & Soil", "Screened Topsoil", "cuft", 3.10m,
            S("type", "Screened topsoil"), P(Pk(0.75m, "cuft"), Pk(1, "cuft", 0.97m), Pk(1, "cuyd", 0.45m)), K("topsoil", "soil", "dirt", "garden", "fill")),
        Item("landscape-fabric", Industry.Landscaping, "Ground Cover", "Woven Weed-Barrier Landscape Fabric", "sqft", 0.14m,
            S("type", "Woven polypropylene", "grade", "Commercial"), P(Pk(300, "sqft"), Pk(200, "sqft", 1.08m), Pk(1500, "sqft", 0.8m)), K("landscape", "fabric", "weed", "barrier", "cloth")),
        Item("paver-12x12", Industry.Landscaping, "Hardscape", "12 in. x 12 in. Concrete Patio Paver, Gray", "each", 2.35m,
            S("size", "12x12", "thickness", "1.5 in"), P(Pk(1, "each")), K("paver", "pavers", "patio", "stone", "stepping", "concrete"), material: "Concrete"),
        Item("drip-tubing-12", Industry.Landscaping, "Irrigation", "1/2 in. Drip Irrigation Tubing", "ft", 0.19m,
            S("size", "1/2 in", "type", "Poly drip tubing"), P(Pk(100, "ft"), Pk(500, "ft", 0.78m)), K("drip", "irrigation", "tubing", "poly", "emitter", "line"), material: "Polyethylene"),
        Item("grass-seed-fescue", Industry.Landscaping, "Lawn", "Tall Fescue Grass Seed Blend", "lb", 4.70m,
            S("type", "Tall fescue blend"), P(Pk(7, "lb"), Pk(20, "lb", 0.86m), Pk(50, "lb", 0.74m)), K("grass", "seed", "fescue", "lawn", "turf")),
        Item("lawn-fertilizer", Industry.Landscaping, "Lawn", "Slow-Release Lawn Fertilizer 24-0-6", "lb", 1.55m,
            S("grade", "24-0-6", "type", "Slow-release granular"), P(Pk(15, "lb"), Pk(45, "lb", 0.85m)), K("fertilizer", "lawn", "food", "nitrogen", "turf")),

        // ---------- Property management ----------
        Item("smoke-alarm-sa10", Industry.PropertyManagement, "Life Safety", "Sentra SA-10 Sealed 10-Year Battery Smoke Alarm", "each", 21.00m,
            S("type", "Photoelectric smoke alarm", "rating", "UL 217", "voltage", "3 V sealed lithium"), P(Pk(1, "each"), Pk(6, "each", 0.88m)), K("smoke", "alarm", "alarms", "detector", "detectors", "fire"), brand: "Sentra", model: "SA-10",
            safety: "Life-safety device. Substitutes must carry the same UL listing and meet local code."),
        Item("smoke-alarm-sa12", Industry.PropertyManagement, "Life Safety", "Sentra SA-12 Sealed 10-Year Battery Smoke Alarm with Voice Alert", "each", 27.00m,
            S("type", "Photoelectric smoke alarm", "rating", "UL 217", "voltage", "3 V sealed lithium"), P(Pk(1, "each"), Pk(6, "each", 0.88m)), K("smoke", "alarm", "alarms", "detector", "voice", "fire"), brand: "Sentra", model: "SA-12",
            safety: "Life-safety device. Substitutes must carry the same UL listing and meet local code."),
        Item("deadbolt-lr-d100", Industry.PropertyManagement, "Door Hardware", "Lockridge D100 Single-Cylinder Deadbolt, Satin Nickel", "each", 24.50m,
            S("type", "Single-cylinder deadbolt", "finish_type", "Satin nickel", "grade", "ANSI Grade 3"), P(Pk(1, "each"), Pk(4, "each", 0.92m)), K("deadbolt", "deadbolts", "lock", "locks", "door", "cylinder"), brand: "Lockridge", model: "D100-SN"),
        Item("led-a19-2700", Industry.PropertyManagement, "Lighting", "60 W Equivalent A19 LED Bulb, 2700K", "each", 1.85m,
            S("wattage", "9 W", "color_temperature", "2700K", "type", "A19 E26"), P(Pk(4, "each"), Pk(8, "each", 0.9m), Pk(16, "each", 0.8m), Pk(1, "each", 1.5m)), K("led", "bulb", "bulbs", "a19", "light", "lamp", "soft", "white")),
        Item("caulk-kitchen-bath", Industry.PropertyManagement, "Maintenance", "10.1 oz. Kitchen and Bath Silicone Caulk, White", "each", 7.80m,
            S("capacity", "10.1 oz", "type", "100% silicone"), P(Pk(1, "each"), Pk(12, "each", 0.86m)), K("caulk", "silicone", "sealant", "kitchen", "bath", "tub")),

        // ---------- General small-business supplies ----------
        Item("nitrile-gloves-l", Industry.General, "Safety Supplies", "Nitrile Disposable Gloves, Large, 5 mil", "each", 0.14m,
            S("size", "Large", "thickness", "5 mil"), P(Pk(100, "each"), Pk(1000, "each", 0.82m)), K("nitrile", "gloves", "glove", "disposable", "exam"), material: "Nitrile"),
        Item("trash-bags-42", Industry.General, "Jobsite Supplies", "42 Gal. 3 mil Contractor Trash Bags", "each", 0.62m,
            S("capacity", "42 gal", "thickness", "3 mil"), P(Pk(20, "each"), Pk(50, "each", 0.86m), Pk(32, "each", 0.93m)), K("contractor", "trash", "bags", "bag", "garbage", "cleanup")),
        Item("copy-paper", Industry.General, "Office Supplies", "8.5 x 11 in. Copy Paper, 20 lb, 500-Sheet Ream", "each", 7.40m,
            S("size", "8.5x11", "grade", "20 lb 92 bright"), P(Pk(1, "each"), Pk(10, "each", 0.78m), Pk(5, "each", 0.86m)), K("copy", "paper", "printer", "ream", "reams", "letter")),
        Item("aa-batteries", Industry.General, "Office Supplies", "AA Alkaline Batteries", "each", 0.72m,
            S("size", "AA", "voltage", "1.5 V", "type", "Alkaline"), P(Pk(24, "each"), Pk(48, "each", 0.86m), Pk(8, "each", 1.25m)), K("aa", "battery", "batteries", "alkaline")),
        Item("shop-towels", Industry.General, "Jobsite Supplies", "Blue Disposable Shop Towels, 55-Sheet Roll", "each", 2.90m,
            S("type", "Disposable shop towel"), P(Pk(1, "each"), Pk(6, "each", 0.88m), Pk(12, "each", 0.82m)), K("shop", "towel", "towels", "rags", "wipes", "paper")),
    };

    public static readonly IReadOnlyList<DemoAlternative> Alternatives = new List<DemoAlternative>
    {
        new("copper-pipe-l-34", "copper-pipe-m-34", false,
            "Type M has a thinner wall than Type L (about 0.032 in vs 0.045 in at 3/4 in.), so its pressure rating is lower.",
            "Same outside diameter, so it uses the same sweat fittings.",
            "Type M is common for residential interior water supply but is not accepted everywhere or for every use (for example underground or some commercial work).",
            "Often meaningfully cheaper per foot where the local plumbing code permits Type M for the application. Confirm with your code official before substituting."),
        new("copper-pipe-l-34", "pex-a-34", false,
            "Different material and joining method: cross-linked polyethylene with expansion fittings instead of soldered copper.",
            "Not a drop-in replacement. Requires PEX fittings, an expansion tool and transition fittings wherever it meets copper.",
            "Flexible and corrosion resistant, but must be protected from sunlight and kept away from heat sources.",
            "Much lower material cost per foot and faster installation for long runs, when the project and code allow PEX."),
        new("faucet-bath-af2100", "faucet-bath-af2000", true,
            "Chrome finish instead of brushed nickel. Same valve cartridge and 4 in. centerset mounting.",
            "Fits the same 4 in. centerset sink drilling and supply connections.",
            "Same manufacturer and series; only the finish differs.",
            "Lower price for the same function if the finish change is acceptable to the customer."),
        new("filter-16x25x1-m8", "filter-16x25x1-m11", true,
            "MERV 11 captures finer particles than MERV 8 and has higher airflow resistance.",
            "Same 16x25x1 nominal size. Check that the air handler tolerates the added static pressure.",
            "Better filtration; may need more frequent replacement.",
            "Use when MERV 8 is out of stock or when better air quality is wanted. This usually costs more, not less."),
        new("filter-16x25x1-m11", "filter-16x25x1-m8", false,
            "MERV 8 filters fewer fine particles than MERV 11.",
            "Same 16x25x1 nominal size.",
            "Lower filtration performance.",
            "Cheaper and less restrictive to airflow, if the lower filtration level meets the site's requirement."),
        new("mulch-brown", "mulch-black", true,
            "Colour only: black dye instead of brown.",
            "Same coverage per cubic foot and same application.",
            "Equivalent material.",
            "Useful when brown is unavailable or black is priced lower, if the colour is acceptable."),
        new("smoke-alarm-sa10", "smoke-alarm-sa12", true,
            "Adds a voice alert. Same photoelectric sensor, sealed 10-year battery and mounting base.",
            "Uses the same mounting bracket.",
            "Same UL 217 listing.",
            "Acceptable when the SA-10 is unavailable. It normally costs more per unit."),
        new("nmb-12-2", "nmb-14-2", false,
            "14 AWG conductors carry 15 A; 12 AWG carry 20 A.",
            "Not interchangeable on a 20 A circuit.",
            "Lower ampacity.",
            "Listed only to make the restriction explicit. A smaller conductor is never an acceptable cost substitute."),
        new("gas-ball-valve-34", "ball-valve-34", false,
            "The water ball valve is not listed for fuel gas.",
            "Not permitted on a gas line.",
            "Different listing and seal materials.",
            "Listed only to make the restriction explicit. A water-rated valve must never replace a gas-rated valve."),
    };

    public static Guid StableGuid(string text) => new(MD5.HashData(Encoding.UTF8.GetBytes(text)));

    public static uint StableHash(string text)
    {
        // FNV-1a: stable across processes and platforms (string.GetHashCode is not).
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= 16777619u;
        }
        return hash;
    }

    public static string DemoGtin(string key)
    {
        var body = "4" + ((ulong)StableHash("gtin:" + key) * 2654435761UL % 10_000_000_000UL).ToString("D10");
        var sum = 0;
        for (var i = 0; i < body.Length; i++) sum += (body[i] - '0') * (i % 2 == 0 ? 3 : 1);
        return body + (10 - sum % 10) % 10;
    }
}
