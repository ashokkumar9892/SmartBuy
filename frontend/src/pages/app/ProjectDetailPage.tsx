import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, CheckCircle2, ClipboardCheck, Link2, Pencil, Plus, Replace, ShieldAlert, Trash2, Undo2 } from 'lucide-react'
import { useState } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { confidenceTone, ProductPicker, UnitSelect } from '../../components/ProductPicker'
import { Badge, Button, Card, Checkbox, ConfirmDialog, EmptyState, ErrorState, Input, Modal, Notice, PageHeader, Select, Skeleton, Spinner, StatusBadge, Table, Tabs, Td, Th, useToast } from '../../components/ui'
import { api, errorMessage } from '../../lib/api'
import { useSession } from '../../lib/auth'
import { formatDate, formatDateTime, qty, toDateInput, unitLabel, unitMoney } from '../../lib/format'
import { INDUSTRIES, industryLabel, type AlternativesResult, type DraftResponse, type ListItem, type ProjectDetail, type PurchaseOrder, type ShoppingList } from '../../lib/types'
import { DraftEditor, DraftNotices, DraftSourcePanel, emptyRow, rowsToItems, rowsValid, type DraftRow } from './NewProjectPage'
import { CartTab, OrdersTab } from './ProjectCart'
import { CompareTab, PlanTab, useComparison } from './ProjectCompare'

type TabId = 'list' | 'compare' | 'plan' | 'cart' | 'orders'

export function useProject(id: string) {
  return useQuery({ queryKey: ['project', id], queryFn: () => api.get<ProjectDetail>(`/api/projects/${id}`) })
}

/** Every list mutation returns the whole project, so the cache is replaced and price-dependent views are refreshed. */
export function useProjectMutation<T>(projectId: string, mutationFn: (input: T) => Promise<ProjectDetail>, successMessage?: string) {
  const queryClient = useQueryClient()
  const toast = useToast()
  return useMutation({
    mutationFn,
    onSuccess: (project) => {
      queryClient.setQueryData(['project', projectId], project)
      queryClient.removeQueries({ queryKey: ['comparison', projectId] })
      queryClient.invalidateQueries({ queryKey: ['cart', projectId] })
      queryClient.invalidateQueries({ queryKey: ['projects'] })
      queryClient.invalidateQueries({ queryKey: ['dashboard'] })
      if (successMessage) toast.success(successMessage)
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
}

export function ProjectDetailPage() {
  const { id = '' } = useParams()
  const session = useSession()
  const navigate = useNavigate()
  const toast = useToast()
  const queryClient = useQueryClient()
  const [params, setParams] = useSearchParams()
  const tab = (params.get('tab') as TabId) || 'list'
  const setTab = (next: TabId) => setParams(next === 'list' ? {} : { tab: next }, { replace: true })
  const [editing, setEditing] = useState(false)
  const [deleting, setDeleting] = useState(false)

  const { data: project, isLoading, error, refetch } = useProject(id)
  const anyApproved = project?.lists.some((l) => l.isApproved) ?? false
  const comparison = useComparison(id, anyApproved && (tab === 'compare' || tab === 'plan'))
  const cartCount = useQuery({ queryKey: ['cart', id], queryFn: () => api.get<{ lineCount: number }>(`/api/projects/${id}/cart`), enabled: !!project })
  const orders = useQuery({ queryKey: ['purchase-orders', 'project', id], queryFn: () => api.get<PurchaseOrder[]>(`/api/purchase-orders?projectId=${id}`), enabled: !!project })

  const remove = useMutation({
    mutationFn: () => api.delete(`/api/projects/${id}`),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['projects'] })
      queryClient.invalidateQueries({ queryKey: ['dashboard'] })
      toast.success('Project deleted.')
      navigate('/app/projects')
    },
    onError: (e) => toast.error(errorMessage(e)),
  })

  const back = <Link to="/app/projects" className="inline-flex items-center gap-1 text-slate-500 hover:text-navy-900"><ArrowLeft className="size-4" /> Projects</Link>
  if (error) return <><PageHeader title="Project" back={back} /><ErrorState error={error} onRetry={refetch} /></>
  if (isLoading || !project) return <><PageHeader title={<Skeleton className="h-8 w-72" />} back={back} /><Skeleton className="h-96 rounded-2xl" /></>

  const itemCount = project.lists.reduce((sum, l) => sum + l.items.length, 0)
  const goToList = () => setTab('list')

  return (
    <>
      <PageHeader
        back={back}
        title={<span className="flex flex-wrap items-center gap-3">{project.name} <StatusBadge status={project.status} /></span>}
        description={
          <>
            {industryLabel(project.industry)} · {itemCount} item{itemCount === 1 ? '' : 's'}
            {project.requiredByDate && <> · needed by <b className="font-medium text-navy-800">{formatDate(project.requiredByDate)}</b></>}
            {project.description && <span className="mt-1 block">{project.description}</span>}
          </>
        }
        actions={
          <>
            {session.permissions.canEditLists && <Button icon={<Pencil className="size-4" />} onClick={() => setEditing(true)}>Edit details</Button>}
            {session.permissions.canManagePurchasing && <Button variant="danger" icon={<Trash2 className="size-4" />} onClick={() => setDeleting(true)}>Delete</Button>}
          </>
        }
      />

      <Tabs<TabId>
        value={tab}
        onChange={setTab}
        tabs={[
          { id: 'list', label: 'Material list', count: itemCount },
          { id: 'compare', label: 'Compare suppliers' },
          { id: 'plan', label: 'Purchasing plan' },
          { id: 'cart', label: 'Cart', count: cartCount.data?.lineCount },
          { id: 'orders', label: 'Purchase orders', count: orders.data?.length },
        ]}
      />

      {tab === 'list' && <ListTab project={project} onCompare={() => setTab('compare')} />}
      {tab === 'compare' && <CompareTab project={project} comparison={comparison} anyApproved={anyApproved} onGoToList={goToList} onGoToPlan={() => setTab('plan')} />}
      {tab === 'plan' && <PlanTab project={project} comparison={comparison} anyApproved={anyApproved} onGoToList={goToList} onGoToCart={() => setTab('cart')} />}
      {tab === 'cart' && <CartTab project={project} onGoToPlan={() => setTab('plan')} onGoToOrders={() => setTab('orders')} />}
      {tab === 'orders' && <OrdersTab orders={orders.data} isLoading={orders.isLoading} onGoToCart={() => setTab('cart')} />}

      {editing && <EditProjectModal project={project} onClose={() => setEditing(false)} />}
      <ConfirmDialog open={deleting} onClose={() => setDeleting(false)} onConfirm={() => remove.mutate()} loading={remove.isPending} danger confirmLabel="Delete project" title={`Delete "${project.name}"?`}>
        The project, its lists and its cart are removed from your workspace. Purchase orders already created are kept.
      </ConfirmDialog>
    </>
  )
}

