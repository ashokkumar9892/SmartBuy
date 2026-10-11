using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartBuy.Domain.Entities;
using SmartBuy.Infrastructure.Ai;
using SmartBuy.Infrastructure.Persistence;
using Stripe;
using Stripe.Checkout;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace SmartBuy.Infrastructure.Services;

public sealed record ParsedUpload(string FileType, DraftList Draft, List<string> Warnings);

/// <summary>
/// Server-side parsing of uploaded purchase lists. Files are validated by size, extension and content signature, are
/// never executed or stored, and their text is only ever treated as list data.
/// </summary>
public static class ListFileParser
{
    public const long MaxBytes = 5 * 1024 * 1024;
    public const int MaxRows = 500;
    public const int MaxPdfPages = 30;
    private static readonly string[] AllowedExtensions = { ".csv", ".xlsx", ".pdf" };

    public static ParsedUpload Parse(string fileName, byte[] content)
    {
        var extension = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension)) throw new ValidationException("Upload a .csv, .xlsx or .pdf file.");
        if (content.Length == 0) throw new ValidationException("The file is empty.");
        if (content.Length > MaxBytes) throw new ValidationException("The file is larger than the 5 MB limit.");

        try
        {
            return extension switch
            {
                ".pdf" => ParsePdf(content),
                ".xlsx" => ParseXlsx(content),
                _ => ParseCsv(content),
            };
        }
        catch (ValidationException)
        {
            throw;
        }
        catch (Exception)
        {
            // Parser libraries throw many exception types on malformed input; none of them are useful to the user.
            throw new ValidationException($"The file could not be read as a valid {extension.TrimStart('.').ToUpperInvariant()} document.");
        }
    }

    private static ParsedUpload ParsePdf(byte[] content)
    {
        if (content.Length < 5 || Encoding.ASCII.GetString(content, 0, 5) != "%PDF-") throw new ValidationException("The file is not a PDF document.");
        var lines = new List<string>();
        var warnings = new List<string>();
        using (var document = PdfDocument.Open(content))
        {
            if (document.NumberOfPages > MaxPdfPages) warnings.Add($"Only the first {MaxPdfPages} pages were read.");
            foreach (var page in document.GetPages().Take(MaxPdfPages))
            {
                var text = ContentOrderTextExtractor.GetText(page);
                lines.AddRange(text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
            }
        }
        if (lines.Count == 0) throw new ValidationException("No text was found in the PDF. Scanned images are not supported; upload a text PDF, CSV or Excel file.");
        var draft = HeuristicListParser.ParseDocumentLines(lines.Take(MaxRows * 4));
        if (draft.Items.Count == 0) warnings.Add("No lines with a quantity were recognized in the PDF.");
        warnings.Add("PDF lists are read line by line. Check every quantity and unit against the original document.");
        return new ParsedUpload("pdf", draft, warnings);
    }

    private static ParsedUpload ParseXlsx(byte[] content)
    {
        if (content.Length < 4 || content[0] != 'P' || content[1] != 'K') throw new ValidationException("The file is not an Excel (.xlsx) workbook.");
        using var stream = new MemoryStream(content);
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.FirstOrDefault() ?? throw new ValidationException("The workbook has no worksheets.");
        var rows = new List<string[]>();
        foreach (var row in sheet.RowsUsed().Take(MaxRows + 20))
        {
            var last = row.LastCellUsed()?.Address.ColumnNumber ?? 0;
            // Read displayed values only; formulas are never evaluated or carried forward.
            rows.Add(Enumerable.Range(1, Math.Min(last, 20)).Select(c => row.Cell(c).GetFormattedString().Trim()).ToArray());
        }
        return FromTable(rows, "xlsx");
    }

    private static ParsedUpload ParseCsv(byte[] content)
    {
        if (content.Contains((byte)0)) throw new ValidationException("The file is not a text CSV file.");
        var text = new UTF8Encoding(false, false).GetString(content).TrimStart('﻿');
        return FromTable(ReadCsv(text).Take(MaxRows + 20).ToList(), "csv");
    }

    /// <summary>RFC 4180 reader: quoted fields, doubled quotes, embedded newlines. Detects comma, semicolon or tab.</summary>
    public static IEnumerable<string[]> ReadCsv(string text)
    {
        var firstLine = text.Split('\n')[0];
        var delimiter = new[] { ',', ';', '\t' }.OrderByDescending(d => firstLine.Count(ch => ch == d)).First();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else field.Append(ch);
            }
            else if (ch == '"' && field.Length == 0) quoted = true;
            else if (ch == delimiter) { row.Add(field.ToString().Trim()); field.Clear(); }
            else if (ch is '\n' or '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString().Trim());
                field.Clear();
                if (row.Any(c => c.Length > 0)) yield return row.ToArray();
                row = new List<string>();
            }
            else field.Append(ch);
        }
        row.Add(field.ToString().Trim());
        if (row.Any(c => c.Length > 0)) yield return row.ToArray();
    }

    private static readonly Dictionary<string, string[]> HeaderAliases = new()
    {
        ["description"] = new[] { "description", "item", "item description", "name", "product", "material description", "item name", "part" },
        ["quantity"] = new[] { "quantity", "qty", "qty.", "count", "amount", "order qty" },
        ["unit"] = new[] { "unit", "units", "uom", "u/m", "unit of measure" },
        ["material"] = new[] { "material" },
        ["specifications"] = new[] { "specifications", "specification", "spec", "specs", "size" },
        ["notes"] = new[] { "notes", "note", "comment", "comments", "remarks" },
    };

    private static ParsedUpload FromTable(List<string[]> rows, string type)
    {
        var warnings = new List<string>();
        if (rows.Count == 0) throw new ValidationException("The file has no rows.");

        var map = new Dictionary<string, int>();
        var header = rows[0];
        for (var c = 0; c < header.Length; c++)
        {
            var cell = header[c].Trim().ToLowerInvariant();
            foreach (var (field, aliases) in HeaderAliases)
                if (!map.ContainsKey(field) && aliases.Contains(cell)) map[field] = c;
        }
        var hasHeader = map.ContainsKey("description");
        if (!hasHeader)
        {
            map = new Dictionary<string, int> { ["description"] = 0, ["quantity"] = 1, ["unit"] = 2 };
            warnings.Add("No header row with a Description column was found, so columns were read as Description, Quantity, Unit.");
        }
        else if (!map.ContainsKey("quantity"))
            warnings.Add("No Quantity column was found; every line was given a quantity of 1. Correct the quantities before approving.");

        string? Cell(string[] row, string field) => map.TryGetValue(field, out var idx) && idx < row.Length && row[idx].Length > 0 ? row[idx] : null;

        var items = new List<DraftItem>();
        var data = rows.Skip(hasHeader ? 1 : 0).ToList();
        if (data.Count > MaxRows) warnings.Add($"Only the first {MaxRows} rows were imported.");
        var lineNo = hasHeader ? 1 : 0;
        foreach (var row in data.Take(MaxRows))
        {
            lineNo++;
            var description = Cell(row, "description");
            if (description is null) continue;
            var quantity = 1m;
            var quantityText = Cell(row, "quantity");
            if (quantityText is not null && !decimal.TryParse(quantityText.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out quantity))
            {
                warnings.Add($"Row {lineNo}: quantity \"{DraftSanitizer.Clean(quantityText, 20)}\" is not a number; set to 1.");
                quantity = 1;
            }
            items.Add(new DraftItem(description, quantity, Cell(row, "unit") ?? "each", Cell(row, "material"), Cell(row, "specifications"), Cell(row, "notes")));
        }
        if (items.Count == 0) warnings.Add("No item rows were found in the file.");
        return new ParsedUpload(type, new DraftList(items, new List<string>()), warnings);
    }
}

