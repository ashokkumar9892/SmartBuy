import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Download, ExternalLink, FileText, Minus, Plus, ShoppingCart, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { Badge, Button, Card, ConfirmDialog, DataStatusBadge, EmptyState, ErrorState, Modal, Notice, SkeletonRows, StatusBadge, Table, Td, Th, Unknown, UpgradeNotice, useToast } from '../../components/ui'
import { api, errorMessage } from '../../lib/api'
import { useSession } from '../../lib/auth'
import { formatDate, money, packLabel } from '../../lib/format'
import type { Cart, CartLine, CartSupplier, ProjectDetail, PurchaseLink, PurchaseOrder } from '../../lib/types'
import { TotalsBlock } from './ProjectCompare'

export function CartTab({ project, onGoToPlan, onGoToOrders }: { project: ProjectDetail; onGoToPlan: () => void; onGoToOrders: () => void }) {
  const session = useSession()
  const toast = useToast()
  const queryClient = useQueryClient()
  const canEdit = session.permissions.canEditLists
  const [clearing, setClearing] = useState(false)
  const [link, setLink] = useState<{ link: PurchaseLink; line: CartLine; supplier: string } | null>(null)
  const { data: cart, isLoading, error, refetch } = useQuery({ queryKey: ['cart', project.id], queryFn: () => api.get<Cart>(`/api/projects/${project.id}/cart`) })
  const setCart = (next: Cart) => queryClient.setQueryData(['cart', project.id], next)
  const onError = (e: unknown) => toast.error(errorMessage(e))

  const update = useMutation({ mutationFn: ({ id, packs }: { id: string; packs: number }) => api.put<Cart>(`/api/cart-items/${id}`, { packs }), onSuccess: setCart, onError })
  const remove = useMutation({ mutationFn: (id: string) => api.delete<Cart>(`/api/cart-items/${id}`), onSuccess: setCart, onError })
  const clear = useMutation({
    mutationFn: (supplierId?: string) => api.delete<Cart>(`/api/projects/${project.id}/cart${supplierId ? `?supplierId=${supplierId}` : ''}`),
    onSuccess: (next) => { setCart(next); setClearing(false) },
    onError,
  })
  const openLink = useMutation({
    mutationFn: ({ line }: { line: CartLine; supplier: string }) => api.post<PurchaseLink>(`/api/cart-items/${line.id}/purchase-link`),
    onSuccess: (result, variables) => setLink({ link: result, ...variables }),
    onError,
  })
  const createOrders = useMutation({
    mutationFn: (supplierId?: string) => api.post<PurchaseOrder[]>(`/api/projects/${project.id}/cart/purchase-orders`, { supplierId: supplierId ?? null }),
    onSuccess: (orders) => {
      queryClient.invalidateQueries({ queryKey: ['cart', project.id] })
      queryClient.invalidateQueries({ queryKey: ['purchase-orders'] })
      queryClient.invalidateQueries({ queryKey: ['dashboard'] })
      toast.success(`${orders.length} draft purchase order${orders.length === 1 ? '' : 's'} created: ${orders.map((o) => o.poNumber).join(', ')}.`)
      onGoToOrders()
    },
    onError,
  })

  if (error) return <ErrorState error={error} onRetry={refetch} />
  if (isLoading || !cart) return <Card><SkeletonRows rows={4} /></Card>
  if (cart.lineCount === 0)
    return (
      <EmptyState icon={<ShoppingCart className="size-5" />} title="The cart is empty"
        description="Add a whole purchasing plan, or pick individual offers on the Compare tab. Lines from different suppliers share this one cart."
        action={<Button variant="primary" onClick={onGoToPlan}>Choose a purchasing plan</Button>} />
    )

  const canOrder = session.plan.purchaseOrders

  return (
    <div className="space-y-6">
      <Notice tone="info" title="You complete each purchase yourself">
        SmartBuy groups the cart by supplier and prepares each purchase. It does not check out or place orders for you. Use a supplier link, export the list, or raise a purchase order.
      </Notice>
      {cart.containsDemoData && <Notice tone="info">Prices in this cart come from DEMO supplier data and are not live quotes.</Notice>}

      <div className="grid gap-6 xl:grid-cols-[1fr_22rem]">
        <div className="min-w-0 space-y-6">
          {cart.suppliers.map((supplier) => (
            <SupplierGroup key={supplier.supplierId} supplier={supplier} canEdit={canEdit} canOrder={canOrder}
              onUpdate={(id, packs) => update.mutate({ id, packs })} onRemove={(id) => remove.mutate(id)}
              onOpenLink={(line) => openLink.mutate({ line, supplier: supplier.supplierName })} linkLoadingId={openLink.isPending ? openLink.variables?.line.id : undefined}
              onExport={() => api.download(`/api/projects/${project.id}/cart/export.csv?supplierId=${supplier.supplierId}`, 'smartbuy-shopping-list.csv').catch(onError)}
              onCreateOrder={() => createOrders.mutate(supplier.supplierId)} creating={createOrders.isPending} />
          ))}
        </div>

        <div className="space-y-4">
          <Card>
            <h2 className="mb-4 text-base font-semibold text-navy-900">Cart summary</h2>
            <ul className="mb-4 space-y-1.5 text-sm">
              {cart.suppliers.map((s) => (
                <li key={s.supplierId} className="flex justify-between gap-3"><span className="text-slate-600">{s.supplierName} — {s.lines.length} item{s.lines.length === 1 ? '' : 's'}</span><span className="tabular text-navy-900">{money(s.totals.subtotal)}</span></li>
              ))}
            </ul>
            <div className="border-t border-slate-200 pt-4"><TotalsBlock totals={cart.totals} /></div>
            <div className="mt-5 space-y-2">
              {canOrder
                ? canEdit && <Button variant="primary" className="w-full" icon={<FileText className="size-4" />} loading={createOrders.isPending} onClick={() => createOrders.mutate(undefined)}>Create purchase orders for all suppliers</Button>
                : <UpgradeNotice feature="Purchase orders" reason="Purchase orders are part of the Business plan. You can still export the list or use supplier links." canManageBilling={session.permissions.canManageBilling} />}
              <Button className="w-full" icon={<Download className="size-4" />} onClick={() => api.download(`/api/projects/${project.id}/cart/export.csv`, 'smartbuy-shopping-list.csv').catch(onError)}>Export full list (CSV)</Button>
              {canEdit && <Button variant="ghost" className="w-full" onClick={() => setClearing(true)}>Empty the cart</Button>}
            </div>
          </Card>
        </div>
      </div>

      <ConfirmDialog open={clearing} onClose={() => setClearing(false)} onConfirm={() => clear.mutate(undefined)} loading={clear.isPending} danger confirmLabel="Empty cart" title="Empty the cart?">
        All {cart.lineCount} line(s) from every supplier are removed. Your lists and comparison are not affected.
      </ConfirmDialog>

      {link && (
        <Modal open onClose={() => setLink(null)} title={`Continue at ${link.supplier}`}
          footer={<><Button onClick={() => setLink(null)}>Cancel</Button><a href={link.link.url} target="_blank" rel="noopener noreferrer" onClick={() => setLink(null)} className="inline-flex h-10 items-center gap-2 rounded-lg bg-navy-900 px-4 text-sm font-medium text-white hover:bg-navy-800">Open {new URL(link.link.url).hostname} <ExternalLink className="size-4" /></a></>}>
          <div className="space-y-3 text-sm text-slate-600">
            <p>You are leaving SmartBuy to buy <b className="text-navy-900">{link.line.packs} × {link.line.productTitle}</b>.</p>
            <Notice tone={link.link.kind === 'SupplierSearch' ? 'warning' : 'info'}>{link.link.note}</Notice>
            <p className="text-xs text-slate-500">
              {link.link.isAffiliate ? 'This is an affiliate link: SmartBuy may earn a commission from the supplier at no cost to you.' : 'This is a plain link with no affiliate or tracking tag.'}
              {' '}Price and availability are confirmed by the supplier at checkout.
            </p>
          </div>
        </Modal>
      )}
    </div>
  )
}

