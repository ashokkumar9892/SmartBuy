import { keepPreviousData, useMutation, useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query'
import clsx from 'clsx'
import { AlertTriangle, ArrowLeft, Check, ChevronDown, ChevronRight, Pencil, Plus, RefreshCw, Search, ShieldAlert, Trash2, X } from 'lucide-react'
import { Fragment, useEffect, useState, type FormEvent, type ReactNode } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { Logo } from '../../components/AppLayout'
import {
  Badge,
  Button,
  Card,
  CardHeader,
  Checkbox,
  ConfirmDialog,
  DefinitionList,
  EmptyState,
  ErrorState,
  Input,
  LinkButton,
  Modal,
  Notice,
  PageHeader,
  Select,
  SkeletonRows,
  Stat,
  Table,
  Tabs,
  Td,
  Textarea,
  Th,
  Unknown,
  useToast,
} from '../../components/ui'
import { api, errorMessage } from '../../lib/api'
import { useAuth, useSession } from '../../lib/auth'
import { UNIT_LABELS, formatDate, formatDateTime, money, qty, timeAgo, unitLabel, unitMoney } from '../../lib/format'
import { INDUSTRIES, industryLabel, type CatalogProduct, type ProviderCapabilities, type UnitOption } from '../../lib/types'

// ---------- API shapes (derived from AdminController) ----------

interface AdminOverview {
  users: number
  organizations: number
  demoOrganizations: number
  projects: number
  purchaseOrders: number
  activeAlerts: number
  matchesNeedingReview: number
  supplierCallsLast24h: number
  supplierFailuresLast24h: number
  aiTokensLast30d: number
  aiCostLast30d: number
  errorsLast24h: number
  organizationsByPlan: Record<string, number>
  aiProvider: string
  databaseProvider: string
}

interface AdminUser {
  id: string
  email: string
  fullName: string
  isActive: boolean
  isPlatformAdmin: boolean
  isDemo: boolean
  createdAt: string
  lastLoginAt: string | null
  organizations: string[]
}

interface AdminOrganization {
  id: string
  name: string
  slug: string
  industry: string
  isDemo: boolean
  plan: string
  planCode: string
  subscriptionStatus: string
  stripeLinked: boolean
  members: number
  projects: number
  createdAt: string
}

interface AdminSupplier {
  id: string
  code: string
  name: string
  kind: string
  mode: string
  providerKey: string
  isEnabled: boolean
  shippingFlatFee: number | null
  freeShippingThreshold: number | null
  minimumOrderAmount: number | null
  cacheTtlMinutes: number
  affiliateProgramEnabled: boolean
  integrationNotes: string | null
  adapterRegistered: boolean
  isConfigured: boolean
  isHealthy: boolean
  statusMessage: string
  capabilities: ProviderCapabilities | null
  simulatedFault: string | null
  listings: number
  lastSyncAt: string | null
  lastSyncSucceeded: boolean | null
}

interface AdminSupplierUpdate {
  isEnabled: boolean
  shippingFlatFee: number | null
  freeShippingThreshold: number | null
  minimumOrderAmount: number | null
  cacheTtlMinutes: number
  affiliateProgramEnabled: boolean
}

type MatchStatus = 'NeedsReview' | 'AutoMatched' | 'Confirmed' | 'Rejected'

interface AdminMatch {
  id: string
  status: string
  method: string
  confidence: number
  reasons: string[]
  supplierName: string
  listingTitle: string
  listingBrand: string | null
  listingModel: string | null
  listingGtin: string | null
  packQuantity: number
  packUnit: string
  listingSpecs: Record<string, string>
  price: number | null
  canonicalProductId: string
  canonicalName: string
  canonicalUnit: string
  canonicalSpecs: Record<string, string>
  updatedAt: string
}

interface CatalogInput {
  name: string
  industry: string
  category: string
  brand: string | null
  model: string | null
  mpn: string | null
  gtin: string | null
  material: string | null
  baseUnit: string
  isSafetyCritical: boolean
  safetyNote: string | null
  specifications: Record<string, string>
}

interface UsageBucket {
  key: string
  calls: number
  failures: number
  inputTokens: number
  outputTokens: number
  cost: number
  averageMs: number
}

interface ProviderUsage {
  windowDays: number
  bySupplier: UsageBucket[]
  recent: { id: string; supplier: string; operation: string; success: boolean; durationMs: number; itemCount: number | null; error: string | null; createdAt: string }[]
}

interface AiUsage {
  windowDays: number
  activeProvider: string
  activeModel: string | null
  costNote: string
  total: UsageBucket
  byFeature: UsageBucket[]
  byProvider: UsageBucket[]
  recent: {
    id: string
    provider: string
    model: string | null
    feature: string
    inputTokens: number
    outputTokens: number
    estimatedCostUsd: number
    success: boolean
    durationMs: number
    error: string | null
    createdAt: string
  }[]
}

interface ProjectAnalytics {
  totalProjects: number
  byStatus: Record<string, number>
  byIndustry: Record<string, number>
  createdLast30Days: number
  comparisonRuns: number
  projectsWithPricedPlan: number
  baselineTotal: number
  optimizedTotal: number
  averageSavingsPercent: number
  note: string
}

interface AffiliateClicks {
  total: number
  taggedAffiliateClicks: number
  note: string
  bySupplier: { supplier: string; clicks: number; affiliate: number }[]
  recent: { id: string; supplier: string; targetUrl: string; isAffiliateLink: boolean; createdAt: string }[]
}

interface ErrorLogRow {
  id: string
  message: string
  path: string | null
  method: string | null
  traceId: string | null
  detail: string | null
  userId: string | null
  createdAt: string
}

interface AuditRow {
  id: string
  action: string
  entityType: string | null
  entityId: string | null
  details: string | null
  organizationId: string | null
  userId: string | null
  ipAddress: string | null
  createdAt: string
}

interface FeatureFlag {
  key: string
  description: string
  isEnabled: boolean
  updatedAt: string
}

interface AlertJobResult {
  alertsChecked: number
  alertsTriggered: number
  providerIssues: number
  ranAt: string
}

// ---------- Shared helpers ----------

const num = new Intl.NumberFormat('en-US')
const humanize = (value: string) => value.replace(/([a-z])([A-Z])/g, '$1 $2')
const shortId = (value: string | null) => (value ? value.slice(0, 8) : '—')

function useDebounced<T>(value: T, delay = 300) {
  const [debounced, setDebounced] = useState(value)
  useEffect(() => {
    const timer = window.setTimeout(() => setDebounced(value), delay)
    return () => window.clearTimeout(timer)
  }, [value, delay])
  return debounced
}

/** Loading skeleton, error with retry, then the loaded content. */
function Loadable<T>({ query, rows = 5, children }: { query: UseQueryResult<T>; rows?: number; children: (data: T) => ReactNode }) {
  if (query.isPending) return <SkeletonRows rows={rows} />
  if (query.isError) return <ErrorState error={query.error} onRetry={() => query.refetch()} />
  return <>{children(query.data)}</>
}

function Switch({ checked, onChange, label, disabled }: { checked: boolean; onChange: (next: boolean) => void; label: string; disabled?: boolean }) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      title={label}
      disabled={disabled}
      onClick={() => onChange(!checked)}
      className={clsx(
        'relative inline-flex h-6 w-11 shrink-0 items-center rounded-full transition-colors focus:outline-none focus-visible:ring-2 focus-visible:ring-accent-500/40 disabled:opacity-50',
        checked ? 'bg-accent-600' : 'bg-slate-300',
      )}
    >
      <span className={clsx('inline-block size-5 rounded-full bg-white shadow transition-transform', checked ? 'translate-x-5.5' : 'translate-x-0.5')} />
    </button>
  )
}

function YesNo({ value, yes = 'Yes', no = 'No', noTone = 'neutral' }: { value: boolean; yes?: string; no?: string; noTone?: 'neutral' | 'red' | 'amber' }) {
  return <Badge tone={value ? 'green' : noTone}>{value ? yes : no}</Badge>
}

function TableCard({ title, description, action, children }: { title: ReactNode; description?: ReactNode; action?: ReactNode; children: ReactNode }) {
  return (
    <Card padded={false}>
      <div className="px-5 pt-5 sm:px-6 sm:pt-6">
        <CardHeader title={title} description={description} action={action} />
      </div>
      {children}
    </Card>
  )
}

function Bars({ entries, emptyLabel }: { entries: [string, number][]; emptyLabel: string }) {
  if (entries.length === 0) return <p className="text-sm text-slate-500">{emptyLabel}</p>
  const max = Math.max(...entries.map(([, value]) => value))
  return (
    <ul className="space-y-2.5">
      {entries.map(([label, value]) => (
        <li key={label} className="grid grid-cols-[minmax(6rem,10rem)_1fr_auto] items-center gap-3 text-sm">
          <span className="truncate text-navy-800" title={label}>
            {label}
          </span>
          <span className="h-2.5 overflow-hidden rounded-full bg-slate-100" aria-hidden>
            <span className="block h-full rounded-full bg-accent-500" style={{ width: `${max > 0 ? (value / max) * 100 : 0}%` }} />
          </span>
          <span className="w-12 text-right font-medium text-navy-900 tabular">{num.format(value)}</span>
        </li>
      ))}
    </ul>
  )
}

function SpecList({ specs }: { specs: Record<string, string> }) {
  const entries = Object.entries(specs)
  if (entries.length === 0) return <span className="text-slate-400">No specifications recorded</span>
  return (
    <dl className="space-y-0.5 text-xs">
      {entries.map(([key, value]) => (
        <div key={key} className="flex gap-1.5">
          <dt className="shrink-0 text-slate-500">{key.replace(/_/g, ' ')}:</dt>
          <dd className="text-navy-800">{value}</dd>
        </div>
      ))}
    </dl>
  )
}

