using System.Globalization;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;
using SmartBuy.Domain.Entities;
using SmartBuy.Domain.Pricing;
using SmartBuy.Domain.Rules;
using SmartBuy.Infrastructure.Persistence;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace SmartBuy.Infrastructure.Services;

public sealed record PoItemDto(Guid Id, int LineNumber, string Description, string? SupplierSku, decimal Quantity, decimal PackQuantity, string Unit, decimal UnitPrice, decimal LineTotal, decimal QuantityReceived);

public sealed record PoApprovalDto(Guid Id, string Status, string RequestedBy, string? DecidedBy, DateTime RequestedAt, DateTime? DecidedAt, string? Comment);

public sealed record PoDto(
    Guid Id,
    string PoNumber,
    string Status,
    Guid SupplierId,
    string SupplierName,
    Guid? ProjectId,
    string? ProjectName,
    DateTime? RequestedDeliveryDate,
    string? ShipTo,
    string? InternalNotes,
    decimal Subtotal,
    decimal? ShippingEstimate,
    decimal? TaxEstimate,
    decimal Total,
    bool TotalExcludesUnknowns,
    string CreatedBy,
    DateTime CreatedAt,
    DateTime? ApprovedAt,
    string? ApprovedBy,
    DateTime? SentAt,
    string? SentVia,
    string? SentTo,
    Guid Version,
    IReadOnlyList<PoItemDto> Items,
    IReadOnlyList<PoApprovalDto> Approvals,
    IReadOnlyList<string> AllowedActions);

public sealed record PoItemInput(string Description, string? SupplierSku, decimal Quantity, decimal PackQuantity, string Unit, decimal UnitPrice);

public sealed record PoInput(Guid SupplierId, Guid? ProjectId, DateTime? RequestedDeliveryDate, string? ShipTo, string? InternalNotes, decimal? ShippingEstimate, decimal? TaxEstimate, List<PoItemInput> Items, Guid? Version = null);

public sealed record ReceiveLine(Guid ItemId, decimal QuantityReceived);

public sealed class SmtpOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? FromAddress { get; set; }
    public string FromName { get; set; } = "SmartBuy AI";
    public bool UseStartTls { get; set; } = true;
}

public sealed record DeliveryReceipt(string Channel, string Recipient, string Confirmation);

/// <summary>A way to hand a purchase order to a supplier. A PO is only marked Sent after one of these returns a receipt.</summary>
public interface IPurchaseOrderDeliveryChannel
{
    string Name { get; }
    bool IsConfigured { get; }
    Task<DeliveryReceipt> SendAsync(PoDto po, string organizationName, string recipient, byte[] pdf, CancellationToken ct);
}

public sealed class SmtpPurchaseOrderChannel(IOptions<SmtpOptions> options) : IPurchaseOrderDeliveryChannel
{
    private readonly SmtpOptions _options = options.Value;
    public string Name => "Email (SMTP)";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.Host) && !string.IsNullOrWhiteSpace(_options.FromAddress);

    public async Task<DeliveryReceipt> SendAsync(PoDto po, string organizationName, string recipient, byte[] pdf, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress!));
        message.To.Add(MailboxAddress.Parse(recipient));
        message.Subject = $"Purchase order {po.PoNumber} from {organizationName}";
        var body = new BodyBuilder
        {
            TextBody = $"Please find attached purchase order {po.PoNumber} from {organizationName}.\n\n" +
                       $"Lines: {po.Items.Count}\nSubtotal: {po.Subtotal.ToString("C", CultureInfo.GetCultureInfo("en-US"))}\n" +
                       (po.RequestedDeliveryDate is { } d ? $"Requested delivery: {d:yyyy-MM-dd}\n" : "") +
                       "\nPlease reply to confirm price and availability.\n",
        };
        body.Attachments.Add($"{po.PoNumber}.pdf", pdf, new ContentType("application", "pdf"));
        message.Body = body.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(_options.Host!, _options.Port, _options.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto, ct);
        if (!string.IsNullOrEmpty(_options.Username))
            await client.AuthenticateAsync(_options.Username, _options.Password ?? "", ct);
        var response = await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
        return new DeliveryReceipt(Name, recipient, $"Accepted by mail server {_options.Host}: {response}");
    }
}