function EditProjectModal({ project, onClose }: { project: ProjectDetail; onClose: () => void }) {
  const [form, setForm] = useState({ name: project.name, description: project.description ?? '', industry: project.industry, requiredByDate: toDateInput(project.requiredByDate), status: project.status })
  const save = useProjectMutation(project.id, () =>
    api.put<ProjectDetail>(`/api/projects/${project.id}`, { name: form.name.trim(), description: form.description.trim() || null, industry: form.industry, requiredByDate: form.requiredByDate || null, status: form.status, version: project.version }), 'Project updated.')
  return (
    <Modal open onClose={onClose} title="Edit project details"
      footer={<><Button onClick={onClose}>Cancel</Button><Button variant="primary" loading={save.isPending} disabled={!form.name.trim()} onClick={() => save.mutate(undefined, { onSuccess: onClose })}>Save changes</Button></>}>
      <div className="space-y-4">
        <Input label="Project name" maxLength={200} value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
        <div className="grid gap-4 sm:grid-cols-2">
          <Select label="Industry" value={form.industry} onChange={(e) => setForm({ ...form, industry: e.target.value })}>
            {INDUSTRIES.map((i) => <option key={i.value} value={i.value}>{i.label}</option>)}
          </Select>
          <Select label="Status" value={form.status} onChange={(e) => setForm({ ...form, status: e.target.value })}>
            {['Draft', 'Active', 'Ordered', 'Completed', 'Archived'].map((s) => <option key={s}>{s}</option>)}
          </Select>
        </div>
        <Input label="Materials needed by" type="date" value={form.requiredByDate} onChange={(e) => setForm({ ...form, requiredByDate: e.target.value })} hint="Changing this changes which offers meet the deadline." />
        <Input label="Notes" maxLength={2000} value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} />
      </div>
    </Modal>
  )
}

