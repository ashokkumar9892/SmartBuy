using SmartBuy.Domain.Entities;

namespace SmartBuy.Domain.Rules;

public static class PurchaseOrderRules
{
    private static readonly Dictionary<PurchaseOrderStatus, PurchaseOrderStatus[]> Transitions = new()
    {
        [PurchaseOrderStatus.Draft] = new[] { PurchaseOrderStatus.PendingApproval, PurchaseOrderStatus.Approved, PurchaseOrderStatus.Cancelled },
        [PurchaseOrderStatus.PendingApproval] = new[] { PurchaseOrderStatus.Approved, PurchaseOrderStatus.Draft, PurchaseOrderStatus.Cancelled },
        [PurchaseOrderStatus.Approved] = new[] { PurchaseOrderStatus.Sent, PurchaseOrderStatus.Cancelled },
        [PurchaseOrderStatus.Sent] = new[] { PurchaseOrderStatus.PartiallyReceived, PurchaseOrderStatus.Received, PurchaseOrderStatus.Cancelled },
        [PurchaseOrderStatus.PartiallyReceived] = new[] { PurchaseOrderStatus.PartiallyReceived, PurchaseOrderStatus.Received },
        [PurchaseOrderStatus.Received] = Array.Empty<PurchaseOrderStatus>(),
        [PurchaseOrderStatus.Cancelled] = Array.Empty<PurchaseOrderStatus>(),
    };

    public static bool CanTransition(PurchaseOrderStatus from, PurchaseOrderStatus to) =>
        Transitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    public static bool IsEditable(PurchaseOrderStatus status) => status == PurchaseOrderStatus.Draft;

    public static string FormatNumber(int year, int sequence) => $"PO-{year}-{sequence:0000}";

    /// <summary>Recomputes line totals and header totals. Unknown shipping/tax stay null and are excluded from the total.</summary>
    public static void Recalculate(PurchaseOrder po)
    {
        foreach (var item in po.Items)
            item.LineTotal = decimal.Round(item.Quantity * item.UnitPrice, 2, MidpointRounding.AwayFromZero);
        po.Subtotal = po.Items.Sum(i => i.LineTotal);
        po.Total = po.Subtotal + (po.ShippingEstimate ?? 0) + (po.TaxEstimate ?? 0);
    }

    /// <summary>Status implied by what has been received so far.</summary>
    public static PurchaseOrderStatus ReceiptStatus(PurchaseOrder po)
    {
        if (po.Items.Count > 0 && po.Items.All(i => i.QuantityReceived >= i.Quantity)) return PurchaseOrderStatus.Received;
        return po.Items.Any(i => i.QuantityReceived > 0) ? PurchaseOrderStatus.PartiallyReceived : PurchaseOrderStatus.Sent;
    }
}

public enum PlanFeature
{
    AiMaterialLists,
    AdvancedOptimization,
    PriceAlerts,
    PurchaseOrders,
    AdvancedReporting,
    ApprovalWorkflows
}

public sealed record EntitlementResult(bool Allowed, string? Reason)
{
    public static readonly EntitlementResult Ok = new(true, null);
}

public static class Entitlements
{
    public static bool HasFeature(Plan plan, PlanFeature feature) => feature switch
    {
        PlanFeature.AiMaterialLists => plan.AiMaterialLists,
        PlanFeature.AdvancedOptimization => plan.AdvancedOptimization,
        PlanFeature.PriceAlerts => plan.PriceAlerts,
        PlanFeature.PurchaseOrders => plan.PurchaseOrders,
        PlanFeature.AdvancedReporting => plan.AdvancedReporting,
        PlanFeature.ApprovalWorkflows => plan.ApprovalWorkflows,
        _ => false
    };

    public static EntitlementResult CheckFeature(Plan plan, PlanFeature feature) =>
        HasFeature(plan, feature)
            ? EntitlementResult.Ok
            : new EntitlementResult(false, $"{Describe(feature)} is not included in the {plan.Name} plan.");

    public static EntitlementResult CheckProjectQuota(Plan plan, int projectsCreatedThisMonth) =>
        plan.MaxProjectsPerMonth is { } max && projectsCreatedThisMonth >= max
            ? new EntitlementResult(false, $"The {plan.Name} plan allows {max} new project(s) per month and this organization has already created {projectsCreatedThisMonth}.")
            : EntitlementResult.Ok;

    public static EntitlementResult CheckMemberQuota(Plan plan, int currentMembers) =>
        plan.MaxMembers is { } max && currentMembers >= max
            ? new EntitlementResult(false, $"The {plan.Name} plan allows {max} team member(s).")
            : EntitlementResult.Ok;

