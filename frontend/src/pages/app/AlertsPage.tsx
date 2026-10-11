import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { BellRing, PackageSearch, Pencil, Play, Plus, Search, Tag, Trash2, TrendingDown } from 'lucide-react'
import { useEffect, useState, type FormEvent } from 'react'
import { Badge, Button, Card, ConfirmDialog, EmptyState, ErrorState, Input, Modal, Notice, PageHeader, Select, SkeletonRows, Table, Td, Th, UpgradeNotice, useToast } from '../../components/ui'
import { api, ApiError, errorMessage } from '../../lib/api'
import { useSession } from '../../lib/auth'
import { formatDateTime, timeAgo, unitLabel, unitMoney } from '../../lib/format'
import type { CatalogProduct, PriceAlert, Supplier } from '../../lib/types'

type AlertType = PriceAlert['type']
type AlertBody = { type: AlertType; productId: string; supplierId: string | null; targetPrice: number | null; isActive: boolean }
type RunResult = { alertsChecked: number; alertsTriggered: number; providerIssues: number; ranAt: string }
type PickedProduct = { id: string; name: string; unit: string }

const RULES: { type: AlertType; name: string; explanation: string; icon: typeof Tag }[] = [
  { type: 'PriceBelow', name: 'Price drop', explanation: 'Notify when the price per unit falls to or below a target', icon: TrendingDown },
  { type: 'BackInStock', name: 'Back in stock', explanation: 'Notify when a product that was out of stock becomes available', icon: PackageSearch },
  { type: 'BetterOffer', name: 'Better offer', explanation: 'Notify when another supplier beats your current supplier, or a price you enter', icon: BellRing },
]
const ruleName = (type: AlertType) => RULES.find((rule) => rule.type === type)?.name ?? type

function ruleText(alert: PriceAlert) {
  const per = `per ${unitLabel(alert.unit)}`
  const price = alert.targetPrice === null ? null : `${unitMoney(alert.targetPrice)} ${per}`
  switch (alert.type) {
    case 'PriceBelow':
      return price ? `Price falls to ${price} or below` : 'Price falls to the target (no target set)'
    case 'BackInStock':
      return 'Becomes available after being out of stock'
    case 'BetterOffer':
      if (alert.supplierName && price) return `Another supplier beats ${alert.supplierName} or ${price}`
      if (alert.supplierName) return `Another supplier beats ${alert.supplierName}`
      return price ? `A supplier beats ${price}` : 'A supplier offers a better price'
  }
}

function scopeText(alert: PriceAlert) {
  if (alert.type === 'BetterOffer') return alert.supplierName ? `Compared against ${alert.supplierName}` : 'All suppliers'
  return alert.supplierName ?? 'Any supplier'
}

function useDebounced(value: string, delay = 300) {
  const [debounced, setDebounced] = useState(value)
  useEffect(() => {
    const timer = window.setTimeout(() => setDebounced(value), delay)
    return () => window.clearTimeout(timer)
  }, [value, delay])
  return debounced
}