function UsageTable({ heading, buckets, tokens }: { heading: string; buckets: UsageBucket[]; tokens?: boolean }) {
  return (
    <Table>
      <thead>
        <tr>
          <Th>{heading}</Th>
          <Th align="right">Calls</Th>
          <Th align="right">Failures</Th>
          {tokens && <Th align="right">Input tokens</Th>}
          {tokens && <Th align="right">Output tokens</Th>}
          {tokens && <Th align="right">Est. cost</Th>}
          <Th align="right">Average time</Th>
        </tr>
      </thead>
      <tbody>
        {buckets.map((bucket) => (
          <tr key={bucket.key}>
            <Td className="font-medium text-navy-900">{bucket.key}</Td>
            <Td align="right">{num.format(bucket.calls)}</Td>
            <Td align="right" className={bucket.failures > 0 ? 'font-medium text-red-700' : undefined}>
              {num.format(bucket.failures)}
            </Td>
            {tokens && <Td align="right">{num.format(bucket.inputTokens)}</Td>}
            {tokens && <Td align="right">{num.format(bucket.outputTokens)}</Td>}
            {tokens && <Td align="right">{unitMoney(bucket.cost)}</Td>}
            <Td align="right">{bucket.calls > 0 ? `${num.format(bucket.averageMs)} ms` : '—'}</Td>
          </tr>
        ))}
      </tbody>
    </Table>
  )
}

// ---------- Page shell ----------

const TABS = [
  { id: 'overview', label: 'Overview' },
  { id: 'organizations', label: 'Organizations' },
  { id: 'users', label: 'Users' },
  { id: 'suppliers', label: 'Suppliers' },
  { id: 'catalog', label: 'Catalog' },
  { id: 'matches', label: 'Match review' },
  { id: 'api-usage', label: 'API usage' },
  { id: 'ai-usage', label: 'AI usage' },
  { id: 'analytics', label: 'Analytics' },
  { id: 'affiliate', label: 'Affiliate clicks' },
  { id: 'errors', label: 'Errors' },
  { id: 'audit', label: 'Audit log' },
  { id: 'flags', label: 'Feature flags' },
  { id: 'operations', label: 'Operations' },
] as const

type TabId = (typeof TABS)[number]['id']

const useOverview = (enabled = true) => useQuery({ queryKey: ['admin', 'overview'], queryFn: () => api.get<AdminOverview>('/api/admin/overview'), enabled })

export function AdminPage() {
  const session = useSession()
  const [searchParams, setSearchParams] = useSearchParams()
  const isAdmin = session.user.isPlatformAdmin
  const overview = useOverview(isAdmin)
  const requested = searchParams.get('tab')
  const tab: TabId = TABS.find((t) => t.id === requested)?.id ?? 'overview'

  return (
    <div className="min-h-screen bg-slate-50">
      <header className="sticky top-0 z-20 border-b border-slate-200 bg-white/90 backdrop-blur">
        <div className="mx-auto flex h-16 w-full max-w-7xl items-center gap-4 px-4 sm:px-6 lg:px-8">
          <Link to="/app" aria-label="FindMyPrice workspace">
            <Logo />
          </Link>
          <span className="hidden h-6 w-px bg-slate-200 sm:block" aria-hidden />
          <span className="hidden text-sm font-semibold text-navy-900 sm:block">Platform administration</span>
          <Link to="/app" className="ml-auto inline-flex items-center gap-1.5 rounded-lg px-3 py-2 text-sm font-medium text-navy-700 hover:bg-navy-50">
            <ArrowLeft className="size-4" aria-hidden />
            Back to workspace
          </Link>
        </div>
      </header>

      <main className="mx-auto w-full max-w-7xl px-4 py-6 sm:px-6 lg:px-8 lg:py-8">
        {!isAdmin ? (
          <EmptyState
            icon={<ShieldAlert className="size-6" />}
            title="Platform administrator access is required"
            description="This area is for FindMyPrice platform operators. Organization owners and administrators manage their own organization from the workspace, and are not platform administrators."
            action={<LinkButton to="/app" variant="primary">Back to workspace</LinkButton>}
          />
        ) : (
          <>
            <PageHeader title="Platform administration" description={`Cross-organization operations for FindMyPrice operators. Signed in as ${session.user.fullName}. Changes made here are recorded in the audit log.`} />
            <Tabs
              tabs={TABS.map((t) => ({ id: t.id, label: t.label, count: t.id === 'matches' ? overview.data?.matchesNeedingReview : undefined }))}
              value={tab}
              onChange={(id) => setSearchParams({ tab: id })}
            />
            {tab === 'overview' && <OverviewTab />}
            {tab === 'organizations' && <OrganizationsTab />}
            {tab === 'users' && <UsersTab />}
            {tab === 'suppliers' && <SuppliersTab />}
            {tab === 'catalog' && <CatalogTab />}
            {tab === 'matches' && <MatchesTab />}
            {tab === 'api-usage' && <ProviderUsageTab />}
            {tab === 'ai-usage' && <AiUsageTab />}
            {tab === 'analytics' && <AnalyticsTab />}
            {tab === 'affiliate' && <AffiliateTab />}
            {tab === 'errors' && <ErrorsTab />}
            {tab === 'audit' && <AuditTab />}
            {tab === 'flags' && <FlagsTab />}
            {tab === 'operations' && <OperationsTab />}
          </>
        )}
      </main>
    </div>
  )
}

// ---------- Overview ----------

function OverviewTab() {
  const query = useOverview()
  return (
    <Loadable query={query} rows={6}>
      {(data) => {
        const plans = Object.entries(data.organizationsByPlan).sort((a, b) => b[1] - a[1])
        return (
          <div className="space-y-6">
            <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
              <Stat label="Users" value={num.format(data.users)} />
              <Stat label="Organizations" value={num.format(data.organizations)} hint={`${num.format(data.demoOrganizations)} demo`} />
              <Stat label="Projects" value={num.format(data.projects)} />
              <Stat label="Purchase orders" value={num.format(data.purchaseOrders)} />
              <Stat label="Active price alerts" value={num.format(data.activeAlerts)} />
              <Stat
                label="Matches needing review"
                value={num.format(data.matchesNeedingReview)}
                hint={data.matchesNeedingReview > 0 ? <Link to="/admin?tab=matches" className="font-medium text-accent-600 hover:underline">Open the review queue</Link> : 'Queue is clear'}
              />
              <Stat label="Supplier calls (24 h)" value={num.format(data.supplierCallsLast24h)} hint={`${num.format(data.supplierFailuresLast24h)} failed`} />
              <Stat label="Errors (24 h)" value={num.format(data.errorsLast24h)} hint={data.errorsLast24h > 0 ? <Link to="/admin?tab=errors" className="font-medium text-accent-600 hover:underline">View errors</Link> : undefined} />
              <Stat label="AI tokens (30 days)" value={num.format(data.aiTokensLast30d)} hint="Input and output combined" />
              <Stat label="Estimated AI cost (30 days)" value={unitMoney(data.aiCostLast30d)} hint="Estimate from configured token rates" />
            </div>
            <div className="grid gap-6 lg:grid-cols-2">
              <Card>
                <CardHeader title="Organizations by plan" description="Organizations with a subscription record." />
                <Bars entries={plans} emptyLabel="No subscriptions recorded yet." />
              </Card>
              <Card>
                <CardHeader title="System" />
                <DefinitionList
                  items={[
                    { label: 'Active AI provider', value: data.aiProvider },
                    { label: 'Database provider', value: data.databaseProvider },
                  ]}
                />
              </Card>
            </div>
          </div>
        )
      }}
    </Loadable>
  )
}

// ---------- Organizations ----------

const PLAN_OPTIONS = [
  { code: 'free', label: 'Free' },
  { code: 'pro', label: 'Pro' },
  { code: 'business', label: 'Business' },
]

