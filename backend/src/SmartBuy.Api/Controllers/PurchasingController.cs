using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SmartBuy.Api.Infrastructure;
using SmartBuy.Domain.Entities;
using SmartBuy.Domain.Optimization;
using SmartBuy.Infrastructure.Persistence;
using SmartBuy.Infrastructure.Services;

namespace SmartBuy.Api.Controllers;

public sealed record ExplainRequest([Required] string Strategy);
public sealed record ApplyPlanRequest(PurchasingStrategy Strategy);
public sealed record AddCartItemRequest(Guid OfferId, [Range(1, 100_000)] int? Packs, Guid? ShoppingListItemId);
public sealed record UpdateCartItemRequest([Range(1, 100_000)] int Packs);
public sealed record ApproveSubstitutionRequest(Guid AlternativeProductId, bool Acknowledged);
public sealed record CreateFromCartRequest(Guid? SupplierId, DateTime? RequestedDeliveryDate);
public sealed record DecisionRequest([MaxLength(1000)] string? Comment);
public sealed record SendRequest([EmailAddress, MaxLength(320)] string? Recipient);
public sealed record RecordSentRequest([MaxLength(500)] string? Note);
public sealed record ReceiveRequest([Required] List<ReceiveLine> Lines);
public sealed record PurchaseOrderSettings(bool DeliveryConfigured, bool ApprovalWorkflows, bool PurchaseOrdersEnabled);

