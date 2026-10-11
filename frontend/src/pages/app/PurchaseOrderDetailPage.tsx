import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, Ban, CheckCircle2, ClipboardCheck, Download, PackageCheck, Pencil, Plus, Send, Trash2, Undo2, UploadCloud } from 'lucide-react'
import { useState, type FormEvent, type ReactNode } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import {
  Button,
  Card,
  CardHeader,
  ConfirmDialog,
  DefinitionList,
  ErrorState,
  Input,
  Modal,
  Notice,
  PageHeader,
  Select,
  Skeleton,
  SkeletonRows,
  StatusBadge,
  Table,
  Td,
  Textarea,
  Th,
  Unavailable,
  Unknown,
  useToast,
} from '../../components/ui'
import { api, ApiError, errorMessage } from '../../lib/api'
import { formatDateTime, money, qty, toDateInput, UNIT_LABELS, unitLabel } from '../../lib/format'
import type { PoAction, PoItem, PurchaseOrder } from '../../lib/types'

const FOOTNOTE = 'FindMyPrice records and sends purchase orders. It does not place orders with suppliers or confirm that a supplier accepted one.'
const RECEIVING_STATUSES = ['Sent', 'Partially Received', 'Received']

const dayFormat = new Intl.DateTimeFormat('en-US', { month: 'short', day: 'numeric', year: 'numeric', timeZone: 'UTC' })

/** A requested delivery date is a calendar day, so it is formatted without shifting it into the viewer's time zone. */
function formatDay(value: string | null) {
  if (!value) return null
  const day = new Date(`${value.slice(0, 10)}T00:00:00Z`)
  return Number.isNaN(day.getTime()) ? null : dayFormat.format(day)
}

const notSet = (text = 'Not specified') => <span className="text-slate-400">{text}</span>

type DialogKind = 'approve' | 'reject' | 'cancel' | 'delete' | 'send' | 'record_sent' | 'receive'
type ActionRequest = { path: string; body?: unknown; success: string }

// ---------- Totals ----------

function TotalsBlock({ subtotal, shipping, tax, total, excludesUnknowns, preview }: { subtotal: number; shipping: number | null; tax: number | null; total: number; excludesUnknowns: boolean; preview?: boolean }) {
  const row = (label: string, value: ReactNode, strong?: boolean) => (
    <div className={strong ? 'flex items-baseline justify-between gap-6 border-t border-slate-200 pt-2 text-base font-semibold text-navy-900' : 'flex items-baseline justify-between gap-6 text-sm text-slate-600'}>
      <dt>{label}</dt>
      <dd className="tabular text-right">{value}</dd>
    </div>
  )
  return (
    <div className="flex justify-end px-5 py-4">
      <dl className="w-full max-w-sm space-y-2">
        {row('Subtotal', money(subtotal))}
        {row('Shipping (estimate)', shipping === null ? <Unknown /> : money(shipping))}
        {row('Tax (estimate)', tax === null ? <Unknown /> : money(tax))}
        {row(excludesUnknowns ? 'Total (excludes unknown amounts)' : 'Total', money(total), true)}
        {excludesUnknowns && <p className="text-xs text-amber-700">Unknown amounts are not counted as zero. The final total will be higher once they are known.</p>}
        {preview && <p className="text-xs text-slate-500">Preview of your unsaved changes. Saved totals are calculated by the server.</p>}
      </dl>
    </div>
  )
}

// ---------- Draft editor ----------

type Row = { key: number; description: string; supplierSku: string; quantity: string; packQuantity: string; unit: string; unitPrice: string }

const toRow = (item: PoItem, key: number): Row => ({
  key,
  description: item.description,
  supplierSku: item.supplierSku ?? '',
  quantity: String(item.quantity),
  packQuantity: String(item.packQuantity),
  unit: item.unit,
  unitPrice: String(item.unitPrice),
})

const parseNumber = (text: string) => {
  const trimmed = text.trim()
  if (trimmed === '') return null
  const value = Number(trimmed)
  return Number.isFinite(value) ? value : Number.NaN
}