function OrganizationsTab() {
  const toast = useToast()
  const queryClient = useQueryClient()
  const { refreshSession } = useAuth()
  const query = useQuery({ queryKey: ['admin', 'organizations'], queryFn: () => api.get<AdminOrganization[]>('/api/admin/organizations') })
  const [pending, setPending] = useState<{ organization: AdminOrganization; planCode: string } | null>(null)
  const [error, setError] = useState<string | null>(null)

  const setPlan = useMutation({
    mutationFn: (input: { organization: AdminOrganization; planCode: string }) => api.put(`/api/admin/organizations/${input.organization.id}/plan`, { planCode: input.planCode }),
    onSuccess: async (_, input) => {
      toast.success(`${input.organization.name} is now on the ${PLAN_OPTIONS.find((p) => p.code === input.planCode)?.label ?? input.planCode} plan.`)
      setPending(null)
      queryClient.invalidateQueries({ queryKey: ['admin', 'organizations'] })
      queryClient.invalidateQueries({ queryKey: ['admin', 'overview'] })
      queryClient.invalidateQueries({ queryKey: ['admin', 'audit'] })
      queryClient.invalidateQueries({ queryKey: ['billing'] })
      await refreshSession()
    },
    onError: (err) => setError(errorMessage(err)),
  })

  return (
    <Loadable query={query}>
      {(organizations) =>
        organizations.length === 0 ? (
          <EmptyState title="No organizations yet" description="Organizations appear here when someone registers or the demo data is created." />
        ) : (
          <TableCard title="Organizations and subscriptions" description="Most recent 500. Changing a plan here is an operator override and takes no payment.">
            <Table>
              <thead>
                <tr>
                  <Th>Organization</Th>
                  <Th>Industry</Th>
                  <Th>Plan</Th>
                  <Th>Subscription</Th>
                  <Th>Stripe</Th>
                  <Th align="right">Members</Th>
                  <Th align="right">Projects</Th>
                  <Th>Created</Th>
                </tr>
              </thead>
              <tbody>
                {organizations.map((organization) => (
                  <tr key={organization.id}>
                    <Td>
                      <span className="flex flex-wrap items-center gap-2 font-medium text-navy-900">
                        {organization.name}
                        {organization.isDemo && <Badge tone="violet">Demo</Badge>}
                      </span>
                      <span className="text-xs text-slate-500">{organization.slug}</span>
                    </Td>
                    <Td>{industryLabel(organization.industry)}</Td>
                    <Td>
                      <Select
                        aria-label={`Plan for ${organization.name}`}
                        className="w-36"
                        value={organization.planCode}
                        onChange={(e) => {
                          setError(null)
                          setPending({ organization, planCode: e.target.value })
                        }}
                      >
                        {!PLAN_OPTIONS.some((p) => p.code === organization.planCode) && <option value={organization.planCode}>{organization.plan}</option>}
                        {PLAN_OPTIONS.map((plan) => (
                          <option key={plan.code} value={plan.code}>
                            {plan.label}
                          </option>
                        ))}
                      </Select>
                    </Td>
                    <Td>
                      <Badge tone={organization.subscriptionStatus === 'Active' ? 'green' : 'amber'}>{humanize(organization.subscriptionStatus)}</Badge>
                    </Td>
                    <Td>
                      <YesNo value={organization.stripeLinked} yes="Linked" no="Not linked" />
                    </Td>
                    <Td align="right">{num.format(organization.members)}</Td>
                    <Td align="right">{num.format(organization.projects)}</Td>
                    <Td className="text-slate-600">{formatDate(organization.createdAt)}</Td>
                  </tr>
                ))}
              </tbody>
            </Table>
            <ConfirmDialog
              open={!!pending}
              onClose={() => setPending(null)}
              onConfirm={() => pending && setPlan.mutate(pending)}
              title="Override this organization's plan?"
              confirmLabel="Change plan"
              loading={setPlan.isPending}
            >
              <p>
                <strong className="text-navy-900">{pending?.organization.name}</strong> will move from {pending?.organization.plan} to{' '}
                <strong className="text-navy-900">{PLAN_OPTIONS.find((p) => p.code === pending?.planCode)?.label ?? pending?.planCode}</strong> and its subscription is set to Active. No payment is taken
                {pending?.organization.stripeLinked ? ', and the linked Stripe subscription is not changed' : ''}. This override is recorded in the audit log.
              </p>
              {error && (
                <Notice tone="error" className="mt-3">
                  {error}
                </Notice>
              )}
            </ConfirmDialog>
          </TableCard>
        )
      }
    </Loadable>
  )
}

// ---------- Users ----------

type UserChange = { user: AdminUser; isActive: boolean; isPlatformAdmin: boolean; title: string; body: string; danger: boolean }

function UsersTab() {
  const toast = useToast()
  const queryClient = useQueryClient()
  const [search, setSearch] = useState('')
  const term = useDebounced(search.trim())
  const query = useQuery({
    queryKey: ['admin', 'users', term],
    queryFn: () => api.get<AdminUser[]>(`/api/admin/users${term ? `?search=${encodeURIComponent(term)}` : ''}`),
    placeholderData: keepPreviousData,
  })
  const [pending, setPending] = useState<UserChange | null>(null)
  const [error, setError] = useState<string | null>(null)

  const update = useMutation({
    mutationFn: (change: UserChange) => api.put(`/api/admin/users/${change.user.id}`, { isActive: change.isActive, isPlatformAdmin: change.isPlatformAdmin }),
    onSuccess: (_, change) => {
      toast.success(`${change.user.fullName} was updated.`)
      setPending(null)
      queryClient.invalidateQueries({ queryKey: ['admin', 'users'] })
      queryClient.invalidateQueries({ queryKey: ['admin', 'audit'] })
    },
    onError: (err) => setError(errorMessage(err)),
  })

  const ask = (change: UserChange) => {
    setError(null)
    setPending(change)
  }

  return (
    <div className="space-y-4">
      <div className="relative max-w-md">
        <Search className="pointer-events-none absolute left-3 top-3 size-4 text-slate-400" aria-hidden />
        <input
          type="search"
          aria-label="Search users by name or email"
          placeholder="Search by name or email"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          className="block h-10 w-full rounded-lg border border-slate-300 bg-white pl-9 pr-3 text-sm text-navy-900 shadow-sm placeholder:text-slate-400 focus:border-accent-500 focus:outline-none focus:ring-2 focus:ring-accent-500/25"
        />
      </div>
      <Loadable query={query}>
        {(users) =>
          users.length === 0 ? (
            <EmptyState title={term ? 'No users match that search' : 'No users yet'} description={term ? 'Try part of the name or email address.' : undefined} />
          ) : (
            <TableCard title="Users" description="Most recent 300. Deactivating a user signs them out everywhere.">
              <Table>
                <thead>
                  <tr>
                    <Th>Name</Th>
                    <Th>Email</Th>
                    <Th>Organizations</Th>
                    <Th>Last sign-in</Th>
                    <Th align="center">Active</Th>
                    <Th align="center">Platform admin</Th>
                  </tr>
                </thead>
                <tbody>
                  {users.map((user) => (
                    <tr key={user.id}>
                      <Td>
                        <span className="flex flex-wrap items-center gap-2 font-medium text-navy-900">
                          {user.fullName}
                          {user.isDemo && <Badge tone="violet">Demo</Badge>}
                        </span>
                        <span className="text-xs text-slate-500">Created {formatDate(user.createdAt)}</span>
                      </Td>
                      <Td className="text-slate-600">{user.email}</Td>
                      <Td className="max-w-xs whitespace-normal text-slate-600">
                        {user.organizations.length === 0 ? <span className="text-slate-400">None</span> : user.organizations.map((o) => <div key={o}>{o}</div>)}
                      </Td>
                      <Td className="text-slate-600">{user.lastLoginAt ? timeAgo(user.lastLoginAt) : 'Never signed in'}</Td>
                      <Td align="center">
                        <Switch
                          checked={user.isActive}
                          label={`${user.fullName} is ${user.isActive ? 'active' : 'deactivated'}`}
                          onChange={(next) =>
                            ask({
                              user,
                              isActive: next,
                              isPlatformAdmin: user.isPlatformAdmin,
                              title: next ? 'Reactivate this user?' : 'Deactivate this user?',
                              body: next ? 'will be able to sign in again.' : 'will be signed out of every session and will not be able to sign in until reactivated.',
                              danger: !next,
                            })
                          }
                        />
                      </Td>
                      <Td align="center">
                        <Switch
                          checked={user.isPlatformAdmin}
                          label={`${user.fullName} ${user.isPlatformAdmin ? 'is' : 'is not'} a platform administrator`}
                          onChange={(next) =>
                            ask({
                              user,
                              isActive: user.isActive,
                              isPlatformAdmin: next,
                              title: next ? 'Grant platform administrator access?' : 'Remove platform administrator access?',
                              body: next ? 'will be able to see and change every organization, user and supplier on this platform.' : 'will lose access to platform administration. Their organization roles are not affected.',
                              danger: next,
                            })
                          }
                        />
                      </Td>
                    </tr>
                  ))}
                </tbody>
              </Table>
            </TableCard>
          )
        }
      </Loadable>
      <ConfirmDialog open={!!pending} onClose={() => setPending(null)} onConfirm={() => pending && update.mutate(pending)} title={pending?.title ?? ''} danger={pending?.danger} loading={update.isPending}>
        <p>
          <strong className="text-navy-900">{pending?.user.fullName}</strong> ({pending?.user.email}) {pending?.body} This change is recorded in the audit log.
        </p>
        {error && (
          <Notice tone="error" className="mt-3">
            {error}
          </Notice>
        )}
      </ConfirmDialog>
    </div>
  )
}

// ---------- Suppliers ----------

const CAPABILITY_LABELS: [keyof ProviderCapabilities, string][] = [
  ['productSearch', 'Product search'],
  ['inventory', 'Inventory'],
  ['purchaseLinks', 'Purchase links'],
  ['cartBuilding', 'Cart building'],
  ['orderPlacement', 'Order placement'],
]