public static class PurchaseOrderExport
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    /// <summary>Neutralizes spreadsheet formula injection and quotes per RFC 4180.</summary>
    public static string CsvCell(string? value)
    {
        var v = value ?? "";
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
        return v.Contains('"') || v.Contains(',') || v.Contains('\n') || v.Contains('\r') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    private static string Num(decimal d) => d.ToString("0.####", CultureInfo.InvariantCulture);

    public static byte[] ToCsv(PoDto po)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PO Number,Status,Supplier,Project,Requested Delivery,Line,Description,Supplier SKU,Packages,Pack Size,Unit,Price per Package,Line Total");
        foreach (var i in po.Items)
        {
            sb.AppendLine(string.Join(',',
                CsvCell(po.PoNumber), CsvCell(po.Status), CsvCell(po.SupplierName), CsvCell(po.ProjectName),
                po.RequestedDeliveryDate?.ToString("yyyy-MM-dd") ?? "", i.LineNumber.ToString(CultureInfo.InvariantCulture), CsvCell(i.Description), CsvCell(i.SupplierSku),
                Num(i.Quantity), Num(i.PackQuantity), CsvCell(i.Unit), Num(i.UnitPrice), Num(i.LineTotal)));
        }
        sb.AppendLine();
        sb.AppendLine($",,,,,,,,,,,Subtotal,{Num(po.Subtotal)}");
        sb.AppendLine($",,,,,,,,,,,Shipping (estimate),{(po.ShippingEstimate is { } s ? Num(s) : "unknown")}");
        sb.AppendLine($",,,,,,,,,,,Tax (estimate),{(po.TaxEstimate is { } t ? Num(t) : "unknown")}");
        sb.AppendLine($",,,,,,,,,,,{(po.TotalExcludesUnknowns ? "Total (excludes unknown amounts)" : "Total")},{Num(po.Total)}");
        return new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    // The standard PDF fonts only cover basic Latin, so anything else is replaced rather than risking a failed export.
    private static string Ascii(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder();
        foreach (var ch in text)
            sb.Append(ch is >= ' ' and <= '~' ? ch : ch is '\n' or '\r' or '\t' ? ' ' : '?');
        var s = sb.ToString();
        return s.Length > max ? s[..(max - 3)] + "..." : s;
    }

    public static byte[] ToPdf(PoDto po, string organizationName, bool containsDemoData)
    {
        var builder = new PdfDocumentBuilder();
        var regular = builder.AddStandard14Font(Standard14Font.Helvetica);
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);
        const double left = 50, right = 562, top = 742, bottom = 60;
        var page = builder.AddPage(PageSize.Letter);
        var y = top;

        void Text(string text, double x, double size = 10, bool isBold = false) =>
            page.AddText(Ascii(text, 200), size, new PdfPoint(x, y), isBold ? bold : regular);
        void RightText(string text, double xRight, double size = 10, bool isBold = false)
        {
            var clean = Ascii(text, 60);
            // Helvetica averages roughly half the font size per character; good enough to right-align numbers.
            page.AddText(clean, size, new PdfPoint(xRight - clean.Length * size * 0.5, y), isBold ? bold : regular);
        }
        void Rule()
        {
            page.DrawLine(new PdfPoint(left, y), new PdfPoint(right, y), 0.5);
        }
        void TableHeader()
        {
            Text("#", left, 9, true); Text("Description", left + 22, 9, true); Text("SKU", left + 262, 9, true);
            RightText("Pkgs", left + 380, 9, true); RightText("Price/Pkg", left + 446, 9, true); RightText("Line Total", right, 9, true);
            y -= 6; Rule(); y -= 14;
        }

        Text("PURCHASE ORDER", left, 20, true);
        RightText(po.PoNumber, right, 14, true);
        y -= 22;
        Text(organizationName, left, 12, true);
        RightText($"Status: {po.Status}", right);
        y -= 16;
        Text($"Created {po.CreatedAt:yyyy-MM-dd} by {po.CreatedBy}", left);
        y -= 22;
        Text("Supplier", left, 9, true); Text("Ship to", left + 262, 9, true);
        y -= 13;
        Text(po.SupplierName, left); Text(Ascii(po.ShipTo, 55).Length == 0 ? "Not specified" : po.ShipTo!, left + 262);
        y -= 13;
        if (po.ProjectName is not null) Text($"Project: {po.ProjectName}", left);
        Text($"Requested delivery: {(po.RequestedDeliveryDate is { } d ? d.ToString("yyyy-MM-dd") : "Not specified")}", left + 262);
        y -= 24;
        TableHeader();

        foreach (var item in po.Items)
        {
            if (y < bottom + 90)
            {
                page = builder.AddPage(PageSize.Letter);
                y = top;
                TableHeader();
            }
            Text(item.LineNumber.ToString(CultureInfo.InvariantCulture), left, 9);
            Text(Ascii(item.Description, 46), left + 22, 9);
            Text(Ascii(item.SupplierSku, 18), left + 262, 9);
            RightText(Num(item.Quantity), left + 380, 9);
            RightText(item.UnitPrice.ToString("C", Us), left + 446, 9);
            RightText(item.LineTotal.ToString("C", Us), right, 9);
            y -= 11;
            Text($"{Num(item.PackQuantity)} {item.Unit} per package", left + 22, 7.5);
            y -= 15;
        }

        y -= 2; Rule(); y -= 18;
        void TotalRow(string label, string value, bool strong = false)
        {
            RightText(label, left + 446, 10, strong); RightText(value, right, 10, strong); y -= 15;
        }
        TotalRow("Subtotal", po.Subtotal.ToString("C", Us));
        TotalRow("Shipping (estimate)", po.ShippingEstimate?.ToString("C", Us) ?? "Unknown");
        TotalRow("Tax (estimate)", po.TaxEstimate?.ToString("C", Us) ?? "Unknown");
        TotalRow(po.TotalExcludesUnknowns ? "Total (excl. unknown)" : "Total", po.Total.ToString("C", Us), true);

        y = bottom + 24;
        if (containsDemoData)
        {
            Text("DEMO DATA: prices on this document come from simulated supplier data and are not supplier quotes.", left, 8, true);
            y -= 11;
        }
        Text("Shipping and tax are estimates. This document is a request to purchase; it does not confirm that an order was placed.", left, 8);
        y -= 11;
        Text("Generated by SmartBuy AI", left, 8);
        return builder.Build();
    }
}