// ---------- Material list tab ----------

function ListTab({ project, onCompare }: { project: ProjectDetail; onCompare: () => void }) {
  const session = useSession()
  const canEdit = session.permissions.canEditLists
  const [adding, setAdding] = useState(false)

  return (
    <div className="space-y-6">
      {project.lists.length === 0 ? (
        <EmptyState icon={<ClipboardCheck className="size-5" />} title="This project has no shopping list yet" description="Add a list by entering items, describing the job, or uploading a file."
          action={canEdit && <Button variant="primary" icon={<Plus className="size-4" />} onClick={() => setAdding(true)}>Add a list</Button>} />
      ) : (
        project.lists.map((list) => <ListCard key={list.id} project={project} list={list} canEdit={canEdit} onCompare={onCompare} />)
      )}
      {project.lists.length > 0 && canEdit && <Button icon={<Plus className="size-4" />} onClick={() => setAdding(true)}>Add another list</Button>}
      {adding && <AddListModal project={project} onClose={() => setAdding(false)} />}
    </div>
  )
}

function ListCard({ project, list, canEdit, onCompare }: { project: ProjectDetail; list: ShoppingList; canEdit: boolean; onCompare: () => void }) {
  const session = useSession()
  const [editingItem, setEditingItem] = useState<ListItem | 'new' | null>(null)
  const [alternativesFor, setAlternativesFor] = useState<ListItem | null>(null)
  const [confirmDelete, setConfirmDelete] = useState(false)
  const approve = useProjectMutation(project.id, () => api.post<ProjectDetail>(`/api/lists/${list.id}/approve`), 'List approved. You can now compare suppliers.')
  const reopen = useProjectMutation(project.id, () => api.post<ProjectDetail>(`/api/lists/${list.id}/reopen`), 'List reopened for review.')
  const removeList = useProjectMutation(project.id, () => api.delete<ProjectDetail>(`/api/lists/${list.id}`), 'List deleted.')
  const removeItem = useProjectMutation(project.id, (itemId: string) => api.delete<ProjectDetail>(`/api/items/${itemId}`))
  const unlinked = list.items.filter((i) => !i.canonicalProductId).length
  const machineMade = list.items.some((i) => i.source === 'Ai' || i.source === 'Upload' || i.source === 'Reuse')

  return (
    <Card padded={false}>
      <div className="flex flex-wrap items-start justify-between gap-4 px-5 pt-5 sm:px-6 sm:pt-6">
        <div>
          <h2 className="flex flex-wrap items-center gap-2.5 text-base font-semibold text-navy-900">
            {list.name}
            {list.isApproved ? <Badge tone="green"><CheckCircle2 className="size-3" /> Approved {formatDate(list.approvedAt)}</Badge> : <Badge tone="amber">Needs review</Badge>}
          </h2>
          {list.sourceText && <p className="mt-1 max-w-3xl text-sm italic text-slate-500">"{list.sourceText}"</p>}
        </div>
        {canEdit && (
          <div className="flex flex-wrap gap-2">
            <Button size="sm" icon={<Plus className="size-4" />} onClick={() => setEditingItem('new')}>Add item</Button>
            {list.isApproved
              ? <Button size="sm" icon={<Undo2 className="size-4" />} loading={reopen.isPending} onClick={() => reopen.mutate(undefined)}>Reopen for review</Button>
              : <Button size="sm" variant="accent" icon={<ClipboardCheck className="size-4" />} loading={approve.isPending} disabled={list.items.length === 0} onClick={() => approve.mutate(undefined)}>Approve quantities</Button>}
            <Button size="sm" variant="ghost" aria-label={`Delete list ${list.name}`} onClick={() => setConfirmDelete(true)}><Trash2 className="size-4" /></Button>
          </div>
        )}
      </div>

      <div className="space-y-3 px-5 py-4 sm:px-6">
        {!list.isApproved && (
          <Notice tone="warning" title="Review the quantities before pricing">
            {machineMade ? 'This list was drafted automatically or copied. ' : ''}
            Check each quantity, unit and linked product, then choose <b>Approve quantities</b>. Suppliers are not searched for a list until a person has approved it.
            Draft quantities are a planning aid, not an engineered or construction-grade estimate.
          </Notice>
        )}
        {list.assumptions.length > 0 && (
          <Notice tone="info" title="Assumptions behind this draft">
            <ul className="list-disc space-y-1 pl-4">{list.assumptions.map((a, i) => <li key={i}>{a}</li>)}</ul>
          </Notice>
        )}
        {unlinked > 0 && <Notice tone="warning">{unlinked} line{unlinked === 1 ? ' is' : 's are'} not linked to a catalog product and cannot be priced. Use the link button on the line to choose one.</Notice>}
        {list.isApproved && list.items.length > 0 && (
          <Notice tone="success" action={<Button size="sm" variant="primary" onClick={onCompare}>Compare suppliers</Button>}>This list is approved and ready to compare.</Notice>
        )}
      </div>

      {list.items.length === 0 ? (
        <p className="px-6 pb-6 text-sm text-slate-500">No items yet.</p>
      ) : (
        <Table>
          <thead><tr><Th>#</Th><Th>Material</Th><Th align="right">Quantity</Th><Th>Catalog product</Th><Th>Source</Th><Th /></tr></thead>
          <tbody>
            {list.items.map((item) => {
              const match = confidenceTone(item.canonicalProductId ? item.matchConfidence ?? 1 : null)
              return (
                <tr key={item.id}>
                  <Td className="text-slate-400 tabular">{item.lineNumber}</Td>
                  <Td className="max-w-sm">
                    <p className="font-medium text-navy-900">{item.description}</p>
                    {(item.specifications || item.material || item.notes) && <p className="mt-0.5 text-xs text-slate-500">{[item.material, item.specifications, item.notes].filter(Boolean).join(' · ')}</p>}
                  </Td>
                  <Td align="right" className="whitespace-nowrap font-medium text-navy-900">{qty(item.quantity)} <span className="font-normal text-slate-500">{unitLabel(item.unit)}</span></Td>
                  <Td className="max-w-xs">
                    {item.canonicalProductName ? <p className="text-navy-900">{item.canonicalProductName}</p> : <p className="text-slate-400">Not linked</p>}
                    <span className="mt-1 flex flex-wrap gap-1.5">
                      <Badge tone={match.tone}>{match.label}</Badge>
                      {item.isSafetyCritical && <Badge tone="red" title="Substitutes for this item must carry the same rating or listing."><ShieldAlert className="size-3" /> Safety-critical</Badge>}
                      {item.approvedSubstituteName && <Badge tone="violet" title={`Approved ${formatDateTime(item.substitutionApprovedAt)}`}><Replace className="size-3" /> Substitute allowed: {item.approvedSubstituteName}</Badge>}
                    </span>
                  </Td>
                  <Td><Badge>{item.source === 'Ai' ? 'AI draft' : item.source}</Badge></Td>
                  <Td align="right" className="whitespace-nowrap">
                    {item.canonicalProductId && <Button size="sm" variant="ghost" onClick={() => setAlternativesFor(item)} icon={<Replace className="size-4" />}>Alternatives</Button>}
                    {canEdit && (
                      <>
                        <Button size="sm" variant="ghost" aria-label={`Edit line ${item.lineNumber}`} onClick={() => setEditingItem(item)}><Pencil className="size-4" /></Button>
                        <Button size="sm" variant="ghost" aria-label={`Delete line ${item.lineNumber}`} onClick={() => removeItem.mutate(item.id)}><Trash2 className="size-4" /></Button>
                      </>
                    )}
                  </Td>
                </tr>
              )
            })}
          </tbody>
        </Table>
      )}

      {editingItem && <ItemModal project={project} list={list} item={editingItem === 'new' ? null : editingItem} onClose={() => setEditingItem(null)} />}
      {alternativesFor && <AlternativesModal project={project} item={alternativesFor} canApprove={session.permissions.canManagePurchasing} onClose={() => setAlternativesFor(null)} />}
      <ConfirmDialog open={confirmDelete} onClose={() => setConfirmDelete(false)} onConfirm={() => removeList.mutate(undefined, { onSuccess: () => setConfirmDelete(false) })} loading={removeList.isPending} danger confirmLabel="Delete list" title={`Delete "${list.name}"?`}>
        All {list.items.length} item(s) on this list are removed from the project.
      </ConfirmDialog>
    </Card>
  )
}