function SuppliersTab() {
  const toast = useToast()
  const queryClient = useQueryClient()
  const query = useQuery({ queryKey: ['admin', 'suppliers'], queryFn: () => api.get<AdminSupplier[]>('/api/admin/suppliers') })
  const [editing, setEditing] = useState<AdminSupplier | null>(null)
  const [editError, setEditError] = useState<string | null>(null)

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: ['admin', 'suppliers'] })
    queryClient.invalidateQueries({ queryKey: ['admin', 'audit'] })
    queryClient.invalidateQueries({ queryKey: ['suppliers'] })
  }

  const save = useMutation({
    mutationFn: (input: { supplier: AdminSupplier; body: AdminSupplierUpdate }) => api.put(`/api/admin/suppliers/${input.supplier.id}`, input.body),
    onSuccess: (_, input) => {
      toast.success(`${input.supplier.name} was updated.`)
      setEditing(null)
      invalidate()
    },
    onError: (err) => setEditError(errorMessage(err)),
  })

  const setFault = useMutation({
    mutationFn: (input: { supplier: AdminSupplier; fault: string }) => api.post(`/api/admin/suppliers/${input.supplier.id}/fault`, { fault: input.fault }),
    onSuccess: (_, input) => {
      toast.success(input.fault === 'none' ? `Simulated fault cleared for ${input.supplier.name}.` : `${input.supplier.name} now simulates ${input.fault === 'error' ? 'an error' : 'a timeout'}.`)
      invalidate()
    },
    onError: (err) => toast.error(errorMessage(err)),
  })

  return (
    <Loadable query={query}>
      {(suppliers) =>
        suppliers.length === 0 ? (
          <EmptyState title="No suppliers configured" description="Suppliers are created by the server's seed data and provider configuration." />
        ) : (
          <div className="space-y-4">
            <Notice>
              Mock suppliers return simulated demo prices and stock. Live suppliers call a real provider API and only work once the adapter is registered and configured with credentials on the server.
            </Notice>
            <div className="grid gap-4 xl:grid-cols-2">
              {suppliers.map((supplier) => {
                const fault = supplier.simulatedFault && supplier.simulatedFault !== 'none' ? supplier.simulatedFault : 'none'
                return (
                  <Card key={supplier.id}>
                    <div className="mb-4 flex flex-wrap items-start justify-between gap-3">
                      <div className="min-w-0">
                        <h2 className="flex flex-wrap items-center gap-2 text-base font-semibold text-navy-900">
                          {supplier.name}
                          <Badge tone={supplier.mode === 'Live' ? 'green' : 'violet'} title={supplier.mode === 'Live' ? 'Calls a real supplier API.' : 'Simulated demo data. Not live supplier quotes.'}>
                            {supplier.mode}
                          </Badge>
                          <Badge tone={supplier.isEnabled ? 'blue' : 'neutral'}>{supplier.isEnabled ? 'Enabled' : 'Disabled'}</Badge>
                        </h2>
                        <p className="mt-0.5 text-xs text-slate-500">
                          {supplier.code} · {humanize(supplier.kind)} · provider key <code className="rounded bg-slate-100 px-1 py-0.5">{supplier.providerKey}</code>
                        </p>
                      </div>
                      <Button
                        size="sm"
                        icon={<Pencil className="size-4" aria-hidden />}
                        onClick={() => {
                          setEditError(null)
                          setEditing(supplier)
                        }}
                      >
                        Edit
                      </Button>
                    </div>

                    <div className="mb-3 flex flex-wrap gap-1.5">
                      <YesNo value={supplier.adapterRegistered} yes="Adapter registered" no="No adapter registered" noTone="red" />
                      <YesNo value={supplier.isConfigured} yes="Configured" no="Not configured" noTone="amber" />
                      <YesNo value={supplier.isHealthy} yes="Healthy" no="Not healthy" noTone="red" />
                      {fault !== 'none' && <Badge tone="amber">Simulating {fault}</Badge>}
                    </div>
                    <p className="mb-4 text-sm text-slate-600">{supplier.statusMessage}</p>

                    <DefinitionList
                      items={[
                        { label: 'Listings', value: num.format(supplier.listings) },
                        {
                          label: 'Last sync',
                          value: supplier.lastSyncAt ? (
                            <span className="inline-flex flex-wrap items-center gap-2">
                              {formatDateTime(supplier.lastSyncAt)}
                              {supplier.lastSyncSucceeded !== null && <Badge tone={supplier.lastSyncSucceeded ? 'green' : 'red'}>{supplier.lastSyncSucceeded ? 'Succeeded' : 'Failed'}</Badge>}
                            </span>
                          ) : (
                            'No calls logged recently'
                          ),
                        },
                        { label: 'Flat shipping fee', value: supplier.shippingFlatFee === null ? <Unknown title="No shipping fee is recorded, so shipping is shown to customers as Unknown." /> : money(supplier.shippingFlatFee) },
                        { label: 'Free shipping from', value: money(supplier.freeShippingThreshold, 'No threshold') },
                        { label: 'Minimum order', value: money(supplier.minimumOrderAmount, 'No minimum') },
                        { label: 'Price cache', value: `${num.format(supplier.cacheTtlMinutes)} min` },
                        { label: 'Affiliate program', value: supplier.affiliateProgramEnabled ? 'Enabled' : 'Off' },
                        {
                          label: 'Capabilities',
                          value: supplier.capabilities ? (
                            <span className="flex flex-wrap gap-1">
                              {CAPABILITY_LABELS.map(([key, label]) => (
                                <Badge key={key} tone={supplier.capabilities?.[key] ? 'green' : 'neutral'} title={supplier.capabilities?.[key] ? 'Supported' : 'Not supported'}>
                                  {supplier.capabilities?.[key] ? <Check className="size-3" aria-hidden /> : <X className="size-3" aria-hidden />}
                                  {label}
                                </Badge>
                              ))}
                            </span>
                          ) : (
                            'Not reported (no adapter)'
                          ),
                        },
                      ]}
                    />

                    {supplier.integrationNotes && (
                      <p className="mt-4 rounded-lg bg-slate-50 px-3 py-2 text-sm text-slate-600">
                        <span className="font-medium text-navy-800">Integration notes: </span>
                        {supplier.integrationNotes}
                      </p>
                    )}

                    {supplier.mode === 'Mock' && (
                      <div className="mt-4 border-t border-slate-100 pt-4">
                        <Select
                          label="Simulate fault"
                          className="max-w-xs"
                          value={fault}
                          disabled={setFault.isPending}
                          onChange={(e) => setFault.mutate({ supplier, fault: e.target.value })}
                          hint="In-memory only, for demonstrating failure handling"
                        >
                          <option value="none">None (responds normally)</option>
                          <option value="error">Error</option>
                          <option value="timeout">Timeout</option>
                        </Select>
                      </div>
                    )}
                  </Card>
                )
              })}
            </div>

            <Modal
              open={!!editing}
              onClose={() => setEditing(null)}
              title={editing ? `Edit ${editing.name}` : 'Edit supplier'}
              description="Commercial terms used when estimating order totals."
              footer={
                <>
                  <Button onClick={() => setEditing(null)}>Cancel</Button>
                  <Button variant="primary" type="submit" form="supplier-form" loading={save.isPending}>
                    Save supplier
                  </Button>
                </>
              }
            >
              {editing && <SupplierForm key={editing.id} supplier={editing} error={editError} onError={setEditError} onSubmit={(body) => save.mutate({ supplier: editing, body })} />}
            </Modal>
          </div>
        )
      }
    </Loadable>
  )
}

/** Blank means "not set" (null). Returns undefined when the text is not a valid amount. */
function parseAmount(text: string): number | null | undefined {
  const trimmed = text.trim()
  if (trimmed === '') return null
  const value = Number(trimmed)
  return Number.isFinite(value) && value >= 0 ? value : undefined
}

function SupplierForm({ supplier, error, onError, onSubmit }: { supplier: AdminSupplier; error: string | null; onError: (message: string | null) => void; onSubmit: (body: AdminSupplierUpdate) => void }) {
  const text = (value: number | null) => (value === null ? '' : String(value))
  const [isEnabled, setIsEnabled] = useState(supplier.isEnabled)
  const [shippingFlatFee, setShippingFlatFee] = useState(text(supplier.shippingFlatFee))
  const [freeShippingThreshold, setFreeShippingThreshold] = useState(text(supplier.freeShippingThreshold))
  const [minimumOrderAmount, setMinimumOrderAmount] = useState(text(supplier.minimumOrderAmount))
  const [cacheTtlMinutes, setCacheTtlMinutes] = useState(String(supplier.cacheTtlMinutes))
  const [affiliateProgramEnabled, setAffiliateProgramEnabled] = useState(supplier.affiliateProgramEnabled)

  const submit = (event: FormEvent) => {
    event.preventDefault()
    onError(null)
    const fee = parseAmount(shippingFlatFee)
    const threshold = parseAmount(freeShippingThreshold)
    const minimum = parseAmount(minimumOrderAmount)
    const ttl = Number(cacheTtlMinutes)
    if (fee === undefined || threshold === undefined || minimum === undefined) return onError('Amounts must be zero or more. Leave a field blank if it does not apply.')
    if (!Number.isInteger(ttl) || ttl < 1 || ttl > 10080) return onError('Price cache must be a whole number of minutes between 1 and 10,080 (7 days).')
    onSubmit({ isEnabled, shippingFlatFee: fee, freeShippingThreshold: threshold, minimumOrderAmount: minimum, cacheTtlMinutes: ttl, affiliateProgramEnabled })
  }

  return (
    <form id="supplier-form" onSubmit={submit} className="space-y-4" noValidate>
      <Checkbox label="Enabled" description="Disabled suppliers are not queried for prices." checked={isEnabled} onChange={(e) => setIsEnabled(e.target.checked)} />
      <div className="grid gap-4 sm:grid-cols-2">
        <Input
          label="Flat shipping fee ($)"
          inputMode="decimal"
          placeholder="Unknown"
          value={shippingFlatFee}
          onChange={(e) => setShippingFlatFee(e.target.value)}
          hint="Blank means unknown: shipping is shown as Unknown, not as free. Enter 0 only if shipping really is free."
        />
        <Input label="Free shipping from ($)" inputMode="decimal" placeholder="No threshold" value={freeShippingThreshold} onChange={(e) => setFreeShippingThreshold(e.target.value)} hint="Blank means there is no free-shipping threshold." />
        <Input label="Minimum order ($)" inputMode="decimal" placeholder="No minimum" value={minimumOrderAmount} onChange={(e) => setMinimumOrderAmount(e.target.value)} hint="Blank means there is no minimum order." />
        <Input label="Price cache (minutes)" inputMode="numeric" value={cacheTtlMinutes} onChange={(e) => setCacheTtlMinutes(e.target.value)} hint="How long a fetched price is reused, 1 to 10,080." />
      </div>
      <Checkbox
        label="Affiliate program enabled"
        description="Outbound links only carry an affiliate tag when a real tag is also configured on the server."
        checked={affiliateProgramEnabled}
        onChange={(e) => setAffiliateProgramEnabled(e.target.checked)}
      />
      {error && <Notice tone="error">{error}</Notice>}
    </form>
  )
}

// ---------- Catalog ----------