public sealed class BillingOptions
{
    public string? StripeSecretKey { get; set; }
    public string? StripeWebhookSecret { get; set; }
    /// <summary>Stripe Price IDs keyed by plan code, e.g. { "pro": "price_...", "business": "price_..." }.</summary>
    public Dictionary<string, string> Prices { get; set; } = new();
    /// <summary>Lets an owner switch plans without payment. For local development and demo tenants only.</summary>
    public bool AllowManualPlanChange { get; set; }
}

public sealed class AppOptions
{
    /// <summary>Public origin of the frontend. All billing redirects are built from this, never from client input.</summary>
    public string FrontendUrl { get; set; } = "http://localhost:5173";
}

public sealed record BillingStatus(bool StripeConfigured, bool TestMode, bool ManualPlanChangeAllowed);

public sealed class BillingService(AppDbContext db, ITenantContext tenant, IOptions<BillingOptions> options, IOptions<AppOptions> app, AuditService audit, ILogger<BillingService> logger)
{
    private readonly BillingOptions _options = options.Value;

    public bool StripeConfigured => !string.IsNullOrWhiteSpace(_options.StripeSecretKey);

    public async Task<BillingStatus> StatusAsync(CancellationToken ct)
    {
        var isDemo = await db.Organizations.AsNoTracking().Where(o => o.Id == tenant.OrganizationId).Select(o => o.IsDemo).FirstOrDefaultAsync(ct);
        return new BillingStatus(StripeConfigured, _options.StripeSecretKey?.StartsWith("sk_test_", StringComparison.Ordinal) == true,
            !StripeConfigured && (_options.AllowManualPlanChange || isDemo));
    }