    public static string Describe(PlanFeature feature) => feature switch
    {
        PlanFeature.AiMaterialLists => "AI material lists",
        PlanFeature.AdvancedOptimization => "Advanced optimization",
        PlanFeature.PriceAlerts => "Price alerts",
        PlanFeature.PurchaseOrders => "Purchase orders",
        PlanFeature.AdvancedReporting => "Advanced reporting",
        PlanFeature.ApprovalWorkflows => "Team approval workflows",
        _ => feature.ToString()
    };
}

/// <summary>What a role may do inside its own organization. Platform administration is a separate flag on the user.</summary>
public static class RolePermissions
{
    public static bool CanView(OrgRole role) => role >= OrgRole.Viewer;
    public static bool CanEditLists(OrgRole role) => role >= OrgRole.Employee;
    public static bool CanManagePurchasing(OrgRole role) => role >= OrgRole.PurchasingManager;
    public static bool CanApprovePurchaseOrders(OrgRole role) => role >= OrgRole.PurchasingManager;
    public static bool CanManageOrganization(OrgRole role) => role >= OrgRole.Administrator;
    public static bool CanManageBilling(OrgRole role) => role == OrgRole.Owner;
    /// <summary>Nobody can grant a role above their own, and only owners create owners.</summary>
    public static bool CanAssignRole(OrgRole actor, OrgRole target) => CanManageOrganization(actor) && target <= actor;
}

public sealed record AlertOffer(Guid SupplierId, string SupplierName, decimal UnitPrice, bool? InStock);

public sealed record AlertEvaluation(bool Triggered, string? Title, string? Message, decimal? Price, bool? InStockNow);

/// <summary>Pure price-alert rule evaluation over the current offers for the alert's product.</summary>
public static class PriceAlertRules
{
    public static AlertEvaluation Evaluate(PriceAlert alert, string productName, string unit, IReadOnlyList<AlertOffer> offers)
    {
        var scoped = alert.Type != PriceAlertType.BetterOffer && alert.SupplierId is { } only
            ? offers.Where(o => o.SupplierId == only).ToList()
            : offers.ToList();
        bool? inStockNow = scoped.Count == 0 ? null : scoped.Any(o => o.InStock == true) ? true : scoped.All(o => o.InStock == false) ? false : null;
        var none = new AlertEvaluation(false, null, null, null, inStockNow);
        if (scoped.Count == 0) return none;

        switch (alert.Type)
        {
            case PriceAlertType.PriceBelow:
            {
                if (alert.TargetPrice is not { } target) return none;
                var best = scoped.OrderBy(o => o.UnitPrice).ThenBy(o => o.SupplierId).First();
                if (best.UnitPrice > target || alert.LastTriggeredPrice == best.UnitPrice) return none;
                return new AlertEvaluation(true, $"Price drop: {productName}",
                    $"{best.SupplierName} lists {productName} at ${best.UnitPrice:0.00} per {unit}, at or below your target of ${target:0.00}.", best.UnitPrice, inStockNow);
            }
            case PriceAlertType.BackInStock:
            {
                // Only a confirmed out-of-stock → in-stock change fires; unknown inventory never does.
                if (alert.LastKnownInStock != false || inStockNow != true) return none;
                var seller = scoped.Where(o => o.InStock == true).OrderBy(o => o.UnitPrice).ThenBy(o => o.SupplierId).First();
                return new AlertEvaluation(true, $"Back in stock: {productName}",
                    $"{productName} is back in stock at {seller.SupplierName} (${seller.UnitPrice:0.00} per {unit}).", seller.UnitPrice, inStockNow);
            }
            case PriceAlertType.BetterOffer:
            {
                decimal? reference = alert.SupplierId is { } current
                    ? offers.Where(o => o.SupplierId == current).Select(o => (decimal?)o.UnitPrice).Min()
                    : alert.TargetPrice;
                if (reference is null) return none;
                var better = offers.Where(o => o.SupplierId != alert.SupplierId && o.UnitPrice < reference && o.InStock != false)
                    .OrderBy(o => o.UnitPrice).ThenBy(o => o.SupplierId).FirstOrDefault();
                if (better is null || alert.LastTriggeredPrice == better.UnitPrice) return none;
                var pct = reference.Value == 0 ? 0 : (reference.Value - better.UnitPrice) / reference.Value;
                return new AlertEvaluation(true, $"Better offer: {productName}",
                    $"{better.SupplierName} offers a comparable {productName} at ${better.UnitPrice:0.00} per {unit}, {pct:P0} below your reference price of ${reference:0.00}.", better.UnitPrice, inStockNow);
            }
            default:
                return none;
        }
    }
}