function ItemModal({ project, list, item, onClose }: { project: ProjectDetail; list: ShoppingList; item: ListItem | null; onClose: () => void }) {
  const [form, setForm] = useState({
    description: item?.description ?? '', quantity: item ? String(item.quantity) : '1', unit: item?.unit ?? 'each', material: item?.material ?? '', specifications: item?.specifications ?? '', notes: item?.notes ?? '',
    productId: item?.canonicalProductId ?? null, productName: item?.canonicalProductName ?? null,
  })
  const [picking, setPicking] = useState(false)
  const body = () => ({ description: form.description.trim(), quantity: Number(form.quantity), unit: form.unit, material: form.material.trim() || null, specifications: form.specifications.trim() || null, notes: form.notes.trim() || null, canonicalProductId: form.productId, source: item?.source ?? 'Manual' })
  const save = useProjectMutation(project.id, () => (item ? api.put<ProjectDetail>(`/api/items/${item.id}`, body()) : api.post<ProjectDetail>(`/api/lists/${list.id}/items`, body())), item ? 'Item updated.' : 'Item added.')
  const valid = form.description.trim().length > 0 && Number(form.quantity) > 0
  const changingProduct = item?.approvedSubstituteProductId && form.productId !== item.canonicalProductId

  return (
    <Modal open onClose={onClose} title={item ? `Edit line ${item.lineNumber}` : 'Add item'}
      footer={<><Button onClick={onClose}>Cancel</Button><Button variant="primary" disabled={!valid} loading={save.isPending} onClick={() => save.mutate(undefined, { onSuccess: onClose })}>{item ? 'Save' : 'Add item'}</Button></>}>
      <div className="space-y-4">
        <Input label="Material" maxLength={500} value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} placeholder="e.g. 3/4 in. copper 90-degree elbow" />
        <div className="grid grid-cols-2 gap-4">
          <Input label="Quantity" type="number" min="0" step="any" value={form.quantity} onChange={(e) => setForm({ ...form, quantity: e.target.value })} error={Number(form.quantity) > 0 ? null : 'Enter a quantity greater than zero.'} />
          <div>
            <span className="mb-1.5 block text-sm font-medium text-navy-800">Unit</span>
            <UnitSelect value={form.unit} onChange={(unit) => setForm({ ...form, unit })} className="h-10 w-full rounded-lg border border-slate-300 bg-white px-3 text-sm shadow-sm focus:border-accent-500 focus:outline-none" />
          </div>
        </div>
        <div>
          <span className="mb-1.5 block text-sm font-medium text-navy-800">Catalog product</span>
          <button type="button" disabled={!form.description.trim()} onClick={() => setPicking(true)} className="flex h-10 w-full items-center justify-between gap-2 rounded-lg border border-slate-300 px-3 text-left text-sm shadow-sm hover:border-accent-400 disabled:bg-slate-50 disabled:text-slate-400">
            <span className="truncate">{form.productName ?? 'Choose a product to make this line comparable'}</span>
            <Link2 className="size-4 shrink-0 text-slate-400" />
          </button>
          {changingProduct && <p className="mt-1 text-xs text-amber-700">Changing the product removes the substitution you approved for this line.</p>}
        </div>
        <div className="grid gap-4 sm:grid-cols-2">
          <Input label="Material type (optional)" maxLength={100} value={form.material} onChange={(e) => setForm({ ...form, material: e.target.value })} />
          <Input label="Specifications (optional)" maxLength={300} value={form.specifications} onChange={(e) => setForm({ ...form, specifications: e.target.value })} />
        </div>
        <Input label="Notes (optional)" maxLength={500} value={form.notes} onChange={(e) => setForm({ ...form, notes: e.target.value })} />
      </div>
      <ProductPicker open={picking} onClose={() => setPicking(false)} description={form.description} currentId={form.productId} onPick={(p) => setForm((f) => ({ ...f, productId: p?.id ?? null, productName: p?.name ?? null }))} />
    </Modal>
  )
}