function CatalogTab() {
  const toast = useToast()
  const queryClient = useQueryClient()
  const [search, setSearch] = useState('')
  const term = useDebounced(search.trim())
  const query = useQuery({
    queryKey: ['admin', 'catalog', term],
    queryFn: () => api.get<CatalogProduct[]>(`/api/admin/catalog${term ? `?query=${encodeURIComponent(term)}` : ''}`),
    placeholderData: keepPreviousData,
  })
  const units = useQuery({ queryKey: ['admin', 'units'], queryFn: () => api.get<UnitOption[]>('/api/catalog/units'), staleTime: 10 * 60_000 })
  const [editing, setEditing] = useState<CatalogProduct | 'new' | null>(null)
  const [error, setError] = useState<string | null>(null)

  const save = useMutation({
    mutationFn: (input: { id: string | null; body: CatalogInput }) => (input.id ? api.put<CatalogProduct>(`/api/admin/catalog/${input.id}`, input.body) : api.post<CatalogProduct>('/api/admin/catalog', input.body)),
    onSuccess: (product, input) => {
      toast.success(input.id ? `${product.name} was updated.` : `${product.name} was added to the catalog.`)
      setEditing(null)
      queryClient.invalidateQueries({ queryKey: ['admin', 'catalog'] })
      queryClient.invalidateQueries({ queryKey: ['admin', 'audit'] })
      queryClient.invalidateQueries({ queryKey: ['admin', 'matches'] })
    },
    onError: (err) => setError(errorMessage(err)),
  })

  const open = (target: CatalogProduct | 'new') => {
    setError(null)
    setEditing(target)
  }

  const unitOptions = units.data?.length ? units.data.map((u) => ({ code: u.code, label: `${u.label} (${u.code})` })) : Object.entries(UNIT_LABELS).map(([code, label]) => ({ code, label: `${label} (${code})` }))

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="relative w-full max-w-md">
          <Search className="pointer-events-none absolute left-3 top-3 size-4 text-slate-400" aria-hidden />
          <input
            type="search"
            aria-label="Search the catalog by name or category"
            placeholder="Search by name or category"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            className="block h-10 w-full rounded-lg border border-slate-300 bg-white pl-9 pr-3 text-sm text-navy-900 shadow-sm placeholder:text-slate-400 focus:border-accent-500 focus:outline-none focus:ring-2 focus:ring-accent-500/25"
          />
        </div>
        <Button variant="primary" icon={<Plus className="size-4" aria-hidden />} onClick={() => open('new')}>
          Add product
        </Button>
      </div>

      <Loadable query={query}>
        {(products) =>
          products.length === 0 ? (
            <EmptyState
              title={term ? 'No catalog products match that search' : 'The catalog is empty'}
              description={term ? 'Search looks at the product name and category.' : 'Canonical products are what supplier listings are matched against.'}
              action={!term && <Button variant="primary" onClick={() => open('new')}>Add product</Button>}
            />
          ) : (
            <TableCard title="Canonical product catalog" description="The products supplier listings are matched to. Up to 300 are shown; search to narrow the list.">
              <Table>
                <thead>
                  <tr>
                    <Th>Product</Th>
                    <Th>Industry</Th>
                    <Th>Category</Th>
                    <Th>Brand / model</Th>
                    <Th>Unit</Th>
                    <Th>Specifications</Th>
                    <Th align="right">Actions</Th>
                  </tr>
                </thead>
                <tbody>
                  {products.map((product) => (
                    <tr key={product.id}>
                      <Td className="max-w-xs whitespace-normal">
                        <span className="font-medium text-navy-900">{product.name}</span>
                        {product.isSafetyCritical && (
                          <div className="mt-1">
                            <Badge tone="red" title={product.safetyNote ?? 'Substitutions for this product need explicit approval.'}>
                              <AlertTriangle className="size-3" aria-hidden />
                              Safety-critical
                            </Badge>
                          </div>
                        )}
                        {product.material && <div className="mt-0.5 text-xs text-slate-500">Material: {product.material}</div>}
                      </Td>
                      <Td>{industryLabel(product.industry)}</Td>
                      <Td>{product.category}</Td>
                      <Td className="text-slate-600">{[product.brand, product.model].filter(Boolean).join(' / ') || <span className="text-slate-400">Not specified</span>}</Td>
                      <Td>{unitLabel(product.baseUnit)}</Td>
                      <Td className="max-w-xs whitespace-normal">
                        <SpecList specs={product.specifications} />
                      </Td>
                      <Td align="right">
                        <Button size="sm" icon={<Pencil className="size-4" aria-hidden />} onClick={() => open(product)}>
                          Edit
                        </Button>
                      </Td>
                    </tr>
                  ))}
                </tbody>
              </Table>
            </TableCard>
          )
        }
      </Loadable>

      <Modal
        open={!!editing}
        onClose={() => setEditing(null)}
        wide
        title={editing && editing !== 'new' ? 'Edit catalog product' : 'Add catalog product'}
        footer={
          <>
            <Button onClick={() => setEditing(null)}>Cancel</Button>
            <Button variant="primary" type="submit" form="catalog-form" loading={save.isPending}>
              {editing && editing !== 'new' ? 'Save product' : 'Add product'}
            </Button>
          </>
        }
      >
        {editing && (
          <CatalogForm
            key={editing === 'new' ? 'new' : editing.id}
            product={editing === 'new' ? null : editing}
            unitOptions={unitOptions}
            error={error}
            onError={setError}
            onSubmit={(body) => save.mutate({ id: editing === 'new' ? null : editing.id, body })}
          />
        )}
      </Modal>
    </div>
  )
}

type SpecRow = { id: number; key: string; value: string }

function CatalogForm({ product, unitOptions, error, onError, onSubmit }: { product: CatalogProduct | null; unitOptions: { code: string; label: string }[]; error: string | null; onError: (message: string | null) => void; onSubmit: (body: CatalogInput) => void }) {
  const [name, setName] = useState(product?.name ?? '')
  const [industry, setIndustry] = useState(product?.industry ?? INDUSTRIES[0].value)
  const [category, setCategory] = useState(product?.category ?? '')
  const [brand, setBrand] = useState(product?.brand ?? '')
  const [model, setModel] = useState(product?.model ?? '')
  const [mpn, setMpn] = useState(product?.mpn ?? '')
  const [gtin, setGtin] = useState(product?.gtin ?? '')
  const [material, setMaterial] = useState(product?.material ?? '')
  const [baseUnit, setBaseUnit] = useState(product?.baseUnit ?? 'each')
  const [isSafetyCritical, setIsSafetyCritical] = useState(product?.isSafetyCritical ?? false)
  const [safetyNote, setSafetyNote] = useState(product?.safetyNote ?? '')
  const [specs, setSpecs] = useState<SpecRow[]>(() => Object.entries(product?.specifications ?? {}).map(([key, value], index) => ({ id: index, key, value })))
  const [nextId, setNextId] = useState(specs.length)

  const clean = (value: string) => value.trim() || null

  const submit = (event: FormEvent) => {
    event.preventDefault()
    onError(null)
    if (!name.trim() || !category.trim()) return onError('Enter the product name and category.')
    const specifications: Record<string, string> = {}
    for (const row of specs) {
      const key = row.key.trim()
      if (!key && !row.value.trim()) continue
      if (!key || !row.value.trim()) return onError('Each specification needs both a name and a value. Remove any row you do not need.')
      if (key in specifications) return onError(`The specification "${key}" is listed twice.`)
      specifications[key] = row.value.trim()
    }
    onSubmit({
      name: name.trim(),
      industry,
      category: category.trim(),
      brand: clean(brand),
      model: clean(model),
      mpn: clean(mpn),
      gtin: clean(gtin),
      material: clean(material),
      baseUnit,
      isSafetyCritical,
      safetyNote: clean(safetyNote),
      specifications,
    })
  }

  const updateSpec = (id: number, patch: Partial<SpecRow>) => setSpecs((rows) => rows.map((row) => (row.id === id ? { ...row, ...patch } : row)))

  return (
    <form id="catalog-form" onSubmit={submit} className="space-y-4" noValidate>
      <Input label="Product name" value={name} onChange={(e) => setName(e.target.value)} maxLength={300} required />
      <div className="grid gap-4 sm:grid-cols-2">
        <Select label="Industry" value={industry} onChange={(e) => setIndustry(e.target.value)}>
          {!INDUSTRIES.some((i) => i.value === industry) && <option value={industry}>{industry}</option>}
          {INDUSTRIES.map((i) => (
            <option key={i.value} value={i.value}>
              {i.label}
            </option>
          ))}
        </Select>
        <Input label="Category" value={category} onChange={(e) => setCategory(e.target.value)} maxLength={100} required />
        <Input label="Brand" value={brand} onChange={(e) => setBrand(e.target.value)} maxLength={100} />
        <Input label="Model" value={model} onChange={(e) => setModel(e.target.value)} maxLength={100} />
        <Input label="Manufacturer part number (MPN)" value={mpn} onChange={(e) => setMpn(e.target.value)} maxLength={100} />
        <Input label="GTIN / UPC" value={gtin} onChange={(e) => setGtin(e.target.value)} maxLength={20} inputMode="numeric" hint="Checked for a valid length and check digit." />
        <Input label="Material" value={material} onChange={(e) => setMaterial(e.target.value)} maxLength={100} />
        <Select label="Base unit" value={baseUnit} onChange={(e) => setBaseUnit(e.target.value)} hint="Prices are compared per this unit.">
          {!unitOptions.some((u) => u.code === baseUnit) && <option value={baseUnit}>{baseUnit}</option>}
          {unitOptions.map((u) => (
            <option key={u.code} value={u.code}>
              {u.label}
            </option>
          ))}
        </Select>
      </div>
      <Checkbox label="Safety-critical" description="Substitutes are never suggested automatically and need explicit approval." checked={isSafetyCritical} onChange={(e) => setIsSafetyCritical(e.target.checked)} />
      {isSafetyCritical && <Textarea label="Safety note" rows={2} value={safetyNote} onChange={(e) => setSafetyNote(e.target.value)} maxLength={500} hint="Shown to buyers wherever this product appears." />}

      <fieldset>
        <legend className="mb-1.5 text-sm font-medium text-navy-800">Specifications</legend>
        <p className="mb-2 text-xs text-slate-500">Used to compare supplier listings with this product. Names are stored in lower case with underscores.</p>
        <div className="space-y-2">
          {specs.length === 0 && <p className="text-sm text-slate-500">No specifications yet.</p>}
          {specs.map((row, index) => (
            <div key={row.id} className="flex items-start gap-2">
              <Input aria-label={`Specification ${index + 1} name`} placeholder="Name, e.g. diameter" className="flex-1" value={row.key} onChange={(e) => updateSpec(row.id, { key: e.target.value })} />
              <Input aria-label={`Specification ${index + 1} value`} placeholder="Value, e.g. 3/4 in" className="flex-1" value={row.value} onChange={(e) => updateSpec(row.id, { value: e.target.value })} />
              <Button variant="ghost" aria-label={`Remove specification ${index + 1}`} icon={<Trash2 className="size-4" aria-hidden />} onClick={() => setSpecs((rows) => rows.filter((r) => r.id !== row.id))} />
            </div>
          ))}
        </div>
        <Button
          size="sm"
          className="mt-2"
          icon={<Plus className="size-4" aria-hidden />}
          onClick={() => {
            setSpecs((rows) => [...rows, { id: nextId, key: '', value: '' }])
            setNextId((id) => id + 1)
          }}
        >
          Add specification
        </Button>
      </fieldset>

      {error && <Notice tone="error">{error}</Notice>}
    </form>
  )
}