public sealed class PurchaseOrderService(
    AppDbContext db,
    ITenantContext tenant,
    EntitlementService entitlements,
    AuditService audit,
    IEnumerable<IPurchaseOrderDeliveryChannel> channels,
    TimeProvider time)
{
    public bool DeliveryConfigured => channels.Any(c => c.IsConfigured);

    private IQueryable<PurchaseOrder> Query() => db.PurchaseOrders
        .Include(p => p.Items).Include(p => p.Approvals).Include(p => p.Supplier).Include(p => p.Project).AsSplitQuery();

    public async Task<List<PoDto>> ListAsync(Guid? projectId, PurchaseOrderStatus? status, OrgRole role, CancellationToken ct)
    {
        var query = Query().AsNoTracking();
        if (projectId is not null) query = query.Where(p => p.ProjectId == projectId);
        if (status is not null) query = query.Where(p => p.Status == status);
        var list = await query.OrderByDescending(p => p.CreatedAt).Take(500).ToListAsync(ct);
        var names = await UserNamesAsync(list, ct);
        var approvals = await entitlements.HasAsync(PlanFeature.ApprovalWorkflows, ct);
        return list.Select(p => ToDto(p, names, role, approvals)).ToList();
    }

    public async Task<PoDto> GetAsync(Guid id, OrgRole role, CancellationToken ct)
    {
        var po = await Query().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Purchase order not found.");
        return ToDto(po, await UserNamesAsync(new[] { po }, ct), role, await entitlements.HasAsync(PlanFeature.ApprovalWorkflows, ct));
    }

    public async Task<PoDto> CreateAsync(PoInput input, OrgRole role, CancellationToken ct)
    {
        await entitlements.RequireAsync(PlanFeature.PurchaseOrders, ct);
        ValidateInput(input);
        if (!await db.Suppliers.AnyAsync(s => s.Id == input.SupplierId, ct)) throw new ValidationException("Unknown supplier.");
        if (input.ProjectId is { } projectId && !await db.PurchasingProjects.AnyAsync(p => p.Id == projectId, ct)) throw new ValidationException("Unknown project.");

        var po = new PurchaseOrder
        {
            SupplierId = input.SupplierId,
            ProjectId = input.ProjectId,
            Status = PurchaseOrderStatus.Draft,
            CreatedByUserId = tenant.UserId ?? Guid.Empty,
        };
        Apply(po, input);
        await SaveNewAsync(po, ct);
        audit.Record("purchase_order.created", nameof(PurchaseOrder), po.Id, po.PoNumber);
        await db.SaveChangesAsync(ct);
        return await GetAsync(po.Id, role, ct);
    }

    /// <summary>Creates one draft PO per supplier from the project cart and removes those lines from the cart.</summary>
    public async Task<List<PoDto>> CreateFromCartAsync(Guid projectId, Guid? supplierId, DateTime? requestedDeliveryDate, OrgRole role, CancellationToken ct)
    {
        await entitlements.RequireAsync(PlanFeature.PurchaseOrders, ct);
        var project = await db.PurchasingProjects.FirstOrDefaultAsync(p => p.Id == projectId, ct) ?? throw new NotFoundException("Project not found.");
        var lines = await db.CartItems
            .Where(c => c.ProjectId == projectId && (supplierId == null || c.SupplierId == supplierId))
            .Include(c => c.Supplier).Include(c => c.SupplierProduct!).ThenInclude(sp => sp.Offers.Where(o => o.IsCurrent))
            .AsSplitQuery().ToListAsync(ct);
        if (lines.Count == 0) throw new BusinessRuleException("The cart has no lines for that supplier.");

        var organization = await db.Organizations.AsNoTracking().FirstAsync(o => o.Id == tenant.OrganizationId, ct);
        var itemIds = lines.Where(l => l.ShoppingListItemId is not null).Select(l => l.ShoppingListItemId!.Value).ToList();
        var listItems = await db.ShoppingListItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);

        var created = new List<Guid>();
        foreach (var group in lines.GroupBy(l => l.SupplierId))
        {
            var supplier = group.First().Supplier!;
            var po = new PurchaseOrder
            {
                SupplierId = supplier.Id,
                ProjectId = projectId,
                Status = PurchaseOrderStatus.Draft,
                RequestedDeliveryDate = requestedDeliveryDate ?? project.RequiredByDate,
                ShipTo = organization.ShippingAddress,
                CreatedByUserId = tenant.UserId ?? Guid.Empty,
            };
            var n = 1;
            foreach (var line in group.OrderBy(l => l.CreatedAt))
            {
                var price = line.SupplierProduct!.Offers.FirstOrDefault(o => o.IsCurrent)?.Price ?? line.PackPriceSnapshot;
                po.Items.Add(new PurchaseOrderItem
                {
                    LineNumber = n++,
                    Description = line.SupplierProduct.Title,
                    SupplierSku = line.SupplierProduct.ExternalId,
                    CanonicalProductId = line.ShoppingListItemId is { } id && listItems.TryGetValue(id, out var li) ? li.CanonicalProductId : null,
                    Quantity = line.Packs,
                    PackQuantity = line.SupplierProduct.PackQuantity,
                    Unit = line.SupplierProduct.PackUnit,
                    UnitPrice = price,
                });
            }
            PurchaseOrderRules.Recalculate(po);
            var terms = new SupplierTerms(supplier.Id, supplier.Name, supplier.ShippingFlatFee, supplier.FreeShippingThreshold, supplier.MinimumOrderAmount);
            po.ShippingEstimate = OrderCalculator.Shipping(terms, po.Subtotal);
            po.TaxEstimate = OrderCalculator.Tax(po.Subtotal, organization.EstimatedTaxRatePercent);
            if (group.Any(l => l.DataSource == OfferDataSource.Demo))
                po.InternalNotes = "Prices on this PO came from DEMO supplier data. Confirm pricing with the supplier before sending.";
            PurchaseOrderRules.Recalculate(po);
            await SaveNewAsync(po, ct);
            db.CartItems.RemoveRange(group);
            audit.Record("purchase_order.created_from_cart", nameof(PurchaseOrder), po.Id, po.PoNumber);
            created.Add(po.Id);
        }
        if (project.Status == ProjectStatus.Draft) project.Status = ProjectStatus.Active;
        await db.SaveChangesAsync(ct);

        var result = new List<PoDto>();
        foreach (var id in created) result.Add(await GetAsync(id, role, ct));
        return result;
    }

    public async Task<PoDto> UpdateAsync(Guid id, PoInput input, OrgRole role, CancellationToken ct)
    {
        ValidateInput(input);
        var po = await Query().FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Purchase order not found.");
        if (!PurchaseOrderRules.IsEditable(po.Status)) throw new BusinessRuleException($"A purchase order in status {Label(po.Status)} cannot be edited.");
        if (input.Version is { } version) db.Entry(po).Property(p => p.Version).OriginalValue = version;
        if (po.SupplierId != input.SupplierId)
        {
            if (!await db.Suppliers.AnyAsync(s => s.Id == input.SupplierId, ct)) throw new ValidationException("Unknown supplier.");
            po.SupplierId = input.SupplierId;
        }
        db.PurchaseOrderItems.RemoveRange(po.Items);
        po.Items.Clear();
        Apply(po, input);
        audit.Record("purchase_order.updated", nameof(PurchaseOrder), po.Id, po.PoNumber);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, role, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var po = await db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Purchase order not found.");
        if (po.Status is not (PurchaseOrderStatus.Draft or PurchaseOrderStatus.Cancelled))
            throw new BusinessRuleException("Only draft or cancelled purchase orders can be deleted.");
        db.PurchaseOrders.Remove(po);
        audit.Record("purchase_order.deleted", nameof(PurchaseOrder), po.Id, po.PoNumber);
        await db.SaveChangesAsync(ct);
    }

    public async Task<PoDto> SubmitAsync(Guid id, OrgRole role, CancellationToken ct)
    {
        await entitlements.RequireAsync(PlanFeature.ApprovalWorkflows, ct);
        var po = await LoadForTransitionAsync(id, PurchaseOrderStatus.PendingApproval, ct);
        if (po.Items.Count == 0) throw new BusinessRuleException("Add at least one line before submitting.");
        po.Status = PurchaseOrderStatus.PendingApproval;
        po.Approvals.Add(new ApprovalRequest { RequestedByUserId = tenant.UserId ?? Guid.Empty, Status = ApprovalStatus.Pending });
        audit.Record("purchase_order.submitted", nameof(PurchaseOrder), po.Id, po.PoNumber);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, role, ct);
    }

    public async Task<PoDto> ApproveAsync(Guid id, string? comment, OrgRole role, CancellationToken ct)
    {
        if (!RolePermissions.CanApprovePurchaseOrders(role)) throw new ForbiddenException("Your role cannot approve purchase orders.");
        var po = await LoadForTransitionAsync(id, PurchaseOrderStatus.Approved, ct);
        if (po.Items.Count == 0) throw new BusinessRuleException("Add at least one line before approving.");
        var now = time.GetUtcNow().UtcDateTime;
        var pending = po.Approvals.FirstOrDefault(a => a.Status == ApprovalStatus.Pending);
        if (pending is not null)
        {
            pending.Status = ApprovalStatus.Approved;
            pending.DecidedByUserId = tenant.UserId;
            pending.DecidedAt = now;
            pending.Comment = Clean(comment);
        }
        po.Status = PurchaseOrderStatus.Approved;
        po.ApprovedByUserId = tenant.UserId;
        po.ApprovedAt = now;
        audit.Record("purchase_order.approved", nameof(PurchaseOrder), po.Id, po.PoNumber);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, role, ct);
    }

    public async Task<PoDto> RejectAsync(Guid id, string? comment, OrgRole role, CancellationToken ct)
    {
        if (!RolePermissions.CanApprovePurchaseOrders(role)) throw new ForbiddenException("Your role cannot reject purchase orders.");
        var po = await Query().FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Purchase order not found.");
        if (po.Status != PurchaseOrderStatus.PendingApproval) throw new BusinessRuleException("Only purchase orders pending approval can be rejected.");
        var pending = po.Approvals.FirstOrDefault(a => a.Status == ApprovalStatus.Pending);
        if (pending is not null)
        {
            pending.Status = ApprovalStatus.Rejected;
            pending.DecidedByUserId = tenant.UserId;
            pending.DecidedAt = time.GetUtcNow().UtcDateTime;
            pending.Comment = Clean(comment);
        }
        po.Status = PurchaseOrderStatus.Draft;
        audit.Record("purchase_order.rejected", nameof(PurchaseOrder), po.Id, po.PoNumber);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, role, ct);
    }

    /// <summary>
    /// Sends the PO through a configured channel. The status only changes after the channel returns a receipt;
    /// any failure leaves the PO Approved and surfaces the error.
    /// </summary>
    public async Task<PoDto> SendAsync(Guid id, string? recipientOverride, OrgRole role, CancellationToken ct)
    {
        var po = await LoadForTransitionAsync(id, PurchaseOrderStatus.Sent, ct);
        var channel = channels.FirstOrDefault(c => c.IsConfigured)
                      ?? throw new BusinessRuleException("No delivery channel is configured, so this purchase order cannot be sent from SmartBuy. Configure SMTP on the server, or export the PDF and record it as sent once you have delivered it yourself.", "delivery_not_configured");
        var saved = await db.SavedSuppliers.AsNoTracking().FirstOrDefaultAsync(s => s.SupplierId == po.SupplierId, ct);
        var recipient = string.IsNullOrWhiteSpace(recipientOverride) ? saved?.OrderEmail : recipientOverride.Trim();
        if (string.IsNullOrWhiteSpace(recipient) || !MailboxAddress.TryParse(recipient, out _))
            throw new BusinessRuleException("No valid order email address is saved for this supplier. Add one under Suppliers or enter a recipient.", "recipient_missing");

        var organization = await db.Organizations.AsNoTracking().FirstAsync(o => o.Id == tenant.OrganizationId, ct);
        var dto = await GetAsync(id, role, ct);
        DeliveryReceipt receipt;
        try
        {
            receipt = await channel.SendAsync(dto, organization.Name, recipient, PurchaseOrderExport.ToPdf(dto, organization.Name, organization.IsDemo), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            audit.Record("purchase_order.send_failed", nameof(PurchaseOrder), po.Id, ex.Message);
            await db.SaveChangesAsync(ct);
            throw new BusinessRuleException($"The purchase order was not sent: {ex.Message}", "delivery_failed");
        }
        po.Status = PurchaseOrderStatus.Sent;
        po.SentAt = time.GetUtcNow().UtcDateTime;
        po.SentVia = receipt.Channel;
        po.SentTo = receipt.Recipient;
        audit.Record("purchase_order.sent", nameof(PurchaseOrder), po.Id, receipt.Confirmation);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, role, ct);
    }

    /// <summary>For orders the user delivered themselves (phone, counter, their own email). Recorded as such, never as a SmartBuy send.</summary>
    public async Task<PoDto> RecordSentManuallyAsync(Guid id, string? note, OrgRole role, CancellationToken ct)
    {
        var po = await LoadForTransitionAsync(id, PurchaseOrderStatus.Sent, ct);
        po.Status = PurchaseOrderStatus.Sent;
        po.SentAt = time.GetUtcNow().UtcDateTime;
        po.SentVia = "Recorded manually (delivered outside SmartBuy)";
        po.SentTo = Clean(note);
        audit.Record("purchase_order.recorded_sent", nameof(PurchaseOrder), po.Id, po.SentTo);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, role, ct);
    }

    public async Task<PoDto> ReceiveAsync(Guid id, List<ReceiveLine> lines, OrgRole role, CancellationToken ct)
    {
        var po = await Query().FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Purchase order not found.");
        if (po.Status is not (PurchaseOrderStatus.Sent or PurchaseOrderStatus.PartiallyReceived))
            throw new BusinessRuleException("Only sent purchase orders can be received.");
        foreach (var line in lines)
        {
            var item = po.Items.FirstOrDefault(i => i.Id == line.ItemId) ?? throw new ValidationException("A received line does not belong to this purchase order.");
            if (line.QuantityReceived < 0 || line.QuantityReceived > item.Quantity * 10) throw new ValidationException("Received quantity is out of range.");
            item.QuantityReceived = line.QuantityReceived;
        }
        var next = PurchaseOrderRules.ReceiptStatus(po);
        if (next != po.Status && !PurchaseOrderRules.CanTransition(po.Status, next))
            throw new BusinessRuleException("Received quantities cannot be reduced to zero once receiving has started.");
        po.Status = next;
        audit.Record("purchase_order.received", nameof(PurchaseOrder), po.Id, Label(next));
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, role, ct);
    }

    public async Task<PoDto> CancelAsync(Guid id, OrgRole role, CancellationToken ct)
    {
        var po = await LoadForTransitionAsync(id, PurchaseOrderStatus.Cancelled, ct);
        foreach (var pending in po.Approvals.Where(a => a.Status == ApprovalStatus.Pending))
        {
            pending.Status = ApprovalStatus.Rejected;
            pending.DecidedByUserId = tenant.UserId;
            pending.DecidedAt = time.GetUtcNow().UtcDateTime;
            pending.Comment = "Purchase order cancelled.";
        }
        po.Status = PurchaseOrderStatus.Cancelled;
        audit.Record("purchase_order.cancelled", nameof(PurchaseOrder), po.Id, po.PoNumber);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, role, ct);
    }

    private async Task<PurchaseOrder> LoadForTransitionAsync(Guid id, PurchaseOrderStatus target, CancellationToken ct)
    {
        var po = await Query().FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Purchase order not found.");
        if (!PurchaseOrderRules.CanTransition(po.Status, target))
            throw new BusinessRuleException($"A purchase order in status {Label(po.Status)} cannot move to {Label(target)}.");
        return po;
    }

    private async Task SaveNewAsync(PurchaseOrder po, CancellationToken ct)
    {
        var year = time.GetUtcNow().UtcDateTime.Year;
        var prefix = $"PO-{year}-";
        // Deleted POs keep their numbers so a number is never reused.
        var count = await db.PurchaseOrders.IgnoreQueryFilters().CountAsync(p => p.OrganizationId == tenant.OrganizationId && p.PoNumber.StartsWith(prefix), ct);
        db.PurchaseOrders.Add(po);
        for (var attempt = 1; ; attempt++)
        {
            po.PoNumber = PurchaseOrderRules.FormatNumber(year, count + attempt);
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException) when (attempt < 5)
            {
                // Another request took this number concurrently; the unique index rejected ours, so try the next.
            }
        }
    }

    private static void ValidateInput(PoInput input)
    {
        if (input.Items is null) throw new ValidationException("Items are required.");
        if (input.Items.Count > 500) throw new ValidationException("A purchase order can have at most 500 lines.");
        if (input.ShippingEstimate is < 0 || input.TaxEstimate is < 0) throw new ValidationException("Estimates cannot be negative.");
        foreach (var item in input.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Description) || item.Description.Length > 500) throw new ValidationException("Each line needs a description of at most 500 characters.");
            if (item.Quantity <= 0 || item.Quantity > 1_000_000) throw new ValidationException("Line quantity must be greater than zero.");
            if (item.UnitPrice < 0 || item.UnitPrice > 10_000_000) throw new ValidationException("Line price is out of range.");
            if (item.PackQuantity <= 0) throw new ValidationException("Pack size must be greater than zero.");
        }
    }

    private static void Apply(PurchaseOrder po, PoInput input)
    {
        po.ProjectId = input.ProjectId;
        po.RequestedDeliveryDate = input.RequestedDeliveryDate;
        po.ShipTo = Clean(input.ShipTo);
        po.InternalNotes = Clean(input.InternalNotes);
        po.ShippingEstimate = input.ShippingEstimate;
        po.TaxEstimate = input.TaxEstimate;
        var n = 1;
        foreach (var item in input.Items)
        {
            po.Items.Add(new PurchaseOrderItem
            {
                LineNumber = n++,
                Description = item.Description.Trim(),
                SupplierSku = Clean(item.SupplierSku),
                Quantity = item.Quantity,
                PackQuantity = item.PackQuantity,
                Unit = string.IsNullOrWhiteSpace(item.Unit) ? "each" : item.Unit.Trim(),
                UnitPrice = item.UnitPrice,
            });
        }
        PurchaseOrderRules.Recalculate(po);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length > 2000 ? value.Trim()[..2000] : value.Trim();

    public static string Label(PurchaseOrderStatus status) => status switch
    {
        PurchaseOrderStatus.PendingApproval => "Pending Approval",
        PurchaseOrderStatus.PartiallyReceived => "Partially Received",
        _ => status.ToString()
    };

    private async Task<Dictionary<Guid, string>> UserNamesAsync(IEnumerable<PurchaseOrder> orders, CancellationToken ct)
    {
        var ids = orders.SelectMany(p => new Guid?[] { p.CreatedByUserId, p.ApprovedByUserId }
                .Concat(p.Approvals.SelectMany(a => new Guid?[] { a.RequestedByUserId, a.DecidedByUserId })))
            .Where(g => g is not null).Select(g => g!.Value).Distinct().ToList();
        // Names are limited to members of the active organization.
        return await db.OrganizationMembers.AsNoTracking().Where(m => ids.Contains(m.UserId)).Select(m => new { m.UserId, m.User!.FullName }).ToDictionaryAsync(x => x.UserId, x => x.FullName, ct);
    }

    private PoDto ToDto(PurchaseOrder p, Dictionary<Guid, string> names, OrgRole role, bool approvalWorkflows)
    {
        string Name(Guid? id) => id is { } g && names.TryGetValue(g, out var n) ? n : "Former member";
        var actions = new List<string>();
        var canManage = RolePermissions.CanManagePurchasing(role);
        var canEdit = RolePermissions.CanEditLists(role);
        switch (p.Status)
        {
            case PurchaseOrderStatus.Draft:
                if (canEdit) { actions.Add("edit"); actions.Add("delete"); }
                if (canEdit && approvalWorkflows) actions.Add("submit");
                if (canManage) actions.Add("approve");
                if (canManage) actions.Add("cancel");
                break;
            case PurchaseOrderStatus.PendingApproval:
                if (canManage) { actions.Add("approve"); actions.Add("reject"); actions.Add("cancel"); }
                break;
            case PurchaseOrderStatus.Approved:
                if (canManage) { if (DeliveryConfigured) actions.Add("send"); actions.Add("record_sent"); actions.Add("cancel"); }
                break;
            case PurchaseOrderStatus.Sent:
                if (canEdit) actions.Add("receive");
                if (canManage) actions.Add("cancel");
                break;
            case PurchaseOrderStatus.PartiallyReceived:
                if (canEdit) actions.Add("receive");
                break;
            case PurchaseOrderStatus.Cancelled:
                if (canManage) actions.Add("delete");
                break;
        }
        return new PoDto(p.Id, p.PoNumber, Label(p.Status), p.SupplierId, p.Supplier?.Name ?? "", p.ProjectId, p.Project?.Name,
            p.RequestedDeliveryDate, p.ShipTo, p.InternalNotes, p.Subtotal, p.ShippingEstimate, p.TaxEstimate, p.Total,
            p.ShippingEstimate is null || p.TaxEstimate is null, Name(p.CreatedByUserId), p.CreatedAt, p.ApprovedAt,
            p.ApprovedByUserId is null ? null : Name(p.ApprovedByUserId), p.SentAt, p.SentVia, p.SentTo, p.Version,
            p.Items.OrderBy(i => i.LineNumber).Select(i => new PoItemDto(i.Id, i.LineNumber, i.Description, i.SupplierSku, i.Quantity, i.PackQuantity, i.Unit, i.UnitPrice, i.LineTotal, i.QuantityReceived)).ToList(),
            p.Approvals.OrderBy(a => a.CreatedAt).Select(a => new PoApprovalDto(a.Id, a.Status.ToString(), Name(a.RequestedByUserId), a.DecidedByUserId is null ? null : Name(a.DecidedByUserId), a.CreatedAt, a.DecidedAt, a.Comment)).ToList(),
            actions);
    }
}
