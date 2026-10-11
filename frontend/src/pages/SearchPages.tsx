import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import clsx from 'clsx'
import { Bookmark, BookmarkCheck, ExternalLink, LogOut, MousePointerClick, Scale, Search, ShieldAlert, Trash2, Truck, User } from 'lucide-react'
import { useEffect, useRef, useState, type FormEvent, type ReactNode } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { Logo } from '../components/AppLayout'
import { Badge, Button, DataStatusBadge, EmptyState, ErrorState, LinkButton, Notice, Skeleton, Unknown, useToast } from '../components/ui'
import { api, errorMessage } from '../lib/api'
import { useAuth, usePublicConfig } from '../lib/auth'
import { deliveryLabel, money, packLabel, timeAgo, unitLabel, unitMoney } from '../lib/format'
import type { SavedProduct, SearchOffer, SearchProduct, SearchResponse } from '../lib/types'

const fallbackSuggestions = ['copper pipe', 'LED bulb', 'air filter', 'interior paint', 'smoke alarm', 'mulch']

// ---------- Shell shared by the public pages ----------

function SiteHeader() {
  const { session, logout } = useAuth()
  const { data: config } = usePublicConfig()
  const navigate = useNavigate()
  return (
    <header className="sticky top-0 z-30 border-b border-slate-200/80 bg-white/90 backdrop-blur">
      <div className="mx-auto flex h-16 max-w-6xl items-center justify-between gap-4 px-4 sm:px-6">
        <Link to="/" aria-label="FindMyPrice home"><Logo /></Link>
        <nav className="flex items-center gap-1.5 text-sm" aria-label="Account">
          {session ? (
            <>
              {config?.savedItemsEnabled && <LinkButton to="/saved" variant="ghost" size="sm" icon={<Bookmark className="size-4" />}>Saved</LinkButton>}
              {config?.workspaceEnabled && <LinkButton to="/app" variant="ghost" size="sm">Business workspace</LinkButton>}
              <span className="hidden items-center gap-1.5 px-2 text-slate-500 sm:flex"><User className="size-4" />{session.user.fullName.split(' ')[0]}</span>
              <Button variant="ghost" size="sm" icon={<LogOut className="size-4" />} onClick={async () => { await logout(); navigate('/') }}>Sign out</Button>
            </>
          ) : (
            <>
              <LinkButton to="/login" variant="ghost" size="sm">Sign in</LinkButton>
              <LinkButton to="/register" variant="primary" size="sm">Create account</LinkButton>
            </>
          )}
        </nav>
      </div>
    </header>
  )
}

function SiteFooter() {
  const { data: config } = usePublicConfig()
  return (
    <footer className="mt-auto bg-navy-950 text-navy-300">
      <div className="mx-auto flex max-w-6xl flex-col gap-5 px-6 py-9 sm:flex-row sm:items-start sm:justify-between">
        <div>
          <Logo light />
          <p className="mt-2 text-sm">Search once. See every supplier's price. Buy where it's best.</p>
          {config?.workspaceEnabled && <Link to="/business" className="mt-3 inline-block text-sm font-medium text-accent-400 hover:underline">FindMyPrice for business</Link>}
        </div>
        <p className="max-w-md text-xs leading-relaxed text-navy-400">
          FindMyPrice is an independent comparison tool. It does not sell products or take payment: you buy on the supplier's own site.
          Supplier names belong to their owners and appear for comparison only. Prices marked DEMO are sample data, not supplier quotes.
        </p>
      </div>
    </footer>
  )
}

export function PublicShell({ children }: { children: ReactNode }) {
  return (
    <div className="flex min-h-screen flex-col bg-canvas">
      <SiteHeader />
      {children}
      <SiteFooter />
    </div>
  )
}