function DraftEditor({ po, onSaved, onCancel, onConflict }: { po: PurchaseOrder; onSaved: (po: PurchaseOrder) => void; onCancel: () => void; onConflict: () => void }) {
  const toast = useToast()
  // The version loaded when editing started. A newer server version must reject this save rather than be overwritten.
  const [version] = useState(po.version)
  const [deliveryDate, setDeliveryDate] = useState(toDateInput(po.requestedDeliveryDate))
  const [shipTo, setShipTo] = useState(po.shipTo ?? '')
  const [internalNotes, setInternalNotes] = useState(po.internalNotes ?? '')
  const [shipping, setShipping] = useState(po.shippingEstimate === null ? '' : String(po.shippingEstimate))
  const [tax, setTax] = useState(po.taxEstimate === null ? '' : String(po.taxEstimate))
  const [rows, setRows] = useState<Row[]>(() => po.items.map((item, index) => toRow(item, index)))
  const [nextKey, setNextKey] = useState(po.items.length)
  const [problem, setProblem] = useState<string | null>(null)
  const [conflict, setConflict] = useState(false)

  const save = useMutation({
    mutationFn: (body: unknown) => api.put<PurchaseOrder>(`/api/purchase-orders/${po.id}`, body),
    onSuccess: (saved) => {
      toast.success('Purchase order saved.')
      onSaved(saved)
    },
    onError: (error) => {
      const message = errorMessage(error)
      setProblem(message)
      toast.error(message)
      if (error instanceof ApiError && error.status === 409 && error.code === 'concurrency') {
        setConflict(true)
        onConflict()
      }
    },
  })

  const update = (key: number, patch: Partial<Row>) => setRows((current) => current.map((row) => (row.key === key ? { ...row, ...patch } : row)))
  const addRow = () => {
    setRows((current) => [...current, { key: nextKey, description: '', supplierSku: '', quantity: '1', packQuantity: '1', unit: 'each', unitPrice: '' }])
    setNextKey((key) => key + 1)
  }

  const shippingValue = parseNumber(shipping)
  const taxValue = parseNumber(tax)
  const subtotal = rows.reduce((sum, row) => {
    const line = (parseNumber(row.quantity) ?? 0) * (parseNumber(row.unitPrice) ?? 0)
    return sum + (Number.isFinite(line) ? line : 0)
  }, 0)
  const known = (value: number | null) => (value !== null && Number.isFinite(value) ? value : null)

  const submit = (event: FormEvent) => {
    event.preventDefault()
    setProblem(null)
    if (Number.isNaN(shippingValue) || (shippingValue !== null && shippingValue < 0)) return setProblem('Shipping estimate must be a number of zero or more, or left blank if it is unknown.')
    if (Number.isNaN(taxValue) || (taxValue !== null && taxValue < 0)) return setProblem('Tax estimate must be a number of zero or more, or left blank if it is unknown.')
    const items = []
    for (const [index, row] of rows.entries()) {
      const line = `Line ${index + 1}`
      const quantity = parseNumber(row.quantity)
      const packQuantity = parseNumber(row.packQuantity)
      const unitPrice = parseNumber(row.unitPrice)
      if (!row.description.trim()) return setProblem(`${line}: enter a description.`)
      if (row.description.trim().length > 500) return setProblem(`${line}: the description can be at most 500 characters.`)
      if (quantity === null || Number.isNaN(quantity) || quantity <= 0) return setProblem(`${line}: packages must be greater than zero.`)
      if (packQuantity === null || Number.isNaN(packQuantity) || packQuantity <= 0) return setProblem(`${line}: pack size must be greater than zero.`)
      if (unitPrice === null || Number.isNaN(unitPrice) || unitPrice < 0) return setProblem(`${line}: enter the price per package. FindMyPrice does not assume a price.`)
      items.push({ description: row.description.trim(), supplierSku: row.supplierSku.trim() || null, quantity, packQuantity, unit: row.unit || 'each', unitPrice })
    }
    save.mutate({
      supplierId: po.supplierId,
      projectId: po.projectId,
      requestedDeliveryDate: deliveryDate ? `${deliveryDate}T00:00:00Z` : null,
      shipTo: shipTo.trim() || null,
      internalNotes: internalNotes.trim() || null,
      shippingEstimate: shippingValue,
      taxEstimate: taxValue,
      items,
      version,
    })
  }

  const unitCodes = Object.keys(UNIT_LABELS)

  return (
    <form onSubmit={submit} className="space-y-6" noValidate>
      <Card>
        <CardHeader title="Order details" description={`Draft for ${po.supplierName}${po.projectName ? ` · ${po.projectName}` : ''}`} />
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <Input label="Requested delivery date" type="date" value={deliveryDate} onChange={(event) => setDeliveryDate(event.target.value)} />
          <div className="hidden sm:block" />
          <Input
            label="Shipping estimate (USD)"
            type="number"
            inputMode="decimal"
            min={0}
            step="0.01"
            placeholder="Unknown"
            value={shipping}
            onChange={(event) => setShipping(event.target.value)}
            hint="Leave blank if unknown. Blank is recorded as unknown, not as $0."
          />
          <Input
            label="Tax estimate (USD)"
            type="number"
            inputMode="decimal"
            min={0}
            step="0.01"
            placeholder="Unknown"
            value={tax}
            onChange={(event) => setTax(event.target.value)}
            hint="Leave blank if unknown. Blank is recorded as unknown, not as $0."
          />
          <Textarea label="Ship to" rows={3} maxLength={2000} value={shipTo} onChange={(event) => setShipTo(event.target.value)} placeholder="Delivery address" />
          <Textarea label="Internal notes" rows={3} maxLength={2000} value={internalNotes} onChange={(event) => setInternalNotes(event.target.value)} hint="Shown to your team on this page. Not printed on the PDF or CSV." />
        </div>
      </Card>

      <Card padded={false} className="overflow-hidden">
        <div className="flex flex-wrap items-center justify-between gap-3 px-5 pt-5">
          <div>
            <h2 className="text-base font-semibold text-navy-900">Line items</h2>
            <p className="mt-0.5 text-sm text-slate-500">Quantity is the number of packages. Line total is packages × price per package.</p>
          </div>
          <Button size="sm" icon={<Plus className="size-4" aria-hidden />} onClick={addRow} disabled={rows.length >= 500}>
            Add line
          </Button>
        </div>
        {rows.length === 0 ? (
          <p className="px-5 py-8 text-center text-sm text-slate-500">No line items yet. Add a line to start building this order.</p>
        ) : (
          <Table className="mt-4">
            <thead>
              <tr>
                <Th>#</Th>
                <Th>Description</Th>
                <Th>Supplier SKU</Th>
                <Th>Packages</Th>
                <Th>Pack size</Th>
                <Th>Unit</Th>
                <Th>Price / package</Th>
                <Th align="right">Line total</Th>
                <Th />
              </tr>
            </thead>
            <tbody>
              {rows.map((row, index) => {
                const lineTotal = (parseNumber(row.quantity) ?? Number.NaN) * (parseNumber(row.unitPrice) ?? Number.NaN)
                const n = index + 1
                return (
                  <tr key={row.key}>
                    <Td className="pt-5 text-slate-500">{n}</Td>
                    <Td>
                      <Input aria-label={`Line ${n} description`} className="w-64" maxLength={500} value={row.description} onChange={(event) => update(row.key, { description: event.target.value })} />
                    </Td>
                    <Td>
                      <Input aria-label={`Line ${n} supplier SKU`} className="w-32" value={row.supplierSku} onChange={(event) => update(row.key, { supplierSku: event.target.value })} />
                    </Td>
                    <Td>
                      <Input aria-label={`Line ${n} packages`} className="w-24" type="number" inputMode="decimal" min={0} step="any" value={row.quantity} onChange={(event) => update(row.key, { quantity: event.target.value })} />
                    </Td>
                    <Td>
                      <Input aria-label={`Line ${n} pack size`} className="w-24" type="number" inputMode="decimal" min={0} step="any" value={row.packQuantity} onChange={(event) => update(row.key, { packQuantity: event.target.value })} />
                    </Td>
                    <Td>
                      <Select aria-label={`Line ${n} unit`} className="w-28" value={row.unit} onChange={(event) => update(row.key, { unit: event.target.value })}>
                        {!unitCodes.includes(row.unit) && <option value={row.unit}>{row.unit}</option>}
                        {unitCodes.map((code) => (
                          <option key={code} value={code}>
                            {unitLabel(code)}
                          </option>
                        ))}
                      </Select>
                    </Td>
                    <Td>
                      <Input aria-label={`Line ${n} price per package`} className="w-28" type="number" inputMode="decimal" min={0} step="0.01" placeholder="0.00" value={row.unitPrice} onChange={(event) => update(row.key, { unitPrice: event.target.value })} />
                    </Td>
                    <Td align="right" className="pt-5 font-medium text-navy-900">
                      {Number.isFinite(lineTotal) ? money(lineTotal) : '—'}
                    </Td>
                    <Td>
                      <Button variant="ghost" size="sm" aria-label={`Remove line ${n}`} icon={<Trash2 className="size-4" aria-hidden />} onClick={() => setRows((current) => current.filter((r) => r.key !== row.key))} />
                    </Td>
                  </tr>
                )
              })}
            </tbody>
          </Table>
        )}
        <TotalsBlock
          subtotal={subtotal}
          shipping={known(shippingValue)}
          tax={known(taxValue)}
          total={subtotal + (known(shippingValue) ?? 0) + (known(taxValue) ?? 0)}
          excludesUnknowns={known(shippingValue) === null || known(taxValue) === null}
          preview
        />
      </Card>

      {problem && (
        <Notice
          tone="error"
          title={conflict ? 'This purchase order changed while you were editing' : 'The purchase order was not saved'}
          action={
            conflict ? (
              <Button size="sm" onClick={onCancel}>
                Discard my changes and reload
              </Button>
            ) : undefined
          }
        >
          {problem}
        </Notice>
      )}

      <div className="flex flex-wrap justify-end gap-2">
        <Button onClick={onCancel} disabled={save.isPending}>
          Discard changes
        </Button>
        <Button variant="primary" type="submit" loading={save.isPending} disabled={conflict}>
          Save purchase order
        </Button>
      </div>
    </form>
  )
}