function AddListModal({ project, onClose }: { project: ProjectDetail; onClose: () => void }) {
  const [mode, setMode] = useState<'manual' | 'describe' | 'upload'>('manual')
  const [name, setName] = useState(`List ${project.lists.length + 1}`)
  const [rows, setRows] = useState<DraftRow[]>([emptyRow()])
  const [draft, setDraft] = useState<DraftResponse | null>(null)
  const [sourceText, setSourceText] = useState<string | null>(null)
  const save = useProjectMutation(project.id, () => api.post<ProjectDetail>(`/api/projects/${project.id}/lists`, { name: name.trim(), sourceText, assumptions: draft?.assumptions ?? [], items: rowsToItems(rows) }), 'List added. Review and approve it to include it in comparisons.')
  const showEditor = mode === 'manual' || draft !== null

  return (
    <Modal open onClose={onClose} title="Add a list" wide
      footer={<><Button onClick={onClose}>Cancel</Button><Button variant="primary" loading={save.isPending} disabled={!name.trim() || !showEditor || !rowsValid(rows)} onClick={() => save.mutate(undefined, { onSuccess: onClose })}>Add list</Button></>}>
      <div className="space-y-5">
        <div className="grid gap-4 sm:grid-cols-2">
          <Input label="List name" maxLength={200} value={name} onChange={(e) => setName(e.target.value)} />
          <Select label="Build it by" value={mode} onChange={(e) => { const next = e.target.value as typeof mode; setMode(next); setDraft(null); setSourceText(null); setRows(next === 'manual' ? [emptyRow()] : []) }}>
            <option value="manual">Entering items</option>
            <option value="describe">Describing the job</option>
            <option value="upload">Uploading a file</option>
          </Select>
        </div>
        {mode !== 'manual' && !draft && <DraftSourcePanel source={mode} industry={project.industry} onDraft={(d, r, text) => { setDraft(d); setRows(r); setSourceText(text) }} />}
        {draft && <DraftNotices draft={draft} />}
        {showEditor && <DraftEditor rows={rows} onChange={setRows} />}
      </div>
    </Modal>
  )
}