function ProductPicker({ value, onChange, error }: { value: PickedProduct | null; onChange: (product: PickedProduct | null) => void; error: string | null }) {
  const [text, setText] = useState('')
  const term = useDebounced(text.trim())
  const search = useQuery({
    queryKey: ['catalog-products', term],
    queryFn: () => api.get<CatalogProduct[]>(`/api/catalog/products?query=${encodeURIComponent(term)}`),
    enabled: !value && term.length >= 2,
    staleTime: 60_000,
  })

  if (value) {
    return (
      <div>
        <p className="mb-1.5 text-sm font-medium text-navy-800">Product</p>
        <div className="flex items-center justify-between gap-3 rounded-lg border border-slate-300 bg-slate-50 px-3 py-2">
          <div className="min-w-0">
            <p className="truncate text-sm font-medium text-navy-900">{value.name}</p>
            <p className="text-xs text-slate-500">Priced per {unitLabel(value.unit)}</p>
          </div>
          <Button size="sm" onClick={() => onChange(null)}>
            Change
          </Button>
        </div>
      </div>
    )
  }

  return (
    <div>
      <div className="relative">
        <Input
          label="Product"
          type="search"
          autoComplete="off"
          placeholder="Search the catalog by name, category, brand or model"
          value={text}
          error={error}
          onChange={(event) => setText(event.target.value)}
          hint="Type at least 2 characters, then pick a product from the results."
        />
        <Search className="pointer-events-none absolute right-3 top-[2.35rem] size-4 text-slate-400" aria-hidden />
      </div>
      {term.length >= 2 && (
        <div className="mt-2 max-h-56 overflow-y-auto rounded-lg border border-slate-200" role="listbox" aria-label="Catalog results">
          {search.isLoading ? (
            <p className="px-3 py-3 text-sm text-slate-500" role="status">
              Searching the catalog…
            </p>
          ) : search.isError ? (
            <p className="px-3 py-3 text-sm text-red-700">{errorMessage(search.error)}</p>
          ) : !search.data?.length ? (
            <p className="px-3 py-3 text-sm text-slate-500">No catalog products match &ldquo;{term}&rdquo;.</p>
          ) : (
            search.data.map((product) => (
              <button
                key={product.id}
                type="button"
                role="option"
                aria-selected={false}
                onClick={() => onChange({ id: product.id, name: product.name, unit: product.baseUnit })}
                className="block w-full border-b border-slate-100 px-3 py-2 text-left last:border-0 hover:bg-accent-50 focus:bg-accent-50 focus:outline-none"
              >
                <span className="block text-sm font-medium text-navy-900">{product.name}</span>
                <span className="block text-xs text-slate-500">
                  {[product.category, product.brand, `per ${unitLabel(product.baseUnit)}`].filter(Boolean).join(' · ')}
                </span>
              </button>
            ))
          )}
        </div>
      )}
    </div>
  )
}

function AlertModal({ alert, suppliers, onClose, onSaved }: { alert: PriceAlert | null; suppliers: Supplier[]; onClose: () => void; onSaved: () => void }) {
  const toast = useToast()
  const [type, setType] = useState<AlertType>(alert?.type ?? 'PriceBelow')
  const [product, setProduct] = useState<PickedProduct | null>(alert ? { id: alert.productId, name: alert.productName, unit: alert.unit } : null)
  const [supplierId, setSupplierId] = useState(alert?.supplierId ?? '')
  const [price, setPrice] = useState(alert?.targetPrice === null || alert?.targetPrice === undefined ? '' : String(alert.targetPrice))
  const [errors, setErrors] = useState<{ product?: string; price?: string; supplier?: string }>({})

  const save = useMutation({
    mutationFn: (body: AlertBody) => (alert ? api.put(`/api/price-alerts/${alert.id}`, body) : api.post('/api/price-alerts', body)),
    onSuccess: () => {
      toast.success(alert ? 'Price alert updated.' : 'Price alert created.')
      onSaved()
    },
  })

  const rule = RULES.find((entry) => entry.type === type)!
  const showPrice = type !== 'BackInStock'
  const per = product ? ` per ${unitLabel(product.unit)}` : ' per unit'

  const submit = (event: FormEvent) => {
    event.preventDefault()
    const next: typeof errors = {}
    if (!product) next.product = 'Choose a catalog product.'
    let targetPrice: number | null = null
    if (showPrice && price.trim() !== '') {
      targetPrice = Number(price)
      if (!Number.isFinite(targetPrice) || targetPrice <= 0) next.price = 'Enter a price greater than zero.'
    }
    // Mirrors the server's rules so the reason is shown next to the field.
    if (type === 'PriceBelow' && price.trim() === '') next.price = 'Enter the target price per unit.'
    if (type === 'BetterOffer' && !supplierId && price.trim() === '') next.supplier = 'Choose your current supplier or enter the price you pay now.'
    setErrors(next)
    if (!product || Object.keys(next).length > 0) return
    save.mutate({ type, productId: product.id, supplierId: supplierId || null, targetPrice, isActive: alert?.isActive ?? true })
  }

  const supplierLabel = type === 'BetterOffer' ? 'Your current supplier' : 'Supplier (optional)'
  const supplierHint =
    type === 'BetterOffer'
      ? 'The alert triggers when a different supplier offers a lower price per unit than this one.'
      : type === 'BackInStock'
        ? 'Leave as any supplier to be told when anyone has it available.'
        : 'Leave as any supplier to watch every supplier for this product.'
  const priceLabel = type === 'BetterOffer' ? `Price you pay now${per} (USD)` : `Target price${per} (USD)`

  return (
    <Modal
      open
      onClose={onClose}
      title={alert ? 'Edit price alert' : 'New price alert'}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" type="submit" form="alert-form" loading={save.isPending}>
            {alert ? 'Save alert' : 'Create alert'}
          </Button>
        </>
      }
    >
      <form id="alert-form" onSubmit={submit} className="space-y-4" noValidate>
        <Select label="Alert type" value={type} onChange={(event) => setType(event.target.value as AlertType)} hint={`${rule.explanation}.`}>
          {RULES.map((entry) => (
            <option key={entry.type} value={entry.type}>
              {entry.name}
            </option>
          ))}
        </Select>
        <ProductPicker
          value={product}
          error={errors.product ?? null}
          onChange={(picked) => {
            setProduct(picked)
            setErrors((current) => ({ ...current, product: undefined }))
          }}
        />
        <Select label={supplierLabel} value={supplierId} error={errors.supplier} hint={supplierHint} onChange={(event) => setSupplierId(event.target.value)}>
          <option value="">{type === 'BetterOffer' ? 'No current supplier' : 'Any supplier'}</option>
          {suppliers.map((supplier) => (
            <option key={supplier.id} value={supplier.id}>
              {supplier.name}
            </option>
          ))}
          {alert?.supplierId && !suppliers.some((supplier) => supplier.id === alert.supplierId) && <option value={alert.supplierId}>{alert.supplierName ?? 'Current selection'}</option>}
        </Select>
        {showPrice && (
          <Input
            label={priceLabel}
            type="number"
            inputMode="decimal"
            min={0}
            step="0.0001"
            placeholder="0.00"
            value={price}
            error={errors.price}
            onChange={(event) => setPrice(event.target.value)}
            hint={type === 'BetterOffer' ? 'Optional when a current supplier is chosen. Prices are compared per unit, not per package.' : 'Required. Prices are compared per unit, not per package.'}
          />
        )}
        {save.isError && (
          <Notice tone="error" title={save.error instanceof ApiError && save.error.code === 'plan_limit' ? 'Not included in your plan' : undefined}>
            {errorMessage(save.error)}
          </Notice>
        )}
      </form>
    </Modal>
  )
}

