import { useQuery } from '@tanstack/react-query'
import { AlertTriangle, ArrowRight, Bell, CheckCircle2, ClipboardList, FileText, FolderKanban, PiggyBank, Plus, Scale, Store } from 'lucide-react'
import { Link } from 'react-router-dom'
import { Badge, Card, CardHeader, EmptyState, ErrorState, LinkButton, Notice, PageHeader, Skeleton, Stat, StatusBadge, Table, Td, Th } from '../../components/ui'
import { api } from '../../lib/api'
import { useSession } from '../../lib/auth'
import { money, timeAgo } from '../../lib/format'
import type { Dashboard } from '../../lib/types'

export function DashboardPage() {
  const session = useSession()
  const { data, isLoading, error, refetch } = useQuery({ queryKey: ['dashboard'], queryFn: () => api.get<Dashboard>('/api/dashboard') })
  const firstName = session.user.fullName.split(' ')[0]

  return (
    <>
      <PageHeader
        title={`Welcome back, ${firstName}`}
        description={`${session.organization.name} · ${session.plan.name} plan`}
        actions={session.permissions.canEditLists && <LinkButton to="/app/projects/new" variant="primary" icon={<Plus className="size-4" />}>New purchasing project</LinkButton>}
      />

      {error ? (
        <ErrorState error={error} onRetry={refetch} />
      ) : isLoading || !data ? (
        <DashboardSkeleton />
      ) : (
        <div className="space-y-6">
          {data.listsAwaitingReview > 0 && (
            <Notice tone="warning" title={`${data.listsAwaitingReview} shopping list${data.listsAwaitingReview === 1 ? '' : 's'} waiting for quantity review`} action={<LinkButton to="/app/projects" size="sm">Review</LinkButton>}>
              Lists are not compared against suppliers until someone on your team has reviewed and approved the quantities.
            </Notice>
          )}

          <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
            <Stat label="Purchasing projects" value={data.totalProjects} hint={`${data.activeProjects} in progress${data.projectQuota !== null ? ` · ${data.projectsThisMonth} of ${data.projectQuota} created this month` : ''}`} icon={<FolderKanban className="size-5" />} />
            <Stat label="Shopping lists" value={data.shoppingLists} hint={data.listsAwaitingReview ? `${data.listsAwaitingReview} awaiting review` : 'All reviewed'} icon={<ClipboardList className="size-5" />} tone="blue" />
            <Stat label="Pending purchase orders" value={data.pendingPurchaseOrders} hint="Draft, pending approval or approved" icon={<FileText className="size-5" />} tone="amber" />
            <Stat label="Active price alerts" value={data.activePriceAlerts} hint={data.unreadNotifications ? `${data.unreadNotifications} unread notification${data.unreadNotifications === 1 ? '' : 's'}` : 'No unread notifications'} icon={<Bell className="size-5" />} tone="green" />
          </div>

          <SavingsCard data={data} />

          <div className="grid gap-6 xl:grid-cols-2">
            <Card padded={false}>
              <div className="px-5 pt-5 sm:px-6 sm:pt-6">
                <CardHeader title="Recent supplier comparisons" description="Baseline is one-stop shopping at a single supplier." action={<Link to="/app/projects" className="text-sm font-medium text-accent-600 hover:underline">All projects</Link>} />
              </div>
              {data.recentComparisons.length === 0 ? (
                <div className="px-6 pb-6"><EmptyState icon={<Scale className="size-5" />} title="No comparisons yet" description="Approve a shopping list, then run a supplier comparison from the project page." /></div>
              ) : (
                <Table>
                  <thead><tr><Th>Project</Th><Th align="right">Baseline</Th><Th align="right">Optimized</Th><Th align="right">Difference</Th></tr></thead>
                  <tbody>
                    {data.recentComparisons.map((run, index) => {
                      const difference = run.baselineCost !== null && run.optimizedCost !== null ? run.baselineCost - run.optimizedCost : null
                      return (
                        <tr key={`${run.projectId}-${index}`}>
                          <Td>
                            <Link to={`/app/projects/${run.projectId}?tab=compare`} className="font-medium text-navy-900 hover:text-accent-700">{run.projectName}</Link>
                            <p className="text-xs text-slate-500">{run.matchedItemCount} of {run.itemCount} items priced · {run.supplierCount} suppliers · {timeAgo(run.ranAt)}</p>
                          </Td>
                          <Td align="right">{money(run.baselineCost, 'Not priced')}</Td>
                          <Td align="right">{money(run.optimizedCost, 'Not priced')}</Td>
                          <Td align="right">
                            {difference === null ? <span className="text-slate-400">—</span> : <span className={difference > 0 ? 'font-semibold text-emerald-700' : 'text-slate-600'}>{money(difference)}</span>}
                            {run.hasUnknowns && <span className="mt-0.5 flex items-center justify-end gap-1 text-xs text-amber-700" title="Some shipping, tax or items are unknown and not included."><AlertTriangle className="size-3" /> has unknowns</span>}
                          </Td>
                        </tr>
                      )
                    })}
                  </tbody>
                </Table>
              )}
            </Card>

            <Card padded={false}>
              <div className="px-5 pt-5 sm:px-6 sm:pt-6">
                <CardHeader title="Current projects" action={<Link to="/app/projects" className="text-sm font-medium text-accent-600 hover:underline">View all</Link>} />
              </div>
              {data.recentProjects.length === 0 ? (
                <div className="px-6 pb-6">
                  <EmptyState icon={<FolderKanban className="size-5" />} title="Create your first project" description="Describe a job, enter items, or upload a list. FindMyPrice compares it across suppliers."
                    action={session.permissions.canEditLists && <LinkButton to="/app/projects/new" variant="primary">New project</LinkButton>} />
                </div>
              ) : (
                <ul className="divide-y divide-slate-100">
                  {data.recentProjects.map((project) => (
                    <li key={project.id}>
                      <Link to={`/app/projects/${project.id}`} className="flex items-center justify-between gap-4 px-5 py-3.5 hover:bg-slate-50 sm:px-6">
                        <span className="min-w-0">
                          <span className="block truncate font-medium text-navy-900">{project.name}</span>
                          <span className="block text-xs text-slate-500">{project.itemCount} item{project.itemCount === 1 ? '' : 's'} · updated {timeAgo(project.updatedAt)}</span>
                        </span>
                        <span className="flex shrink-0 items-center gap-2">
                          {project.needsReview && <Badge tone="amber">Needs review</Badge>}
                          <StatusBadge status={project.status} />
                          <ArrowRight className="size-4 text-slate-400" />
                        </span>
                      </Link>
                    </li>
                  ))}
                </ul>
              )}
            </Card>
          </div>

          <div className="grid gap-6 xl:grid-cols-3">
            <OrdersCard title="Pending purchase orders" orders={data.pendingOrders} empty="No purchase orders are waiting on you." />
            <OrdersCard title="Purchase history" orders={data.purchaseHistory} empty="Sent and received purchase orders will appear here." />
            <Card>
              <CardHeader title="Supplier integrations" action={<Link to="/app/suppliers" className="text-sm font-medium text-accent-600 hover:underline">Details</Link>} />
              <ul className="space-y-2.5">
                {data.suppliers.map((supplier) => (
                  <li key={supplier.id} className="flex items-center justify-between gap-3 text-sm" title={supplier.message}>
                    <span className="flex min-w-0 items-center gap-2">
                      {supplier.isHealthy ? <CheckCircle2 className="size-4 shrink-0 text-emerald-500" /> : <AlertTriangle className="size-4 shrink-0 text-amber-500" />}
                      <span className="truncate text-navy-900">{supplier.name}</span>
                    </span>
                    {supplier.mode === 'Live' ? <Badge tone="green">LIVE</Badge> : <Badge tone="violet">DEMO DATA</Badge>}
                  </li>
                ))}
              </ul>
              {data.suppliers.every((s) => s.mode !== 'Live') && (
                <p className="mt-4 flex items-start gap-2 border-t border-slate-100 pt-3 text-xs leading-relaxed text-slate-500">
                  <Store className="mt-0.5 size-3.5 shrink-0" /> No live supplier API is connected. All prices currently come from demo data.
                </p>
              )}
            </Card>
          </div>
        </div>
      )}
    </>
  )
}