// ---------- Match review ----------

const MATCH_STATUSES: { value: MatchStatus; label: string }[] = [
  { value: 'NeedsReview', label: 'Needs review' },
  { value: 'AutoMatched', label: 'Auto-matched' },
  { value: 'Confirmed', label: 'Confirmed' },
  { value: 'Rejected', label: 'Rejected' },
]

function MatchesTab() {
  const toast = useToast()
  const queryClient = useQueryClient()
  const [status, setStatus] = useState<MatchStatus>('NeedsReview')
  const query = useQuery({ queryKey: ['admin', 'matches', status], queryFn: () => api.get<AdminMatch[]>(`/api/admin/matches?status=${status}`) })

  const review = useMutation({
    mutationFn: (input: { match: AdminMatch; decision: 'confirm' | 'reject' }) => api.post(`/api/admin/matches/${input.match.id}/${input.decision}`),
    onSuccess: (_, input) => {
      toast.success(input.decision === 'confirm' ? 'Match confirmed. This listing can now be priced for customers.' : 'Match rejected. This listing will not be priced against that product.')
      queryClient.invalidateQueries({ queryKey: ['admin', 'matches'] })
      queryClient.invalidateQueries({ queryKey: ['admin', 'overview'] })
      queryClient.invalidateQueries({ queryKey: ['admin', 'audit'] })
    },
    onError: (err) => toast.error(errorMessage(err)),
  })

  return (
    <div className="space-y-4">
      <Notice>Listings matched with low confidence are not priced for customers until a person confirms them. Name similarity alone never auto-matches.</Notice>
      <Select label="Show matches with status" className="max-w-xs" value={status} onChange={(e) => setStatus(e.target.value as MatchStatus)}>
        {MATCH_STATUSES.map((s) => (
          <option key={s.value} value={s.value}>
            {s.label}
          </option>
        ))}
      </Select>
      <Loadable query={query} rows={4}>
        {(matches) =>
          matches.length === 0 ? (
            <EmptyState
              icon={<Check className="size-6" />}
              title={status === 'NeedsReview' ? 'The review queue is clear' : `No ${MATCH_STATUSES.find((s) => s.value === status)?.label.toLowerCase()} matches`}
              description={status === 'NeedsReview' ? 'New low-confidence matches appear here when supplier listings are synced.' : undefined}
            />
          ) : (
            <div className="space-y-4">
              <p className="text-sm text-slate-500">
                {num.format(matches.length)} {matches.length === 1 ? 'match' : 'matches'}, lowest confidence first{matches.length >= 300 ? ' (first 300 shown)' : ''}.
              </p>
              {matches.map((match) => (
                <MatchCard
                  key={match.id}
                  match={match}
                  busy={review.isPending && review.variables?.match.id === match.id ? review.variables.decision : null}
                  disabled={review.isPending}
                  onDecision={(decision) => review.mutate({ match, decision })}
                />
              ))}
            </div>
          )
        }
      </Loadable>
    </div>
  )
}

function Fact({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex gap-2 text-sm">
      <dt className="w-24 shrink-0 text-slate-500">{label}</dt>
      <dd className="min-w-0 break-words text-navy-900">{children}</dd>
    </div>
  )
}

const notGiven = <span className="text-slate-400">Not provided</span>

function MatchCard({ match, busy, disabled, onDecision }: { match: AdminMatch; busy: 'confirm' | 'reject' | null; disabled: boolean; onDecision: (decision: 'confirm' | 'reject') => void }) {
  const percent = Math.round(Math.min(1, Math.max(0, match.confidence)) * 100)
  const barColor = percent >= 85 ? 'bg-emerald-500' : percent >= 60 ? 'bg-amber-500' : 'bg-red-500'
  return (
    <Card>
      <div className="grid gap-4 md:grid-cols-2">
        <div className="rounded-xl border border-slate-200 p-4">
          <p className="text-xs font-semibold uppercase tracking-wide text-slate-500">Supplier listing</p>
          <p className="mt-1 font-semibold text-navy-900">{match.listingTitle}</p>
          <dl className="mt-3 space-y-1.5">
            <Fact label="Supplier">{match.supplierName}</Fact>
            <Fact label="Brand">{match.listingBrand ?? notGiven}</Fact>
            <Fact label="Model">{match.listingModel ?? notGiven}</Fact>
            <Fact label="GTIN">{match.listingGtin ?? notGiven}</Fact>
            <Fact label="Pack">
              {qty(match.packQuantity)} {unitLabel(match.packUnit)}
            </Fact>
            <Fact label="Price">{match.price === null ? <Unknown title="This listing has no current offer price.">No current price</Unknown> : `${money(match.price)} per pack`}</Fact>
            <Fact label="Specs">
              <SpecList specs={match.listingSpecs} />
            </Fact>
          </dl>
        </div>
        <div className="rounded-xl border border-accent-200 bg-accent-50/40 p-4">
          <p className="text-xs font-semibold uppercase tracking-wide text-slate-500">Catalog product</p>
          <p className="mt-1 font-semibold text-navy-900">{match.canonicalName}</p>
          <dl className="mt-3 space-y-1.5">
            <Fact label="Base unit">{unitLabel(match.canonicalUnit)}</Fact>
            <Fact label="Specs">
              <SpecList specs={match.canonicalSpecs} />
            </Fact>
          </dl>
        </div>
      </div>

      <div className="mt-4 grid gap-4 md:grid-cols-2">
        <div>
          <div className="flex items-center justify-between text-sm">
            <span className="font-medium text-navy-800">Confidence</span>
            <span className="font-semibold text-navy-900 tabular">{percent}%</span>
          </div>
          <div className="mt-1.5 h-2.5 overflow-hidden rounded-full bg-slate-100" role="progressbar" aria-label="Match confidence" aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent}>
            <div className={clsx('h-full rounded-full', barColor)} style={{ width: `${percent}%` }} />
          </div>
          <div className="mt-2 flex flex-wrap items-center gap-1.5 text-xs text-slate-500">
            <Badge>Method: {humanize(match.method)}</Badge>
            <Badge>{humanize(match.status)}</Badge>
            <span>Updated {timeAgo(match.updatedAt)}</span>
          </div>
        </div>
        <div>
          <p className="text-sm font-medium text-navy-800">Why this match was suggested</p>
          {match.reasons.length === 0 ? (
            <p className="mt-1 text-sm text-slate-500">No reasons were recorded.</p>
          ) : (
            <ul className="mt-1 list-disc space-y-0.5 pl-5 text-sm text-slate-600">
              {match.reasons.map((reason, index) => (
                <li key={index}>{reason}</li>
              ))}
            </ul>
          )}
        </div>
      </div>

      <div className="mt-4 flex flex-wrap justify-end gap-2 border-t border-slate-100 pt-4">
        {match.status !== 'Rejected' && (
          <Button variant="danger" icon={<X className="size-4" aria-hidden />} loading={busy === 'reject'} disabled={disabled} onClick={() => onDecision('reject')}>
            Reject
          </Button>
        )}
        {match.status !== 'Confirmed' && (
          <Button variant="primary" icon={<Check className="size-4" aria-hidden />} loading={busy === 'confirm'} disabled={disabled} onClick={() => onDecision('confirm')}>
            Confirm match
          </Button>
        )}
      </div>
    </Card>
  )
}

// ---------- Supplier API usage ----------

