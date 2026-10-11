import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import clsx from 'clsx'
import { ArrowLeft, Copy, FileUp, Keyboard, Link2, Plus, Sparkles, Trash2, Wand2 } from 'lucide-react'
import { useRef, useState, type ReactNode } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { confidenceTone, ProductPicker, UnitSelect } from '../../components/ProductPicker'
import { Badge, Button, Card, CardHeader, Input, LinkButton, Notice, PageHeader, Select, Textarea, UpgradeNotice, useToast } from '../../components/ui'
import { api, ApiError, errorMessage } from '../../lib/api'
import { useSession } from '../../lib/auth'
import { INDUSTRIES, type DraftResponse, type ProjectDetail, type ProjectSummary, type ResolveCandidate } from '../../lib/types'

type Source = 'describe' | 'manual' | 'upload' | 'reuse'

export type DraftRow = {
  key: number
  description: string
  quantity: string
  unit: string
  material: string | null
  specifications: string | null
  notes: string | null
  productId: string | null
  productName: string | null
  confidence: number | null
  candidates?: ResolveCandidate[]
  source: 'Manual' | 'Ai' | 'Upload'
}

let nextKey = 1
export const emptyRow = (): DraftRow => ({ key: nextKey++, description: '', quantity: '1', unit: 'each', material: null, specifications: null, notes: null, productId: null, productName: null, confidence: null, source: 'Manual' })

const sources: { id: Source; icon: typeof Sparkles; title: string; text: string }[] = [
  { id: 'describe', icon: Sparkles, title: 'Describe the job', text: 'Write what you need in plain language and get a draft list to review.' },
  { id: 'manual', icon: Keyboard, title: 'Enter items', text: 'Type each material, quantity and unit yourself.' },
  { id: 'upload', icon: FileUp, title: 'Upload a list', text: 'Import a CSV, Excel (.xlsx) or text PDF purchase list.' },
  { id: 'reuse', icon: Copy, title: 'Reuse a project', text: 'Start from the lists of a previous project.' },
]