function ActiveSwitch({ checked, disabled, label, reason, onChange }: { checked: boolean; disabled: boolean; label: string; reason?: string; onChange: () => void }) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      title={reason}
      disabled={disabled}
      onClick={onChange}
      className={`relative inline-flex h-6 w-11 shrink-0 items-center rounded-full transition-colors focus:outline-none focus-visible:ring-2 focus-visible:ring-accent-500/40 disabled:cursor-not-allowed disabled:opacity-50 ${checked ? 'bg-accent-600' : 'bg-slate-300'}`}
    >
      <span className={`inline-block size-5 rounded-full bg-white shadow transition-transform ${checked ? 'translate-x-5.5' : 'translate-x-0.5'}`} />
    </button>
  )
}

export function AlertsPage() {
  const session = useSession()
  const queryClient = useQueryClient()
  const toast = useToast()
  const [editor, setEditor] = useState<{ alert: PriceAlert | null } | null>(null)
  const [deleting, setDeleting] = useState<PriceAlert | null>(null)
  const [runResult, setRunResult] = useState<RunResult | null>(null)

  const alerts = useQuery({ queryKey: ['price-alerts'], queryFn: () => api.get<PriceAlert[]>('/api/price-alerts') })
  const suppliers = useQuery({ queryKey: ['suppliers'], queryFn: () => api.get<Supplier[]>('/api/suppliers') })

  const planIncluded = session.plan.priceAlerts
  const canEdit = session.permissions.canEditLists
  const canCreate = canEdit && planIncluded

  const refresh = () => {
    queryClient.invalidateQueries({ queryKey: ['price-alerts'] })
    queryClient.invalidateQueries({ queryKey: ['notifications'] })
    queryClient.invalidateQueries({ queryKey: ['dashboard'] })
  }

  const run = useMutation({
    mutationFn: () => api.post<RunResult>('/api/price-alerts/run'),
    onSuccess: (result) => {
      setRunResult(result)
      toast.success(`Checked ${result.alertsChecked} alert${result.alertsChecked === 1 ? '' : 's'}; ${result.alertsTriggered} triggered.`)
      refresh()
    },
    onError: (error) => toast.error(errorMessage(error)),
  })

  const toggle = useMutation({
    mutationFn: (alert: PriceAlert) => {
      const body: AlertBody = { type: alert.type, productId: alert.productId, supplierId: alert.supplierId, targetPrice: alert.targetPrice, isActive: !alert.isActive }
      return api.put(`/api/price-alerts/${alert.id}`, body)
    },
    onSuccess: (_, alert) => {
      toast.success(alert.isActive ? 'Alert paused.' : 'Alert activated.')
      refresh()
    },
    onError: (error) => toast.error(errorMessage(error)),
  })

  const remove = useMutation({
    mutationFn: (alert: PriceAlert) => api.delete(`/api/price-alerts/${alert.id}`),
    onSuccess: () => {
      toast.success('Price alert deleted.')
      setDeleting(null)
      refresh()
    },
    onError: (error) => toast.error(errorMessage(error)),
  })

  const anyMock = suppliers.data?.some((supplier) => supplier.mode === 'Mock') ?? false
  const newButton = canCreate ? (
    <Button variant="primary" icon={<Plus className="size-4" aria-hidden />} onClick={() => setEditor({ alert: null })}>
      New alert
    </Button>
  ) : undefined

  return (
    <>
      <PageHeader
        title="Price alerts"
        description="Get a notification when a catalog product reaches a price, comes back in stock, or is offered for less by another supplier."
        actions={
          <>
            {canCreate && (
              <Button icon={<Play className="size-4" aria-hidden />} loading={run.isPending} onClick={() => run.mutate()}>
                Run alert check now
              </Button>
            )}
            {newButton}
          </>
        }
      />

      <div className="space-y-6">
        {!planIncluded && (
          <UpgradeNotice
            feature="Price alerts"
            reason="New alerts cannot be created or run on your current plan. Alerts you already have are listed below and can be paused or deleted."
            canManageBilling={session.permissions.canManageBilling}
          />
        )}
        {planIncluded && !canEdit && <Notice tone="info">Your role can view price alerts but cannot create, change or run them.</Notice>}

        {canCreate && <p className="-mt-2 text-sm text-slate-500">In production this check runs on a schedule. This button runs it once, now.</p>}

        {runResult && (
          <Notice
            tone={runResult.providerIssues > 0 ? 'warning' : 'success'}
            title={`Alert check finished ${formatDateTime(runResult.ranAt)}`}
            action={
              <Button size="sm" variant="ghost" onClick={() => setRunResult(null)}>
                Dismiss
              </Button>
            }
          >
            {runResult.alertsChecked} alert{runResult.alertsChecked === 1 ? '' : 's'} checked, {runResult.alertsTriggered} triggered.
            {runResult.alertsTriggered > 0 && ' Triggered alerts appear in your notifications.'}
            {runResult.providerIssues > 0 &&
              ` ${runResult.providerIssues} supplier${runResult.providerIssues === 1 ? '' : 's'} could not be refreshed, so some alerts were checked against earlier prices or not at all.`}
          </Notice>
        )}

        <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
          {RULES.map((rule) => (
            <Card key={rule.type} className="flex items-start gap-3">
              <div className="flex size-10 shrink-0 items-center justify-center rounded-xl bg-accent-50 text-accent-600">
                <rule.icon className="size-5" aria-hidden />
              </div>
              <div className="min-w-0">
                <h2 className="text-sm font-semibold text-navy-900">{rule.name}</h2>
                <p className="mt-0.5 text-sm text-slate-600">{rule.explanation}.</p>
              </div>
            </Card>
          ))}
        </div>

        {anyMock && (
          <Notice tone="info" title="Current prices include demo data">
            Suppliers in Mock mode return simulated DEMO prices and stock. A current price shown here from one of those suppliers is not a live quote, and alerts that trigger on it are demonstrations.
          </Notice>
        )}

        {alerts.isLoading ? (
          <Card>
            <SkeletonRows rows={5} />
          </Card>
        ) : alerts.isError ? (
          <ErrorState error={alerts.error} onRetry={() => alerts.refetch()} />
        ) : !alerts.data?.length ? (
          <EmptyState
            icon={<Tag className="size-6" aria-hidden />}
            title="No price alerts yet"
            description={canCreate ? 'Create an alert for a product you buy often. You will be notified in the app when its rule is met.' : 'When alerts exist for your organization they are listed here.'}
            action={newButton}
          />
        ) : (
          <Card padded={false} className="overflow-hidden">
            <Table>
              <thead>
                <tr>
                  <Th>Product</Th>
                  <Th>Rule</Th>
                  <Th>Supplier scope</Th>
                  <Th>Current best price</Th>
                  <Th>Last checked</Th>
                  <Th>Last triggered</Th>
                  <Th align="center">Active</Th>
                  <Th />
                </tr>
              </thead>
              <tbody>
                {alerts.data.map((alert) => {
                  const pending = toggle.isPending && toggle.variables?.id === alert.id
                  // Pausing is always allowed; turning an alert back on needs the plan feature.
                  const toggleBlocked = !canEdit || (!alert.isActive && !planIncluded)
                  return (
                    <tr key={alert.id} className={alert.isActive ? undefined : 'bg-slate-50/60'}>
                      <Td className="max-w-xs whitespace-normal">
                        <span className="font-medium text-navy-900">{alert.productName}</span>
                        <span className="mt-1 block">
                          <Badge tone={alert.type === 'PriceBelow' ? 'blue' : alert.type === 'BackInStock' ? 'green' : 'violet'}>{ruleName(alert.type)}</Badge>
                        </span>
                      </Td>
                      <Td className="max-w-xs whitespace-normal">{ruleText(alert)}</Td>
                      <Td>{scopeText(alert)}</Td>
                      <Td>
                        {alert.currentBestUnitPrice === null ? (
                          <span className="text-slate-500">No current price</span>
                        ) : (
                          <>
                            <span className="font-medium text-navy-900 tabular">
                              {unitMoney(alert.currentBestUnitPrice)} / {unitLabel(alert.unit)}
                            </span>
                            <span className="block text-xs text-slate-500">{alert.currentBestSupplier ?? 'Supplier not reported'}</span>
                          </>
                        )}
                      </Td>
                      <Td>{alert.lastCheckedAt ? <span title={formatDateTime(alert.lastCheckedAt)}>{timeAgo(alert.lastCheckedAt)}</span> : <span className="text-slate-500">Not checked yet</span>}</Td>
                      <Td>
                        {alert.lastTriggeredAt ? (
                          <>
                            <span title={formatDateTime(alert.lastTriggeredAt)}>{timeAgo(alert.lastTriggeredAt)}</span>
                            {alert.lastTriggeredPrice !== null && (
                              <span className="block text-xs text-slate-500">
                                at {unitMoney(alert.lastTriggeredPrice)} / {unitLabel(alert.unit)}
                              </span>
                            )}
                          </>
                        ) : (
                          <span className="text-slate-500">Never</span>
                        )}
                      </Td>
                      <Td align="center">
                        <ActiveSwitch
                          checked={alert.isActive}
                          disabled={toggleBlocked || pending}
                          label={`${alert.isActive ? 'Pause' : 'Activate'} alert for ${alert.productName}`}
                          reason={!canEdit ? 'Your role cannot change alerts.' : !alert.isActive && !planIncluded ? 'Price alerts are not included in your plan.' : undefined}
                          onChange={() => toggle.mutate(alert)}
                        />
                      </Td>
                      <Td align="right">
                        {canEdit && (
                          <span className="inline-flex gap-1">
                            {planIncluded && <Button variant="ghost" size="sm" aria-label={`Edit alert for ${alert.productName}`} icon={<Pencil className="size-4" aria-hidden />} onClick={() => setEditor({ alert })} />}
                            <Button variant="ghost" size="sm" aria-label={`Delete alert for ${alert.productName}`} icon={<Trash2 className="size-4 text-red-600" aria-hidden />} onClick={() => setDeleting(alert)} />
                          </span>
                        )}
                      </Td>
                    </tr>
                  )
                })}
              </tbody>
            </Table>
          </Card>
        )}
      </div>

      {editor && (
        <AlertModal
          key={editor.alert?.id ?? 'new'}
          alert={editor.alert}
          suppliers={suppliers.data ?? []}
          onClose={() => setEditor(null)}
          onSaved={() => {
            setEditor(null)
            refresh()
          }}
        />
      )}
      <ConfirmDialog open={!!deleting} onClose={() => setDeleting(null)} onConfirm={() => deleting && remove.mutate(deleting)} title="Delete this price alert?" confirmLabel="Delete alert" danger loading={remove.isPending}>
        {deleting && (
          <>
            The alert for <strong>{deleting.productName}</strong> will be removed and will no longer be checked. This cannot be undone.
          </>
        )}
      </ConfirmDialog>
    </>
  )
}