// ---------- Action dialogs ----------

type DialogProps = { po: PurchaseOrder; onClose: () => void; run: (request: ActionRequest) => void; pending: boolean; error: string | null }

function CommentDialog({ po, onClose, run, pending, error, mode }: DialogProps & { mode: 'approve' | 'reject' }) {
  const [comment, setComment] = useState('')
  const approve = mode === 'approve'
  return (
    <Modal
      open
      onClose={onClose}
      title={approve ? `Approve ${po.poNumber}` : `Reject ${po.poNumber}`}
      description={approve ? 'Approving makes the order ready to send. It does not send it to the supplier.' : 'Rejecting returns the order to Draft so it can be corrected and resubmitted.'}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant={approve ? 'primary' : 'danger'} loading={pending} onClick={() => run({ path: mode, body: { comment: comment.trim() || null }, success: approve ? `${po.poNumber} approved.` : `${po.poNumber} rejected and returned to Draft.` })}>
            {approve ? 'Approve' : 'Reject'}
          </Button>
        </>
      }
    >
      <div className="space-y-4">
        <Textarea
          label={approve ? 'Comment (optional)' : 'Reason (optional)'}
          rows={3}
          maxLength={1000}
          value={comment}
          onChange={(event) => setComment(event.target.value)}
          hint="Saved in the approval history when this order was submitted for approval."
        />
        {error && <Notice tone="error">{error}</Notice>}
      </div>
    </Modal>
  )
}

