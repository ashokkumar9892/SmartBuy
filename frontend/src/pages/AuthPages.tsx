import { ArrowRight, Building2, FlaskConical } from 'lucide-react'
import { useEffect, useRef, useState, type FormEvent, type ReactNode } from 'react'
import { Link, Navigate, useLocation, useNavigate } from 'react-router-dom'
import { Logo } from '../components/AppLayout'
import { Badge, Button, Input, Notice, Select, Skeleton, Unavailable } from '../components/ui'
import { errorMessage } from '../lib/api'
import { useAuth, usePublicConfig } from '../lib/auth'
import { INDUSTRIES, industryLabel, type DemoBusiness } from '../lib/types'

function AuthShell({ title, subtitle, children, footer }: { title: string; subtitle: ReactNode; children: ReactNode; footer: ReactNode }) {
  return (
    <div className="grid min-h-screen lg:grid-cols-[1fr_1.1fr]">
      <aside className="relative hidden flex-col justify-between overflow-hidden bg-navy-900 p-12 text-white lg:flex">
        <div className="pointer-events-none absolute -right-32 -top-32 size-96 rounded-full bg-accent-600/20 blur-3xl" />
        <Link to="/"><Logo light /></Link>
        <div className="relative">
          <p className="text-3xl font-semibold leading-tight tracking-tight">One List. Every Supplier.<br />The Smarter Way to Buy.</p>
          <ul className="mt-8 space-y-3 text-navy-200">
            <li>Compare the same material across suppliers with pack sizes normalized.</li>
            <li>See the lowest total cost including shipping, with unknowns clearly marked.</li>
            <li>Turn the plan into purchase orders your team can approve.</li>
          </ul>
        </div>
        <p className="relative text-xs text-navy-400">Preview release. Supplier prices come from demo data unless a live integration is connected.</p>
      </aside>
      <main className="flex flex-col justify-center px-6 py-12 sm:px-12">
        <div className="mx-auto w-full max-w-md">
          <Link to="/" className="mb-10 inline-block lg:hidden"><Logo /></Link>
          <h1 className="text-2xl font-semibold tracking-tight text-navy-900">{title}</h1>
          <p className="mt-1.5 text-sm text-slate-500">{subtitle}</p>
          <div className="mt-8">{children}</div>
          <div className="mt-8 text-sm text-slate-500">{footer}</div>
        </div>
      </main>
    </div>
  )
}

type GoogleIdentity = {
  accounts: { id: { initialize: (options: { client_id: string; callback: (response: { credential?: string }) => void }) => void; renderButton: (element: HTMLElement, options: Record<string, unknown>) => void } }
}

/**
 * Google sign-in. Rendered only when the server reports a configured OAuth client ID; the Google script is not even
 * loaded otherwise. The ID token is verified on the server, never trusted in the browser.
 */
function GoogleButton() {
  const { data } = usePublicConfig()
  const { loginWithGoogle } = useAuth()
  const container = useRef<HTMLDivElement>(null)
  const [error, setError] = useState<string | null>(null)
  const clientId = data?.googleEnabled ? data.googleClientId : null

  useEffect(() => {
    if (!clientId) return
    let cancelled = false
    const start = () => {
      const google = (window as unknown as { google?: GoogleIdentity }).google
      if (cancelled || !google || !container.current) return
      google.accounts.id.initialize({
        client_id: clientId,
        callback: (response) => {
          if (!response.credential) return setError('Google did not return a sign-in token.')
          loginWithGoogle(response.credential).catch((e) => setError(errorMessage(e)))
        },
      })
      google.accounts.id.renderButton(container.current, { theme: 'outline', size: 'large', width: 320, text: 'continue_with' })
    }
    const existing = document.querySelector<HTMLScriptElement>('script[data-google-identity]')
    if (existing) start()
    else {
      const script = document.createElement('script')
      script.src = 'https://accounts.google.com/gsi/client'
      script.async = true
      script.dataset.googleIdentity = 'true'
      script.onload = start
      script.onerror = () => setError('Google sign-in could not be loaded.')
      document.head.appendChild(script)
    }
    return () => {
      cancelled = true
    }
  }, [clientId, loginWithGoogle])

  if (!clientId) return <Unavailable label="Continue with Google" reason="Google sign-in is not configured on this server yet." />
  return (
    <div>
      <div ref={container} />
      {error && <p className="mt-2 text-xs text-red-600">{error}</p>}
    </div>
  )
}