// ---------- Alternatives ----------

function AlternativesModal({ project, item, canApprove, onClose }: { project: ProjectDetail; item: ListItem; canApprove: boolean; onClose: () => void }) {
  const queryClient = useQueryClient()
  const toast = useToast()
  const [acknowledged, setAcknowledged] = useState<string | null>(null)
  const { data, isLoading, error } = useQuery({ queryKey: ['alternatives', item.id], queryFn: () => api.get<AlternativesResult>(`/api/items/${item.id}/alternatives`) })
  const refresh = () => {
    queryClient.invalidateQueries({ queryKey: ['project', project.id] })
    queryClient.removeQueries({ queryKey: ['comparison', project.id] })
  }
  const approve = useMutation({
    mutationFn: (alternativeProductId: string) => api.post(`/api/items/${item.id}/substitution`, { alternativeProductId, acknowledged: true }),
    onSuccess: () => { refresh(); toast.success('Substitution approved. Its offers will be included in the next comparison.'); onClose() },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const revoke = useMutation({
    mutationFn: () => api.delete(`/api/items/${item.id}/substitution`),
    onSuccess: () => { refresh(); toast.success('Substitution removed.'); onClose() },
    onError: (e) => toast.error(errorMessage(e)),
  })

  return (
    <Modal open onClose={onClose} title="Product alternatives" description={item.canonicalProductName ?? item.description} wide footer={<Button onClick={onClose}>Close</Button>}>
      {isLoading ? <Spinner label="Checking alternatives and current prices" /> : error ? <ErrorState error={error} /> : data && (
        <div className="space-y-4">
          {data.isSafetyCritical && (
            <Notice tone="error" title="Safety-critical component">
              {data.safetyNote} SmartBuy never proposes a substitute for this item on price alone; only alternatives with the same rating or listing can be approved.
            </Notice>
          )}
          {item.approvedSubstituteName && (
            <Notice tone="info" title={`Currently approved substitute: ${item.approvedSubstituteName}`} action={canApprove && <Button size="sm" variant="danger" loading={revoke.isPending} onClick={() => revoke.mutate()}>Remove</Button>}>
              Approved {formatDateTime(item.substitutionApprovedAt)}. The optimizer may price this product in place of the original.
            </Notice>
          )}
          {data.alternatives.length === 0 ? (
            <EmptyState title="No alternatives on file" description="There are no reviewed substitutes for this product in the catalog." />
          ) : (
            <>
              <div className="rounded-xl border border-slate-200 bg-slate-50/70 p-4 text-sm leading-relaxed text-slate-700">
                <p className="mb-1 flex items-center gap-2 text-xs font-semibold uppercase tracking-wide text-slate-500">
                  Summary <Badge tone={data.explanationSource === 'template' ? 'neutral' : 'blue'}>{data.explanationSource === 'template' ? 'Generated from the facts below' : `AI wording (${data.explanationSource}), figures verified`}</Badge>
                </p>
                {data.explanation}
              </div>
              {data.alternatives.map((alt) => (
                <div key={alt.productId} className="rounded-xl border border-slate-200 p-4">
                  <div className="flex flex-wrap items-start justify-between gap-3">
                    <div>
                      <p className="font-semibold text-navy-900">{alt.name}</p>
                      <div className="mt-1 flex flex-wrap gap-1.5">
                        {alt.eligible ? <Badge tone="green">Can be approved</Badge> : <Badge tone="red">Not available as a substitute</Badge>}
                        {alt.sameCertification && <Badge tone="blue">Same rating / listing</Badge>}
                      </div>
                    </div>
                    <div className="text-right text-sm">
                      {alt.pricesComparable && alt.unitPriceDifference !== null ? (
                        <>
                          <p className={alt.unitPriceDifference < 0 ? 'font-semibold text-emerald-700' : 'font-semibold text-amber-700'}>
                            {unitMoney(Math.abs(alt.unitPriceDifference))} {alt.unitPriceDifference < 0 ? 'less' : 'more'} per {unitLabel(data.alternatives.length ? item.catalogUnit ?? alt.baseUnit : alt.baseUnit)}
                            {alt.percentDifference !== null && ` (${Math.abs(alt.percentDifference)}%)`}
                          </p>
                          <p className="text-xs text-slate-500">{unitMoney(alt.alternativeBestUnitPrice)} at {alt.alternativeBestSupplier} vs {unitMoney(alt.originalBestUnitPrice)} for the original</p>
                        </>
                      ) : alt.alternativeBestUnitPrice !== null ? (
                        <p className="text-xs text-slate-500">Best price {unitMoney(alt.alternativeBestUnitPrice)} per {unitLabel(alt.baseUnit)} at {alt.alternativeBestSupplier}.<br />Not directly comparable per unit.</p>
                      ) : <p className="text-xs text-slate-500">No current price</p>}
                    </div>
                  </div>
                  {!alt.eligible && alt.blockedReason && <Notice tone="warning" className="mt-3">{alt.blockedReason}</Notice>}
                  <dl className="mt-3 grid gap-3 text-sm sm:grid-cols-2">
                    {[['Specification differences', alt.specDifferences], ['Compatibility', alt.compatibility], ['Quality considerations', alt.qualityNotes], ['Why it might work', alt.rationale]].map(([label, value]) => (
                      <div key={label}><dt className="text-xs font-semibold uppercase tracking-wide text-slate-500">{label}</dt><dd className="mt-0.5 leading-relaxed text-slate-700">{value || '—'}</dd></div>
                    ))}
                  </dl>
                  {alt.eligible && item.approvedSubstituteProductId !== alt.productId && (
                    canApprove ? (
                      <div className="mt-4 flex flex-wrap items-center justify-between gap-3 border-t border-slate-100 pt-3">
                        <Checkbox checked={acknowledged === alt.productId} onChange={(e) => setAcknowledged(e.target.checked ? alt.productId : null)} label="I have reviewed these differences and confirm this substitute is acceptable for this job." />
                        <Button variant="primary" size="sm" disabled={acknowledged !== alt.productId} loading={approve.isPending} onClick={() => approve.mutate(alt.productId)}>Approve substitution</Button>
                      </div>
                    ) : <p className="mt-3 border-t border-slate-100 pt-3 text-xs text-slate-500">Only a Purchasing Manager or above can approve a substitution.</p>
                  )}
                </div>
              ))}
              <p className="text-xs leading-relaxed text-slate-500">Alternatives come from a reviewed list, not from free-form AI output. Nothing is substituted unless someone approves it here. Always confirm against the job's code requirements.</p>
            </>
          )}
        </div>
      )}
    </Modal>
  )
}
