import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { FileText, Plus } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { Button, Card, EmptyState, ErrorState, Input, LinkButton, Modal, Notice, PageHeader, Select, SkeletonRows, StatusBadge, Table, Td, Th, UpgradeNotice, useToast } from '../../components/ui'
import { api, errorMessage } from '../../lib/api'
import { useSession } from '../../lib/auth'
import { formatDate, money } from '../../lib/format'
import type { ProjectSummary, PurchaseOrder, PurchaseOrderSettings, Supplier } from '../../lib/types'

const STATUS_OPTIONS: { value: string; label: string }[] = [
  { value: '', label: 'All statuses' },
  { value: 'Draft', label: 'Draft' },
  { value: 'PendingApproval', label: 'Pending Approval' },
  { value: 'Approved', label: 'Approved' },
  { value: 'Sent', label: 'Sent' },
  { value: 'PartiallyReceived', label: 'Partially Received' },
  { value: 'Received', label: 'Received' },
  { value: 'Cancelled', label: 'Cancelled' },
]

const dayFormat = new Intl.DateTimeFormat('en-US', { month: 'short', day: 'numeric', year: 'numeric', timeZone: 'UTC' })

/** A requested delivery date is a calendar day, so it is formatted without shifting it into the viewer's time zone. */
function formatDay(value: string | null) {
  if (!value) return '—'
  const day = new Date(`${value.slice(0, 10)}T00:00:00Z`)
  return Number.isNaN(day.getTime()) ? '—' : dayFormat.format(day)
}

function NewPurchaseOrderModal({ onClose }: { onClose: () => void }) {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const toast = useToast()
  const [supplierId, setSupplierId] = useState('')
  const [projectId, setProjectId] = useState('')
  const [deliveryDate, setDeliveryDate] = useState('')
  const [supplierError, setSupplierError] = useState<string | null>(null)

  const suppliers = useQuery({ queryKey: ['suppliers'], queryFn: () => api.get<Supplier[]>('/api/suppliers') })
  const projects = useQuery({ queryKey: ['projects'], queryFn: () => api.get<ProjectSummary[]>('/api/projects') })

  const create = useMutation({
    mutationFn: () =>
      api.post<PurchaseOrder>('/api/purchase-orders', {
        supplierId,
        projectId: projectId || null,
        requestedDeliveryDate: deliveryDate ? `${deliveryDate}T00:00:00Z` : null,
        items: [],
      }),
    onSuccess: (po) => {
      queryClient.invalidateQueries({ queryKey: ['purchase-orders'] })
      queryClient.invalidateQueries({ queryKey: ['dashboard'] })
      queryClient.setQueryData(['purchase-order', po.id], po)
      toast.success(`Draft ${po.poNumber} created. Add its line items next.`)
      navigate(`/app/purchase-orders/${po.id}`)
    },
  })

  const submit = (event: FormEvent) => {
    event.preventDefault()
    if (!supplierId) {
      setSupplierError('Choose the supplier this order is for.')
      return
    }
    create.mutate()
  }

  return (
    <Modal
      open
      onClose={onClose}
      title="New purchase order"
      description="Creates a blank draft. You add line items on the next screen."
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" type="submit" form="new-po-form" loading={create.isPending} disabled={suppliers.isLoading}>
            Create draft
          </Button>
        </>
      }
    >
      <form id="new-po-form" onSubmit={submit} className="space-y-4">
        {suppliers.isError && <ErrorState error={suppliers.error} onRetry={() => suppliers.refetch()} />}
        <Select
          label="Supplier"
          required
          value={supplierId}
          error={supplierError}
          disabled={suppliers.isLoading}
          onChange={(event) => {
            setSupplierId(event.target.value)
            setSupplierError(null)
          }}
        >
          <option value="">{suppliers.isLoading ? 'Loading suppliers…' : 'Choose a supplier'}</option>
          {suppliers.data?.map((supplier) => (
            <option key={supplier.id} value={supplier.id}>
              {supplier.name}
            </option>
          ))}
        </Select>
        <Select
          label="Project (optional)"
          value={projectId}
          disabled={projects.isLoading}
          hint={projects.isError ? 'Projects could not be loaded. You can still create the order without one.' : undefined}
          onChange={(event) => setProjectId(event.target.value)}
        >
          <option value="">No project</option>
          {projects.data?.map((project) => (
            <option key={project.id} value={project.id}>
              {project.name}
            </option>
          ))}
        </Select>
        <Input label="Requested delivery date (optional)" type="date" value={deliveryDate} onChange={(event) => setDeliveryDate(event.target.value)} />
        {create.isError && <Notice tone="error">{errorMessage(create.error)}</Notice>}
        <p className="text-xs text-slate-500">
          Purchase orders are normally created from a project&rsquo;s cart, which fills in the lines and prices for you. Creating a draft does not send anything to the supplier.
        </p>
      </form>
    </Modal>
  )
}