function SupplierGroup({ supplier, canEdit, canOrder, onUpdate, onRemove, onOpenLink, linkLoadingId, onExport, onCreateOrder, creating }: {
  supplier: CartSupplier
  canEdit: boolean
  canOrder: boolean
  onUpdate: (id: string, packs: number) => void
  onRemove: (id: string) => void
  onOpenLink: (line: CartLine) => void
  linkLoadingId?: string
  onExport: () => void
  onCreateOrder: () => void
  creating: boolean
}) {
  const hasLinks = supplier.checkoutMode !== 'export'
  return (
    <Card padded={false}>
      <div className="flex flex-wrap items-start justify-between gap-3 px-5 pt-5 sm:px-6">
        <div>
          <h2 className="text-base font-semibold text-navy-900">{supplier.supplierName} — {supplier.lines.length} item{supplier.lines.length === 1 ? '' : 's'}</h2>
          <p className="mt-0.5 max-w-xl text-sm text-slate-500">{supplier.checkoutNote}</p>
        </div>
        <div className="flex flex-wrap gap-2">
          <Button size="sm" icon={<Download className="size-4" />} onClick={onExport}>Export</Button>
          {canOrder && canEdit && <Button size="sm" variant="primary" icon={<FileText className="size-4" />} loading={creating} onClick={onCreateOrder}>Create purchase order</Button>}
        </div>
      </div>
      <Table className="mt-4">
        <thead><tr><Th>Product</Th><Th>Package</Th><Th align="center">Packages</Th><Th align="right">Price / pkg</Th><Th align="right">Line total</Th><Th>Data</Th><Th /></tr></thead>
        <tbody>
          {supplier.lines.map((line) => (
            <tr key={line.id}>
              <Td className="max-w-72">
                <span className="font-medium text-navy-900">{line.productTitle}</span>
                <span className="block text-xs text-slate-500">{line.listItemDescription ? `For: ${line.listItemDescription}` : 'Added directly'}{line.sku && ` · SKU ${line.sku}`}</span>
              </Td>
              <Td className="whitespace-nowrap">{packLabel(line.packQuantity, line.packUnit)}</Td>
              <Td align="center">
                {canEdit ? (
                  <span className="inline-flex items-center rounded-lg border border-slate-300">
                    <button type="button" aria-label="Decrease packages" disabled={line.packs <= 1} onClick={() => onUpdate(line.id, line.packs - 1)} className="px-2 py-1.5 text-slate-600 hover:bg-slate-100 disabled:text-slate-300"><Minus className="size-3.5" /></button>
                    <span className="min-w-8 text-center text-sm font-medium tabular">{line.packs}</span>
                    <button type="button" aria-label="Increase packages" onClick={() => onUpdate(line.id, line.packs + 1)} className="px-2 py-1.5 text-slate-600 hover:bg-slate-100"><Plus className="size-3.5" /></button>
                  </span>
                ) : line.packs}
              </Td>
              <Td align="right" className="whitespace-nowrap">
                {money(line.currentPackPrice ?? line.snapshotPackPrice)}
                {line.priceChanged && <span className="block text-xs text-amber-700" title="The supplier's price changed since this line was added.">was {money(line.snapshotPackPrice)}</span>}
              </Td>
              <Td align="right" className="font-medium text-navy-900">{money(line.lineCost)}</Td>
              <Td><DataStatusBadge status={line.dataStatus} /></Td>
              <Td align="right" className="whitespace-nowrap">
                {hasLinks && <Button size="sm" variant="ghost" icon={<ExternalLink className="size-4" />} loading={linkLoadingId === line.id} onClick={() => onOpenLink(line)}>Buy at supplier</Button>}
                {canEdit && <Button size="sm" variant="ghost" aria-label={`Remove ${line.productTitle}`} onClick={() => onRemove(line.id)}><Trash2 className="size-4" /></Button>}
              </Td>
            </tr>
          ))}
        </tbody>
      </Table>
      <div className="flex flex-wrap items-center justify-end gap-x-6 gap-y-1 px-5 py-4 text-sm sm:px-6">
        <span className="text-slate-500">Subtotal <b className="ml-1 font-semibold text-navy-900 tabular">{money(supplier.totals.subtotal)}</b></span>
        <span className="text-slate-500">Shipping <b className="ml-1 font-semibold text-navy-900 tabular">{supplier.totals.shipping === null ? <Unknown /> : supplier.totals.shipping === 0 ? 'Free' : money(supplier.totals.shipping)}</b></span>
        {supplier.totals.amountToFreeShipping !== null && <Badge tone="blue">{money(supplier.totals.amountToFreeShipping)} more for free shipping</Badge>}
        {supplier.totals.belowMinimumOrder && <Badge tone="red">Below minimum order</Badge>}
      </div>
    </Card>
  )
}