function SendDialog({ po, onClose, run, pending, error }: DialogProps) {
  const [recipient, setRecipient] = useState('')
  return (
    <Modal
      open
      onClose={onClose}
      title={`Send ${po.poNumber} to ${po.supplierName}`}
      description="FindMyPrice emails the purchase order PDF through this server's delivery channel."
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" icon={<Send className="size-4" aria-hidden />} loading={pending} onClick={() => run({ path: 'send', body: { recipient: recipient.trim() || null }, success: `${po.poNumber} was sent to the supplier.` })}>
            Send purchase order
          </Button>
        </>
      }
    >
      <div className="space-y-4">
        <Input
          label="Recipient email (optional)"
          type="email"
          autoComplete="off"
          maxLength={320}
          value={recipient}
          onChange={(event) => setRecipient(event.target.value)}
          placeholder="orders@supplier.example"
          hint="Leave blank to use the order email saved for this supplier under Suppliers."
        />
        <p className="text-sm text-slate-600">The order is marked Sent only after the mail server accepts the message. Sending does not place the order or confirm that the supplier accepted it.</p>
        {error && (
          <Notice tone="error" title="The purchase order was not sent">
            {error}
          </Notice>
        )}
      </div>
    </Modal>
  )
}

function RecordSentDialog({ po, onClose, run, pending, error }: DialogProps) {
  const [note, setNote] = useState('')
  return (
    <Modal
      open
      onClose={onClose}
      title={`Record ${po.poNumber} as sent`}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" loading={pending} onClick={() => run({ path: 'record-sent', body: { note: note.trim() || null }, success: `${po.poNumber} recorded as delivered manually.` })}>
            Record as sent
          </Button>
        </>
      }
    >
      <div className="space-y-4">
        <Notice tone="info">Use this when you delivered the order yourself (phone, counter, your own email). FindMyPrice will record it as delivered manually; it does not send anything.</Notice>
        <Textarea label="Note (optional)" rows={3} maxLength={500} value={note} onChange={(event) => setNote(event.target.value)} placeholder="For example: phoned in to the trade counter, spoke with Dana" hint="Saved with the order as the record of how and to whom you delivered it." />
        {error && <Notice tone="error">{error}</Notice>}
      </div>
    </Modal>
  )
}