function Divider() {
  return (
    <div className="my-6 flex items-center gap-3 text-xs uppercase tracking-wide text-slate-400">
      <span className="h-px flex-1 bg-slate-200" /> or <span className="h-px flex-1 bg-slate-200" />
    </div>
  )
}

export function LoginPage() {
  const { session, login } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const from = (location.state as { from?: string } | null)?.from
  // Only ever return to a path inside this app.
  const destination = from && from.startsWith('/') && !from.startsWith('//') ? from : '/app'

  if (session) return <Navigate to={destination} replace />

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await login(email, password)
      navigate(destination, { replace: true })
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <AuthShell
      title="Sign in to SmartBuy"
      subtitle="Welcome back. Enter your details to continue."
      footer={
        <>
          New to SmartBuy? <Link to="/register" className="font-medium text-accent-600 hover:underline">Create a free account</Link>
          <span className="mx-2 text-slate-300">|</span>
          <Link to="/demo" className="font-medium text-accent-600 hover:underline">Try the demo</Link>
        </>
      }
    >
      <form onSubmit={submit} className="space-y-4" noValidate>
        {error && <Notice tone="error">{error}</Notice>}
        <Input label="Work email" type="email" autoComplete="email" required value={email} onChange={(e) => setEmail(e.target.value)} />
        <Input label="Password" type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} />
        <Button type="submit" variant="primary" size="lg" className="w-full" loading={busy} disabled={!email || !password}>
          Sign in
        </Button>
      </form>
      <Divider />
      <GoogleButton />
    </AuthShell>
  )
}

export function RegisterPage() {
  const { session, register } = useAuth()
  const { data: config } = usePublicConfig()
  const [form, setForm] = useState({ fullName: '', organizationName: '', industry: 'Plumbing', email: '', password: '' })
  const justRegistered = useRef(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const set = (key: keyof typeof form) => (e: { target: { value: string } }) => setForm((f) => ({ ...f, [key]: e.target.value }))

  // A brand-new account goes straight to creating its first project; an already signed-in visitor goes to the dashboard.
  if (session) return <Navigate to={justRegistered.current ? '/app/projects/new' : '/app'} replace />

  const passwordProblem = form.password.length === 0 ? null : form.password.length < 10 ? 'Use at least 10 characters.' : !/[a-z]/i.test(form.password) || !/[^a-z]/i.test(form.password) ? 'Mix letters with numbers or symbols.' : null

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      justRegistered.current = true
      await register(form)
    } catch (e) {
      justRegistered.current = false
      setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <AuthShell
      title="Create your SmartBuy account"
      subtitle="Start on the Free plan. No card required."
      footer={<>Already have an account? <Link to="/login" className="font-medium text-accent-600 hover:underline">Sign in</Link></>}
    >
      {config && !config.selfSignupEnabled ? (
        <Notice tone="warning" title="Sign-ups are closed right now">New accounts are temporarily disabled by the platform administrator. You can still explore the demo.</Notice>
      ) : (
        <form onSubmit={submit} className="space-y-4" noValidate>
          {error && <Notice tone="error">{error}</Notice>}
          <Input label="Your name" autoComplete="name" required value={form.fullName} onChange={set('fullName')} />
          <div className="grid gap-4 sm:grid-cols-2">
            <Input label="Business name" autoComplete="organization" required value={form.organizationName} onChange={set('organizationName')} />
            <Select label="Industry" value={form.industry} onChange={set('industry')}>
              {INDUSTRIES.map((i) => <option key={i.value} value={i.value}>{i.label}</option>)}
            </Select>
          </div>
          <Input label="Work email" type="email" autoComplete="email" required value={form.email} onChange={set('email')} />
          <Input label="Password" type="password" autoComplete="new-password" required value={form.password} onChange={set('password')} error={passwordProblem} hint="At least 10 characters, mixing letters with numbers or symbols." />
          <Button type="submit" variant="primary" size="lg" className="w-full" loading={busy} disabled={!form.fullName || !form.organizationName || !form.email || !form.password || !!passwordProblem}>
            Create account
          </Button>
          <p className="text-xs leading-relaxed text-slate-500">You become the owner of a new organization on the Free plan. You can invite your team and change plans later.</p>
        </form>
      )}
      <Divider />
      <GoogleButton />
    </AuthShell>
  )
}

