import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import clsx from 'clsx'
import { BarChart3, Bell, Building2, ChevronDown, ClipboardList, CreditCard, FileText, FlaskConical, LayoutDashboard, LogOut, Menu, ShieldCheck, Store, Tag, X } from 'lucide-react'
import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Link, NavLink, Navigate, Outlet, useLocation, useNavigate } from 'react-router-dom'
import { api } from '../lib/api'
import { useAuth } from '../lib/auth'
import { timeAgo } from '../lib/format'
import { ROLE_LABELS, type Notification } from '../lib/types'
import { Badge, Button, ErrorState, Spinner } from './ui'

export function Logo({ light }: { light?: boolean }) {
  return (
    <span className="inline-flex items-center gap-2.5">
      <img src="/favicon.svg" alt="" className="size-8 rounded-lg" />
      <span className={clsx('text-lg font-semibold tracking-tight', light ? 'text-white' : 'text-navy-900')}>
        FindMy<span className="text-accent-500">Price</span>
      </span>
    </span>
  )
}

const navigation = [
  { to: '/app', label: 'Dashboard', icon: LayoutDashboard, end: true },
  { to: '/app/projects', label: 'Projects', icon: ClipboardList },
  { to: '/app/purchase-orders', label: 'Purchase orders', icon: FileText },
  { to: '/app/alerts', label: 'Price alerts', icon: Tag },
  { to: '/app/suppliers', label: 'Suppliers', icon: Store },
  { to: '/app/reports', label: 'Reports', icon: BarChart3 },
]
const settingsNavigation = [
  { to: '/app/team', label: 'Organization', icon: Building2 },
  { to: '/app/billing', label: 'Plan & billing', icon: CreditCard },
]

function useOutsideClose(onClose: () => void) {
  const ref = useRef<HTMLDivElement>(null)
  useEffect(() => {
    const handler = (event: MouseEvent) => {
      if (ref.current && !ref.current.contains(event.target as Node)) onClose()
    }
    const escape = (event: KeyboardEvent) => event.key === 'Escape' && onClose()
    document.addEventListener('mousedown', handler)
    document.addEventListener('keydown', escape)
    return () => {
      document.removeEventListener('mousedown', handler)
      document.removeEventListener('keydown', escape)
    }
  }, [onClose])
  return ref
}

function Notifications() {
  const [open, setOpen] = useState(false)
  const queryClient = useQueryClient()
  const ref = useOutsideClose(() => setOpen(false))
  const { data } = useQuery({ queryKey: ['notifications'], queryFn: () => api.get<Notification[]>('/api/notifications'), refetchInterval: 60_000 })
  const markRead = useMutation({
    mutationFn: () => api.post('/api/notifications/read-all'),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['notifications'] })
      queryClient.invalidateQueries({ queryKey: ['dashboard'] })
    },
  })
  const unread = data?.filter((n) => !n.isRead).length ?? 0

  return (
    <div className="relative" ref={ref}>
      <button type="button" onClick={() => setOpen((v) => !v)} aria-label={`Notifications${unread ? `, ${unread} unread` : ''}`} aria-expanded={open} className="relative rounded-lg p-2 text-slate-600 hover:bg-slate-100">
        <Bell className="size-5" />
        {unread > 0 && <span className="absolute right-1 top-1 flex size-4 items-center justify-center rounded-full bg-accent-600 text-[10px] font-semibold text-white">{unread > 9 ? '9+' : unread}</span>}
      </button>
      {open && (
        <div className="absolute right-0 z-40 mt-2 w-80 max-w-[calc(100vw-2rem)] overflow-hidden rounded-xl border border-slate-200 bg-white shadow-pop">
          <div className="flex items-center justify-between border-b border-slate-200 px-4 py-3">
            <p className="text-sm font-semibold text-navy-900">Notifications</p>
            {unread > 0 && (
              <button type="button" className="text-xs font-medium text-accent-600 hover:underline" onClick={() => markRead.mutate()}>
                Mark all read
              </button>
            )}
          </div>
          <div className="max-h-96 overflow-y-auto">
            {!data?.length ? (
              <p className="px-4 py-8 text-center text-sm text-slate-500">No notifications yet. Price alerts will appear here when they trigger.</p>
            ) : (
              data.map((n) => (
                <div key={n.id} className={clsx('border-b border-slate-100 px-4 py-3 last:border-0', !n.isRead && 'bg-accent-50/50')}>
                  <p className="text-sm font-medium text-navy-900">{n.title}</p>
                  <p className="mt-0.5 text-sm text-slate-600">{n.body}</p>
                  <p className="mt-1 text-xs text-slate-400">{timeAgo(n.createdAt)}</p>
                </div>
              ))
            )}
          </div>
          <Link to="/app/alerts" onClick={() => setOpen(false)} className="block border-t border-slate-200 px-4 py-2.5 text-center text-sm font-medium text-accent-600 hover:bg-slate-50">
            Manage price alerts
          </Link>
        </div>
      )}
    </div>
  )
}

