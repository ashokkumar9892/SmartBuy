// Shapes returned by the SmartBuy API. Kept in one place so pages agree on the contract.

export type Role = 'Viewer' | 'Employee' | 'PurchasingManager' | 'Administrator' | 'Owner'
export type DataStatus = 'LIVE' | 'DEMO' | 'STALE'
export type Strategy = 'LowestCost' | 'FastestDelivery' | 'Balanced'

export interface Plan {
  code: string
  name: string
  description: string
  monthlyPriceCents: number
  maxProjectsPerMonth: number | null
  maxMembers: number | null
  aiMaterialLists: boolean
  advancedOptimization: boolean
  priceAlerts: boolean
  purchaseOrders: boolean
  advancedReporting: boolean
  approvalWorkflows: boolean
}

export interface OrganizationSummary {
  id: string
  name: string
  slug: string
  industry: string
  isDemo: boolean
  role: Role
}

export interface Session {
  user: { id: string; email: string; fullName: string; isPlatformAdmin: boolean; isDemo: boolean }
  organization: OrganizationSummary
  organizations: OrganizationSummary[]
  plan: Plan
  permissions: {
    canEditLists: boolean
    canManagePurchasing: boolean
    canApprovePurchaseOrders: boolean
    canManageOrganization: boolean
    canManageBilling: boolean
  }
  aiProvider: string
}

export interface AuthResponse {
  accessToken: string
  expiresAt: string
  refreshToken: string
  session: Session
}

export interface DemoBusiness { slug: string; name: string; industry: string; tagline: string; plan: string }

export interface PublicConfig {
  demoEnabled: boolean
  selfSignupEnabled: boolean
  googleEnabled: boolean
  googleClientId: string | null
  demoBusinesses: DemoBusiness[]
  plans: Plan[]
}

export interface ListItem {
  id: string
  lineNumber: number
  description: string
  quantity: number
  unit: string
  material: string | null
  specifications: string | null
  notes: string | null
  source: 'Manual' | 'Ai' | 'Upload' | 'Reuse'
  canonicalProductId: string | null
  canonicalProductName: string | null
  catalogUnit: string | null
  isSafetyCritical: boolean
  matchConfidence: number | null
  approvedSubstituteProductId: string | null
  approvedSubstituteName: string | null
  substitutionApprovedAt: string | null
}

export interface ShoppingList {
  id: string
  name: string
  isApproved: boolean
  approvedAt: string | null
  sourceText: string | null
  assumptions: string[]
  version: string
  items: ListItem[]
}

export interface ProjectDetail {
  id: string
  name: string
  description: string | null
  industry: string
  status: string
  requiredByDate: string | null
  deliveryPostalCode: string | null
  createdAt: string
  updatedAt: string
  version: string
  lists: ShoppingList[]
}

export interface ProjectSummary {
  id: string
  name: string
  description: string | null
  industry: string
  status: string
  requiredByDate: string | null
  createdAt: string
  updatedAt: string
  itemCount: number
  listCount: number
  hasUnapprovedList: boolean
  cartLineCount: number
  purchaseOrderCount: number
  lastBaselineCost: number | null
  lastOptimizedCost: number | null
}

export interface ResolveCandidate {
  productId: string
  name: string
  baseUnit: string
  category: string
  confidence: number
  reason: string
  isSafetyCritical: boolean
}

export interface DraftItem {
  description: string
  quantity: number
  unit: string
  material: string | null
  specifications: string | null
  notes: string | null
  suggestedProductId: string | null
  suggestedProductName: string | null
  confidence: number | null
  candidates: ResolveCandidate[]
}

export interface DraftResponse {
  source: string
  sourceLabel: string
  requiresReview: boolean
  items: DraftItem[]
  assumptions: string[]
  warnings: string[]
}

export interface CatalogProduct {
  id: string
  name: string
  industry: string
  category: string
  brand: string | null
  model: string | null
  mpn: string | null
  gtin: string | null
  material: string | null
  baseUnit: string
  isSafetyCritical: boolean
  safetyNote: string | null
  specifications: Record<string, string>
}

export interface UnitOption { code: string; label: string; dimension: string }

export interface OfferRow {
  offerId: string
  supplierId: string
  supplierName: string
  supplierProductId: string
  productTitle: string
  brand: string | null
  model: string | null
  packQuantity: number
  packUnit: string
  packPrice: number
  unitPrice: number | null
  unitPriceUnit: string
  packsRequired: number | null
  purchasedQuantity: number | null
  subtotal: number | null
  shipping: number | null
  shippingUnknown: boolean
  tax: number | null
  taxUnknown: boolean
  estimatedTotal: number | null
  stockPacks: number | null
  stockStatus: 'in_stock' | 'insufficient' | 'out_of_stock' | 'unknown'
  deliveryDaysMin: number | null
  deliveryDaysMax: number | null
  dataStatus: DataStatus
  fetchedAt: string
  isSubstitute: boolean
  usable: boolean
  unusableReason: string | null
  matchConfidence: number
  matchMethod: string
  isLowestTotal: boolean
}