export function DraftEditor({ rows, onChange }: { rows: DraftRow[]; onChange: (rows: DraftRow[]) => void }) {
  const [picking, setPicking] = useState<number | null>(null)
  const update = (key: number, patch: Partial<DraftRow>) => onChange(rows.map((r) => (r.key === key ? { ...r, ...patch } : r)))
  const pickingRow = rows.find((r) => r.key === picking)

  return (
    <div>
      <div className="overflow-x-auto rounded-xl border border-slate-200">
        <table className="w-full min-w-[720px] text-left text-sm">
          <thead className="bg-slate-50 text-xs uppercase tracking-wide text-slate-500">
            <tr>
              <th className="w-10 px-3 py-2.5 font-semibold">#</th>
              <th className="px-3 py-2.5 font-semibold">Material</th>
              <th className="w-28 px-3 py-2.5 font-semibold">Quantity</th>
              <th className="w-28 px-3 py-2.5 font-semibold">Unit</th>
              <th className="w-72 px-3 py-2.5 font-semibold">Catalog product</th>
              <th className="w-10 px-3 py-2.5" />
            </tr>
          </thead>
          <tbody>
            {rows.map((row, index) => {
              const badQuantity = !(Number(row.quantity) > 0)
              const match = confidenceTone(row.productId ? row.confidence ?? 1 : null)
              return (
                <tr key={row.key} className="border-t border-slate-100 align-top">
                  <td className="px-3 py-3 text-slate-400 tabular">{index + 1}</td>
                  <td className="px-3 py-2">
                    <input aria-label={`Material ${index + 1}`} value={row.description} onChange={(e) => update(row.key, { description: e.target.value })} placeholder="e.g. 3/4 in. Type L copper pipe" maxLength={500} className="h-9 w-full rounded-lg border border-slate-300 px-2.5 text-sm focus:border-accent-500 focus:outline-none" />
                    {(row.specifications || row.notes) && <p className="mt-1 text-xs text-slate-500">{[row.specifications, row.notes].filter(Boolean).join(' · ')}</p>}
                  </td>
                  <td className="px-3 py-2">
                    <input aria-label={`Quantity ${index + 1}`} type="number" min="0" step="any" value={row.quantity} onChange={(e) => update(row.key, { quantity: e.target.value })} aria-invalid={badQuantity} className={clsx('h-9 w-full rounded-lg border px-2.5 text-right text-sm tabular focus:outline-none', badQuantity ? 'border-red-400' : 'border-slate-300 focus:border-accent-500')} />
                  </td>
                  <td className="px-3 py-2"><UnitSelect value={row.unit} onChange={(unit) => update(row.key, { unit })} label={`Unit ${index + 1}`} /></td>
                  <td className="px-3 py-2">
                    <button type="button" onClick={() => setPicking(row.key)} disabled={!row.description.trim()} className="flex w-full items-start justify-between gap-2 rounded-lg border border-slate-300 px-2.5 py-1.5 text-left text-sm hover:border-accent-400 disabled:bg-slate-50 disabled:text-slate-400">
                      <span className="min-w-0">
                        <span className="block truncate text-navy-900">{row.productName ?? 'Choose a product'}</span>
                        <Badge tone={match.tone} className="mt-0.5">{match.label}</Badge>
                      </span>
                      <Link2 className="mt-0.5 size-4 shrink-0 text-slate-400" />
                    </button>
                  </td>
                  <td className="px-2 py-2">
                    <button type="button" aria-label={`Remove line ${index + 1}`} onClick={() => onChange(rows.filter((r) => r.key !== row.key))} className="rounded-lg p-2 text-slate-400 hover:bg-red-50 hover:text-red-600">
                      <Trash2 className="size-4" />
                    </button>
                  </td>
                </tr>
              )
            })}
            {rows.length === 0 && (
              <tr><td colSpan={6} className="px-3 py-8 text-center text-sm text-slate-500">No items yet. Add a line to start the list.</td></tr>
            )}
          </tbody>
        </table>
      </div>
      <Button className="mt-3" size="sm" icon={<Plus className="size-4" />} onClick={() => onChange([...rows, emptyRow()])}>Add line</Button>
      {pickingRow && (
        <ProductPicker
          open
          onClose={() => setPicking(null)}
          description={pickingRow.description}
          candidates={pickingRow.candidates}
          currentId={pickingRow.productId}
          onPick={(product) => update(pickingRow.key, { productId: product?.id ?? null, productName: product?.name ?? null, confidence: product ? pickingRow.candidates?.find((c) => c.productId === product.id)?.confidence ?? 1 : null })}
        />
      )}
    </div>
  )
}

export const rowsFromDraft = (draft: DraftResponse, source: 'Ai' | 'Upload'): DraftRow[] =>
  draft.items.map((item) => ({
    key: nextKey++, description: item.description, quantity: String(item.quantity), unit: item.unit, material: item.material, specifications: item.specifications, notes: item.notes,
    productId: item.suggestedProductId, productName: item.suggestedProductName, confidence: item.confidence, candidates: item.candidates, source,
  }))

export const rowsToItems = (rows: DraftRow[]) =>
  rows.filter((r) => r.description.trim()).map((r) => ({
    description: r.description.trim(), quantity: Number(r.quantity), unit: r.unit, material: r.material, specifications: r.specifications, notes: r.notes, canonicalProductId: r.productId, source: r.source,
  }))

export const rowsValid = (rows: DraftRow[]) => rows.some((r) => r.description.trim()) && rows.filter((r) => r.description.trim()).every((r) => Number(r.quantity) > 0)