function ProviderUsageTab() {
  const query = useQuery({ queryKey: ['admin', 'usage', 'providers'], queryFn: () => api.get<ProviderUsage>('/api/admin/usage/providers') })
  return (
    <Loadable query={query}>
      {(data) =>
        data.bySupplier.length === 0 ? (
          <EmptyState title={`No supplier API calls in the last ${data.windowDays} days`} description="Calls are logged whenever prices or stock are fetched from a supplier adapter." />
        ) : (
          <div className="space-y-6">
            <TableCard title="Calls by supplier" description={`Last ${data.windowDays} days (most recent 5,000 calls).`}>
              <UsageTable heading="Supplier" buckets={data.bySupplier} />
            </TableCard>
            <TableCard title="Recent calls" description="The 50 most recent supplier calls.">
              <Table>
                <thead>
                  <tr>
                    <Th>When</Th>
                    <Th>Supplier</Th>
                    <Th>Operation</Th>
                    <Th>Result</Th>
                    <Th align="right">Items</Th>
                    <Th align="right">Time</Th>
                    <Th>Error</Th>
                  </tr>
                </thead>
                <tbody>
                  {data.recent.map((call) => (
                    <tr key={call.id}>
                      <Td className="text-slate-600">{formatDateTime(call.createdAt)}</Td>
                      <Td className="font-medium text-navy-900">{call.supplier}</Td>
                      <Td>{call.operation}</Td>
                      <Td>
                        <YesNo value={call.success} yes="OK" no="Failed" noTone="red" />
                      </Td>
                      <Td align="right">{qty(call.itemCount)}</Td>
                      <Td align="right">{num.format(call.durationMs)} ms</Td>
                      <Td className="max-w-sm whitespace-normal text-red-700">{call.error ?? <span className="text-slate-400">—</span>}</Td>
                    </tr>
                  ))}
                </tbody>
              </Table>
            </TableCard>
          </div>
        )
      }
    </Loadable>
  )
}

// ---------- AI usage ----------

function AiUsageTab() {
  const query = useQuery({ queryKey: ['admin', 'usage', 'ai'], queryFn: () => api.get<AiUsage>('/api/admin/usage/ai') })
  return (
    <Loadable query={query}>
      {(data) => (
        <div className="space-y-6">
          <Card>
            <CardHeader title="Active AI provider" />
            <DefinitionList
              items={[
                { label: 'Provider', value: data.activeProvider },
                { label: 'Model', value: data.activeModel || <span className="text-slate-400">Not reported</span> },
              ]}
            />
          </Card>
          <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
            <Stat label={`AI calls (${data.windowDays} days)`} value={num.format(data.total.calls)} hint={`${num.format(data.total.failures)} failed`} />
            <Stat label="Input tokens" value={num.format(data.total.inputTokens)} />
            <Stat label="Output tokens" value={num.format(data.total.outputTokens)} />
            <Stat label="Estimated cost" value={unitMoney(data.total.cost)} />
          </div>
          <Notice>{data.costNote}</Notice>
          {data.total.calls === 0 ? (
            <EmptyState title={`No AI calls in the last ${data.windowDays} days`} description="Usage is logged when a material list is drafted or an alternative is explained." />
          ) : (
            <>
              <TableCard title="By feature">
                <UsageTable heading="Feature" buckets={data.byFeature} tokens />
              </TableCard>
              <TableCard title="By provider and model">
                <UsageTable heading="Provider / model" buckets={data.byProvider} tokens />
              </TableCard>
              <TableCard title="Recent calls" description="The 50 most recent AI calls.">
                <Table>
                  <thead>
                    <tr>
                      <Th>When</Th>
                      <Th>Feature</Th>
                      <Th>Provider / model</Th>
                      <Th>Result</Th>
                      <Th align="right">Input</Th>
                      <Th align="right">Output</Th>
                      <Th align="right">Est. cost</Th>
                      <Th align="right">Time</Th>
                      <Th>Error</Th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.recent.map((call) => (
                      <tr key={call.id}>
                        <Td className="text-slate-600">{formatDateTime(call.createdAt)}</Td>
                        <Td className="font-medium text-navy-900">{call.feature}</Td>
                        <Td>{[call.provider, call.model].filter(Boolean).join(' / ')}</Td>
                        <Td>
                          <YesNo value={call.success} yes="OK" no="Failed" noTone="red" />
                        </Td>
                        <Td align="right">{num.format(call.inputTokens)}</Td>
                        <Td align="right">{num.format(call.outputTokens)}</Td>
                        <Td align="right">{unitMoney(call.estimatedCostUsd)}</Td>
                        <Td align="right">{num.format(call.durationMs)} ms</Td>
                        <Td className="max-w-sm whitespace-normal text-red-700">{call.error ?? <span className="text-slate-400">—</span>}</Td>
                      </tr>
                    ))}
                  </tbody>
                </Table>
              </TableCard>
            </>
          )}
        </div>
      )}
    </Loadable>
  )
}

// ---------- Project analytics ----------

function AnalyticsTab() {
  const query = useQuery({ queryKey: ['admin', 'analytics', 'projects'], queryFn: () => api.get<ProjectAnalytics>('/api/admin/analytics/projects') })
  return (
    <Loadable query={query}>
      {(data) => {
        // With no priced plans the server sends zeros; those are "no data", not real totals.
        const priced = data.projectsWithPricedPlan > 0
        const noData = <span className="text-lg font-medium text-slate-400">No data yet</span>
        return (
          <div className="space-y-6">
            <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
              <Stat label="Total projects" value={num.format(data.totalProjects)} hint={`${num.format(data.createdLast30Days)} created in the last 30 days`} />
              <Stat label="Comparison runs" value={num.format(data.comparisonRuns)} />
              <Stat label="Projects with a priced plan" value={num.format(data.projectsWithPricedPlan)} />
              <Stat label="Average savings" value={priced && data.baselineTotal > 0 ? `${data.averageSavingsPercent}%` : noData} tone="green" />
              <Stat label="Baseline total" value={priced ? money(data.baselineTotal) : noData} hint="Latest single-supplier baseline per project" />
              <Stat label="Optimized total" value={priced ? money(data.optimizedTotal) : noData} hint="Latest lowest-cost plan per project" />
            </div>
            <Notice>{data.note}</Notice>
            <div className="grid gap-6 lg:grid-cols-2">
              <Card>
                <CardHeader title="Projects by status" />
                <Bars entries={Object.entries(data.byStatus).sort((a, b) => b[1] - a[1])} emptyLabel="No projects yet." />
              </Card>
              <Card>
                <CardHeader title="Projects by industry" />
                <Bars entries={Object.entries(data.byIndustry).sort((a, b) => b[1] - a[1])} emptyLabel="No projects yet." />
              </Card>
            </div>
          </div>
        )
      }}
    </Loadable>
  )
}

// ---------- Affiliate clicks ----------

function AffiliateTab() {
  const query = useQuery({ queryKey: ['admin', 'affiliate-clicks'], queryFn: () => api.get<AffiliateClicks>('/api/admin/affiliate-clicks') })
  return (
    <Loadable query={query}>
      {(data) => (
        <div className="space-y-6">
          <div className="grid gap-4 sm:grid-cols-2">
            <Stat label="Outbound supplier clicks" value={num.format(data.total)} hint="Most recent 2,000 are counted" />
            <Stat label="Clicks with an affiliate tag" value={num.format(data.taggedAffiliateClicks)} />
          </div>
          <Notice>{data.note}</Notice>
          {data.total === 0 ? (
            <EmptyState title="No outbound clicks logged yet" description="A click is logged each time someone opens a supplier link from a cart or comparison." />
          ) : (
            <>
              <TableCard title="By supplier">
                <Table>
                  <thead>
                    <tr>
                      <Th>Supplier</Th>
                      <Th align="right">Clicks</Th>
                      <Th align="right">With affiliate tag</Th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.bySupplier.map((row) => (
                      <tr key={row.supplier}>
                        <Td className="font-medium text-navy-900">{row.supplier}</Td>
                        <Td align="right">{num.format(row.clicks)}</Td>
                        <Td align="right">{num.format(row.affiliate)}</Td>
                      </tr>
                    ))}
                  </tbody>
                </Table>
              </TableCard>
              <TableCard title="Recent clicks" description="The 50 most recent outbound clicks.">
                <Table>
                  <thead>
                    <tr>
                      <Th>When</Th>
                      <Th>Supplier</Th>
                      <Th>Affiliate tag</Th>
                      <Th>Target URL</Th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.recent.map((click) => (
                      <tr key={click.id}>
                        <Td className="text-slate-600">{formatDateTime(click.createdAt)}</Td>
                        <Td className="font-medium text-navy-900">{click.supplier}</Td>
                        <Td>
                          <YesNo value={click.isAffiliateLink} yes="Tagged" no="No tag" />
                        </Td>
                        <Td className="max-w-md whitespace-normal break-all text-slate-600">{click.targetUrl}</Td>
                      </tr>
                    ))}
                  </tbody>
                </Table>
              </TableCard>
            </>
          )}
        </div>
      )}
    </Loadable>
  )
}

// ---------- Errors ----------

function ErrorsTab() {
  const query = useQuery({ queryKey: ['admin', 'errors'], queryFn: () => api.get<ErrorLogRow[]>('/api/admin/errors') })
  const [expanded, setExpanded] = useState<string | null>(null)
  return (
    <Loadable query={query}>
      {(errors) =>
        errors.length === 0 ? (
          <EmptyState icon={<Check className="size-6" />} title="No errors logged" description="Unhandled server errors are recorded here with their trace ID." />
        ) : (
          <TableCard
            title="Server errors"
            description="The 100 most recent unhandled errors. Select a row to see its detail."
            action={
              <Button size="sm" icon={<RefreshCw className="size-4" aria-hidden />} loading={query.isFetching} onClick={() => query.refetch()}>
                Refresh
              </Button>
            }
          >
            <Table>
              <thead>
                <tr>
                  <Th className="w-8" />
                  <Th>When</Th>
                  <Th>Request</Th>
                  <Th>Message</Th>
                  <Th>Trace ID</Th>
                </tr>
              </thead>
              <tbody>
                {errors.map((error) => {
                  const open = expanded === error.id
                  return (
                    <Fragment key={error.id}>
                      <tr>
                        <Td>
                          <button
                            type="button"
                            aria-expanded={open}
                            aria-label={open ? 'Hide error detail' : 'Show error detail'}
                            onClick={() => setExpanded(open ? null : error.id)}
                            className="rounded p-1 text-slate-500 hover:bg-slate-100"
                          >
                            {open ? <ChevronDown className="size-4" /> : <ChevronRight className="size-4" />}
                          </button>
                        </Td>
                        <Td className="text-slate-600">{formatDateTime(error.createdAt)}</Td>
                        <Td>
                          {error.method || error.path ? (
                            <code className="text-xs">
                              {error.method} {error.path}
                            </code>
                          ) : (
                            <span className="text-slate-400">Not a request</span>
                          )}
                        </Td>
                        <Td className="max-w-md whitespace-normal text-navy-900">{error.message}</Td>
                        <Td>
                          <code className="text-xs text-slate-600">{error.traceId ?? '—'}</code>
                        </Td>
                      </tr>
                      {open && (
                        <tr>
                          <Td colSpan={5} className="bg-slate-50">
                            <p className="mb-2 text-xs text-slate-500">User ID: {error.userId ?? 'not signed in'}</p>
                            {error.detail ? (
                              <pre className="max-h-80 max-w-[calc(100vw-6rem)] overflow-auto whitespace-pre-wrap break-words rounded-lg bg-navy-950 p-3 text-xs leading-relaxed text-slate-100">{error.detail}</pre>
                            ) : (
                              <p className="text-sm text-slate-500">No further detail was recorded for this error.</p>
                            )}
                          </Td>
                        </tr>
                      )}
                    </Fragment>
                  )
                })}
              </tbody>
            </Table>
          </TableCard>
        )
      }
    </Loadable>
  )
}