function SearchBox({ initial = '', large, autoFocus }: { initial?: string; large?: boolean; autoFocus?: boolean }) {
  const [value, setValue] = useState(initial)
  const navigate = useNavigate()
  useEffect(() => setValue(initial), [initial])
  function submit(event: FormEvent) {
    event.preventDefault()
    const query = value.trim()
    if (query.length >= 2) navigate(`/search?q=${encodeURIComponent(query)}`)
  }
  return (
    <form onSubmit={submit} role="search" className={clsx('flex w-full items-center gap-2 rounded-2xl border border-slate-300 bg-white p-1.5 shadow-card focus-within:border-accent-500 focus-within:ring-4 focus-within:ring-accent-500/15', large && 'sm:p-2')}>
      <Search className={clsx('ml-3 shrink-0 text-slate-400', large ? 'size-6' : 'size-5')} aria-hidden />
      <input
        type="search"
        aria-label="Search for a product"
        placeholder="What are you looking for? e.g. copper pipe, LED bulb"
        value={value}
        autoFocus={autoFocus}
        maxLength={120}
        onChange={(e) => setValue(e.target.value)}
        className={clsx('min-w-0 flex-1 bg-transparent px-1 text-navy-900 placeholder:text-slate-400 focus:outline-none', large ? 'h-12 text-lg' : 'h-10 text-base')}
      />
      <Button type="submit" variant="accent" size={large ? 'lg' : 'md'} disabled={value.trim().length < 2}>Find prices</Button>
    </form>
  )
}

function Suggestions({ items }: { items: string[] }) {
  return (
    <div className="flex flex-wrap items-center justify-center gap-2">
      {items.map((item) => (
        <Link key={item} to={`/search?q=${encodeURIComponent(item)}`} className="rounded-full border border-slate-300 bg-white px-3.5 py-1.5 text-sm text-navy-800 hover:border-accent-400 hover:text-accent-700">
          {item}
        </Link>
      ))}
    </div>
  )
}

// ---------- Home ----------

export function HomePage() {
  const steps = [
    { icon: Search, title: 'Search', text: 'Type the product you need. No account, no forms.' },
    { icon: Scale, title: 'Compare', text: 'See each supplier\'s price side by side, with pack sizes worked out per unit.' },
    { icon: MousePointerClick, title: 'Buy at the supplier', text: 'Pick the offer you like and go straight to that supplier\'s site to buy.' },
  ]
  return (
    <PublicShell>
      <section className="relative overflow-hidden bg-navy-900">
        <div className="pointer-events-none absolute -left-40 top-0 size-[30rem] rounded-full bg-accent-600/25 blur-3xl" />
        <div className="relative mx-auto max-w-3xl px-6 pb-20 pt-16 text-center sm:pt-24">
          <h1 className="text-4xl font-semibold leading-tight tracking-tight text-white sm:text-5xl">Find the best price before you buy</h1>
          <p className="mx-auto mt-4 max-w-xl text-lg text-navy-200">Search a product, compare prices across suppliers, and go straight to the one you choose.</p>
          <div className="mt-9"><SearchBox large autoFocus /></div>
          <div className="mt-6">
            <p className="mb-3 text-sm text-navy-300">Popular searches</p>
            <Suggestions items={fallbackSuggestions} />
          </div>
        </div>
      </section>

      <main className="mx-auto w-full max-w-6xl px-6 py-14">
        <div className="grid gap-6 md:grid-cols-3">
          {steps.map((step, index) => (
            <div key={step.title} className="rounded-2xl border border-slate-200 bg-white p-6 shadow-card">
              <div className="flex items-center gap-3">
                <span className="flex size-10 items-center justify-center rounded-xl bg-accent-50 text-accent-600"><step.icon className="size-5" /></span>
                <h2 className="font-semibold text-navy-900">{index + 1}. {step.title}</h2>
              </div>
              <p className="mt-3 text-sm leading-relaxed text-slate-600">{step.text}</p>
            </div>
          ))}
        </div>
        <Notice tone="info" className="mt-10" title="About the prices">
          FindMyPrice is in preview. Until suppliers are connected through their official price feeds, prices shown are sample data and are labelled DEMO.
          Always check the final price on the supplier's site before you buy.
        </Notice>
      </main>
    </PublicShell>
  )
}