export interface ItemComparison {
  itemId: string
  lineNumber: number
  description: string
  quantity: number
  unit: string
  canonicalProductId: string | null
  canonicalProductName: string | null
  isSafetyCritical: boolean
  substituteProductId: string | null
  substituteProductName: string | null
  offers: OfferRow[]
  listingsNeedingReview: number
  problem: string | null
}

export interface SupplierTotals {
  supplierId: string
  supplierName: string
  lineCount: number
  subtotal: number
  shipping: number | null
  tax: number | null
  knownTotal: number
  belowMinimumOrder: boolean
  amountToFreeShipping: number | null
}

export interface Totals {
  subtotal: number
  knownShipping: number
  shippingUnknown: boolean
  tax: number | null
  taxUnknown: boolean
  knownTotal: number
  isComplete: boolean
  suppliers: SupplierTotals[]
}

export interface PlanLine {
  itemId: string
  description: string
  quantity: number
  unit: string
  supplierId: string
  supplierName: string
  offerId: string
  supplierProductId: string
  productTitle: string
  packs: number
  packPrice: number
  lineCost: number
  unitPrice: number
  deliveryDays: number | null
  isSubstitute: boolean
  dataStatus: DataStatus
}

export interface PurchasingPlan {
  strategy: Strategy
  title: string
  summary: string
  lines: PlanLine[]
  unfilledItems: string[]
  totals: Totals
  maxDeliveryDays: number | null
  deliveryUnknown: boolean
  meetsDeadline: boolean | null
  savings: number | null
  savingsPercent: number | null
  uncertainties: string[]
  method: string
}

export interface ComparisonResult {
  projectId: string
  projectName: string
  generatedAt: string
  items: ItemComparison[]
  baseline: { label: string; explanation: string; totals: Totals; lines: PlanLine[] } | null
  plans: PurchasingPlan[]
  lockedStrategies: { strategy: Strategy; title: string; reason: string }[]
  providerIssues: { supplierId: string; supplierName: string; message: string }[]
  warnings: string[]
  taxRatePercent: number | null
  deadlineDays: number | null
  containsDemoData: boolean
  containsStaleData: boolean
}

export interface Alternative {
  productId: string
  name: string
  baseUnit: string
  eligible: boolean
  blockedReason: string | null
  sameCertification: boolean
  specDifferences: string
  compatibility: string
  qualityNotes: string
  rationale: string
  originalBestUnitPrice: number | null
  alternativeBestUnitPrice: number | null
  unitPriceDifference: number | null
  percentDifference: number | null
  pricesComparable: boolean
  alternativeBestSupplier: string | null
}

export interface AlternativesResult {
  itemId: string
  productName: string
  isSafetyCritical: boolean
  safetyNote: string | null
  alternatives: Alternative[]
  explanation: string
  explanationSource: string
}

export interface CartLine {
  id: string
  shoppingListItemId: string | null
  listItemDescription: string | null
  supplierProductId: string
  productTitle: string
  sku: string | null
  packQuantity: number
  packUnit: string
  packs: number
  currentPackPrice: number | null
  snapshotPackPrice: number
  priceChanged: boolean
  lineCost: number
  dataStatus: DataStatus
}

export interface CartSupplier {
  supplierId: string
  supplierName: string
  supplierKind: string
  lines: CartLine[]
  totals: SupplierTotals
  checkoutMode: 'cart' | 'link' | 'export'
  checkoutNote: string
}

export interface Cart {
  projectId: string
  suppliers: CartSupplier[]
  totals: Totals
  lineCount: number
  containsDemoData: boolean
}

export interface PurchaseLink { url: string; kind: string; isAffiliate: boolean; note: string }

export interface PoItem {
  id: string
  lineNumber: number
  description: string
  supplierSku: string | null
  quantity: number
  packQuantity: number
  unit: string
  unitPrice: number
  lineTotal: number
  quantityReceived: number
}

export type PoAction = 'edit' | 'delete' | 'submit' | 'approve' | 'reject' | 'send' | 'record_sent' | 'receive' | 'cancel'

export interface PurchaseOrder {
  id: string
  poNumber: string
  status: string
  supplierId: string
  supplierName: string
  projectId: string | null
  projectName: string | null
  requestedDeliveryDate: string | null
  shipTo: string | null
  internalNotes: string | null
  subtotal: number
  shippingEstimate: number | null
  taxEstimate: number | null
  total: number
  totalExcludesUnknowns: boolean
  createdBy: string
  createdAt: string
  approvedAt: string | null
  approvedBy: string | null
  sentAt: string | null
  sentVia: string | null
  sentTo: string | null
  version: string
  items: PoItem[]
  approvals: { id: string; status: string; requestedBy: string; decidedBy: string | null; requestedAt: string; decidedAt: string | null; comment: string | null }[]
  allowedActions: PoAction[]
}