export function DemoBusinessCards({ compact }: { compact?: boolean }) {
  const { data: config, isLoading, isError } = usePublicConfig()
  const { openDemo } = useAuth()
  const navigate = useNavigate()
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  async function open(business: DemoBusiness) {
    setBusy(business.slug)
    setError(null)
    try {
      await openDemo(business.slug)
      navigate('/app')
    } catch (e) {
      setError(errorMessage(e))
      setBusy(null)
    }
  }

  if (isLoading) return <div className="grid gap-4 sm:grid-cols-3">{[0, 1, 2].map((i) => <Skeleton key={i} className="h-40" />)}</div>
  if (isError) return <Notice tone="error" title="The demo could not be loaded">The SmartBuy server is not reachable right now. Try again in a moment.</Notice>
  if (!config?.demoEnabled || config.demoBusinesses.length === 0)
    return <Notice tone="warning" title="The demo is switched off">The platform administrator has disabled the demo workspaces. Create a free account to explore SmartBuy with your own list.</Notice>

  return (
    <div>
      {error && <Notice tone="error" className="mb-4">{error}</Notice>}
      <div className={compact ? 'grid gap-3' : 'grid gap-4 md:grid-cols-3'}>
        {config.demoBusinesses.map((business) => (
          <button
            key={business.slug}
            type="button"
            disabled={busy !== null}
            onClick={() => open(business)}
            className="group flex flex-col rounded-2xl border border-slate-200 bg-white p-5 text-left shadow-card transition hover:border-accent-400 hover:shadow-pop disabled:opacity-60"
          >
            <span className="flex items-center gap-3">
              <span className="flex size-10 items-center justify-center rounded-xl bg-navy-50 text-navy-700"><Building2 className="size-5" /></span>
              <span>
                <span className="block font-semibold text-navy-900">{business.name}</span>
                <span className="block text-xs text-slate-500">{industryLabel(business.industry)}</span>
              </span>
            </span>
            <span className="mt-3 flex-1 text-sm text-slate-600">{business.tagline}</span>
            <span className="mt-4 flex items-center justify-between">
              <Badge tone="violet">{business.plan.charAt(0).toUpperCase() + business.plan.slice(1)} plan</Badge>
              <span className="inline-flex items-center gap-1 text-sm font-medium text-accent-600">
                {busy === business.slug ? 'Opening…' : 'Open workspace'} <ArrowRight className="size-4 transition group-hover:translate-x-0.5" />
              </span>
            </span>
          </button>
        ))}
      </div>
    </div>
  )
}

export function DemoPage() {
  const { session } = useAuth()
  if (session) return <Navigate to="/app" replace />
  return (
    <div className="min-h-screen bg-canvas">
      <header className="border-b border-slate-200 bg-white">
        <div className="mx-auto flex h-16 max-w-6xl items-center justify-between px-6">
          <Link to="/"><Logo /></Link>
          <Link to="/register" className="text-sm font-medium text-accent-600 hover:underline">Create a free account</Link>
        </div>
      </header>
      <main className="mx-auto max-w-6xl px-6 py-14">
        <Badge tone="violet"><FlaskConical className="size-3.5" /> Interactive demo</Badge>
        <h1 className="mt-4 text-3xl font-semibold tracking-tight text-navy-900">Choose a sample business</h1>
        <p className="mt-2 max-w-2xl text-slate-600">
          Each workspace is a fully working account with sample projects, material lists, supplier comparisons and purchase orders. No sign-up needed.
        </p>
        <Notice tone="info" className="my-8" title="What is real and what is simulated">
          The application, calculations and workflow are real. Supplier prices, stock and delivery times are simulated demo data and are labelled DEMO wherever they appear.
          Demo workspaces are shared with other visitors and may be reset at any time.
        </Notice>
        <DemoBusinessCards />
      </main>
    </div>
  )
}