// ---------- Results ----------

function shippingText(offer: SearchOffer) {
  if (offer.shippingFlatFee === null) return null
  if (offer.shippingFlatFee === 0) return 'Free shipping'
  return offer.freeShippingThreshold !== null ? `${money(offer.shippingFlatFee)} shipping, free over ${money(offer.freeShippingThreshold)}` : `${money(offer.shippingFlatFee)} shipping`
}

function OfferRow({ offer, unit, best }: { offer: SearchOffer; unit: string; best: boolean }) {
  const out = offer.stockStatus === 'out_of_stock'
  const shipping = shippingText(offer)
  return (
    <li className={clsx('flex flex-wrap items-center gap-x-6 gap-y-3 px-5 py-4 sm:px-6', best && 'bg-emerald-50/60', out && 'opacity-60')}>
      <div className="min-w-0 flex-1 basis-56">
        <p className="flex flex-wrap items-center gap-2 font-semibold text-navy-900">
          {offer.supplier}
          {best && <Badge tone="green">Best price</Badge>}
          <DataStatusBadge status={offer.dataStatus} />
        </p>
        <p className="mt-0.5 truncate text-sm text-slate-600" title={offer.title}>{offer.title}</p>
        <p className="mt-1 flex flex-wrap items-center gap-x-3 gap-y-0.5 text-xs text-slate-500">
          <span>{packLabel(offer.packQuantity, offer.packUnit)}</span>
          <span className={out ? 'text-red-600' : offer.stockStatus === 'in_stock' ? 'text-emerald-700' : ''}>{out ? 'Out of stock' : offer.stockStatus === 'in_stock' ? 'In stock' : 'Stock not reported'}</span>
          <span className="inline-flex items-center gap-1"><Truck className="size-3" />{offer.deliveryDaysMin === null && offer.deliveryDaysMax === null ? 'Delivery time unknown' : deliveryLabel(offer.deliveryDaysMin, offer.deliveryDaysMax)}</span>
          <span>{shipping ?? <Unknown>Shipping not published</Unknown>}</span>
        </p>
      </div>
      <div className="text-right">
        <p className="text-xl font-semibold text-navy-900 tabular">{money(offer.price)}</p>
        {offer.unitPrice !== null && (offer.packQuantity !== 1 || offer.packUnit !== unit) && <p className="text-xs text-slate-500">{unitMoney(offer.unitPrice)} per {unitLabel(unit)}</p>}
      </div>
      <div className="w-full sm:w-44">
        {offer.buyUrl ? (
          <a href={offer.buyUrl} target="_blank" rel="noopener noreferrer" title={offer.buyNote ?? undefined}
            className={clsx('inline-flex h-10 w-full items-center justify-center gap-2 rounded-lg px-4 text-sm font-medium', best ? 'bg-accent-600 text-white hover:bg-accent-700' : 'border border-slate-300 bg-white text-navy-900 hover:bg-slate-50')}>
            Buy at {offer.supplier} <ExternalLink className="size-4" />
          </a>
        ) : (
          <p className="rounded-lg border border-dashed border-slate-300 px-3 py-2 text-center text-xs text-slate-500">No online store link for this supplier</p>
        )}
      </div>
    </li>
  )
}