export function PurchaseOrdersPage() {
  const session = useSession()
  const [status, setStatus] = useState('')
  const [creating, setCreating] = useState(false)

  const orders = useQuery({
    queryKey: ['purchase-orders', status],
    queryFn: () => api.get<PurchaseOrder[]>(`/api/purchase-orders${status ? `?status=${encodeURIComponent(status)}` : ''}`),
  })
  const settings = useQuery({ queryKey: ['po-settings'], queryFn: () => api.get<PurchaseOrderSettings>('/api/purchase-orders/settings') })

  const featureOff = settings.data?.purchaseOrdersEnabled === false
  // Creation is offered only once the server has confirmed the plan includes purchase orders.
  const canCreate = session.permissions.canEditLists && settings.data?.purchaseOrdersEnabled === true
  const newButton = canCreate ? (
    <Button variant="primary" icon={<Plus className="size-4" aria-hidden />} onClick={() => setCreating(true)}>
      New purchase order
    </Button>
  ) : undefined

  return (
    <>
      <PageHeader
        title="Purchase orders"
        description="Draft, approve, send and receive purchase orders. FindMyPrice records and sends purchase orders; it does not place orders with suppliers."
        actions={newButton}
      />

      {featureOff && (
        <div className="mb-6">
          <UpgradeNotice
            feature="Purchase orders"
            reason="Your current plan cannot create new purchase orders. Existing orders remain visible below."
            canManageBilling={session.permissions.canManageBilling}
          />
        </div>
      )}

      <div className="mb-4 flex flex-wrap items-end justify-between gap-3">
        <Select label="Status" className="w-full sm:w-60" value={status} onChange={(event) => setStatus(event.target.value)}>
          {STATUS_OPTIONS.map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </Select>
        {orders.data && orders.data.length > 0 && (
          <p className="text-sm text-slate-500">
            {orders.data.length} order{orders.data.length === 1 ? '' : 's'}
          </p>
        )}
      </div>

      {orders.isLoading ? (
        <Card>
          <SkeletonRows rows={6} />
        </Card>
      ) : orders.isError ? (
        <ErrorState error={orders.error} onRetry={() => orders.refetch()} />
      ) : !orders.data?.length ? (
        <EmptyState
          icon={<FileText className="size-6" aria-hidden />}
          title={status ? 'No purchase orders with this status' : 'No purchase orders yet'}
          description={
            status
              ? 'Choose a different status, or show all statuses to see every order.'
              : 'Purchase orders are normally created from a project’s cart: compare suppliers, add items to the cart, then create one order per supplier. You can also start a blank draft.'
          }
          action={
            status ? (
              <Button onClick={() => setStatus('')}>Show all statuses</Button>
            ) : (
              <div className="flex flex-wrap items-center justify-center gap-2">
                <LinkButton to="/app/projects">Go to projects</LinkButton>
                {newButton}
              </div>
            )
          }
        />
      ) : (
        <Card padded={false} className="overflow-hidden">
          <Table>
            <thead>
              <tr>
                <Th>PO number</Th>
                <Th>Status</Th>
                <Th>Supplier</Th>
                <Th>Project</Th>
                <Th>Requested delivery</Th>
                <Th align="right">Total</Th>
                <Th>Created</Th>
              </tr>
            </thead>
            <tbody>
              {orders.data.map((po) => (
                <tr key={po.id} className="hover:bg-slate-50/60">
                  <Td>
                    <Link to={`/app/purchase-orders/${po.id}`} className="font-medium text-accent-700 hover:underline">
                      {po.poNumber}
                    </Link>
                  </Td>
                  <Td>
                    <StatusBadge status={po.status} />
                  </Td>
                  <Td>{po.supplierName || '—'}</Td>
                  <Td>
                    {po.projectId ? (
                      <Link to={`/app/projects/${po.projectId}`} className="text-accent-700 hover:underline">
                        {po.projectName ?? 'View project'}
                      </Link>
                    ) : (
                      <span className="text-slate-400">No project</span>
                    )}
                  </Td>
                  <Td>{formatDay(po.requestedDeliveryDate)}</Td>
                  <Td align="right">
                    <span className="font-medium text-navy-900">{money(po.total)}</span>
                    {po.totalExcludesUnknowns && (
                      <span className="block text-xs text-amber-700" title="Shipping or tax is unknown for this order and is not included in the total.">
                        excl. unknown
                      </span>
                    )}
                  </Td>
                  <Td>
                    <span className="block">{formatDate(po.createdAt)}</span>
                    <span className="block text-xs text-slate-500">{po.createdBy}</span>
                  </Td>
                </tr>
              ))}
            </tbody>
          </Table>
        </Card>
      )}

      {creating && <NewPurchaseOrderModal onClose={() => setCreating(false)} />}
    </>
  )
}