function SavingsCard({ data }: { data: Dashboard }) {
  const percent = data.savingsBaselineTotal > 0 ? (data.savingsOpportunity / data.savingsBaselineTotal) * 100 : 0
  return (
    <Card className="bg-gradient-to-br from-navy-900 to-navy-800 text-white">
      <div className="flex flex-wrap items-start justify-between gap-6">
        <div className="flex items-start gap-4">
          <div className="flex size-11 shrink-0 items-center justify-center rounded-xl bg-white/10"><PiggyBank className="size-5 text-accent-400" /></div>
          <div>
            <p className="text-sm text-navy-200">Estimated savings opportunity</p>
            {data.savingsProjectCount === 0 ? (
              <p className="mt-1 text-lg font-medium text-white">Run a supplier comparison to see an estimate.</p>
            ) : (
              <p className="mt-0.5 text-3xl font-semibold tracking-tight text-white tabular">
                {money(data.savingsOpportunity)} <span className="text-base font-normal text-navy-200">({percent.toFixed(1)}% of baseline)</span>
              </p>
            )}
            {data.savingsContainDemoData && <Badge tone="violet" className="mt-2">Based on DEMO prices</Badge>}
          </div>
        </div>
        {data.savingsProjectCount > 0 && (
          <dl className="grid grid-cols-2 gap-x-8 gap-y-1 text-sm">
            <dt className="text-navy-300">Baseline total</dt>
            <dd className="text-right font-medium text-white tabular">{money(data.savingsBaselineTotal)}</dd>
            <dt className="text-navy-300">Optimized total</dt>
            <dd className="text-right font-medium text-white tabular">{money(data.savingsOptimizedTotal)}</dd>
            <dt className="text-navy-300">Projects included</dt>
            <dd className="text-right font-medium text-white tabular">{data.savingsProjectCount}</dd>
          </dl>
        )}
      </div>
      <p className="mt-4 border-t border-white/10 pt-3 text-xs leading-relaxed text-navy-300">
        <span className="font-medium text-navy-100">How this is measured:</span> {data.savingsBasis} It is an estimate of what the optimized plan would cost against that baseline, not money already saved.
      </p>
    </Card>
  )
}

