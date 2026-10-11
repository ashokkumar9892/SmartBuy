import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Check, CircleAlert, CircleCheck, ExternalLink, Pencil, Star, Store, X } from 'lucide-react'
import { useState, type FormEvent, type ReactNode } from 'react'
import { Badge, Button, Card, Checkbox, EmptyState, ErrorState, Input, Modal, Notice, PageHeader, Skeleton, Textarea, Unknown, useToast } from '../../components/ui'
import { api, errorMessage } from '../../lib/api'
import { useSession } from '../../lib/auth'
import { money } from '../../lib/format'
import type { ProviderCapabilities, Supplier } from '../../lib/types'

const KIND_LABELS: Record<string, string> = {
  Retailer: 'Retailer',
  Marketplace: 'Marketplace',
  LocalSupplier: 'Local supplier',
  ShopifyMerchant: 'Shopify merchant',
  WooCommerceMerchant: 'WooCommerce merchant',
}

const CAPABILITIES: { key: keyof ProviderCapabilities; label: string }[] = [
  { key: 'productSearch', label: 'Product search' },
  { key: 'inventory', label: 'Inventory' },
  { key: 'purchaseLinks', label: 'Purchase links' },
  { key: 'cartBuilding', label: 'Cart building' },
  { key: 'orderPlacement', label: 'Order placement' },
]

const isWebUrl = (value: string | null): value is string => !!value && /^https?:\/\//i.test(value)

function SectionLabel({ children }: { children: ReactNode }) {
  return <h3 className="mb-2 text-xs font-semibold uppercase tracking-wide text-slate-500">{children}</h3>
}

function Term({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex items-baseline justify-between gap-4 text-sm">
      <dt className="text-slate-500">{label}</dt>
      <dd className="min-w-0 break-words text-right text-navy-900">{children}</dd>
    </div>
  )
}

function SavedDetailsModal({ supplier, onClose }: { supplier: Supplier; onClose: () => void }) {
  const queryClient = useQueryClient()
  const toast = useToast()
  const [isPreferred, setIsPreferred] = useState(supplier.isPreferred)
  const [accountNumber, setAccountNumber] = useState(supplier.accountNumber ?? '')
  const [orderEmail, setOrderEmail] = useState(supplier.orderEmail ?? '')
  const [notes, setNotes] = useState(supplier.notes ?? '')
  const [emailError, setEmailError] = useState<string | null>(null)

  const done = (message: string) => {
    queryClient.invalidateQueries({ queryKey: ['suppliers'] })
    toast.success(message)
    onClose()
  }

  const save = useMutation({
    mutationFn: () =>
      api.put(`/api/suppliers/${supplier.id}/saved`, {
        isPreferred,
        accountNumber: accountNumber.trim() || null,
        orderEmail: orderEmail.trim() || null,
        notes: notes.trim() || null,
      }),
    onSuccess: () => done(`Your details for ${supplier.name} were saved.`),
  })
  const remove = useMutation({
    mutationFn: () => api.delete(`/api/suppliers/${supplier.id}/saved`),
    onSuccess: () => done(`Your saved details for ${supplier.name} were removed.`),
  })

  const submit = (event: FormEvent) => {
    event.preventDefault()
    const email = orderEmail.trim()
    if (email && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email)) {
      setEmailError('Enter a valid email address, or leave this blank.')
      return
    }
    setEmailError(null)
    save.mutate()
  }

  const busy = save.isPending || remove.isPending
  const failure = save.isError ? save.error : remove.isError ? remove.error : null

  return (
    <Modal
      open
      onClose={onClose}
      title={`Your account details: ${supplier.name}`}
      description="Stored for your organization only. Nothing here is sent to the supplier."
      footer={
        <>
          {supplier.isSaved && (
            <Button variant="danger" className="mr-auto" loading={remove.isPending} disabled={save.isPending} onClick={() => remove.mutate()}>
              Remove saved details
            </Button>
          )}
          <Button onClick={onClose} disabled={busy}>
            Cancel
          </Button>
          <Button variant="primary" type="submit" form="saved-supplier-form" loading={save.isPending} disabled={remove.isPending}>
            Save details
          </Button>
        </>
      }
    >
      <form id="saved-supplier-form" onSubmit={submit} className="space-y-4" noValidate>
        <Checkbox
          label="Preferred supplier"
          description="Preferred suppliers get a small credit in the Balanced strategy"
          checked={isPreferred}
          onChange={(event) => setIsPreferred(event.target.checked)}
        />
        <Input label="Account number" maxLength={100} autoComplete="off" value={accountNumber} onChange={(event) => setAccountNumber(event.target.value)} />
        <Input
          label="Order email"
          type="email"
          maxLength={320}
          autoComplete="off"
          value={orderEmail}
          error={emailError}
          onChange={(event) => setOrderEmail(event.target.value)}
          hint="Purchase orders are emailed here when email delivery is configured"
        />
        <Textarea label="Notes" rows={3} maxLength={1000} value={notes} onChange={(event) => setNotes(event.target.value)} />
        {failure && <Notice tone="error">{errorMessage(failure)}</Notice>}
      </form>
    </Modal>
  )
}

