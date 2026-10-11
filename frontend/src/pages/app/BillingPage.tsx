import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import clsx from 'clsx'
import { Check, Minus } from 'lucide-react'
import { useEffect, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { Badge, Button, Card, CardHeader, ErrorState, Notice, PageHeader, Skeleton, SkeletonRows, Unavailable, useToast } from '../../components/ui'
import { api, errorMessage } from '../../lib/api'
import { useAuth, useSession } from '../../lib/auth'
import { planPrice } from '../../lib/format'
import type { BillingOverview, Plan } from '../../lib/types'

const FEATURES: { key: keyof Pick<Plan, 'aiMaterialLists' | 'advancedOptimization' | 'priceAlerts' | 'purchaseOrders' | 'advancedReporting' | 'approvalWorkflows'>; label: string }[] = [
  { key: 'aiMaterialLists', label: 'AI material lists' },
  { key: 'advancedOptimization', label: 'Advanced optimization (Fastest/Balanced strategies)' },
  { key: 'priceAlerts', label: 'Price alerts' },
  { key: 'purchaseOrders', label: 'Purchase orders' },
  { key: 'advancedReporting', label: 'Advanced reporting' },
  { key: 'approvalWorkflows', label: 'Team approval workflows' },
]

const humanize = (value: string) => value.replace(/([a-z])([A-Z])/g, '$1 $2')
const statusTone = (status: string) => (status === 'Active' || status === 'Trialing' ? 'green' : status === 'Canceled' || status === 'Cancelled' ? 'red' : 'amber')

export function BillingPage() {
  const session = useSession()
  const toast = useToast()
  const queryClient = useQueryClient()
  const { refreshSession } = useAuth()
  const [searchParams, setSearchParams] = useSearchParams()
  const checkout = searchParams.get('checkout')
  const canManage = session.permissions.canManageBilling
  const [actionError, setActionError] = useState<string | null>(null)

  const query = useQuery({ queryKey: ['billing'], queryFn: () => api.get<BillingOverview>('/api/billing') })

  const refreshEntitlements = async () => {
    queryClient.invalidateQueries({ queryKey: ['billing'] })
    queryClient.invalidateQueries({ queryKey: ['dashboard'] })
    queryClient.invalidateQueries({ queryKey: ['po-settings'] })
    await refreshSession()
  }

  // Returning from Stripe: the webhook may already have applied the new plan.
  useEffect(() => {
    if (checkout !== 'success') return
    queryClient.invalidateQueries({ queryKey: ['billing'] })
    queryClient.invalidateQueries({ queryKey: ['dashboard'] })
    queryClient.invalidateQueries({ queryKey: ['po-settings'] })
    queryClient.invalidateQueries({ queryKey: ['session'] })
  }, [checkout, queryClient])

  const startCheckout = useMutation({
    mutationFn: (planCode: string) => api.post<{ url: string }>('/api/billing/checkout', { planCode }),
    onMutate: () => setActionError(null),
    onSuccess: ({ url }) => window.location.assign(url),
    onError: (err) => {
      setActionError(errorMessage(err))
      toast.error(errorMessage(err))
    },
  })

  const changePlan = useMutation({
    mutationFn: (plan: Plan) => api.post('/api/billing/change-plan', { planCode: plan.code }),
    onMutate: () => setActionError(null),
    onSuccess: async (_, plan) => {
      toast.success(`Your organization is now on the ${plan.name} plan.`)
      await refreshEntitlements()
    },
    onError: (err) => {
      setActionError(errorMessage(err))
      toast.error(errorMessage(err))
    },
  })

  const dismissCheckout = () => {
    const next = new URLSearchParams(searchParams)
    next.delete('checkout')
    setSearchParams(next, { replace: true })
  }

  const header = <PageHeader title="Plan & billing" description="Your current plan, what it includes, and how much of it your organization is using." />

  if (query.isPending)
    return (
      <>
        {header}
        <Skeleton className="mb-6 h-40 w-full" />
        <SkeletonRows rows={3} />
      </>
    )
  if (query.isError)
    return (
      <>
        {header}
        <ErrorState error={query.error} onRetry={() => query.refetch()} />
      </>
    )

  const data = query.data
  const { billing, currentPlan } = data
  const busy = startCheckout.isPending || changePlan.isPending

  const planAction = (plan: Plan) => {
    if (plan.code === currentPlan.code) return <p className="text-sm font-medium text-accent-700">This is your current plan.</p>
    if (!canManage) return <p className="text-xs text-slate-500">Only an organization owner can change the plan.</p>
    if (billing.stripeConfigured)
      return (
        <Button variant="accent" className="w-full" disabled={busy} loading={startCheckout.isPending && startCheckout.variables === plan.code} onClick={() => startCheckout.mutate(plan.code)}>
          Continue to secure checkout
        </Button>
      )
    if (billing.manualPlanChangeAllowed)
      return (
        <Button variant="primary" className="w-full" disabled={busy} loading={changePlan.isPending && changePlan.variables?.code === plan.code} onClick={() => changePlan.mutate(plan)}>
          Switch to this plan (no payment)
        </Button>
      )
    return <Unavailable label="Change plan" reason="Online billing is not configured on this server yet." />
  }

  return (
    <>
      {header}
      <div className="space-y-6">
        {checkout === 'success' && (
          <Notice tone="success" title="Checkout completed" action={<Button size="sm" variant="ghost" onClick={dismissCheckout}>Dismiss</Button>}>
            Checkout completed. Your plan updates as soon as Stripe confirms the payment.
          </Notice>
        )}
        {checkout === 'cancelled' && (
          <Notice tone="warning" title="Checkout cancelled" action={<Button size="sm" variant="ghost" onClick={dismissCheckout}>Dismiss</Button>}>
            No payment was taken and your plan has not changed.
          </Notice>
        )}

        <Card>
          <CardHeader
            title="Current plan"
            action={
              <div className="flex flex-wrap items-center gap-2">
                {billing.stripeConfigured && billing.testMode && <Badge tone="amber" title="Stripe is using test keys. No real payment is taken.">Stripe test mode</Badge>}
                <Badge tone={statusTone(data.subscriptionStatus)}>Subscription: {humanize(data.subscriptionStatus)}</Badge>
              </div>
            }
          />
          <div className="grid gap-6 lg:grid-cols-3">
            <div>
              <p className="text-2xl font-semibold tracking-tight text-navy-900">{currentPlan.name}</p>
              <p className="mt-1 text-sm text-slate-500">
                <span className="text-lg font-semibold text-navy-900 tabular">{planPrice(currentPlan.monthlyPriceCents)}</span> / month
              </p>
              {currentPlan.description && <p className="mt-2 text-sm text-slate-500">{currentPlan.description}</p>}
            </div>
            <Usage label="Projects created this month" used={data.projectsThisMonth} limit={currentPlan.maxProjectsPerMonth} />
            <Usage label="Team members" used={data.members} limit={currentPlan.maxMembers} />
          </div>
        </Card>

        <div>
          <div className="mb-3 flex flex-wrap items-end justify-between gap-2">
            <h2 className="text-base font-semibold text-navy-900">Plans</h2>
          </div>
          {data.pricingNote && <Notice className="mb-4">{data.pricingNote}</Notice>}
          {!canManage && <Notice className="mb-4">Only an organization owner can change the plan. Ask your owner if you need a feature that is not included.</Notice>}
          {canManage && billing.stripeConfigured && (
            <Notice className="mb-4">Card details are entered on Stripe. SmartBuy never sees or stores them.</Notice>
          )}
          {canManage && !billing.stripeConfigured && billing.manualPlanChangeAllowed && (
            <Notice tone="warning" className="mb-4">
              Online billing is not configured on this server. Plan switching is enabled for evaluation and no payment is taken.
            </Notice>
          )}
          {actionError && (
            <Notice tone="error" className="mb-4" title="The plan could not be changed">
              {actionError}
            </Notice>
          )}

          <div className="grid gap-4 lg:grid-cols-3">
            {data.plans.map((plan) => {
              const current = plan.code === currentPlan.code
              return (
                <Card key={plan.code} className={clsx('flex flex-col', current && 'border-accent-400 ring-2 ring-accent-500/30')}>
                  <div className="flex items-start justify-between gap-2">
                    <h3 className="text-lg font-semibold text-navy-900">{plan.name}</h3>
                    {current && <Badge tone="blue">Current plan</Badge>}
                  </div>
                  <p className="mt-2">
                    <span className="text-3xl font-semibold tracking-tight text-navy-900 tabular">{planPrice(plan.monthlyPriceCents)}</span>
                    <span className="text-sm text-slate-500"> / month</span>
                  </p>
                  {plan.description && <p className="mt-2 text-sm text-slate-500">{plan.description}</p>}

                  <ul className="mt-4 space-y-2 border-t border-slate-100 pt-4 text-sm">
                    <FeatureRow included label={plan.maxProjectsPerMonth === null ? 'Unlimited projects per month' : `${plan.maxProjectsPerMonth} ${plan.maxProjectsPerMonth === 1 ? 'project' : 'projects'} per month`} />
                    <FeatureRow included label={plan.maxMembers === null ? 'Unlimited team members' : `${plan.maxMembers} team ${plan.maxMembers === 1 ? 'member' : 'members'}`} />
                    {FEATURES.map((feature) => (
                      <FeatureRow key={feature.key} included={plan[feature.key]} label={feature.label} />
                    ))}
                  </ul>

                  <div className="mt-auto pt-5">{planAction(plan)}</div>
                </Card>
              )
            })}
          </div>
        </div>
      </div>
    </>
  )
}

function FeatureRow({ included, label }: { included: boolean; label: string }) {
  return (
    <li className={clsx('flex items-start gap-2', included ? 'text-navy-800' : 'text-slate-400')}>
      {included ? <Check className="mt-0.5 size-4 shrink-0 text-emerald-600" aria-hidden /> : <Minus className="mt-0.5 size-4 shrink-0" aria-hidden />}
      <span>
        {label}
        <span className="sr-only">{included ? ' (included)' : ' (not included)'}</span>
      </span>
    </li>
  )
}

function Usage({ label, used, limit }: { label: string; used: number; limit: number | null }) {
  const percent = limit === null || limit <= 0 ? null : Math.min(100, Math.round((used / limit) * 100))
  const full = limit !== null && used >= limit
  return (
    <div>
      <p className="text-sm text-slate-500">{label}</p>
      <p className="mt-1 text-lg font-semibold text-navy-900 tabular">
        {used} <span className="text-sm font-normal text-slate-500">{limit === null ? '· unlimited' : `of ${limit}`}</span>
      </p>
      {percent !== null && (
        <div className="mt-2 h-2 overflow-hidden rounded-full bg-slate-100" role="progressbar" aria-label={label} aria-valuemin={0} aria-valuemax={limit ?? undefined} aria-valuenow={used}>
          <div className={clsx('h-full rounded-full', full ? 'bg-amber-500' : 'bg-accent-500')} style={{ width: `${percent}%` }} />
        </div>
      )}
      {full && <p className="mt-1 text-xs text-amber-700">Limit reached on this plan.</p>}
    </div>
  )
}
