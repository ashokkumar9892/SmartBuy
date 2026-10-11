import { useQuery } from '@tanstack/react-query'
import { BarChart3, Download, FileText, Wallet } from 'lucide-react'
import { useState } from 'react'
import { Button, Card, CardHeader, EmptyState, ErrorState, LinkButton, Notice, PageHeader, Skeleton, SkeletonRows, Stat, Table, Td, Th, UpgradeNotice, useToast } from '../../components/ui'
import { api, ApiError, errorMessage } from '../../lib/api'
import { useSession } from '../../lib/auth'
import { money } from '../../lib/format'
import { ROLE_LABELS, type Role, type SpendReport, type SpendRow } from '../../lib/types'

const ROLE_ORDER: Role[] = ['Viewer', 'Employee', 'PurchasingManager', 'Administrator', 'Owner']

const monthFormat = new Intl.DateTimeFormat('en-US', { month: 'short', year: 'numeric', timeZone: 'UTC' })
/** Turns the server's "2026-03" bucket into "Mar 2026"; anything else is shown as sent. */
function monthLabel(label: string) {
  const match = /^(\d{4})-(\d{2})$/.exec(label)
  return match ? monthFormat.format(new Date(Date.UTC(Number(match[1]), Number(match[2]) - 1, 1))) : label
}

function ReportContents() {
  return (
    <Card>
      <CardHeader title="What the spend report contains" />
      <ul className="list-disc space-y-1.5 pl-5 text-sm text-slate-600">
        <li>Total committed spend and the number of purchase orders behind it.</li>
        <li>Spend broken down by supplier, by month and by project.</li>
        <li>Only purchase orders that are Approved, Sent, Partially Received or Received are counted. Drafts and cancelled orders are not.</li>
        <li>A CSV export of the same figures for your accounting tools.</li>
      </ul>
    </Card>
  )
}

export function ReportsPage() {
  const session = useSession()
  const toast = useToast()
  const [exporting, setExporting] = useState(false)
  const hasFeature = session.plan.advancedReporting
  const hasRole = ROLE_ORDER.indexOf(session.organization.role) >= ROLE_ORDER.indexOf('PurchasingManager')
  const enabled = hasFeature && hasRole

  const query = useQuery({
    queryKey: ['reports', 'spend'],
    queryFn: () => api.get<SpendReport>('/api/reports/spend'),
    enabled,
    retry: (failures, error) => !(error instanceof ApiError && (error.status === 402 || error.status === 403)) && failures < 2,
  })

  const exportCsv = async () => {
    setExporting(true)
    try {
      await api.download('/api/reports/spend.csv', 'smartbuy-spend-report.csv')
    } catch (err) {
      toast.error(errorMessage(err))
    } finally {
      setExporting(false)
    }
  }

  const header = (showExport: boolean) => (
    <PageHeader
      title="Reports"
      description="Committed spend across your purchase orders, by supplier, month and project."
      actions={
        showExport && (
          <Button icon={<Download className="size-4" aria-hidden />} onClick={exportCsv} loading={exporting}>
            Export CSV
          </Button>
        )
      }
    />
  )

  if (!hasFeature)
    return (
      <>
        {header(false)}
        <div className="space-y-6">
          <UpgradeNotice
            feature="Advanced reporting"
            reason={`The ${session.plan.name} plan does not include the spend report. Upgrade to see spend by supplier, month and project.`}
            canManageBilling={session.permissions.canManageBilling}
          />
          <ReportContents />
        </div>
      </>
    )

  if (!hasRole)
    return (
      <>
        {header(false)}
        <div className="space-y-6">
          <Notice tone="warning" title="Your role does not include reports">
            Spend reports are available to Purchasing Managers, Administrators and Organization Owners. Your role is {ROLE_LABELS[session.organization.role]}. Ask an administrator to change your role if you need this
            report.
          </Notice>
          <ReportContents />
        </div>
      </>
    )

  if (query.isPending)
    return (
      <>
        {header(false)}
        <div className="mb-6 grid gap-4 sm:grid-cols-2">
          <Skeleton className="h-24 w-full" />
          <Skeleton className="h-24 w-full" />
        </div>
        <SkeletonRows rows={5} />
      </>
    )

  if (query.isError) {
    const error = query.error
    // The session can be out of date if the plan or role changed in another tab.
    if (error instanceof ApiError && error.status === 402)
      return (
        <>
          {header(false)}
          <div className="space-y-6">
            <UpgradeNotice feature="Advanced reporting" reason={error.message} canManageBilling={session.permissions.canManageBilling} />
            <ReportContents />
          </div>
        </>
      )
    if (error instanceof ApiError && error.status === 403)
      return (
        <>
          {header(false)}
          <Notice tone="warning" title="Your role does not include reports">
            {error.message}
          </Notice>
        </>
      )
    return (
      <>
        {header(false)}
        <ErrorState error={error} onRetry={() => query.refetch()} />
      </>
    )
  }

  const report = query.data
  const empty = report.orderCount === 0

  return (
    <>
      {header(!empty)}
      <div className="space-y-6">
        <div className="grid gap-4 sm:grid-cols-2">
          <Stat label="Total committed spend" value={money(report.totalCommitted)} icon={<Wallet className="size-5" aria-hidden />} tone="blue" />
          <Stat label="Purchase orders counted" value={report.orderCount} icon={<FileText className="size-5" aria-hidden />} />
        </div>
        <p className="rounded-xl border border-slate-200 bg-white px-4 py-3 text-sm text-slate-600">
          <span className="font-medium text-navy-900">How spend is counted: </span>
          {report.basis}
        </p>

        {empty ? (
          <EmptyState
            icon={<BarChart3 className="size-6" />}
            title="No committed spend yet"
            description="This report fills in once a purchase order has been approved, sent or received. Draft and cancelled orders are not counted."
            action={<LinkButton to="/app/purchase-orders" variant="primary">View purchase orders</LinkButton>}
          />
        ) : (
          <>
            <SpendSection title="Spend by supplier" labelHeading="Supplier" rows={report.bySupplier} total={report.totalCommitted} />
            <SpendSection title="Spend by month" description="Grouped by the month each purchase order was created." labelHeading="Month" rows={report.byMonth} total={report.totalCommitted} formatLabel={monthLabel} />
            <SpendSection title="Spend by project" labelHeading="Project" rows={report.byProject} total={report.totalCommitted} />
          </>
        )}
      </div>
    </>
  )
}