function SupplierCard({ supplier, canManage, onEdit }: { supplier: Supplier; canManage: boolean; onEdit: () => void }) {
  const mock = supplier.mode === 'Mock'
  const capabilities = supplier.capabilities
  const hasSavedValues = !!(supplier.accountNumber || supplier.orderEmail || supplier.notes)

  return (
    <Card className="flex flex-col gap-5">
      <div>
        <div className="flex flex-wrap items-start justify-between gap-2">
          <div className="min-w-0">
            <h2 className="text-base font-semibold text-navy-900">{supplier.name}</h2>
            <p className="text-sm text-slate-500">{KIND_LABELS[supplier.kind] ?? supplier.kind}</p>
          </div>
          <div className="flex flex-wrap items-center gap-1.5">
            {supplier.isPreferred && (
              <Badge tone="amber" title="Marked as preferred by your organization">
                <Star className="size-3 fill-current" aria-hidden /> Preferred
              </Badge>
            )}
            {mock ? (
              <Badge tone="violet" title="Mock integration. Prices and stock from this supplier are simulated demo data, not live quotes.">
                DEMO DATA
              </Badge>
            ) : (
              <Badge tone="green" title="Connected through an approved supplier API.">
                LIVE
              </Badge>
            )}
          </div>
        </div>
        <p className={`mt-3 flex items-start gap-2 text-sm ${supplier.isHealthy ? 'text-emerald-700' : 'text-amber-800'}`}>
          {supplier.isHealthy ? <CircleCheck className="mt-0.5 size-4 shrink-0" aria-hidden /> : <CircleAlert className="mt-0.5 size-4 shrink-0" aria-hidden />}
          <span>
            <span className="font-medium">{supplier.isHealthy ? 'Integration healthy' : 'Integration needs attention'}</span>
            {supplier.statusMessage && <span className="block text-slate-600">{supplier.statusMessage}</span>}
          </span>
        </p>
        {isWebUrl(supplier.websiteUrl) && (
          <a href={supplier.websiteUrl} target="_blank" rel="noopener noreferrer" className="mt-2 inline-flex items-center gap-1 text-sm font-medium text-accent-700 hover:underline">
            Supplier website <ExternalLink className="size-3.5" aria-hidden />
          </a>
        )}
      </div>

      <div>
        <SectionLabel>Integration capabilities</SectionLabel>
        {capabilities ? (
          <ul className="space-y-1.5 text-sm">
            {CAPABILITIES.map(({ key, label }) => {
              const supported = capabilities[key]
              return (
                <li key={key} className="flex items-center gap-2">
                  {supported ? <Check className="size-4 shrink-0 text-emerald-600" aria-hidden /> : <X className="size-4 shrink-0 text-slate-400" aria-hidden />}
                  <span className={supported ? 'text-navy-900' : 'text-slate-500'}>{label}</span>
                  <span className="ml-auto text-xs text-slate-500">{supported ? 'Supported' : 'Not supported'}</span>
                </li>
              )
            })}
          </ul>
        ) : (
          <p className="text-sm text-slate-500">Capabilities are not reported for this supplier because no integration adapter is registered.</p>
        )}
      </div>

      <div>
        <SectionLabel>Shipping terms</SectionLabel>
        <dl className="space-y-1.5">
          <Term label="Shipping">
            {supplier.shippingFlatFee === null ? <Unknown>Shipping not published</Unknown> : supplier.shippingFlatFee === 0 ? 'Free' : `${money(supplier.shippingFlatFee)} flat fee`}
          </Term>
          {supplier.shippingFlatFee !== null && supplier.shippingFlatFee > 0 && (
            <Term label="Free shipping">{supplier.freeShippingThreshold === null ? 'No free-shipping threshold published' : `On orders of ${money(supplier.freeShippingThreshold)} or more`}</Term>
          )}
          <Term label="Minimum order">{supplier.minimumOrderAmount === null ? 'None published' : money(supplier.minimumOrderAmount)}</Term>
        </dl>
      </div>

      {supplier.integrationNotes && (
        <div>
          <SectionLabel>Integration notes</SectionLabel>
          <p className="whitespace-pre-line text-sm text-slate-600">{supplier.integrationNotes}</p>
        </div>
      )}

      <div className="mt-auto border-t border-slate-200 pt-4">
        <div className="mb-2 flex items-center justify-between gap-3">
          <h3 className="text-xs font-semibold uppercase tracking-wide text-slate-500">Your account details</h3>
          {canManage && (
            <Button size="sm" icon={<Pencil className="size-3.5" aria-hidden />} onClick={onEdit} aria-label={`${supplier.isSaved ? 'Edit' : 'Add'} your account details for ${supplier.name}`}>
              {supplier.isSaved ? 'Edit' : 'Add'}
            </Button>
          )}
        </div>
        {supplier.isSaved && hasSavedValues ? (
          <dl className="space-y-1.5">
            {supplier.accountNumber && <Term label="Account number">{supplier.accountNumber}</Term>}
            {supplier.orderEmail && <Term label="Order email">{supplier.orderEmail}</Term>}
            {supplier.notes && (
              <div className="text-sm">
                <dt className="text-slate-500">Notes</dt>
                <dd className="mt-0.5 whitespace-pre-line break-words text-navy-900">{supplier.notes}</dd>
              </div>
            )}
          </dl>
        ) : (
          <p className="text-sm text-slate-500">
            {supplier.isSaved ? 'Saved with no account number, order email or notes.' : 'No account number or order email saved for this supplier.'}
            {!canManage && ' Only purchasing managers and above can change these.'}
          </p>
        )}
      </div>
    </Card>
  )
}