function OrdersCard({ title, orders, empty }: { title: string; orders: Dashboard['pendingOrders']; empty: string }) {
  return (
    <Card>
      <CardHeader title={title} action={<Link to="/app/purchase-orders" className="text-sm font-medium text-accent-600 hover:underline">All</Link>} />
      {orders.length === 0 ? (
        <p className="py-4 text-sm text-slate-500">{empty}</p>
      ) : (
        <ul className="space-y-3">
          {orders.map((order) => (
            <li key={order.id}>
              <Link to={`/app/purchase-orders/${order.id}`} className="flex items-center justify-between gap-3 text-sm hover:text-accent-700">
                <span className="min-w-0">
                  <span className="block font-medium text-navy-900">{order.poNumber}</span>
                  <span className="block truncate text-xs text-slate-500">{order.supplierName}</span>
                </span>
                <span className="shrink-0 text-right">
                  <span className="block font-medium text-navy-900 tabular">{money(order.total)}</span>
                  <StatusBadge status={order.status} />
                </span>
              </Link>
            </li>
          ))}
        </ul>
      )}
    </Card>
  )
}

function DashboardSkeleton() {
  return (
    <div className="space-y-6" role="status" aria-label="Loading dashboard">
      <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">{[0, 1, 2, 3].map((i) => <Skeleton key={i} className="h-28 rounded-2xl" />)}</div>
      <Skeleton className="h-36 rounded-2xl" />
      <div className="grid gap-6 xl:grid-cols-2"><Skeleton className="h-72 rounded-2xl" /><Skeleton className="h-72 rounded-2xl" /></div>
    </div>
  )
}