function SpendSection({ title, description, labelHeading, rows, total, formatLabel }: { title: string; description?: string; labelHeading: string; rows: SpendRow[]; total: number; formatLabel?: (label: string) => string }) {
  const max = rows.reduce((highest, row) => Math.max(highest, row.total), 0)
  return (
    <Card padded={false}>
      <div className="px-5 pt-5 sm:px-6 sm:pt-6">
        <CardHeader title={title} description={description} />
      </div>
      {rows.length === 0 ? (
        <p className="px-5 pb-5 text-sm text-slate-500 sm:px-6 sm:pb-6">Nothing to show for this grouping.</p>
      ) : (
        <Table>
          <thead>
            <tr>
              <Th>{labelHeading}</Th>
              <Th className="w-1/2 min-w-48">Share of spend</Th>
              <Th align="right">Orders</Th>
              <Th align="right">Total</Th>
            </tr>
          </thead>
          <tbody>
            {rows.map((row) => {
              const width = max > 0 ? Math.max(0, (row.total / max) * 100) : 0
              const share = total > 0 ? (row.total / total) * 100 : null
              return (
                <tr key={row.label}>
                  <Td className="font-medium text-navy-900">{formatLabel ? formatLabel(row.label) : row.label}</Td>
                  <Td className="align-middle">
                    <div className="flex items-center gap-3">
                      <div className="h-2.5 flex-1 overflow-hidden rounded-full bg-slate-100" aria-hidden>
                        <div className="h-full rounded-full bg-accent-500" style={{ width: `${width}%` }} />
                      </div>
                      <span className="w-12 shrink-0 text-right text-xs text-slate-500 tabular">{share === null ? '—' : `${share.toFixed(share < 10 ? 1 : 0)}%`}</span>
                    </div>
                  </Td>
                  <Td align="right">{row.orders}</Td>
                  <Td align="right" className="font-medium text-navy-900">
                    {money(row.total)}
                  </Td>
                </tr>
              )
            })}
          </tbody>
        </Table>
      )}
    </Card>
  )
}