function ProductCard({ product, query }: { product: SearchProduct; query: string }) {
  const { session } = useAuth()
  const { data: config } = usePublicConfig()
  const queryClient = useQueryClient()
  const toast = useToast()
  const canSave = !!session && !!config?.savedItemsEnabled
  const saved = useQuery({ queryKey: ['saved'], queryFn: () => api.get<SavedProduct[]>('/api/saved'), enabled: canSave })
  const isSaved = saved.data?.some((s) => s.productId === product.productId) ?? false
  const toggle = useMutation({
    mutationFn: () => (isSaved ? api.delete<SavedProduct[]>(`/api/saved/${product.productId}`) : api.post<SavedProduct[]>('/api/saved', { productId: product.productId, query })),
    onSuccess: (items) => { queryClient.setQueryData(['saved'], items); toast.success(isSaved ? 'Removed from saved products.' : 'Saved. Find it under Saved.') },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const available = product.offers.filter((o) => o.stockStatus !== 'out_of_stock' && o.unitPrice !== null)
  const bestIndex = available.length ? product.offers.indexOf(available[0]) : -1
  const dearest = available.length > 1 ? available[available.length - 1] : null

  return (
    <section className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-card">
      <div className="flex flex-wrap items-start justify-between gap-3 border-b border-slate-200 px-5 py-4 sm:px-6">
        <div>
          <h2 className="flex flex-wrap items-center gap-2 text-lg font-semibold text-navy-900">
            {product.name}
            {product.isSafetyCritical && <Badge tone="red" title="Check the rating and listing match what your job requires."><ShieldAlert className="size-3" /> Safety-critical</Badge>}
          </h2>
          <p className="mt-0.5 text-sm text-slate-500">
            {product.category} · {product.offers.length} supplier{product.offers.length === 1 ? '' : 's'}
            {dearest && available[0].unitPrice! < dearest.unitPrice! && (
              <> · prices from <b className="font-medium text-navy-800">{unitMoney(available[0].unitPrice)}</b> to {unitMoney(dearest.unitPrice)} per {unitLabel(product.unit)}</>
            )}
          </p>
        </div>
        {canSave && (
          <Button size="sm" loading={toggle.isPending} onClick={() => toggle.mutate()} icon={isSaved ? <BookmarkCheck className="size-4 text-accent-600" /> : <Bookmark className="size-4" />}>
            {isSaved ? 'Saved' : 'Save'}
          </Button>
        )}
      </div>
      <ul className="divide-y divide-slate-100">
        {product.offers.map((offer, index) => <OfferRow key={`${offer.supplier}-${index}`} offer={offer} unit={product.unit} best={index === bestIndex} />)}
      </ul>
    </section>
  )
}

export function SearchPage() {
  const [params] = useSearchParams()
  const query = (params.get('q') ?? '').trim()
  const { data, isLoading, error, refetch } = useQuery({
    queryKey: ['search', query],
    queryFn: () => api.anonymous.get<SearchResponse>(`/api/public/search?q=${encodeURIComponent(query)}`),
    enabled: query.length >= 2,
    staleTime: 60_000,
  })
  const heading = useRef<HTMLHeadingElement>(null)
  useEffect(() => heading.current?.focus(), [query])

  return (
    <PublicShell>
      <div className="border-b border-slate-200 bg-white">
        <div className="mx-auto max-w-4xl px-4 py-5 sm:px-6"><SearchBox initial={query} /></div>
      </div>
      <main className="mx-auto w-full max-w-4xl px-4 py-8 sm:px-6">
        {query.length < 2 ? (
          <EmptyState icon={<Search className="size-5" />} title="Search for a product" description="Type at least two characters, or try one of these." action={<Suggestions items={fallbackSuggestions} />} />
        ) : error ? (
          <ErrorState error={error} onRetry={refetch} />
        ) : isLoading || !data ? (
          <div className="space-y-5" role="status" aria-label="Searching">
            <Skeleton className="h-7 w-64" />
            {[0, 1].map((i) => <Skeleton key={i} className="h-56 rounded-2xl" />)}
          </div>
        ) : data.results.length === 0 ? (
          <EmptyState
            icon={<Search className="size-5" />}
            title={`No prices found for "${data.query}"`}
            description="FindMyPrice is in preview with a limited sample catalog of building, maintenance and business supplies. Try one of these."
            action={<Suggestions items={data.suggestions.slice(0, 8)} />}
          />
        ) : (
          <div className="space-y-6">
            <h1 ref={heading} tabIndex={-1} className="text-xl font-semibold text-navy-900 focus:outline-none">
              {data.results.length} product{data.results.length === 1 ? '' : 's'} for "{data.query}"
            </h1>
            {data.demoData && (
              <Notice tone="warning" title="Sample prices">
                These prices are demo data, not live quotes. The Buy button opens the supplier's own site, where you'll see the real price and complete your purchase.
              </Notice>
            )}
            {data.results.map((product) => <ProductCard key={product.productId} product={product} query={data.query} />)}
            <p className="text-xs leading-relaxed text-slate-500">
              Offers are sorted by price per unit, with out-of-stock offers last. Shipping is shown where the supplier publishes it and is not included in the price.
              FindMyPrice does not sell anything or take payment.
            </p>
          </div>
        )}
      </main>
    </PublicShell>
  )
}

// ---------- Saved products ----------

export function SavedPage() {
  const { session, loading } = useAuth()
  const { data: config } = usePublicConfig()
  const queryClient = useQueryClient()
  const toast = useToast()
  const enabled = !!session && !!config?.savedItemsEnabled
  const { data, isLoading, error, refetch } = useQuery({ queryKey: ['saved'], queryFn: () => api.get<SavedProduct[]>('/api/saved'), enabled })
  const remove = useMutation({
    mutationFn: (productId: string) => api.delete<SavedProduct[]>(`/api/saved/${productId}`),
    onSuccess: (items) => queryClient.setQueryData(['saved'], items),
    onError: (e) => toast.error(errorMessage(e)),
  })

  return (
    <PublicShell>
      <main className="mx-auto w-full max-w-3xl px-4 py-10 sm:px-6">
        <h1 className="mb-6 text-2xl font-semibold tracking-tight text-navy-900">Saved products</h1>
        {loading ? <Skeleton className="h-40 rounded-2xl" /> : !session ? (
          <EmptyState icon={<Bookmark className="size-5" />} title="Sign in to see your saved products" action={<LinkButton to="/login" variant="primary">Sign in</LinkButton>} />
        ) : !config?.savedItemsEnabled ? (
          <Notice tone="info">Saved products are not available on this server.</Notice>
        ) : error ? <ErrorState error={error} onRetry={refetch} /> : isLoading || !data ? <Skeleton className="h-40 rounded-2xl" /> : data.length === 0 ? (
          <EmptyState icon={<Bookmark className="size-5" />} title="Nothing saved yet" description="Search for a product and choose Save to keep it here." action={<LinkButton to="/" variant="primary">Search products</LinkButton>} />
        ) : (
          <ul className="divide-y divide-slate-100 overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-card">
            {data.map((item) => (
              <li key={item.productId} className="flex flex-wrap items-center justify-between gap-3 px-5 py-4">
                <div className="min-w-0">
                  <Link to={`/search?q=${encodeURIComponent(item.query)}`} className="font-medium text-navy-900 hover:text-accent-700">{item.name}</Link>
                  <p className="text-xs text-slate-500">
                    {item.bestPrice !== null ? <>When saved: {unitMoney(item.bestPrice)} per {unitLabel(item.unit)} at {item.bestSupplier}</> : 'No price when saved'} · saved {timeAgo(item.savedAt)}
                  </p>
                </div>
                <div className="flex gap-2">
                  <LinkButton to={`/search?q=${encodeURIComponent(item.query)}`} size="sm">Check prices</LinkButton>
                  <Button size="sm" variant="ghost" aria-label={`Remove ${item.name}`} onClick={() => remove.mutate(item.productId)}><Trash2 className="size-4" /></Button>
                </div>
              </li>
            ))}
          </ul>
        )}
      </main>
    </PublicShell>
  )
}