function AccountMenu() {
  const { session, logout, switchOrganization } = useAuth()
  const [open, setOpen] = useState(false)
  const navigate = useNavigate()
  const ref = useOutsideClose(() => setOpen(false))
  if (!session) return null
  const initials = session.user.fullName.split(' ').map((p) => p[0]).slice(0, 2).join('').toUpperCase()

  return (
    <div className="relative" ref={ref}>
      <button type="button" onClick={() => setOpen((v) => !v)} aria-expanded={open} className="flex items-center gap-2 rounded-lg py-1 pl-1 pr-2 hover:bg-slate-100">
        <span className="flex size-8 items-center justify-center rounded-full bg-navy-900 text-xs font-semibold text-white">{initials}</span>
        <span className="hidden text-left sm:block">
          <span className="block max-w-40 truncate text-sm font-medium leading-tight text-navy-900">{session.user.fullName}</span>
          <span className="block max-w-40 truncate text-xs leading-tight text-slate-500">{session.organization.name}</span>
        </span>
        <ChevronDown className="size-4 text-slate-400" />
      </button>
      {open && (
        <div className="absolute right-0 z-40 mt-2 w-72 overflow-hidden rounded-xl border border-slate-200 bg-white shadow-pop">
          <div className="border-b border-slate-200 px-4 py-3">
            <p className="truncate text-sm font-semibold text-navy-900">{session.user.fullName}</p>
            <p className="truncate text-xs text-slate-500">{session.user.isDemo ? 'Demo account' : session.user.email}</p>
            <div className="mt-2 flex flex-wrap gap-1.5">
              <Badge tone="blue">{ROLE_LABELS[session.organization.role]}</Badge>
              <Badge>{session.plan.name} plan</Badge>
            </div>
          </div>
          {session.organizations.length > 1 && (
            <div className="border-b border-slate-200 py-1">
              <p className="px-4 py-1.5 text-xs font-medium uppercase tracking-wide text-slate-400">Switch organization</p>
              {session.organizations.map((org) => (
                <button
                  key={org.id}
                  type="button"
                  disabled={org.id === session.organization.id}
                  onClick={async () => {
                    await switchOrganization(org.id)
                    setOpen(false)
                    navigate('/app')
                  }}
                  className="flex w-full items-center justify-between px-4 py-2 text-left text-sm text-navy-800 hover:bg-slate-50 disabled:font-semibold disabled:text-accent-700"
                >
                  <span className="truncate">{org.name}</span>
                  {org.id === session.organization.id && <span className="text-xs">Current</span>}
                </button>
              ))}
            </div>
          )}
          {session.user.isPlatformAdmin && (
            <Link to="/admin" onClick={() => setOpen(false)} className="flex items-center gap-2 px-4 py-2.5 text-sm text-navy-800 hover:bg-slate-50">
              <ShieldCheck className="size-4" /> Platform administration
            </Link>
          )}
          <button
            type="button"
            onClick={async () => {
              await logout()
              navigate('/')
            }}
            className="flex w-full items-center gap-2 px-4 py-2.5 text-left text-sm text-navy-800 hover:bg-slate-50"
          >
            <LogOut className="size-4" /> {session.user.isDemo ? 'Exit demo' : 'Sign out'}
          </button>
        </div>
      )}
    </div>
  )
}

