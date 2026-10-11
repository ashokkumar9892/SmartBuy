import { useQuery } from '@tanstack/react-query'
import { Search, ShieldAlert } from 'lucide-react'
import { useEffect, useState } from 'react'
import { api } from '../lib/api'
import { unitLabel } from '../lib/format'
import { industryLabel, type CatalogProduct, type ResolveCandidate, type UnitOption } from '../lib/types'
import { Badge, Button, Modal, Spinner } from './ui'

export function useUnits() {
  return useQuery({ queryKey: ['units'], queryFn: () => api.get<UnitOption[]>('/api/catalog/units'), staleTime: Infinity })
}

/** Unit select limited to the units the server can convert. */
export function UnitSelect({ value, onChange, label = 'Unit', className }: { value: string; onChange: (unit: string) => void; label?: string; className?: string }) {
  const { data } = useUnits()
  const common = ['each', 'ft', 'in', 'yd', 'gal', 'qt', 'lb', 'oz', 'sqft', 'sqyd', 'cuft', 'cuyd', 'pair', 'dozen']
  const options = data ? common.filter((code) => data.some((u) => u.code === code)) : common
  return (
    <select aria-label={label} value={value} onChange={(e) => onChange(e.target.value)} className={className ?? 'h-9 w-full rounded-lg border border-slate-300 bg-white px-2 text-sm focus:border-accent-500 focus:outline-none'}>
      {!options.includes(value) && <option value={value}>{unitLabel(value)}</option>}
      {options.map((code) => <option key={code} value={code}>{unitLabel(code)}</option>)}
    </select>
  )
}

export function confidenceTone(confidence: number | null | undefined): { tone: 'green' | 'amber' | 'red'; label: string } {
  if (confidence === null || confidence === undefined) return { tone: 'red', label: 'Not linked' }
  if (confidence >= 0.7) return { tone: 'green', label: `${Math.round(confidence * 100)}% match` }
  return { tone: 'amber', label: `${Math.round(confidence * 100)}% · check` }
}

type Picked = { id: string; name: string; baseUnit: string; isSafetyCritical: boolean }

/**
 * Links a list line to a catalog product. Suggestions come from the server's deterministic resolver; the user can
 * also search the whole catalog or leave the line unlinked.
 */
export function ProductPicker({ open, onClose, description, candidates, currentId, onPick }: {
  open: boolean
  onClose: () => void
  description: string
  candidates?: ResolveCandidate[]
  currentId: string | null
  onPick: (product: Picked | null) => void
}) {
  const [query, setQuery] = useState('')
  const [debounced, setDebounced] = useState('')
  useEffect(() => {
    const timer = window.setTimeout(() => setDebounced(query.trim()), 250)
    return () => window.clearTimeout(timer)
  }, [query])
  useEffect(() => {
    if (open) setQuery('')
  }, [open])

  const suggestions = useQuery({
    queryKey: ['resolve', description],
    queryFn: () => api.post<ResolveCandidate[]>('/api/material-lists/resolve', { description }),
    enabled: open && !candidates && description.trim().length > 1,
  })
  const search = useQuery({
    queryKey: ['catalog-search', debounced],
    queryFn: () => api.get<CatalogProduct[]>(`/api/catalog/products?query=${encodeURIComponent(debounced)}`),
    enabled: open && debounced.length >= 2,
  })
  const suggested = candidates ?? suggestions.data ?? []

  const row = (key: string, name: string, meta: string, product: Picked, badge?: string) => (
    <li key={key}>
      <button
        type="button"
        onClick={() => { onPick(product); onClose() }}
        className={`flex w-full items-start justify-between gap-3 rounded-lg border px-3 py-2.5 text-left text-sm hover:border-accent-400 hover:bg-accent-50/50 ${product.id === currentId ? 'border-accent-500 bg-accent-50' : 'border-slate-200'}`}
      >
        <span className="min-w-0">
          <span className="flex flex-wrap items-center gap-2 font-medium text-navy-900">
            {name}
            {product.isSafetyCritical && <Badge tone="red"><ShieldAlert className="size-3" /> Safety-critical</Badge>}
          </span>
          <span className="block text-xs text-slate-500">{meta}</span>
        </span>
        <span className="flex shrink-0 flex-col items-end gap-1">
          {badge && <Badge tone="blue">{badge}</Badge>}
          {product.id === currentId && <span className="text-xs font-medium text-accent-700">Selected</span>}
        </span>
      </button>
    </li>
  )

  return (
    <Modal
      open={open}
      onClose={onClose}
      title="Link to a catalog product"
      description={<>For: <span className="font-medium text-navy-800">{description || 'this line'}</span></>}
      wide
      footer={
        <>
          {currentId && <Button variant="danger" onClick={() => { onPick(null); onClose() }}>Remove link</Button>}
          <Button onClick={onClose}>Close</Button>
        </>
      }
    >
      <p className="mb-4 text-sm text-slate-600">
        Suppliers are compared against the product you pick here, so the size, type and rating must be right. Lines without a link are kept on the list but cannot be priced.
      </p>

      {suggested.length > 0 && (
        <div className="mb-5">
          <p className="mb-2 text-xs font-semibold uppercase tracking-wide text-slate-500">Suggested matches</p>
          <ul className="space-y-2">
            {suggested.map((c) => row(c.productId, c.name, `${c.category} · priced per ${unitLabel(c.baseUnit)} · ${c.reason}`, { id: c.productId, name: c.name, baseUnit: c.baseUnit, isSafetyCritical: c.isSafetyCritical }, `${Math.round(c.confidence * 100)}%`))}
          </ul>
        </div>
      )}
      {suggestions.isLoading && <Spinner label="Finding matches" />}

      <label className="mb-2 block text-xs font-semibold uppercase tracking-wide text-slate-500" htmlFor="catalog-search">Search the catalog</label>
      <div className="relative">
        <Search className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-slate-400" aria-hidden />
        <input id="catalog-search" type="search" value={query} onChange={(e) => setQuery(e.target.value)} placeholder="e.g. copper elbow, GFCI, mulch" className="h-10 w-full rounded-lg border border-slate-300 pl-9 pr-3 text-sm focus:border-accent-500 focus:outline-none focus:ring-2 focus:ring-accent-500/25" />
      </div>
      <div className="mt-3">
        {search.isFetching && <Spinner label="Searching" />}
        {search.data && search.data.length === 0 && !search.isFetching && <p className="py-4 text-center text-sm text-slate-500">No catalog products match "{debounced}".</p>}
        {search.data && search.data.length > 0 && (
          <ul className="space-y-2">
            {search.data.slice(0, 20).map((p) => row(p.id, p.name, `${industryLabel(p.industry)} · ${p.category} · priced per ${unitLabel(p.baseUnit)}`, { id: p.id, name: p.name, baseUnit: p.baseUnit, isSafetyCritical: p.isSafetyCritical }))}
          </ul>
        )}
      </div>
    </Modal>
  )
}