export function OrdersTab({ orders, isLoading, onGoToCart }: { orders: PurchaseOrder[] | undefined; isLoading: boolean; onGoToCart: () => void }) {
  if (isLoading || !orders) return <Card><SkeletonRows rows={3} /></Card>
  if (orders.length === 0)
    return <EmptyState icon={<FileText className="size-5" />} title="No purchase orders for this project" description="Create purchase orders from the cart, one per supplier." action={<Button onClick={onGoToCart}>Go to the cart</Button>} />
  return (
    <Card padded={false}>
      <Table>
        <thead><tr><Th>PO number</Th><Th>Status</Th><Th>Supplier</Th><Th>Requested delivery</Th><Th align="right">Total</Th><Th>Created</Th></tr></thead>
        <tbody>
          {orders.map((order) => (
            <tr key={order.id} className="hover:bg-slate-50/60">
              <Td><Link to={`/app/purchase-orders/${order.id}`} className="font-medium text-navy-900 hover:text-accent-700">{order.poNumber}</Link></Td>
              <Td><StatusBadge status={order.status} /></Td>
              <Td>{order.supplierName}</Td>
              <Td>{formatDate(order.requestedDeliveryDate)}</Td>
              <Td align="right" className="font-medium text-navy-900">{money(order.total)}{order.totalExcludesUnknowns && <span className="block text-xs font-normal text-amber-700">excl. unknown</span>}</Td>
              <Td className="text-slate-500">{formatDate(order.createdAt)}</Td>
            </tr>
          ))}
        </tbody>
      </Table>
    </Card>
  )
}