function ReceiveDialog({ po, onClose, run, pending, error }: DialogProps) {
  const [values, setValues] = useState<Record<string, string>>(() => Object.fromEntries(po.items.map((item) => [item.id, String(item.quantityReceived)])))
  const [problem, setProblem] = useState<string | null>(null)

  const submit = () => {
    const lines = []
    for (const item of po.items) {
      const value = parseNumber(values[item.id] ?? '')
      if (value === null || Number.isNaN(value) || value < 0) return setProblem(`Line ${item.lineNumber}: enter the quantity received so far (zero or more).`)
      lines.push({ itemId: item.id, quantityReceived: value })
    }
    setProblem(null)
    run({ path: 'receive', body: { lines }, success: 'Received quantities saved.' })
  }

  return (
    <Modal
      open
      wide
      onClose={onClose}
      title={`Receive ${po.poNumber}`}
      description="Enter the total number of packages received so far for each line, not just today's delivery."
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" loading={pending} onClick={submit}>
            Save received quantities
          </Button>
        </>
      }
    >
      <div className="space-y-4">
        <div className="flex justify-end">
          <Button size="sm" icon={<PackageCheck className="size-4" aria-hidden />} onClick={() => setValues(Object.fromEntries(po.items.map((item) => [item.id, String(item.quantity)])))}>
            Receive all
          </Button>
        </div>
        <div className="overflow-hidden rounded-xl border border-slate-200">
          <Table>
            <thead>
              <tr>
                <Th>#</Th>
                <Th>Description</Th>
                <Th align="right">Ordered</Th>
                <Th>Received so far</Th>
              </tr>
            </thead>
            <tbody>
              {po.items.map((item) => (
                <tr key={item.id}>
                  <Td className="text-slate-500">{item.lineNumber}</Td>
                  <Td className="max-w-xs whitespace-normal">
                    {item.description}
                    {item.supplierSku && <span className="block text-xs text-slate-500">SKU {item.supplierSku}</span>}
                  </Td>
                  <Td align="right">{qty(item.quantity)}</Td>
                  <Td>
                    <Input
                      aria-label={`Line ${item.lineNumber} quantity received`}
                      className="w-28"
                      type="number"
                      inputMode="decimal"
                      min={0}
                      step="any"
                      value={values[item.id] ?? ''}
                      onChange={(event) => setValues((current) => ({ ...current, [item.id]: event.target.value }))}
                    />
                  </Td>
                </tr>
              ))}
            </tbody>
          </Table>
        </div>
        {(problem ?? error) && <Notice tone="error">{problem ?? error}</Notice>}
      </div>
    </Modal>
  )
}

// ---------- Page ----------

