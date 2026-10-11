import { useQuery } from '@tanstack/react-query'
import { FolderKanban, Plus, Search } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { Badge, Card, EmptyState, ErrorState, LinkButton, PageHeader, SkeletonRows, StatusBadge, Table, Td, Th } from '../../components/ui'
import { api } from '../../lib/api'
import { useSession } from '../../lib/auth'
import { formatDate, money, timeAgo } from '../../lib/format'
import { industryLabel, type ProjectSummary } from '../../lib/types'

const statuses = ['Draft', 'Active', 'Ordered', 'Completed', 'Archived']

export function ProjectsPage() {
  const session = useSession()
  const [search, setSearch] = useState('')
  const [status, setStatus] = useState('')
  const { data, isLoading, error, refetch } = useQuery({
    queryKey: ['projects', status],
    queryFn: () => api.get<ProjectSummary[]>(`/api/projects${status ? `?status=${status}` : ''}`),
  })
  const term = search.trim().toLowerCase()
  const projects = data?.filter((p) => !term || p.name.toLowerCase().includes(term) || (p.description ?? '').toLowerCase().includes(term)) ?? []
  const canCreate = session.permissions.canEditLists

  return (
    <>
      <PageHeader
        title="Purchasing projects"
        description="Each project holds the shopping lists for one job, the supplier comparison, the cart and its purchase orders."
        actions={canCreate && <LinkButton to="/app/projects/new" variant="primary" icon={<Plus className="size-4" />}>New project</LinkButton>}
      />

      <div className="mb-4 flex flex-wrap items-center gap-3">
        <div className="relative min-w-56 flex-1 sm:max-w-sm">
          <Search className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-slate-400" aria-hidden />
          <input
            type="search"
            aria-label="Search projects"
            placeholder="Search projects"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            className="h-10 w-full rounded-lg border border-slate-300 bg-white pl-9 pr-3 text-sm shadow-sm focus:border-accent-500 focus:outline-none focus:ring-2 focus:ring-accent-500/25"
          />
        </div>
        <select aria-label="Filter by status" value={status} onChange={(e) => setStatus(e.target.value)} className="h-10 rounded-lg border border-slate-300 bg-white px-3 text-sm shadow-sm focus:border-accent-500 focus:outline-none">
          <option value="">All statuses</option>
          {statuses.map((s) => <option key={s} value={s}>{s}</option>)}
        </select>
      </div>

      {error ? (
        <ErrorState error={error} onRetry={refetch} />
      ) : isLoading ? (
        <Card><SkeletonRows rows={5} /></Card>
      ) : projects.length === 0 ? (
        <EmptyState
          icon={<FolderKanban className="size-5" />}
          title={data?.length ? 'No projects match your search' : 'No projects yet'}
          description={data?.length ? 'Try a different search term or status.' : 'Create a project to build a material list and compare it across suppliers.'}
          action={!data?.length && canCreate ? <LinkButton to="/app/projects/new" variant="primary" icon={<Plus className="size-4" />}>New project</LinkButton> : undefined}
        />
      ) : (
        <Card padded={false}>
          <Table>
            <thead>
              <tr>
                <Th>Project</Th>
                <Th>Status</Th>
                <Th align="right">Items</Th>
                <Th>Needed by</Th>
                <Th align="right">Baseline</Th>
                <Th align="right">Optimized</Th>
                <Th>Updated</Th>
              </tr>
            </thead>
            <tbody>
              {projects.map((project) => (
                <tr key={project.id} className="hover:bg-slate-50/60">
                  <Td>
                    <Link to={`/app/projects/${project.id}`} className="font-medium text-navy-900 hover:text-accent-700">{project.name}</Link>
                    <p className="text-xs text-slate-500">
                      {industryLabel(project.industry)}
                      {project.cartLineCount > 0 && ` · ${project.cartLineCount} in cart`}
                      {project.purchaseOrderCount > 0 && ` · ${project.purchaseOrderCount} PO${project.purchaseOrderCount === 1 ? '' : 's'}`}
                    </p>
                  </Td>
                  <Td>
                    <span className="flex flex-wrap items-center gap-1.5">
                      <StatusBadge status={project.status} />
                      {project.hasUnapprovedList && <Badge tone="amber" title="A list in this project has not been reviewed and approved yet.">Needs review</Badge>}
                    </span>
                  </Td>
                  <Td align="right">{project.itemCount}</Td>
                  <Td>{formatDate(project.requiredByDate)}</Td>
                  <Td align="right">{project.lastBaselineCost === null ? <span className="text-slate-400">Not compared</span> : money(project.lastBaselineCost)}</Td>
                  <Td align="right">{project.lastOptimizedCost === null ? <span className="text-slate-400">—</span> : <span className="font-medium text-navy-900">{money(project.lastOptimizedCost)}</span>}</Td>
                  <Td className="text-slate-500">{timeAgo(project.updatedAt)}</Td>
                </tr>
              ))}
            </tbody>
          </Table>
        </Card>
      )}
    </>
  )
}