export interface PurchaseOrderSettings { deliveryConfigured: boolean; approvalWorkflows: boolean; purchaseOrdersEnabled: boolean }

export interface ProviderCapabilities { productSearch: boolean; inventory: boolean; purchaseLinks: boolean; cartBuilding: boolean; orderPlacement: boolean }

export interface Supplier {
  id: string
  code: string
  name: string
  kind: string
  mode: 'Mock' | 'Live'
  isEnabled: boolean
  websiteUrl: string | null
  shippingFlatFee: number | null
  freeShippingThreshold: number | null
  minimumOrderAmount: number | null
  integrationNotes: string | null
  isHealthy: boolean
  statusMessage: string
  capabilities: ProviderCapabilities | null
  isSaved: boolean
  isPreferred: boolean
  accountNumber: string | null
  orderEmail: string | null
  notes: string | null
}

export interface PriceAlert {
  id: string
  type: 'PriceBelow' | 'BackInStock' | 'BetterOffer'
  productId: string
  productName: string
  unit: string
  supplierId: string | null
  supplierName: string | null
  targetPrice: number | null
  isActive: boolean
  lastCheckedAt: string | null
  lastTriggeredAt: string | null
  lastTriggeredPrice: number | null
  currentBestUnitPrice: number | null
  currentBestSupplier: string | null
  createdAt: string
}

export interface Notification { id: string; title: string; body: string; createdAt: string; isRead: boolean; priceAlertId: string | null }

export interface DashboardOrder { id: string; poNumber: string; status: string; supplierName: string; total: number; createdAt: string }

export interface Dashboard {
  totalProjects: number
  activeProjects: number
  shoppingLists: number
  listsAwaitingReview: number
  pendingPurchaseOrders: number
  activePriceAlerts: number
  unreadNotifications: number
  savingsOpportunity: number
  savingsBaselineTotal: number
  savingsOptimizedTotal: number
  savingsProjectCount: number
  savingsBasis: string
  savingsContainDemoData: boolean
  recentProjects: { id: string; name: string; status: string; updatedAt: string; itemCount: number; needsReview: boolean }[]
  recentComparisons: {
    projectId: string
    projectName: string
    ranAt: string
    itemCount: number
    matchedItemCount: number
    supplierCount: number
    baselineCost: number | null
    baselineLabel: string | null
    optimizedCost: number | null
    hasUnknowns: boolean
  }[]
  pendingOrders: DashboardOrder[]
  purchaseHistory: DashboardOrder[]
  suppliers: { id: string; name: string; mode: string; isHealthy: boolean; message: string }[]
  projectsThisMonth: number
  projectQuota: number | null
}

export interface Organization {
  id: string
  name: string
  slug: string
  industry: string
  isDemo: boolean
  estimatedTaxRatePercent: number | null
  postalCode: string | null
  shippingAddress: string | null
  version: string
}

export interface Member { userId: string; email: string; fullName: string; role: Role; isActive: boolean; joinedAt: string; lastLoginAt: string | null; isYou: boolean }

export interface BillingOverview {
  currentPlan: Plan
  subscriptionStatus: string
  plans: Plan[]
  billing: { stripeConfigured: boolean; testMode: boolean; manualPlanChangeAllowed: boolean }
  projectsThisMonth: number
  members: number
  pricingNote: string
}

export interface SpendRow { label: string; orders: number; total: number }
export interface SpendReport { totalCommitted: number; orderCount: number; bySupplier: SpendRow[]; byMonth: SpendRow[]; byProject: SpendRow[]; basis: string }

export const INDUSTRIES: { value: string; label: string }[] = [
  { value: 'Plumbing', label: 'Plumbing' },
  { value: 'Electrical', label: 'Electrical' },
  { value: 'Hvac', label: 'HVAC' },
  { value: 'Construction', label: 'Construction' },
  { value: 'HomeRenovation', label: 'Home renovation' },
  { value: 'Landscaping', label: 'Landscaping' },
  { value: 'PropertyManagement', label: 'Property management' },
  { value: 'General', label: 'General business supplies' },
]

export const industryLabel = (value: string) => INDUSTRIES.find((i) => i.value === value)?.label ?? value

export const ROLE_LABELS: Record<Role, string> = {
  Viewer: 'Viewer',
  Employee: 'Employee',
  PurchasingManager: 'Purchasing Manager',
  Administrator: 'Administrator',
  Owner: 'Organization Owner',
}