    public async Task<string> CreateCheckoutUrlAsync(string planCode, CancellationToken ct)
    {
        if (!StripeConfigured) throw new BusinessRuleException("Online billing is not configured on this server.", "billing_not_configured");
        var plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Code == planCode && p.IsActive, ct) ?? throw new NotFoundException("Plan not found.");
        var priceId = _options.Prices.TryGetValue(plan.Code, out var configured) ? configured : plan.StripePriceId;
        if (string.IsNullOrWhiteSpace(priceId)) throw new BusinessRuleException($"No Stripe price is configured for the {plan.Name} plan.", "billing_not_configured");

        var subscription = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(ct);
        var baseUrl = app.Value.FrontendUrl.TrimEnd('/');
        var session = await new SessionService(new StripeClient(_options.StripeSecretKey)).CreateAsync(new SessionCreateOptions
        {
            Mode = "subscription",
            ClientReferenceId = tenant.OrganizationId.ToString(),
            Customer = subscription?.StripeCustomerId,
            LineItems = new List<SessionLineItemOptions> { new() { Price = priceId, Quantity = 1 } },
            Metadata = new Dictionary<string, string> { ["organizationId"] = tenant.OrganizationId.ToString()!, ["planCode"] = plan.Code },
            SuccessUrl = $"{baseUrl}/app/billing?checkout=success",
            CancelUrl = $"{baseUrl}/app/billing?checkout=cancelled",
        }, cancellationToken: ct);
        audit.Record("billing.checkout_started", "Plan", plan.Code);
        await db.SaveChangesAsync(ct);
        return session.Url;
    }

    public async Task ChangePlanManuallyAsync(string planCode, CancellationToken ct)
    {
        var status = await StatusAsync(ct);
        if (!status.ManualPlanChangeAllowed)
            throw new BusinessRuleException(StripeConfigured ? "Plans are changed through checkout on this server." : "Plan changes are not enabled on this server.", "manual_plan_change_disabled");
        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Code == planCode && p.IsActive, ct) ?? throw new NotFoundException("Plan not found.");
        var subscription = await db.Subscriptions.FirstOrDefaultAsync(ct);
        if (subscription is null) db.Subscriptions.Add(new Domain.Entities.Subscription { PlanId = plan.Id, Status = SubscriptionStatus.Active });
        else
        {
            subscription.PlanId = plan.Id;
            subscription.Status = SubscriptionStatus.Active;
        }
        audit.Record("billing.plan_changed_manually", "Plan", plan.Code, "No payment was taken.");
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Applies a verified Stripe webhook. Must run in a system scope: the event, not a signed-in user, identifies the organization.
    /// Returns false when the signature is invalid.
    /// </summary>
    public async Task<bool> HandleWebhookAsync(string payload, string? signatureHeader, CancellationToken ct)
    {
        if (!StripeConfigured || string.IsNullOrWhiteSpace(_options.StripeWebhookSecret)) return false;
        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(payload, signatureHeader, _options.StripeWebhookSecret);
        }
        catch (StripeException ex)
        {
            logger.LogWarning("Rejected Stripe webhook: {Message}", ex.Message);
            return false;
        }

        switch (stripeEvent.Data.Object)
        {
            case Session session when stripeEvent.Type == "checkout.session.completed":
            {
                if (!Guid.TryParse(session.ClientReferenceId, out var organizationId)) break;
                var planCode = session.Metadata is not null && session.Metadata.TryGetValue("planCode", out var code) ? code : null;
                var plan = await db.Plans.FirstOrDefaultAsync(p => p.Code == planCode, ct);
                if (plan is null) break;
                var subscription = await db.Subscriptions.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.OrganizationId == organizationId, ct);
                if (subscription is null)
                {
                    subscription = new Domain.Entities.Subscription { OrganizationId = organizationId };
                    db.Subscriptions.Add(subscription);
                }
                subscription.PlanId = plan.Id;
                subscription.Status = SubscriptionStatus.Active;
                subscription.StripeCustomerId = session.CustomerId;
                subscription.StripeSubscriptionId = session.SubscriptionId;
                audit.Record("billing.subscription_activated", "Plan", plan.Code, organizationId: organizationId);
                break;
            }
            case Stripe.Subscription stripeSubscription when stripeEvent.Type is "customer.subscription.updated" or "customer.subscription.deleted":
            {
                var subscription = await db.Subscriptions.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.StripeSubscriptionId == stripeSubscription.Id, ct);
                if (subscription is null) break;
                subscription.Status = stripeEvent.Type == "customer.subscription.deleted" ? SubscriptionStatus.Canceled : stripeSubscription.Status switch
                {
                    "active" => SubscriptionStatus.Active,
                    "trialing" => SubscriptionStatus.Trialing,
                    "past_due" or "unpaid" or "incomplete" => SubscriptionStatus.PastDue,
                    _ => SubscriptionStatus.Canceled,
                };
                audit.Record("billing.subscription_updated", nameof(Domain.Entities.Subscription), subscription.Id, subscription.Status.ToString(), organizationId: subscription.OrganizationId);
                break;
            }
        }
        await db.SaveChangesAsync(ct);
        return true;
    }
}