[ApiController]
[Route("api")]
[RequireRole]
public sealed class PurchasingController(
    AppDbContext db,
    ComparisonService comparison,
    PlanExplanationService explanations,
    AlternativesService alternatives,
    CartService cart,
    PurchaseOrderService orders,
    EntitlementService entitlements) : ApiController
{
    // ---------- Comparison and optimization ----------

    /// <summary>Refreshes supplier offers and returns the comparison table, the baseline and the purchasing plans.</summary>
    [HttpPost("projects/{projectId:guid}/comparison")]
    public Task<ComparisonResult> Compare(Guid projectId, [FromQuery] bool refresh = false, CancellationToken ct = default) =>
        comparison.CompareAsync(projectId, refresh, record: true, ct);

    /// <summary>Plain-language explanation of a computed plan. The numbers always come from the optimizer, never from the AI.</summary>
    [HttpPost("projects/{projectId:guid}/comparison/explain"), EnableRateLimiting("ai")]
    public Task<PlanExplanation> Explain(Guid projectId, ExplainRequest request, CancellationToken ct) =>
        explanations.ExplainAsync(projectId, request.Strategy, ct);

    // ---------- Alternatives and substitutions ----------

    [HttpGet("items/{itemId:guid}/alternatives"), EnableRateLimiting("ai")]
    public Task<AlternativesResult> Alternatives(Guid itemId, [FromQuery] bool explain = true, CancellationToken ct = default) =>
        alternatives.GetAsync(itemId, explain, ct);

    /// <summary>Records an explicit, acknowledged user approval of a substitution. There is no automatic path to this state.</summary>
    [HttpPost("items/{itemId:guid}/substitution"), RequireRole(OrgRole.PurchasingManager)]
    public async Task<IActionResult> ApproveSubstitution(Guid itemId, ApproveSubstitutionRequest request, CancellationToken ct)
    {
        await alternatives.ApproveAsync(itemId, request.AlternativeProductId, request.Acknowledged, ct);
        return NoContent();
    }

    [HttpDelete("items/{itemId:guid}/substitution"), RequireRole(OrgRole.PurchasingManager)]
    public async Task<IActionResult> RevokeSubstitution(Guid itemId, CancellationToken ct)
    {
        await alternatives.RevokeAsync(itemId, ct);
        return NoContent();
    }

    // ---------- Universal cart ----------

    [HttpGet("projects/{projectId:guid}/cart")]
    public Task<CartDto> GetCart(Guid projectId, CancellationToken ct) => cart.GetAsync(projectId, ct);

    [HttpPost("projects/{projectId:guid}/cart/apply-plan"), RequireRole(OrgRole.Employee)]
    public Task<CartDto> ApplyPlan(Guid projectId, ApplyPlanRequest request, CancellationToken ct) => cart.ApplyPlanAsync(projectId, request.Strategy, ct);

    [HttpPost("projects/{projectId:guid}/cart/items"), RequireRole(OrgRole.Employee)]
    public Task<CartDto> AddToCart(Guid projectId, AddCartItemRequest request, CancellationToken ct) =>
        cart.AddAsync(projectId, request.OfferId, request.Packs, request.ShoppingListItemId, ct);

    [HttpPut("cart-items/{id:guid}"), RequireRole(OrgRole.Employee)]
    public Task<CartDto> UpdateCartItem(Guid id, UpdateCartItemRequest request, CancellationToken ct) => cart.UpdateAsync(id, request.Packs, ct);

    [HttpDelete("cart-items/{id:guid}"), RequireRole(OrgRole.Employee)]
    public Task<CartDto> RemoveCartItem(Guid id, CancellationToken ct) => cart.RemoveAsync(id, ct);

    [HttpDelete("projects/{projectId:guid}/cart"), RequireRole(OrgRole.Employee)]
    public Task<CartDto> ClearCart(Guid projectId, [FromQuery] Guid? supplierId, CancellationToken ct) => cart.ClearAsync(projectId, supplierId, ct);

    /// <summary>Returns a validated link to the supplier's own site, or 404 when no permitted link exists. SmartBuy never checks out on the user's behalf.</summary>
    [HttpPost("cart-items/{id:guid}/purchase-link")]
    public async Task<ActionResult<PurchaseLinkDto>> PurchaseLink(Guid id, CancellationToken ct)
    {
        var link = await cart.BuildPurchaseLinkAsync(id, ct);
        return link is null
            ? Problem(statusCode: 404, title: "This supplier has no approved purchase link. Use a purchase order or export the list instead.")
            : link;
    }

    [HttpGet("projects/{projectId:guid}/cart/export.csv")]
    public async Task<IActionResult> ExportCart(Guid projectId, [FromQuery] Guid? supplierId, CancellationToken ct)
    {
        var data = await cart.GetAsync(projectId, ct);
        var sb = new StringBuilder();
        sb.AppendLine("Supplier,Product,SKU,Pack Size,Pack Unit,Packages,Price per Package,Line Total,Data Status,For List Item");
        foreach (var supplier in data.Suppliers.Where(s => supplierId is null || s.SupplierId == supplierId))
        {
            foreach (var line in supplier.Lines)
                sb.AppendLine(string.Join(',', PurchaseOrderExport.CsvCell(supplier.SupplierName), PurchaseOrderExport.CsvCell(line.ProductTitle), PurchaseOrderExport.CsvCell(line.Sku),
                    line.PackQuantity.ToString("0.####", CultureInfo.InvariantCulture), PurchaseOrderExport.CsvCell(line.PackUnit), line.Packs.ToString(CultureInfo.InvariantCulture),
                    (line.CurrentPackPrice ?? line.SnapshotPackPrice).ToString("0.00", CultureInfo.InvariantCulture), line.LineCost.ToString("0.00", CultureInfo.InvariantCulture),
                    line.DataStatus, PurchaseOrderExport.CsvCell(line.ListItemDescription)));
            sb.AppendLine($"{PurchaseOrderExport.CsvCell(supplier.SupplierName)},Subtotal,,,,,,{supplier.Totals.Subtotal.ToString("0.00", CultureInfo.InvariantCulture)},,");
            sb.AppendLine($"{PurchaseOrderExport.CsvCell(supplier.SupplierName)},Shipping (estimate),,,,,,{(supplier.Totals.Shipping is { } s ? s.ToString("0.00", CultureInfo.InvariantCulture) : "unknown")},,");
        }
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv", "smartbuy-shopping-list.csv");
    }

    // ---------- Purchase orders ----------

    [HttpGet("purchase-orders/settings")]
    public async Task<PurchaseOrderSettings> Settings(CancellationToken ct) => new(orders.DeliveryConfigured,
        await entitlements.HasAsync(Domain.Rules.PlanFeature.ApprovalWorkflows, ct), await entitlements.HasAsync(Domain.Rules.PlanFeature.PurchaseOrders, ct));

    [HttpGet("purchase-orders")]
    public Task<List<PoDto>> ListOrders([FromQuery] Guid? projectId, [FromQuery] PurchaseOrderStatus? status, CancellationToken ct) => orders.ListAsync(projectId, status, Role, ct);

    [HttpGet("purchase-orders/{id:guid}")]
    public Task<PoDto> GetOrder(Guid id, CancellationToken ct) => orders.GetAsync(id, Role, ct);

    [HttpPost("purchase-orders"), RequireRole(OrgRole.Employee)]
    public Task<PoDto> CreateOrder(PoInput request, CancellationToken ct) => orders.CreateAsync(request, Role, ct);

    [HttpPost("projects/{projectId:guid}/cart/purchase-orders"), RequireRole(OrgRole.Employee)]
    public Task<List<PoDto>> CreateFromCart(Guid projectId, CreateFromCartRequest request, CancellationToken ct) =>
        orders.CreateFromCartAsync(projectId, request.SupplierId, request.RequestedDeliveryDate, Role, ct);

    [HttpPut("purchase-orders/{id:guid}"), RequireRole(OrgRole.Employee)]
    public Task<PoDto> UpdateOrder(Guid id, PoInput request, CancellationToken ct) => orders.UpdateAsync(id, request, Role, ct);

    [HttpDelete("purchase-orders/{id:guid}"), RequireRole(OrgRole.Employee)]
    public async Task<IActionResult> DeleteOrder(Guid id, CancellationToken ct)
    {
        await orders.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("purchase-orders/{id:guid}/submit"), RequireRole(OrgRole.Employee)]
    public Task<PoDto> Submit(Guid id, CancellationToken ct) => orders.SubmitAsync(id, Role, ct);

    [HttpPost("purchase-orders/{id:guid}/approve"), RequireRole(OrgRole.PurchasingManager)]
    public Task<PoDto> Approve(Guid id, DecisionRequest request, CancellationToken ct) => orders.ApproveAsync(id, request.Comment, Role, ct);

    [HttpPost("purchase-orders/{id:guid}/reject"), RequireRole(OrgRole.PurchasingManager)]
    public Task<PoDto> Reject(Guid id, DecisionRequest request, CancellationToken ct) => orders.RejectAsync(id, request.Comment, Role, ct);

    /// <summary>Sends through the configured delivery channel. Returns 409 (and changes nothing) when no channel is configured or delivery fails.</summary>
    [HttpPost("purchase-orders/{id:guid}/send"), RequireRole(OrgRole.PurchasingManager)]
    public Task<PoDto> Send(Guid id, SendRequest request, CancellationToken ct) => orders.SendAsync(id, request.Recipient, Role, ct);

    /// <summary>Records that the user delivered the PO themselves. Stored as a manual record, not as a SmartBuy delivery.</summary>
    [HttpPost("purchase-orders/{id:guid}/record-sent"), RequireRole(OrgRole.PurchasingManager)]
    public Task<PoDto> RecordSent(Guid id, RecordSentRequest request, CancellationToken ct) => orders.RecordSentManuallyAsync(id, request.Note, Role, ct);

    [HttpPost("purchase-orders/{id:guid}/receive"), RequireRole(OrgRole.Employee)]
    public Task<PoDto> Receive(Guid id, ReceiveRequest request, CancellationToken ct) => orders.ReceiveAsync(id, request.Lines, Role, ct);

    [HttpPost("purchase-orders/{id:guid}/cancel"), RequireRole(OrgRole.PurchasingManager)]
    public Task<PoDto> Cancel(Guid id, CancellationToken ct) => orders.CancelAsync(id, Role, ct);

    [HttpGet("purchase-orders/{id:guid}/export.csv")]
    public async Task<IActionResult> ExportCsv(Guid id, CancellationToken ct)
    {
        var po = await orders.GetAsync(id, Role, ct);
        return File(PurchaseOrderExport.ToCsv(po), "text/csv", $"{po.PoNumber}.csv");
    }

    [HttpGet("purchase-orders/{id:guid}/export.pdf")]
    public async Task<IActionResult> ExportPdf(Guid id, CancellationToken ct)
    {
        var po = await orders.GetAsync(id, Role, ct);
        var organization = await db.Organizations.AsNoTracking().FirstAsync(o => o.Id == OrganizationId, ct);
        return File(PurchaseOrderExport.ToPdf(po, organization.Name, organization.IsDemo), "application/pdf", $"{po.PoNumber}.pdf");
    }
}