/** The three ways of producing draft rows. Shared by the new-project wizard and "add a list" on an existing project. */
export function DraftSourcePanel({ source, industry, onDraft }: { source: 'describe' | 'upload'; industry: string; onDraft: (draft: DraftResponse, rows: DraftRow[], sourceText: string) => void }) {
  const session = useSession()
  const [text, setText] = useState('')
  const [error, setError] = useState<string | null>(null)
  const fileInput = useRef<HTMLInputElement>(null)

  const generate = useMutation({
    mutationFn: () => api.post<DraftResponse>('/api/material-lists/generate', { text, industry }),
    onSuccess: (draft) => { setError(null); onDraft(draft, rowsFromDraft(draft, 'Ai'), text) },
    onError: (e) => setError(errorMessage(e)),
  })
  const upload = useMutation({
    mutationFn: (file: File) => api.upload<DraftResponse>('/api/material-lists/parse-upload', file).then((draft) => ({ draft, file })),
    onSuccess: ({ draft, file }) => { setError(null); onDraft(draft, rowsFromDraft(draft, 'Upload'), `Imported from ${file.name}`) },
    onError: (e) => setError(errorMessage(e)),
  })

  if (source === 'describe') {
    if (!session.plan.aiMaterialLists)
      return <UpgradeNotice feature="AI material lists" reason={`Describing a job in plain language is available on the Pro and Business plans. On ${session.plan.name} you can enter items or upload a list.`} canManageBilling={session.permissions.canManageBilling} />
    return (
      <div className="space-y-4">
        {error && <Notice tone="error">{error}</Notice>}
        <Textarea
          label="What does this job need?"
          rows={5}
          maxLength={4000}
          value={text}
          onChange={(e) => setText(e.target.value)}
          placeholder="I need 50 feet of 3/4-inch copper pipe, 20 elbows, 10 shutoff valves, and 5 faucets for a residential plumbing project."
          hint="Include quantities where you know them. Where you do not, a draft quantity is proposed and listed as an assumption for you to check."
        />
        <div className="flex flex-wrap items-center gap-3">
          <Button variant="accent" icon={<Wand2 className="size-4" />} loading={generate.isPending} disabled={text.trim().length < 3} onClick={() => generate.mutate()}>Generate draft list</Button>
          <button type="button" className="text-sm font-medium text-accent-600 hover:underline" onClick={() => setText('Build a materials list for replacing plumbing fixtures in three bathrooms.')}>Try an example</button>
        </div>
        <p className="text-xs leading-relaxed text-slate-500">
          {session.aiProvider === 'mock'
            ? 'No language model is configured on this server, so a built-in rule-based parser produces the draft (demo mode). It reads quantities you write and applies a few job templates.'
            : `Drafts are generated by the ${session.aiProvider} AI provider.`}{' '}
          Generated quantities are a starting point for your review, not an engineered take-off.
        </p>
      </div>
    )
  }

  return (
    <div className="space-y-4">
      {error && <Notice tone="error">{error}</Notice>}
      <input ref={fileInput} type="file" accept=".csv,.xlsx,.pdf" className="sr-only" aria-label="Purchase list file"
        onChange={(e) => { const file = e.target.files?.[0]; if (file) upload.mutate(file); e.target.value = '' }} />
      <button type="button" onClick={() => fileInput.current?.click()} disabled={upload.isPending}
        className="flex w-full flex-col items-center gap-2 rounded-2xl border-2 border-dashed border-slate-300 bg-slate-50/60 px-6 py-10 text-center hover:border-accent-400 hover:bg-accent-50/40 disabled:opacity-60">
        <FileUp className="size-7 text-accent-600" />
        <span className="font-medium text-navy-900">{upload.isPending ? 'Reading file…' : 'Choose a CSV, Excel or PDF file'}</span>
        <span className="text-sm text-slate-500">Up to 5 MB. The file is read on the server and not stored.</span>
      </button>
      <ul className="list-disc space-y-1 pl-5 text-xs leading-relaxed text-slate-500">
        <li>CSV and Excel: a header row with <b>Description</b> (or Item), <b>Quantity</b> (or Qty) and <b>Unit</b> (or UOM). Optional: Material, Specifications, Notes.</li>
        <li>PDF: text PDFs only, one item per line with its quantity, for example "50 ft 3/4 in copper pipe". Scanned images are not read.</li>
      </ul>
    </div>
  )
}

