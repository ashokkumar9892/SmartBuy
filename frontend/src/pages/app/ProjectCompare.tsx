import { useMutation, useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query'
import clsx from 'clsx'
import { AlertTriangle, ArrowRight, ChevronDown, Clock, Lock, RefreshCw, Replace, Scale, ShieldAlert, ShoppingCart, Sparkles, Trophy, Truck, Zap } from 'lucide-react'
import { useState } from 'react'
import { Badge, Button, Card, CardHeader, DataStatusBadge, EmptyState, ErrorState, LinkButton, Notice, Skeleton, Table, Td, Th, Unknown, useToast } from '../../components/ui'
import { api, ApiError, errorMessage } from '../../lib/api'
import { useSession } from '../../lib/auth'
import { deliveryLabel, money, packLabel, qty, timeAgo, unitLabel, unitMoney } from '../../lib/format'
import type { Cart, ComparisonResult, ItemComparison, OfferRow, ProjectDetail, PurchasingPlan, Strategy, Totals } from '../../lib/types'

export function useComparison(projectId: string, enabled: boolean) {
  return useQuery({
    queryKey: ['comparison', projectId],
    queryFn: () => api.post<ComparisonResult>(`/api/projects/${projectId}/comparison`),
    enabled,
    staleTime: 5 * 60_000,
  })
}

type ComparisonQuery = UseQueryResult<ComparisonResult>

function useRefreshPrices(projectId: string) {
  const queryClient = useQueryClient()
  const toast = useToast()
  return useMutation({
    mutationFn: () => api.post<ComparisonResult>(`/api/projects/${projectId}/comparison?refresh=true`),
    onSuccess: (result) => {
      queryClient.setQueryData(['comparison', projectId], result)
      queryClient.invalidateQueries({ queryKey: ['dashboard'] })
      queryClient.invalidateQueries({ queryKey: ['projects'] })
      toast.success(result.providerIssues.length ? `Prices refreshed. ${result.providerIssues.length} supplier(s) could not be reached.` : 'Prices refreshed from all suppliers.')
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
}

function NotReady({ onGoToList }: { onGoToList: () => void }) {
  return (
    <EmptyState
      icon={<Scale className="size-5" />}
      title="Approve a shopping list first"
      description="Suppliers are only searched once a person has reviewed and approved the quantities on at least one list."
      action={<Button variant="primary" onClick={onGoToList}>Go to the material list</Button>}
    />
  )
}

function ComparisonNotices({ result }: { result: ComparisonResult }) {
  return (
    <div className="space-y-3">
      {result.containsDemoData && (
        <Notice tone="info" title="These are demo prices">
          Supplier prices, stock and delivery times on this page come from simulated demo data, not live supplier quotes. Each row is labelled DEMO, LIVE or STALE.
        </Notice>
      )}
      {result.providerIssues.map((issue) => (
        <Notice key={issue.supplierId} tone="warning" title={`${issue.supplierName} could not be refreshed`}>{issue.message}</Notice>
      ))}
      {result.warnings.map((warning, index) => <Notice key={index} tone="warning">{warning}</Notice>)}
    </div>
  )
}

// ---------- Compare tab ----------

export function CompareTab({ project, comparison, anyApproved, onGoToList, onGoToPlan }: { project: ProjectDetail; comparison: ComparisonQuery; anyApproved: boolean; onGoToList: () => void; onGoToPlan: () => void }) {
  const refresh = useRefreshPrices(project.id)
  if (!anyApproved) return <NotReady onGoToList={onGoToList} />
  if (comparison.error) return <ErrorState error={comparison.error} onRetry={comparison.refetch} />
  if (comparison.isLoading || !comparison.data) return <ComparisonSkeleton label="Asking every supplier for current offers" />
  const result = comparison.data
  const priced = result.items.filter((i) => i.offers.some((o) => o.usable)).length

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="text-sm text-slate-600">
          <b className="font-semibold text-navy-900">{priced} of {result.items.length}</b> items priced · compared {timeAgo(result.generatedAt)}
          {result.taxRatePercent !== null && <> · estimated tax {result.taxRatePercent}%</>}
          {result.deadlineDays !== null && <> · needed within {result.deadlineDays} day{result.deadlineDays === 1 ? '' : 's'}</>}
        </p>
        <div className="flex gap-2">
          <Button icon={<RefreshCw className={clsx('size-4', refresh.isPending && 'animate-spin')} />} loading={false} disabled={refresh.isPending} onClick={() => refresh.mutate()}>
            {refresh.isPending ? 'Refreshing…' : 'Refresh prices'}
          </Button>
          <Button variant="primary" icon={<ArrowRight className="order-last size-4" />} onClick={onGoToPlan}>See purchasing plan</Button>
        </div>
      </div>
      <ComparisonNotices result={result} />
      {result.items.map((item, index) => <ItemOffers key={item.itemId} projectId={project.id} item={item} defaultOpen={index < 2} />)}
      <p className="text-xs leading-relaxed text-slate-500">
        Shipping shown per row is what that supplier would charge for that line alone; the purchasing plan works out shipping once per supplier across the whole order.
        Tax is an estimate on merchandise at your organization's rate. Unknown values are excluded from totals and marked.
      </p>
    </div>
  )
}

function StockCell({ offer }: { offer: OfferRow }) {
  switch (offer.stockStatus) {
    case 'in_stock': return <span className="text-emerald-700">{offer.stockPacks} in stock</span>
    case 'insufficient': return <span className="text-red-600">Only {offer.stockPacks} in stock</span>
    case 'out_of_stock': return <span className="text-red-600">Out of stock</span>
    default: return <Unknown title="The supplier did not report inventory for this item.">Not reported</Unknown>
  }
}

function ItemOffers({ projectId, item, defaultOpen }: { projectId: string; item: ItemComparison; defaultOpen: boolean }) {
  const session = useSession()
  const toast = useToast()
  const queryClient = useQueryClient()
  const [open, setOpen] = useState(defaultOpen)
  const usable = item.offers.filter((o) => o.usable)
  const best = usable.find((o) => o.isLowestTotal)
  const add = useMutation({
    mutationFn: (offer: OfferRow) => api.post<Cart>(`/api/projects/${projectId}/cart/items`, { offerId: offer.offerId, packs: offer.packsRequired, shoppingListItemId: item.itemId }),
    onSuccess: (cart, offer) => {
      queryClient.setQueryData(['cart', projectId], cart)
      toast.success(`Added to cart from ${offer.supplierName}. It replaces any earlier choice for this line.`)
    },
    onError: (e) => toast.error(errorMessage(e)),
  })

  return (
    <Card padded={false}>
      <button type="button" onClick={() => setOpen((v) => !v)} aria-expanded={open} className="flex w-full flex-wrap items-center justify-between gap-3 px-5 py-4 text-left sm:px-6">
        <span className="min-w-0">
          <span className="flex flex-wrap items-center gap-2 font-semibold text-navy-900">
            <span className="text-slate-400 tabular">{item.lineNumber}.</span> {item.description}
            {item.isSafetyCritical && <Badge tone="red"><ShieldAlert className="size-3" /> Safety-critical</Badge>}
            {item.substituteProductName && <Badge tone="violet"><Replace className="size-3" /> Substitute allowed</Badge>}
          </span>
          <span className="mt-0.5 block text-sm text-slate-500">
            Need {qty(item.quantity)} {unitLabel(item.unit)}
            {item.canonicalProductName && item.canonicalProductName !== item.description && <> · catalog: {item.canonicalProductName}</>}
          </span>
        </span>
        <span className="flex shrink-0 items-center gap-3 text-sm">
          {best ? (
            <span className="text-right">
              <span className="block font-semibold text-navy-900 tabular">from {money(best.estimatedTotal)}</span>
              <span className="block text-xs text-slate-500">{usable.length} usable offer{usable.length === 1 ? '' : 's'}</span>
            </span>
          ) : <Badge tone="amber">No usable offer</Badge>}
          <ChevronDown className={clsx('size-5 text-slate-400 transition-transform', open && 'rotate-180')} />
        </span>
      </button>

      {open && (
        <div className="border-t border-slate-200">
          {item.problem && <div className="px-5 pt-4 sm:px-6"><Notice tone="warning">{item.problem}</Notice></div>}
          {item.listingsNeedingReview > 0 && (
            <p className="px-5 pt-3 text-xs text-slate-500 sm:px-6">
              {item.listingsNeedingReview} more supplier listing{item.listingsNeedingReview === 1 ? ' was' : 's were'} found but matched with low confidence. They are held for manual review and are not priced here.
            </p>
          )}
          {item.offers.length > 0 && (
            <Table className="mt-3">
              <thead>
                <tr>
                  <Th>Supplier</Th><Th>Product · brand / model</Th><Th>Package</Th><Th align="right">Unit price</Th><Th align="right">Required</Th>
                  <Th align="right">Item subtotal</Th><Th align="right">Shipping</Th><Th align="right">Est. tax</Th><Th>Stock</Th><Th>Delivery</Th><Th align="right">Est. total</Th><Th />
                </tr>
              </thead>
              <tbody>
                {item.offers.map((offer) => (
                  <tr key={offer.offerId} className={clsx(offer.isLowestTotal && 'bg-emerald-50/50', !offer.usable && 'opacity-60')}>
                    <Td className="whitespace-nowrap font-medium text-navy-900">
                      {offer.supplierName}
                      {offer.isLowestTotal && <span className="mt-0.5 flex items-center gap-1 text-xs font-medium text-emerald-700"><Trophy className="size-3" /> Lowest total</span>}
                      <span className="mt-1 flex items-center gap-1.5"><DataStatusBadge status={offer.dataStatus} /><span className="text-xs font-normal text-slate-400" title={`Match: ${offer.matchMethod}, ${Math.round(offer.matchConfidence * 100)}%`}>{timeAgo(offer.fetchedAt)}</span></span>
                    </Td>
                    <Td className="max-w-52">
                      <span className="line-clamp-2" title={offer.productTitle}>{offer.productTitle}</span>
                      <span className="block text-xs text-slate-500">{[offer.brand, offer.model].filter(Boolean).join(' ') || 'Unbranded'}</span>
                      {offer.isSubstitute && <Badge tone="violet" className="mt-1">Approved substitute</Badge>}
                    </Td>
                    <Td className="whitespace-nowrap">{packLabel(offer.packQuantity, offer.packUnit)}<span className="block text-xs text-slate-500">{money(offer.packPrice)} each</span></Td>
                    <Td align="right" className="whitespace-nowrap">{offer.unitPrice === null ? '—' : <>{unitMoney(offer.unitPrice)}<span className="block text-xs text-slate-500">per {unitLabel(offer.unitPriceUnit)}</span></>}</Td>
                    <Td align="right" className="whitespace-nowrap">{offer.packsRequired === null ? '—' : <>{offer.packsRequired} pkg<span className="block text-xs text-slate-500">= {qty(offer.purchasedQuantity)} {unitLabel(item.unit)}</span></>}</Td>
                    <Td align="right">{offer.subtotal === null ? '—' : money(offer.subtotal)}</Td>
                    <Td align="right">{offer.subtotal === null ? '—' : offer.shippingUnknown ? <Unknown /> : offer.shipping === 0 ? <span className="text-emerald-700">Free</span> : money(offer.shipping)}</Td>
                    <Td align="right">{offer.subtotal === null ? '—' : offer.taxUnknown ? <Unknown title="No tax rate is set for your organization." /> : money(offer.tax)}</Td>
                    <Td className="whitespace-nowrap"><StockCell offer={offer} /></Td>
                    <Td className="whitespace-nowrap">{offer.deliveryDaysMin === null && offer.deliveryDaysMax === null ? <Unknown /> : deliveryLabel(offer.deliveryDaysMin, offer.deliveryDaysMax)}</Td>
                    <Td align="right" className="whitespace-nowrap font-semibold text-navy-900">
                      {offer.estimatedTotal === null ? '—' : money(offer.estimatedTotal)}
                      {(offer.shippingUnknown || offer.taxUnknown) && offer.estimatedTotal !== null && <span className="block text-xs font-normal text-amber-700">excl. unknown</span>}
                    </Td>
                    <Td align="right">
                      {offer.usable ? (
                        session.permissions.canEditLists
                          ? <Button size="sm" icon={<ShoppingCart className="size-4" />} loading={add.isPending && add.variables?.offerId === offer.offerId} onClick={() => add.mutate(offer)}>Add</Button>
                          : null
                      ) : <span className="block max-w-40 text-xs text-red-600">{offer.unusableReason}</span>}
                    </Td>
                  </tr>
                ))}
              </tbody>
            </Table>
          )}
        </div>
      )}
    </Card>
  )
}

function ComparisonSkeleton({ label }: { label: string }) {
  return (
    <div className="space-y-4" role="status">
      <p className="text-sm text-slate-500">{label}…</p>
      {[0, 1, 2].map((i) => <Skeleton key={i} className="h-24 rounded-2xl" />)}
    </div>
  )
}

// ---------- Plan tab ----------

const strategyIcons: Record<Strategy, typeof Trophy> = { LowestCost: Trophy, FastestDelivery: Zap, Balanced: Scale }

export function TotalsBlock({ totals, title = 'Estimated total' }: { totals: Totals; title?: string }) {
  return (
    <dl className="space-y-1.5 text-sm">
      <div className="flex justify-between gap-6"><dt className="text-slate-500">Merchandise</dt><dd className="tabular text-navy-900">{money(totals.subtotal)}</dd></div>
      <div className="flex justify-between gap-6">
        <dt className="text-slate-500">Shipping</dt>
        <dd className="tabular text-navy-900">{money(totals.knownShipping)}{totals.shippingUnknown && <span className="ml-1.5 text-amber-700">+ unknown</span>}</dd>
      </div>
      <div className="flex justify-between gap-6"><dt className="text-slate-500">Estimated tax</dt><dd className="tabular text-navy-900">{totals.taxUnknown ? <Unknown title="No tax rate is set for your organization." /> : money(totals.tax)}</dd></div>
      <div className="flex justify-between gap-6 border-t border-slate-200 pt-2 text-base font-semibold">
        <dt className="text-navy-900">{title}</dt>
        <dd className="tabular text-navy-900">{money(totals.knownTotal)}</dd>
      </div>
      {!totals.isComplete && <p className="flex items-start gap-1.5 text-xs text-amber-700"><AlertTriangle className="mt-0.5 size-3.5 shrink-0" /> Excludes amounts that are unknown.</p>}
    </dl>
  )
}

export function PlanTab({ project, comparison, anyApproved, onGoToList, onGoToCart }: { project: ProjectDetail; comparison: ComparisonQuery; anyApproved: boolean; onGoToList: () => void; onGoToCart: () => void }) {
  const session = useSession()
  const toast = useToast()
  const queryClient = useQueryClient()
  const [selected, setSelected] = useState<Strategy>('LowestCost')
  const refresh = useRefreshPrices(project.id)

  const apply = useMutation({
    mutationFn: (strategy: Strategy) => api.post<Cart>(`/api/projects/${project.id}/cart/apply-plan`, { strategy }),
    onSuccess: (cart) => {
      queryClient.setQueryData(['cart', project.id], cart)
      toast.success('Plan added to the cart, grouped by supplier.')
      onGoToCart()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const explain = useMutation({
    mutationFn: (strategy: Strategy) => api.post<{ strategy: string; text: string; source: string }>(`/api/projects/${project.id}/comparison/explain`, { strategy }),
    onError: (e) => toast.error(errorMessage(e)),
  })

  if (!anyApproved) return <NotReady onGoToList={onGoToList} />
  if (comparison.error) return <ErrorState error={comparison.error} onRetry={comparison.refetch} />
  if (comparison.isLoading || !comparison.data) return <ComparisonSkeleton label="Building purchasing plans" />
  const result = comparison.data
  const plan = result.plans.find((p) => p.strategy === selected) ?? result.plans[0]
  if (!plan || !result.baseline)
    return (
      <div className="space-y-4">
        <ComparisonNotices result={result} />
        <EmptyState icon={<Scale className="size-5" />} title="No plan could be built yet" description="None of the items on the approved lists has a usable supplier offer. Link each line to a catalog product, or check the Compare tab for details." action={<Button onClick={onGoToList}>Go to the material list</Button>} />
      </div>
    )
  const explanation = explain.data?.strategy === plan.strategy ? explain.data : null

  return (
    <div className="space-y-6">
      <ComparisonNotices result={result} />

      <Card>
        <CardHeader
          title="Baseline for comparison"
          description={result.baseline.explanation}
          action={<Button size="sm" icon={<RefreshCw className={clsx('size-4', refresh.isPending && 'animate-spin')} />} disabled={refresh.isPending} onClick={() => refresh.mutate()}>Refresh prices</Button>}
        />
        <div className="flex flex-wrap items-end justify-between gap-4">
          <p className="text-sm text-slate-600">{result.baseline.label}</p>
          <p className="text-2xl font-semibold text-navy-900 tabular">{money(result.baseline.totals.knownTotal)}{!result.baseline.totals.isComplete && <span className="ml-2 text-sm font-normal text-amber-700">excl. unknown amounts</span>}</p>
        </div>
      </Card>

      <div className="grid gap-4 lg:grid-cols-3" role="radiogroup" aria-label="Purchasing strategy">
        {result.plans.map((option) => <StrategyCard key={option.strategy} plan={option} selected={option.strategy === plan.strategy} onSelect={() => setSelected(option.strategy)} />)}
        {result.lockedStrategies.map((locked) => {
          const Icon = strategyIcons[locked.strategy]
          return (
            <div key={locked.strategy} className="flex flex-col rounded-2xl border border-dashed border-slate-300 bg-slate-50/70 p-5">
              <span className="flex items-center gap-2 font-semibold text-slate-500"><Icon className="size-5" /> {locked.title} <Lock className="size-4" /></span>
              <p className="mt-2 flex-1 text-sm text-slate-500">{locked.reason}</p>
              {session.permissions.canManageBilling
                ? <LinkButton to="/app/billing" size="sm" variant="accent" className="mt-4 self-start">View plans</LinkButton>
                : <p className="mt-4 text-xs text-slate-500">Ask your organization owner to upgrade.</p>}
            </div>
          )
        })}
      </div>

      <div className="grid gap-6 xl:grid-cols-[1fr_22rem]">
        <Card padded={false} className="min-w-0">
          <div className="px-5 pt-5 sm:px-6 sm:pt-6"><CardHeader title={`${plan.title}: what to buy where`} description={plan.summary} /></div>
          <Table>
            <thead><tr><Th>Item</Th><Th>Supplier</Th><Th>Product</Th><Th align="right">Packages</Th><Th align="right">Price / pkg</Th><Th align="right">Line cost</Th><Th>Delivery</Th><Th>Data</Th></tr></thead>
            <tbody>
              {plan.lines.map((line) => (
                <tr key={line.itemId}>
                  <Td className="max-w-52"><span className="font-medium text-navy-900">{line.description}</span><span className="block text-xs text-slate-500">Need {qty(line.quantity)} {unitLabel(line.unit)}</span></Td>
                  <Td className="whitespace-nowrap">{line.supplierName}</Td>
                  <Td className="max-w-56"><span className="line-clamp-2" title={line.productTitle}>{line.productTitle}</span>{line.isSubstitute && <Badge tone="violet" className="mt-1">Approved substitute</Badge>}</Td>
                  <Td align="right">{line.packs}</Td>
                  <Td align="right">{money(line.packPrice)}</Td>
                  <Td align="right" className="font-medium text-navy-900">{money(line.lineCost)}</Td>
                  <Td className="whitespace-nowrap">{line.deliveryDays === null ? <Unknown /> : deliveryLabel(line.deliveryDays, line.deliveryDays)}</Td>
                  <Td><DataStatusBadge status={line.dataStatus} /></Td>
                </tr>
              ))}
            </tbody>
          </Table>
          {plan.unfilledItems.length > 0 && (
            <div className="px-5 py-4 sm:px-6">
              <Notice tone="warning" title={`${plan.unfilledItems.length} item(s) are not in this plan`}>No usable offer was found for: {plan.unfilledItems.join('; ')}. They are not included in any total.</Notice>
            </div>
          )}
        </Card>

        <div className="min-w-0 space-y-6">
          <Card>
            <CardHeader title="Plan total" />
            <TotalsBlock totals={plan.totals} />
            {plan.savings !== null && (
              <div className={clsx('mt-4 rounded-xl px-4 py-3', plan.savings > 0 ? 'bg-emerald-50' : plan.savings < 0 ? 'bg-amber-50' : 'bg-slate-50')}>
                <p className="text-xs uppercase tracking-wide text-slate-600">{plan.savings >= 0 ? 'Estimated savings vs baseline' : 'Costs more than baseline'}</p>
                <p className={clsx('mt-0.5 text-xl font-semibold tabular', plan.savings > 0 ? 'text-emerald-700' : plan.savings < 0 ? 'text-amber-800' : 'text-navy-900')}>
                  {money(Math.abs(plan.savings))}{plan.savingsPercent !== null && <span className="ml-1.5 text-sm font-normal">({Math.abs(plan.savingsPercent)}%)</span>}
                </p>
                <p className="mt-0.5 text-xs text-slate-500">Baseline {money(result.baseline.totals.knownTotal)} − plan {money(plan.totals.knownTotal)}</p>
              </div>
            )}
            {session.permissions.canEditLists
              ? <Button variant="primary" className="mt-5 w-full" icon={<ShoppingCart className="size-4" />} loading={apply.isPending} onClick={() => apply.mutate(plan.strategy)}>Add this plan to the cart</Button>
              : <p className="mt-5 text-xs text-slate-500">Viewers cannot change the cart.</p>}
            <p className="mt-2 text-xs text-slate-500">Replaces the current cart for this project. Nothing is ordered.</p>
          </Card>

          <Card>
            <CardHeader title="By supplier" />
            <ul className="divide-y divide-slate-100">
              {plan.totals.suppliers.map((supplier) => (
                <li key={supplier.supplierId} className="py-3 first:pt-0 last:pb-0">
                  <div className="flex items-baseline justify-between gap-3">
                    <span className="font-medium text-navy-900">{supplier.supplierName}</span>
                    <span className="font-semibold text-navy-900 tabular">{money(supplier.subtotal)}</span>
                  </div>
                  <p className="mt-0.5 flex flex-wrap items-center gap-x-2 text-xs text-slate-500">
                    {supplier.lineCount} item{supplier.lineCount === 1 ? '' : 's'} ·
                    <span className="inline-flex items-center gap-1"><Truck className="size-3" />{supplier.shipping === null ? <span className="text-amber-700">shipping unknown</span> : supplier.shipping === 0 ? 'no shipping charge' : `${money(supplier.shipping)} shipping`}</span>
                  </p>
                  {supplier.amountToFreeShipping !== null && <p className="mt-0.5 text-xs text-accent-700">{money(supplier.amountToFreeShipping)} more for free shipping</p>}
                  {supplier.belowMinimumOrder && <p className="mt-0.5 text-xs text-red-600">Below this supplier's minimum order</p>}
                </li>
              ))}
            </ul>
          </Card>
        </div>
      </div>

      {plan.uncertainties.length > 0 && (
        <Notice tone="warning" title="What is uncertain in this plan">
          <ul className="list-disc space-y-1 pl-4">{plan.uncertainties.map((note, index) => <li key={index}>{note}</li>)}</ul>
        </Notice>
      )}

      <Card>
        <CardHeader
          title="Explain this plan"
          description="A plain-language summary. The figures always come from the optimizer; AI may only reword them."
          action={<Button icon={<Sparkles className="size-4" />} loading={explain.isPending} onClick={() => explain.mutate(plan.strategy)}>{explanation ? 'Explain again' : 'Explain'}</Button>}
        />
        {explanation ? (
          <div className="rounded-xl bg-slate-50 p-4 text-sm leading-relaxed text-slate-700">
            <Badge tone={explanation.source === 'template' ? 'neutral' : 'blue'} className="mb-2">{explanation.source === 'template' ? 'Generated from the plan data (no language model)' : `AI wording (${explanation.source}), every figure checked against the plan`}</Badge>
            <p>{explanation.text}</p>
          </div>
        ) : explain.error instanceof ApiError ? <Notice tone="error">{explain.error.message}</Notice> : <p className="text-sm text-slate-500">Choose Explain to see why these suppliers were picked.</p>}
        <p className="mt-3 flex items-center gap-1.5 text-xs text-slate-400"><Clock className="size-3.5" /> Optimizer method: {plan.method}. Results are deterministic for the same offers.</p>
      </Card>
    </div>
  )
}

function StrategyCard({ plan, selected, onSelect }: { plan: PurchasingPlan; selected: boolean; onSelect: () => void }) {
  const Icon = strategyIcons[plan.strategy]
  return (
    <button type="button" role="radio" aria-checked={selected} onClick={onSelect}
      className={clsx('flex flex-col rounded-2xl border bg-white p-5 text-left shadow-card transition', selected ? 'border-accent-500 ring-2 ring-accent-500/30' : 'border-slate-200 hover:border-slate-300')}>
      <span className="flex items-center justify-between gap-2">
        <span className="flex items-center gap-2 font-semibold text-navy-900"><Icon className={clsx('size-5', selected ? 'text-accent-600' : 'text-slate-400')} /> {plan.title}</span>
        {selected && <Badge tone="blue">Selected</Badge>}
      </span>
      <span className="mt-3 text-2xl font-semibold tracking-tight text-navy-900 tabular">{money(plan.totals.knownTotal)}</span>
      {!plan.totals.isComplete && <span className="text-xs text-amber-700">excludes unknown amounts</span>}
      <span className="mt-3 space-y-1 text-sm text-slate-600">
        <span className="block">{plan.totals.suppliers.length} supplier{plan.totals.suppliers.length === 1 ? '' : 's'} · {plan.lines.length} item{plan.lines.length === 1 ? '' : 's'}</span>
        <span className="block">
          Arrives in {plan.maxDeliveryDays === null ? 'unknown time' : `up to ${plan.maxDeliveryDays} day${plan.maxDeliveryDays === 1 ? '' : 's'}`}{plan.deliveryUnknown && plan.maxDeliveryDays !== null && ' (some unknown)'}
          {plan.meetsDeadline === true && <span className="text-emerald-700"> · meets deadline</span>}
          {plan.meetsDeadline === false && <span className="text-red-600"> · deadline not confirmed</span>}
        </span>
        {plan.savings !== null && (
          <span className={clsx('block font-medium', plan.savings > 0 ? 'text-emerald-700' : plan.savings < 0 ? 'text-amber-700' : 'text-slate-600')}>
            {plan.savings > 0 ? `${money(plan.savings)} below baseline` : plan.savings < 0 ? `${money(-plan.savings)} above baseline` : 'Same as baseline'}
          </span>
        )}
      </span>
    </button>
  )
}