// ---------- Audit log ----------

function AuditTab() {
  const query = useQuery({ queryKey: ['admin', 'audit'], queryFn: () => api.get<AuditRow[]>('/api/admin/audit') })
  return (
    <Loadable query={query}>
      {(rows) =>
        rows.length === 0 ? (
          <EmptyState title="The audit log is empty" description="Sign-ins, approvals, plan changes and administrator actions are recorded here." />
        ) : (
          <TableCard
            title="Audit log"
            description="The 200 most recent recorded actions across all organizations."
            action={
              <Button size="sm" icon={<RefreshCw className="size-4" aria-hidden />} loading={query.isFetching} onClick={() => query.refetch()}>
                Refresh
              </Button>
            }
          >
            <Table>
              <thead>
                <tr>
                  <Th>When</Th>
                  <Th>Action</Th>
                  <Th>Entity</Th>
                  <Th>Details</Th>
                  <Th>Org ID</Th>
                  <Th>User ID</Th>
                  <Th>IP</Th>
                </tr>
              </thead>
              <tbody>
                {rows.map((row) => (
                  <tr key={row.id}>
                    <Td className="text-slate-600">{formatDateTime(row.createdAt)}</Td>
                    <Td>
                      <code className="text-xs font-medium text-navy-900">{row.action}</code>
                    </Td>
                    <Td className="text-slate-600">
                      {row.entityType ?? '—'}
                      {row.entityId && (
                        <code className="ml-1.5 text-xs text-slate-500" title={row.entityId}>
                          {row.entityId.length > 12 ? shortId(row.entityId) : row.entityId}
                        </code>
                      )}
                    </Td>
                    <Td className="max-w-sm whitespace-normal text-slate-600">{row.details ?? '—'}</Td>
                    <Td>
                      <code className="text-xs text-slate-600" title={row.organizationId ?? undefined}>
                        {shortId(row.organizationId)}
                      </code>
                    </Td>
                    <Td>
                      <code className="text-xs text-slate-600" title={row.userId ?? undefined}>
                        {shortId(row.userId)}
                      </code>
                    </Td>
                    <Td className="text-slate-600">{row.ipAddress ?? '—'}</Td>
                  </tr>
                ))}
              </tbody>
            </Table>
            <p className="px-5 py-3 text-xs text-slate-500 sm:px-6">IDs are shortened to their first 8 characters. Hover over one to see it in full.</p>
          </TableCard>
        )
      }
    </Loadable>
  )
}

// ---------- Feature flags ----------

function FlagsTab() {
  const toast = useToast()
  const queryClient = useQueryClient()
  const query = useQuery({ queryKey: ['admin', 'flags'], queryFn: () => api.get<FeatureFlag[]>('/api/admin/flags') })
  const toggle = useMutation({
    mutationFn: (input: { flag: FeatureFlag; isEnabled: boolean }) => api.put(`/api/admin/flags/${encodeURIComponent(input.flag.key)}`, { isEnabled: input.isEnabled }),
    onSuccess: (_, input) => {
      toast.success(`${input.flag.key} is now ${input.isEnabled ? 'enabled' : 'disabled'}.`)
      queryClient.invalidateQueries({ queryKey: ['admin', 'flags'] })
      queryClient.invalidateQueries({ queryKey: ['admin', 'audit'] })
      queryClient.invalidateQueries({ queryKey: ['public-config'] })
    },
    onError: (err) => toast.error(errorMessage(err)),
  })

  return (
    <Loadable query={query}>
      {(flags) =>
        flags.length === 0 ? (
          <EmptyState title="No feature flags defined" description="Flags are created by the server's seed data." />
        ) : (
          <Card padded={false}>
            <div className="px-5 pt-5 sm:px-6 sm:pt-6">
              <CardHeader title="Feature flags" description="Changes apply to every organization immediately and are recorded in the audit log." />
            </div>
            <ul className="divide-y divide-slate-100 border-t border-slate-100">
              {flags.map((flag) => (
                <li key={flag.key} className="flex items-start justify-between gap-4 px-5 py-4 sm:px-6">
                  <div className="min-w-0">
                    <p className="flex flex-wrap items-center gap-2">
                      <code className="text-sm font-semibold text-navy-900">{flag.key}</code>
                      <Badge tone={flag.isEnabled ? 'green' : 'neutral'}>{flag.isEnabled ? 'Enabled' : 'Disabled'}</Badge>
                    </p>
                    <p className="mt-1 text-sm text-slate-600">{flag.description}</p>
                    <p className="mt-1 text-xs text-slate-400">Updated {formatDateTime(flag.updatedAt)}</p>
                  </div>
                  <Switch
                    checked={flag.isEnabled}
                    label={`${flag.isEnabled ? 'Disable' : 'Enable'} ${flag.key}`}
                    disabled={toggle.isPending}
                    onChange={(next) => toggle.mutate({ flag, isEnabled: next })}
                  />
                </li>
              ))}
            </ul>
          </Card>
        )
      }
    </Loadable>
  )
}

// ---------- Operations ----------

function OperationsTab() {
  const toast = useToast()
  const queryClient = useQueryClient()
  const [confirmReset, setConfirmReset] = useState(false)
  const [resetError, setResetError] = useState<string | null>(null)

  const reset = useMutation({
    mutationFn: () => api.post('/api/admin/demo/reset'),
    onSuccess: () => {
      toast.success('Demo data was reset. The three sample businesses were recreated.')
      setConfirmReset(false)
      queryClient.invalidateQueries({ queryKey: ['admin'] })
    },
    onError: (err) => setResetError(errorMessage(err)),
  })

  const runAlerts = useMutation({
    mutationFn: () => api.post<AlertJobResult>('/api/admin/jobs/price-alerts/run'),
    onSuccess: (result) => {
      toast.success(`Price alerts checked: ${result.alertsChecked}, triggered: ${result.alertsTriggered}.`)
      queryClient.invalidateQueries({ queryKey: ['admin', 'overview'] })
      queryClient.invalidateQueries({ queryKey: ['admin', 'usage'] })
      queryClient.invalidateQueries({ queryKey: ['notifications'] })
      queryClient.invalidateQueries({ queryKey: ['alerts'] })
    },
    onError: (err) => toast.error(errorMessage(err)),
  })

  return (
    <div className="grid gap-6 lg:grid-cols-2">
      <Card>
        <CardHeader title="Reset demo data" description="Deletes the three sample businesses and recreates them from scratch, including their projects, carts and purchase orders." />
        <p className="mb-4 text-sm text-slate-600">Real customer organizations are not touched. Anyone currently exploring a demo business loses the changes they made there.</p>
        <Button
          variant="danger"
          icon={<RefreshCw className="size-4" aria-hidden />}
          onClick={() => {
            setResetError(null)
            setConfirmReset(true)
          }}
        >
          Reset demo data
        </Button>
      </Card>

      <Card>
        <CardHeader title="Price alerts" description="Runs the scheduled price-alert check immediately for every organization instead of waiting for the next scheduled run." />
        <Button variant="primary" loading={runAlerts.isPending} onClick={() => runAlerts.mutate()}>
          Run price alerts now (all organizations)
        </Button>
        {runAlerts.isError && (
          <Notice tone="error" className="mt-4">
            {errorMessage(runAlerts.error)}
          </Notice>
        )}
        {runAlerts.data && (
          <div className="mt-4 rounded-xl border border-slate-200 p-4">
            <p className="mb-3 text-xs text-slate-500">Last run from this page: {formatDateTime(runAlerts.data.ranAt)}</p>
            <DefinitionList
              items={[
                { label: 'Alerts checked', value: num.format(runAlerts.data.alertsChecked) },
                { label: 'Alerts triggered', value: num.format(runAlerts.data.alertsTriggered) },
                { label: 'Supplier issues', value: num.format(runAlerts.data.providerIssues) },
              ]}
            />
          </div>
        )}
      </Card>

      <ConfirmDialog open={confirmReset} onClose={() => setConfirmReset(false)} onConfirm={() => reset.mutate()} title="Reset demo data?" confirmLabel="Delete and recreate" danger loading={reset.isPending}>
        <p>This deletes the three sample businesses with everything in them and recreates them from the seed data. It cannot be undone. Real customer organizations are not affected.</p>
        {resetError && (
          <Notice tone="error" className="mt-3">
            {resetError}
          </Notice>
        )}
      </ConfirmDialog>
    </div>
  )
}