function SideNav({ onNavigate }: { onNavigate?: () => void }) {
  const item = (entry: (typeof navigation)[number]) => (
    <NavLink
      key={entry.to}
      to={entry.to}
      end={'end' in entry ? entry.end : false}
      onClick={onNavigate}
      className={({ isActive }) =>
        clsx('flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium transition-colors', isActive ? 'bg-white/12 text-white' : 'text-navy-200 hover:bg-white/6 hover:text-white')
      }
    >
      <entry.icon className="size-4.5 shrink-0" aria-hidden />
      {entry.label}
    </NavLink>
  )
  return (
    <nav className="flex flex-1 flex-col gap-1 px-3 py-4" aria-label="Main">
      {navigation.map(item)}
      <p className="mt-5 px-3 pb-1 text-xs font-medium uppercase tracking-wider text-navy-400">Settings</p>
      {settingsNavigation.map(item)}
    </nav>
  )
}

export function RequireAuth({ children }: { children: ReactNode }) {
  const { session, loading, loadError, retry } = useAuth()
  const location = useLocation()
  if (loading) return <Spinner label="Loading your workspace" />
  // A failed request is not the same as being signed out: keep the user here and let them retry.
  if (!session && loadError)
    return (
      <div className="mx-auto max-w-lg px-6 py-24">
        <ErrorState error={loadError} onRetry={retry} />
      </div>
    )
  if (!session) return <Navigate to="/login" replace state={{ from: location.pathname + location.search }} />
  return <>{children}</>
}

export function AppLayout() {
  const { session } = useAuth()
  const [mobileOpen, setMobileOpen] = useState(false)
  const location = useLocation()
  useEffect(() => setMobileOpen(false), [location.pathname])
  if (!session) return null

  return (
    <div className="min-h-screen lg:pl-64">
      <aside className="fixed inset-y-0 left-0 z-30 hidden w-64 flex-col bg-navy-900 lg:flex">
        <Link to="/app" className="flex h-16 items-center px-5">
          <Logo light />
        </Link>
        <SideNav />
        <div className="border-t border-white/10 px-5 py-4 text-xs text-navy-300">
          <p className="font-medium text-navy-100">{session.organization.name}</p>
          <p className="mt-0.5">{session.plan.name} plan</p>
        </div>
      </aside>

      {mobileOpen && (
        <div className="fixed inset-0 z-40 lg:hidden">
          <div className="absolute inset-0 bg-navy-950/60" onClick={() => setMobileOpen(false)} />
          <aside className="absolute inset-y-0 left-0 flex w-72 max-w-[85vw] flex-col bg-navy-900">
            <div className="flex h-16 items-center justify-between px-5">
              <Logo light />
              <button type="button" aria-label="Close menu" onClick={() => setMobileOpen(false)} className="rounded-lg p-2 text-navy-200 hover:bg-white/10">
                <X className="size-5" />
              </button>
            </div>
            <SideNav onNavigate={() => setMobileOpen(false)} />
          </aside>
        </div>
      )}

      <header className="sticky top-0 z-20 flex h-16 items-center gap-3 border-b border-slate-200 bg-white/90 px-4 backdrop-blur sm:px-6">
        <button type="button" aria-label="Open menu" onClick={() => setMobileOpen(true)} className="rounded-lg p-2 text-slate-600 hover:bg-slate-100 lg:hidden">
          <Menu className="size-5" />
        </button>
        <div className="lg:hidden">
          <Logo />
        </div>
        <div className="ml-auto flex items-center gap-1.5">
          <Notifications />
          <AccountMenu />
        </div>
      </header>

      {session.organization.isDemo && <DemoBanner />}

      <main className="mx-auto w-full max-w-7xl px-4 py-6 sm:px-6 lg:px-8 lg:py-8">
        <Outlet />
      </main>
    </div>
  )
}

function DemoBanner() {
  const { logout } = useAuth()
  const navigate = useNavigate()
  return (
    <div className="flex flex-wrap items-center justify-center gap-x-4 gap-y-1.5 border-b border-violet-200 bg-violet-50 px-4 py-2 text-sm text-violet-900">
      <span className="inline-flex items-center gap-2 font-medium">
        <FlaskConical className="size-4" aria-hidden />
        Demo workspace
      </span>
      <span>Sample business with simulated supplier prices and stock. Nothing here is a live quote, and no order is ever placed.</span>
      <Button
        size="sm"
        variant="accent"
        onClick={async () => {
          await logout()
          navigate('/register')
        }}
      >
        Create a free account
      </Button>
    </div>
  )
}