export function PurchaseOrderDetailPage() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const toast = useToast()
  const [editing, setEditing] = useState(false)
  const [dialog, setDialog] = useState<DialogKind | null>(null)
  const [exporting, setExporting] = useState<'pdf' | 'csv' | null>(null)

  const query = useQuery({ queryKey: ['purchase-order', id], queryFn: () => api.get<PurchaseOrder>(`/api/purchase-orders/${id}`), enabled: !!id })
  const po = query.data

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: ['purchase-order', id] })
    queryClient.invalidateQueries({ queryKey: ['purchase-orders'] })
    queryClient.invalidateQueries({ queryKey: ['dashboard'] })
  }

  const action = useMutation({
    mutationFn: (request: ActionRequest) => api.post<PurchaseOrder>(`/api/purchase-orders/${id}/${request.path}`, request.body ?? {}),
    onSuccess: (updated, request) => {
      queryClient.setQueryData(['purchase-order', id], updated)
      invalidate()
      setDialog(null)
      toast.success(request.success)
    },
    onError: (error) => {
      toast.error(errorMessage(error))
      // A 409 means the order is not in the state this page assumed, so show what the server has now.
      if (error instanceof ApiError && error.status === 409) query.refetch()
    },
  })

  const remove = useMutation({
    mutationFn: () => api.delete(`/api/purchase-orders/${id}`),
    onSuccess: () => {
      toast.success(`${po?.poNumber ?? 'Purchase order'} deleted.`)
      queryClient.removeQueries({ queryKey: ['purchase-order', id] })
      queryClient.invalidateQueries({ queryKey: ['purchase-orders'] })
      queryClient.invalidateQueries({ queryKey: ['dashboard'] })
      navigate('/app/purchase-orders')
    },
    onError: (error) => {
      toast.error(errorMessage(error))
      setDialog(null)
      if (error instanceof ApiError && error.status === 409) query.refetch()
    },
  })

  const back = (
    <Link to="/app/purchase-orders" className="inline-flex items-center gap-1.5 font-medium text-slate-500 hover:text-navy-800">
      <ArrowLeft className="size-4" aria-hidden /> Purchase orders
    </Link>
  )

  if (query.isLoading) {
    return (
      <>
        <PageHeader title={<Skeleton className="h-8 w-56" />} back={back} />
        <div className="space-y-6">
          <Card>
            <SkeletonRows rows={3} />
          </Card>
          <Card>
            <SkeletonRows rows={5} />
          </Card>
        </div>
      </>
    )
  }
  if (query.isError || !po) {
    return (
      <>
        <PageHeader title="Purchase order" back={back} />
        <ErrorState error={query.error ?? new Error('Purchase order not found.')} onRetry={() => query.refetch()} />
      </>
    )
  }

  const can = (name: PoAction) => po.allowedActions.includes(name)
  const openDialog = (kind: DialogKind) => {
    action.reset()
    setDialog(kind)
  }
  const closeDialog = () => setDialog(null)
  const download = async (kind: 'pdf' | 'csv') => {
    setExporting(kind)
    try {
      await api.download(`/api/purchase-orders/${po.id}/export.${kind}`, `${po.poNumber}.${kind}`)
    } catch (error) {
      toast.error(errorMessage(error))
    } finally {
      setExporting(null)
    }
  }

  const showReceived = RECEIVING_STATUSES.includes(po.status)
  const isEditing = editing && can('edit')
  const sendUnavailable = po.status === 'Approved' && !can('send') && can('record_sent')
  const dialogProps: DialogProps = { po, onClose: closeDialog, run: (request) => action.mutate(request), pending: action.isPending, error: action.isError ? errorMessage(action.error) : null }
  const busy = action.isPending && dialog === null

  const summary: { label: string; value: ReactNode }[] = [
    { label: 'Supplier', value: po.supplierName || notSet('Unknown supplier') },
    {
      label: 'Project',
      value: po.projectId ? (
        <Link to={`/app/projects/${po.projectId}`} className="text-accent-700 hover:underline">
          {po.projectName ?? 'View project'}
        </Link>
      ) : (
        notSet('No project')
      ),
    },
    { label: 'Requested delivery', value: formatDay(po.requestedDeliveryDate) ?? notSet() },
    { label: 'Ship to', value: po.shipTo ? <span className="whitespace-pre-line">{po.shipTo}</span> : notSet() },
    { label: 'Created', value: `${po.createdBy} · ${formatDateTime(po.createdAt)}` },
    { label: 'Approved', value: po.approvedAt ? `${po.approvedBy ?? 'Unknown'} · ${formatDateTime(po.approvedAt)}` : notSet('Not approved') },
  ]
  if (po.sentAt) {
    summary.push(
      { label: 'Sent via', value: po.sentVia ?? notSet('Not recorded') },
      { label: 'Sent to', value: po.sentTo ? <span className="whitespace-pre-line break-words">{po.sentTo}</span> : notSet('Not recorded') },
      { label: 'Sent at', value: formatDateTime(po.sentAt) },
    )
  }

  const exportButtons = (
    <>
      <Button icon={<Download className="size-4" aria-hidden />} loading={exporting === 'pdf'} disabled={exporting !== null} onClick={() => download('pdf')}>
        PDF
      </Button>
      <Button icon={<Download className="size-4" aria-hidden />} loading={exporting === 'csv'} disabled={exporting !== null} onClick={() => download('csv')}>
        CSV
      </Button>
    </>
  )

  return (
    <>
      <PageHeader
        back={back}
        title={
          <span className="flex flex-wrap items-center gap-3">
            {po.poNumber}
            <StatusBadge status={po.status} />
          </span>
        }
        description={`Purchase order for ${po.supplierName || 'an unknown supplier'}${po.projectName ? ` · ${po.projectName}` : ''}`}
        actions={isEditing ? exportButtons : undefined}
      />

      {!isEditing && (
        <div className="mb-6 flex flex-wrap items-start gap-2">
          {can('edit') && (
            <Button icon={<Pencil className="size-4" aria-hidden />} onClick={() => setEditing(true)} disabled={busy}>
              Edit
            </Button>
          )}
          {can('submit') && (
            <Button variant="primary" icon={<UploadCloud className="size-4" aria-hidden />} loading={busy} onClick={() => action.mutate({ path: 'submit', success: `${po.poNumber} submitted for approval.` })}>
              Submit for approval
            </Button>
          )}
          {can('approve') && (
            <Button variant={can('submit') ? 'secondary' : 'primary'} icon={<CheckCircle2 className="size-4" aria-hidden />} onClick={() => openDialog('approve')} disabled={busy}>
              Approve
            </Button>
          )}
          {can('reject') && (
            <Button icon={<Undo2 className="size-4" aria-hidden />} onClick={() => openDialog('reject')} disabled={busy}>
              Reject
            </Button>
          )}
          {can('send') && (
            <Button variant="primary" icon={<Send className="size-4" aria-hidden />} onClick={() => openDialog('send')}>
              Send to supplier
            </Button>
          )}
          {sendUnavailable && <Unavailable label="Send to supplier" reason="No email delivery channel is configured on this server." />}
          {can('record_sent') && (
            <Button icon={<ClipboardCheck className="size-4" aria-hidden />} onClick={() => openDialog('record_sent')}>
              Record as sent
            </Button>
          )}
          {can('receive') && (
            <Button variant="primary" icon={<PackageCheck className="size-4" aria-hidden />} onClick={() => openDialog('receive')}>
              Receive items
            </Button>
          )}
          {exportButtons}
          {can('cancel') && (
            <Button variant="danger" icon={<Ban className="size-4" aria-hidden />} onClick={() => openDialog('cancel')} disabled={busy}>
              Cancel order
            </Button>
          )}
          {can('delete') && (
            <Button variant="danger" icon={<Trash2 className="size-4" aria-hidden />} onClick={() => openDialog('delete')} disabled={busy}>
              Delete
            </Button>
          )}
        </div>
      )}

      {isEditing ? (
        <DraftEditor
          key={po.id}
          po={po}
          onSaved={(saved) => {
            queryClient.setQueryData(['purchase-order', id], saved)
            invalidate()
            setEditing(false)
          }}
          onCancel={() => setEditing(false)}
          onConflict={() => query.refetch()}
        />
      ) : (
        <div className="space-y-6">
          {po.internalNotes && (
            <Notice tone="warning" title="Internal notes">
              <span className="whitespace-pre-line">{po.internalNotes}</span>
            </Notice>
          )}

          <Card>
            <CardHeader title="Summary" />
            <DefinitionList items={summary} />
          </Card>

          <Card padded={false} className="overflow-hidden">
            <div className="px-5 pt-5">
              <h2 className="text-base font-semibold text-navy-900">Line items</h2>
              <p className="mt-0.5 text-sm text-slate-500">Quantity is the number of packages ordered.</p>
            </div>
            {po.items.length === 0 ? (
              <p className="px-5 py-8 text-center text-sm text-slate-500">
                This purchase order has no line items yet.{can('edit') ? ' Choose Edit to add lines.' : ''}
              </p>
            ) : (
              <Table className="mt-4">
                <thead>
                  <tr>
                    <Th>#</Th>
                    <Th>Description</Th>
                    <Th>SKU</Th>
                    <Th align="right">Packages</Th>
                    <Th>Pack size</Th>
                    <Th align="right">Price / package</Th>
                    <Th align="right">Line total</Th>
                    {showReceived && <Th align="right">Received</Th>}
                  </tr>
                </thead>
                <tbody>
                  {po.items.map((item) => (
                    <tr key={item.id}>
                      <Td className="text-slate-500">{item.lineNumber}</Td>
                      <Td className="max-w-md whitespace-normal font-medium text-navy-900">{item.description}</Td>
                      <Td>{item.supplierSku ?? <span className="text-slate-400">—</span>}</Td>
                      <Td align="right">{qty(item.quantity)}</Td>
                      <Td>
                        {qty(item.packQuantity)} {unitLabel(item.unit)}
                      </Td>
                      <Td align="right">{money(item.unitPrice)}</Td>
                      <Td align="right" className="font-medium text-navy-900">
                        {money(item.lineTotal)}
                      </Td>
                      {showReceived && (
                        <Td align="right">
                          <span className={item.quantityReceived >= item.quantity ? 'text-emerald-700' : item.quantityReceived > 0 ? 'text-amber-700' : 'text-slate-500'}>
                            {qty(item.quantityReceived)} of {qty(item.quantity)}
                          </span>
                        </Td>
                      )}
                    </tr>
                  ))}
                </tbody>
              </Table>
            )}
            <TotalsBlock subtotal={po.subtotal} shipping={po.shippingEstimate} tax={po.taxEstimate} total={po.total} excludesUnknowns={po.totalExcludesUnknowns} />
          </Card>

          <Card>
            <CardHeader title="Approval history" />
            {po.approvals.length === 0 ? (
              <p className="text-sm text-slate-500">This purchase order has not been submitted for approval.</p>
            ) : (
              <ul className="divide-y divide-slate-100">
                {po.approvals.map((approval) => (
                  <li key={approval.id} className="flex flex-wrap items-start gap-x-4 gap-y-1 py-3 first:pt-0 last:pb-0">
                    <StatusBadge status={approval.status} />
                    <div className="min-w-0 flex-1 text-sm">
                      <p className="text-navy-900">
                        Requested by {approval.requestedBy} · {formatDateTime(approval.requestedAt)}
                      </p>
                      <p className="text-slate-600">
                        {approval.decidedAt ? `${approval.status} by ${approval.decidedBy ?? 'Unknown'} · ${formatDateTime(approval.decidedAt)}` : 'Awaiting a decision'}
                      </p>
                      {approval.comment && <p className="mt-1 whitespace-pre-line rounded-lg bg-slate-50 px-3 py-2 text-slate-700">{approval.comment}</p>}
                    </div>
                  </li>
                ))}
              </ul>
            )}
          </Card>
        </div>
      )}

      <p className="mt-6 text-xs text-slate-500">{FOOTNOTE}</p>

      {dialog === 'approve' && <CommentDialog mode="approve" {...dialogProps} />}
      {dialog === 'reject' && <CommentDialog mode="reject" {...dialogProps} />}
      {dialog === 'send' && <SendDialog {...dialogProps} />}
      {dialog === 'record_sent' && <RecordSentDialog {...dialogProps} />}
      {dialog === 'receive' && <ReceiveDialog {...dialogProps} />}
      <ConfirmDialog
        open={dialog === 'cancel'}
        onClose={closeDialog}
        onConfirm={() => action.mutate({ path: 'cancel', success: `${po.poNumber} cancelled.` })}
        title={`Cancel ${po.poNumber}?`}
        confirmLabel="Cancel purchase order"
        danger
        loading={action.isPending}
      >
        The purchase order will be marked Cancelled in FindMyPrice.
        {po.sentAt ? ' This order was already sent: cancelling here does not notify the supplier, so contact them directly.' : ' Nothing has been sent to the supplier.'}
      </ConfirmDialog>
      <ConfirmDialog open={dialog === 'delete'} onClose={closeDialog} onConfirm={() => remove.mutate()} title={`Delete ${po.poNumber}?`} confirmLabel="Delete purchase order" danger loading={remove.isPending}>
        This permanently removes the purchase order and its line items. The number {po.poNumber} will not be reused. This cannot be undone.
      </ConfirmDialog>
    </>
  )
}