export function SuppliersPage() {
  const session = useSession()
  const [editing, setEditing] = useState<Supplier | null>(null)
  const suppliers = useQuery({ queryKey: ['suppliers'], queryFn: () => api.get<Supplier[]>('/api/suppliers') })
  const canManage = session.permissions.canManagePurchasing

  return (
    <>
      <PageHeader title="Suppliers" description="The suppliers FindMyPrice compares, how each one is connected, and the account details your organization keeps for them." />

      <div className="space-y-6">
        <Notice tone="info" title="Mock integrations serve demo data">
          Integrations in &ldquo;Mock&rdquo; mode serve simulated DEMO data. Live prices appear only for suppliers connected through an approved API.
        </Notice>

        {suppliers.isLoading ? (
          <div className="grid grid-cols-1 gap-6 md:grid-cols-2 xl:grid-cols-3" role="status" aria-label="Loading suppliers">
            {Array.from({ length: 3 }, (_, index) => (
              <Card key={index} className="space-y-4">
                <Skeleton className="h-6 w-2/3" />
                <Skeleton className="h-4 w-1/3" />
                <Skeleton className="h-28 w-full" />
                <Skeleton className="h-16 w-full" />
              </Card>
            ))}
          </div>
        ) : suppliers.isError ? (
          <ErrorState error={suppliers.error} onRetry={() => suppliers.refetch()} />
        ) : !suppliers.data?.length ? (
          <EmptyState icon={<Store className="size-6" aria-hidden />} title="No suppliers are enabled" description="No supplier integrations are enabled on this server, so there is nothing to compare yet. A platform administrator can enable suppliers." />
        ) : (
          <div className="grid grid-cols-1 gap-6 md:grid-cols-2 xl:grid-cols-3">
            {suppliers.data.map((supplier) => (
              <SupplierCard key={supplier.id} supplier={supplier} canManage={canManage} onEdit={() => setEditing(supplier)} />
            ))}
          </div>
        )}

        <p className="text-xs text-slate-500">FindMyPrice does not place orders with any supplier. Order placement is listed for each integration so that this is explicit.</p>
      </div>

      {editing && <SavedDetailsModal key={editing.id} supplier={editing} onClose={() => setEditing(null)} />}
    </>
  )
}