export function DraftNotices({ draft }: { draft: DraftResponse }) {
  return (
    <div className="space-y-3">
      <Notice tone="warning" title="Review required before pricing">
        This list is a draft from: <b>{draft.sourceLabel}</b>. Check every quantity, unit and linked product. Suppliers are only searched after you approve the list on the project page.
      </Notice>
      {draft.assumptions.length > 0 && (
        <Notice tone="info" title="Assumptions made">
          <ul className="list-disc space-y-1 pl-4">{draft.assumptions.map((a, i) => <li key={i}>{a}</li>)}</ul>
        </Notice>
      )}
      {draft.warnings.length > 0 && (
        <Notice tone="warning" title="Things to check">
          <ul className="list-disc space-y-1 pl-4">{draft.warnings.map((w, i) => <li key={i}>{w}</li>)}</ul>
        </Notice>
      )}
    </div>
  )
}

function Step({ number, title, children }: { number: number; title: string; children: ReactNode }) {
  return (
    <Card>
      <CardHeader title={<span className="flex items-center gap-3"><span className="flex size-7 items-center justify-center rounded-full bg-navy-900 text-xs font-semibold text-white">{number}</span>{title}</span>} />
      {children}
    </Card>
  )
}

export function NewProjectPage() {
  const session = useSession()
  const navigate = useNavigate()
  const toast = useToast()
  const queryClient = useQueryClient()
  const [details, setDetails] = useState({ name: '', description: '', industry: session.organization.industry, requiredByDate: '' })
  const [source, setSource] = useState<Source>(session.plan.aiMaterialLists ? 'describe' : 'manual')
  const [rows, setRows] = useState<DraftRow[]>([emptyRow()])
  const [draft, setDraft] = useState<DraftResponse | null>(null)
  const [sourceText, setSourceText] = useState<string | null>(null)
  const [reuseId, setReuseId] = useState('')
  const [error, setError] = useState<string | null>(null)

  const projects = useQuery({ queryKey: ['projects', ''], queryFn: () => api.get<ProjectSummary[]>('/api/projects'), enabled: source === 'reuse' })

  const create = useMutation({
    mutationFn: () =>
      api.post<ProjectDetail>('/api/projects', {
        name: details.name.trim(),
        description: details.description.trim() || null,
        industry: details.industry,
        requiredByDate: details.requiredByDate || null,
        reuseProjectId: source === 'reuse' ? reuseId : null,
        initialList: source === 'reuse' ? null : { name: 'Material list', sourceText, assumptions: draft?.assumptions ?? [], items: rowsToItems(rows) },
      }),
    onSuccess: (project) => {
      queryClient.invalidateQueries({ queryKey: ['projects'] })
      queryClient.invalidateQueries({ queryKey: ['dashboard'] })
      toast.success('Project created. Review and approve the list to compare suppliers.')
      navigate(`/app/projects/${project.id}`)
    },
    onError: (e) => setError(errorMessage(e)),
  })

  if (!session.permissions.canEditLists)
    return (
      <>
        <PageHeader title="New purchasing project" />
        <Notice tone="warning" title="Your role is read-only">Viewers cannot create projects. Ask an administrator in your organization for the Employee role or higher.</Notice>
      </>
    )

  const showEditor = source === 'manual' || ((source === 'describe' || source === 'upload') && draft !== null)
  const ready = details.name.trim().length > 0 && (source === 'reuse' ? !!reuseId : showEditor && rowsValid(rows))
  const quotaError = create.error instanceof ApiError && create.error.code === 'plan_limit'

  return (
    <>
      <PageHeader
        title="New purchasing project"
        description="Build the material list for one job. You will review it before any supplier is searched."
        back={<Link to="/app/projects" className="inline-flex items-center gap-1 text-slate-500 hover:text-navy-900"><ArrowLeft className="size-4" /> Projects</Link>}
      />
      <div className="space-y-6">
        <Step number={1} title="Project details">
          <div className="grid gap-4 md:grid-cols-2">
            <Input label="Project name" required maxLength={200} value={details.name} onChange={(e) => setDetails({ ...details, name: e.target.value })} placeholder="e.g. Henderson residence repipe" />
            <Select label="Industry" value={details.industry} onChange={(e) => setDetails({ ...details, industry: e.target.value })}>
              {INDUSTRIES.map((i) => <option key={i.value} value={i.value}>{i.label}</option>)}
            </Select>
            <Input label="Materials needed by (optional)" type="date" value={details.requiredByDate} onChange={(e) => setDetails({ ...details, requiredByDate: e.target.value })} hint="Used as the delivery deadline when comparing suppliers." />
            <Input label="Notes (optional)" maxLength={2000} value={details.description} onChange={(e) => setDetails({ ...details, description: e.target.value })} />
          </div>
        </Step>

        <Step number={2} title="How do you want to build the list?">
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4" role="radiogroup" aria-label="List source">
            {sources.map((option) => (
              <button
                key={option.id}
                type="button"
                role="radio"
                aria-checked={source === option.id}
                onClick={() => { setSource(option.id); setDraft(null); setSourceText(null); setError(null); setRows(option.id === 'manual' ? [emptyRow()] : []) }}
                className={clsx('flex flex-col items-start rounded-xl border p-4 text-left transition', source === option.id ? 'border-accent-500 bg-accent-50 ring-1 ring-accent-500' : 'border-slate-200 hover:border-slate-300')}
              >
                <option.icon className={clsx('size-5', source === option.id ? 'text-accent-600' : 'text-slate-500')} />
                <span className="mt-2 flex items-center gap-2 font-medium text-navy-900">
                  {option.title}
                  {option.id === 'describe' && !session.plan.aiMaterialLists && <Badge>Pro</Badge>}
                </span>
                <span className="mt-1 text-xs leading-relaxed text-slate-500">{option.text}</span>
              </button>
            ))}
          </div>

          <div className="mt-6">
            {(source === 'describe' || source === 'upload') && (
              <DraftSourcePanel source={source} industry={details.industry} onDraft={(d, r, text) => { setDraft(d); setRows(r); setSourceText(text) }} />
            )}
            {source === 'reuse' && (
              projects.data && projects.data.length === 0 ? (
                <Notice tone="info">There are no previous projects to reuse yet.</Notice>
              ) : (
                <Select label="Project to copy lists from" value={reuseId} onChange={(e) => setReuseId(e.target.value)} hint="Lists are copied as a new draft. Approved substitutions are not carried over, and the copy must be reviewed again.">
                  <option value="">{projects.isLoading ? 'Loading…' : 'Choose a project'}</option>
                  {projects.data?.map((p) => <option key={p.id} value={p.id}>{p.name} ({p.itemCount} items)</option>)}
                </Select>
              )
            )}
          </div>
        </Step>

        {showEditor && (
          <Step number={3} title={source === 'manual' ? 'Enter the items' : 'Check the draft list'}>
            {draft && <div className="mb-5"><DraftNotices draft={draft} /></div>}
            {draft && rows.length === 0 ? (
              <Notice tone="info" title="Nothing was recognized">Add lines manually below, or go back and try different text or another file.</Notice>
            ) : null}
            <DraftEditor rows={rows} onChange={setRows} />
            <p className="mt-3 text-xs text-slate-500">Linking a line to a catalog product is what makes it comparable across suppliers. You can also finish linking later on the project page.</p>
          </Step>
        )}

        {error && (quotaError ? <UpgradeNotice feature="More projects this month" reason={error} canManageBilling={session.permissions.canManageBilling} /> : <Notice tone="error">{error}</Notice>)}

        <div className="flex flex-wrap items-center justify-end gap-3">
          <LinkButton to="/app/projects" variant="ghost">Cancel</LinkButton>
          <Button variant="primary" size="lg" disabled={!ready} loading={create.isPending} onClick={() => { setError(null); create.mutate() }}>
            Create project
          </Button>
        </div>
      </div>
    </>
  )
}
