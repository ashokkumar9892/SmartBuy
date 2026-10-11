import clsx from 'clsx'
import { ArrowRight, BadgeDollarSign, Boxes, Building, Check, ChevronDown, ClipboardCheck, FileText, Hammer, Leaf, ListChecks, Mail, Menu, Paintbrush, Scale, ShieldCheck, Sparkles, Thermometer, Truck, Wrench, X, Zap } from 'lucide-react'
import { useMemo, useState, type ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { Logo } from '../components/AppLayout'
import { Badge, Button, LinkButton, Modal } from '../components/ui'
import { useAuth, usePublicConfig } from '../lib/auth'
import { money, planPrice } from '../lib/format'
import type { Plan } from '../lib/types'
import { DemoBusinessCards } from './AuthPages'

function Section({ id, eyebrow, title, description, children, tinted }: { id?: string; eyebrow: string; title: string; description?: string; children: ReactNode; tinted?: boolean }) {
  return (
    <section id={id} className={clsx('scroll-mt-20 py-20 sm:py-24', tinted && 'bg-white')}>
      <div className="mx-auto max-w-6xl px-6">
        <div className="mx-auto max-w-2xl text-center">
          <p className="text-sm font-semibold uppercase tracking-widest text-accent-600">{eyebrow}</p>
          <h2 className="mt-3 text-3xl font-semibold tracking-tight text-navy-900 sm:text-4xl">{title}</h2>
          {description && <p className="mt-4 text-lg text-slate-600">{description}</p>}
        </div>
        <div className="mt-14">{children}</div>
      </div>
    </section>
  )
}

// ---------- Interactive sample comparison (illustrative figures, computed in the browser) ----------

type SampleSupplier = { name: string; shipping: number | null; freeOver: number | null; days: number }
type SampleOffer = { supplier: string; pack: number; packLabel: string; price: number }
type SampleItem = { name: string; unit: string; defaultQty: number; offers: SampleOffer[] }

const sampleSuppliers: SampleSupplier[] = [
  { name: 'Supplier A', shipping: 8.99, freeOver: 45, days: 3 },
  { name: 'Supplier B', shipping: 7.99, freeOver: 45, days: 4 },
  { name: 'Local Supply', shipping: 25, freeOver: 500, days: 1 },
]

const sampleItems: SampleItem[] = [
  { name: '3/4 in. Type L copper pipe', unit: 'ft', defaultQty: 50, offers: [
    { supplier: 'Supplier A', pack: 10, packLabel: '10 ft stick', price: 52.1 },
    { supplier: 'Supplier B', pack: 10, packLabel: '10 ft stick', price: 50.4 },
    { supplier: 'Local Supply', pack: 20, packLabel: '20 ft stick', price: 93.8 },
  ] },
  { name: '3/4 in. copper 90° elbow', unit: 'each', defaultQty: 20, offers: [
    { supplier: 'Supplier A', pack: 10, packLabel: 'Bag of 10', price: 21.4 },
    { supplier: 'Supplier B', pack: 25, packLabel: 'Bag of 25', price: 47.3 },
    { supplier: 'Local Supply', pack: 1, packLabel: 'Single', price: 2.3 },
  ] },
  { name: 'Quarter-turn shutoff valve', unit: 'each', defaultQty: 10, offers: [
    { supplier: 'Supplier A', pack: 5, packLabel: 'Pack of 5', price: 49.8 },
    { supplier: 'Supplier B', pack: 1, packLabel: 'Single', price: 11.0 },
  ] },
  { name: 'Single-handle bathroom faucet', unit: 'each', defaultQty: 5, offers: [
    { supplier: 'Supplier A', pack: 1, packLabel: 'Single', price: 99.5 },
    { supplier: 'Local Supply', pack: 1, packLabel: 'Single', price: 84.0 },
  ] },
]

function shippingFor(supplier: SampleSupplier, subtotal: number) {
  if (subtotal === 0) return 0
  if (supplier.shipping === null) return 0
  return supplier.freeOver !== null && subtotal >= supplier.freeOver ? 0 : supplier.shipping
}

function SampleComparison() {
  const [quantities, setQuantities] = useState(sampleItems.map((i) => i.defaultQty))
  const [strategy, setStrategy] = useState<'cost' | 'speed'>('cost')

  const result = useMemo(() => {
    const lineCost = (offer: SampleOffer, quantity: number) => Math.ceil(quantity / offer.pack) * offer.price
    const evaluate = (choice: number[]) => {
      const bySupplier = new Map<string, number>()
      choice.forEach((offerIndex, itemIndex) => {
        const offer = sampleItems[itemIndex].offers[offerIndex]
        bySupplier.set(offer.supplier, (bySupplier.get(offer.supplier) ?? 0) + lineCost(offer, quantities[itemIndex]))
      })
      let subtotal = 0
      let shipping = 0
      let days = 0
      bySupplier.forEach((amount, name) => {
        const supplier = sampleSuppliers.find((s) => s.name === name)!
        subtotal += amount
        shipping += shippingFor(supplier, amount)
        days = Math.max(days, supplier.days)
      })
      return { subtotal, shipping, total: subtotal + shipping, days, suppliers: bySupplier.size }
    }

    // Small enough to try every combination, exactly like the server does for small lists.
    let best: { choice: number[]; score: ReturnType<typeof evaluate> } | null = null
    const walk = (index: number, choice: number[]) => {
      if (index === sampleItems.length) {
        const score = evaluate(choice)
        const better = !best || (strategy === 'cost' ? score.total < best.score.total : score.days < best.score.days || (score.days === best.score.days && score.total < best.score.total))
        if (better) best = { choice: [...choice], score }
        return
      }
      sampleItems[index].offers.forEach((_, offerIndex) => walk(index + 1, [...choice, offerIndex]))
    }
    walk(0, [])

    // Baseline: everything from Supplier A, the only one carrying all four items.
    const baselineChoice = sampleItems.map((item) => item.offers.findIndex((o) => o.supplier === 'Supplier A'))
    return { best: best!, baseline: evaluate(baselineChoice), lineCost }
  }, [quantities, strategy])

  const savings = result.baseline.total - result.best.score.total

  return (
    <div className="overflow-hidden rounded-3xl border border-slate-200 bg-white shadow-pop">
      <div className="flex flex-wrap items-center justify-between gap-3 border-b border-slate-200 bg-slate-50/70 px-5 py-4">
        <div>
          <p className="font-semibold text-navy-900">Sample: bathroom repipe materials</p>
          <p className="text-xs text-slate-500">Illustrative figures to show how the comparison works. Change a quantity or the strategy.</p>
        </div>
        <div className="flex rounded-lg border border-slate-300 bg-white p-0.5 text-sm" role="group" aria-label="Strategy">
          {([['cost', 'Lowest cost'], ['speed', 'Fastest delivery']] as const).map(([id, label]) => (
            <button key={id} type="button" onClick={() => setStrategy(id)} aria-pressed={strategy === id} className={clsx('rounded-md px-3 py-1.5 font-medium', strategy === id ? 'bg-navy-900 text-white' : 'text-slate-600 hover:bg-slate-100')}>
              {label}
            </button>
          ))}
        </div>
      </div>
      <div className="overflow-x-auto">
        <table className="w-full min-w-[640px] text-left text-sm">
          <thead className="text-xs uppercase tracking-wide text-slate-500">
            <tr>
              <th className="px-5 py-3 font-semibold">Material</th>
              <th className="px-3 py-3 font-semibold">Need</th>
              {sampleSuppliers.map((s) => <th key={s.name} className="px-3 py-3 text-right font-semibold">{s.name}</th>)}
            </tr>
          </thead>
          <tbody>
            {sampleItems.map((item, itemIndex) => (
              <tr key={item.name} className="border-t border-slate-100">
                <td className="px-5 py-3 font-medium text-navy-900">{item.name}</td>
                <td className="px-3 py-3">
                  <span className="inline-flex items-center gap-1.5">
                    <input
                      type="number"
                      min={1}
                      max={999}
                      aria-label={`Quantity of ${item.name}`}
                      value={quantities[itemIndex]}
                      onChange={(e) => setQuantities((q) => q.map((v, i) => (i === itemIndex ? Math.max(1, Math.min(999, Number(e.target.value) || 1)) : v)))}
                      className="h-8 w-16 rounded-md border border-slate-300 px-2 text-right tabular focus:border-accent-500 focus:outline-none"
                    />
                    <span className="text-slate-500">{item.unit}</span>
                  </span>
                </td>
                {sampleSuppliers.map((supplier) => {
                  const offerIndex = item.offers.findIndex((o) => o.supplier === supplier.name)
                  if (offerIndex < 0) return <td key={supplier.name} className="px-3 py-3 text-right text-slate-400">Not carried</td>
                  const offer = item.offers[offerIndex]
                  const chosen = result.best.choice[itemIndex] === offerIndex
                  const packs = Math.ceil(quantities[itemIndex] / offer.pack)
                  return (
                    <td key={supplier.name} className={clsx('px-3 py-3 text-right', chosen && 'bg-accent-50')}>
                      <span className={clsx('block font-semibold tabular', chosen ? 'text-accent-700' : 'text-navy-900')}>{money(result.lineCost(offer, quantities[itemIndex]))}</span>
                      <span className="block text-xs text-slate-500">{packs} × {offer.packLabel}</span>
                      {chosen && <span className="mt-0.5 inline-flex items-center gap-0.5 text-xs font-medium text-accent-700"><Check className="size-3" /> In plan</span>}
                    </td>
                  )
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <div className="grid gap-px border-t border-slate-200 bg-slate-200 sm:grid-cols-3">
        <div className="bg-white px-5 py-4">
          <p className="text-xs uppercase tracking-wide text-slate-500">Baseline: all from Supplier A</p>
          <p className="mt-1 text-xl font-semibold text-navy-900 tabular">{money(result.baseline.total)}</p>
        </div>
        <div className="bg-white px-5 py-4">
          <p className="text-xs uppercase tracking-wide text-slate-500">Optimized plan · {result.best.score.suppliers} supplier{result.best.score.suppliers === 1 ? '' : 's'} · {result.best.score.days} day{result.best.score.days === 1 ? '' : 's'}</p>
          <p className="mt-1 text-xl font-semibold text-navy-900 tabular">{money(result.best.score.total)} <span className="text-sm font-normal text-slate-500">incl. {money(result.best.score.shipping)} shipping</span></p>
        </div>
        <div className={clsx('px-5 py-4', savings >= 0 ? 'bg-emerald-50' : 'bg-amber-50')}>
          <p className="text-xs uppercase tracking-wide text-slate-600">{savings >= 0 ? 'Difference vs baseline' : 'Costs more than baseline'}</p>
          <p className={clsx('mt-1 text-xl font-semibold tabular', savings >= 0 ? 'text-emerald-700' : 'text-amber-800')}>
            {money(Math.abs(savings))} <span className="text-sm font-normal">({((Math.abs(savings) / result.baseline.total) * 100).toFixed(1)}%)</span>
          </p>
        </div>
      </div>
    </div>
  )
}

// ---------- Static content ----------

const benefits = [
  { icon: Scale, title: 'True apples-to-apples comparison', text: 'Products are matched by UPC, part number and verified specifications, then pack sizes are normalized. A bag of 10 is never confused with 10 bags.' },
  { icon: BadgeDollarSign, title: 'Lowest total cost, not lowest sticker', text: 'The optimizer weighs item prices, pack rounding, minimum orders and shipping thresholds across every supplier at once.' },
  { icon: Sparkles, title: 'Lists from plain language', text: 'Describe the job, upload a spreadsheet or PDF, or reuse a past project. You review and approve every quantity before anything is priced.' },
  { icon: ShieldCheck, title: 'Honest about uncertainty', text: 'Unknown shipping, tax or stock is shown as unknown, never as zero. Every price is labelled Live, Demo or Stale.' },
  { icon: FileText, title: 'Purchase orders with approvals', text: 'Turn a plan into numbered purchase orders per supplier, route them for approval, and export to PDF or CSV.' },
  { icon: Truck, title: 'You stay in control of buying', text: 'FindMyPrice prepares the purchase and links you to each supplier. It never places an order behind your back.' },
]

const industries = [
  { icon: Wrench, name: 'Plumbing', text: 'Pipe, fittings, valves, fixtures' },
  { icon: Zap, name: 'Electrical', text: 'Wire, devices, breakers, lighting' },
  { icon: Thermometer, name: 'HVAC', text: 'Filters, controls, duct, pumps' },
  { icon: Hammer, name: 'Construction', text: 'Lumber, drywall, fasteners, concrete' },
  { icon: Paintbrush, name: 'Home renovation', text: 'Paint, flooring, tile, mortar' },
  { icon: Leaf, name: 'Landscaping', text: 'Mulch, soil, pavers, irrigation' },
  { icon: Building, name: 'Property management', text: 'Turnover and maintenance supplies' },
  { icon: Boxes, name: 'General business', text: 'Jobsite and office consumables' },
]

const steps = [
  { icon: ListChecks, title: 'Build your list', text: 'Type what the job needs, enter items manually, upload CSV, Excel or PDF, or reuse a previous project.' },
  { icon: ClipboardCheck, title: 'Review and approve', text: 'Check the draft quantities and the matched catalog product on every line. Nothing is priced until you approve.' },
  { icon: Scale, title: 'Compare and optimize', text: 'See every supplier side by side, then choose Lowest Cost, Fastest Delivery or Balanced.' },
  { icon: FileText, title: 'Buy your way', text: 'Fill one cart grouped by supplier, open supplier links, export the list, or raise purchase orders.' },
]

const faqs = [
  { q: 'Are the prices live?', a: 'Only where a supplier is connected through an approved API. Until then FindMyPrice runs on clearly labelled demo data so you can evaluate the workflow. Every price shows whether it is Live, Demo or Stale, and demo prices are never presented as supplier quotes.' },
  { q: 'Does FindMyPrice place orders for me?', a: 'No. FindMyPrice builds the purchasing plan, groups your cart by supplier, links you to each supplier and produces purchase orders. Checkout happens on the supplier\'s own site or through your normal ordering process. An order is only ever reported as placed when a supported supplier API confirms it.' },
  { q: 'How are savings calculated?', a: 'Against a stated baseline: buying the whole list from the single supplier that carries the most of it. The optimized total and the baseline are shown side by side, and anything unknown, such as unpublished shipping, is excluded from both and flagged.' },
  { q: 'Can I trust the AI-generated quantities?', a: 'Treat them as a starting draft. AI proposes items and quantities and lists its assumptions; a person must review and approve the list before supplier search. They are not engineered or construction-grade take-offs. All money calculations are done by deterministic code, not by AI.' },
  { q: 'Will it swap in cheaper products automatically?', a: 'Never. Alternatives are suggestions with the price, specification and compatibility differences spelled out, and each one needs your explicit approval. Safety-critical electrical, plumbing and building components are not offered as substitutes on price alone.' },
  { q: 'Which suppliers are supported?', a: 'The adapter architecture covers national retailers, marketplaces, independent local suppliers and Shopify or WooCommerce merchants. Each is switched on only when its official API access and terms allow it. FindMyPrice does not scrape retailer websites.' },
  { q: 'Is my company\'s data separate from other companies?', a: 'Yes. Each organization has its own users, projects, lists, purchase orders, suppliers and alerts, and isolation is enforced on every query on the server.' },
]

function planFeatures(plan: Plan): string[] {
  const features = [
    plan.maxProjectsPerMonth === null ? 'Unlimited projects' : `${plan.maxProjectsPerMonth} new projects per month`,
    plan.maxMembers === 1 ? 'Single user' : `Up to ${plan.maxMembers ?? 'unlimited'} team members`,
    plan.advancedOptimization ? 'Lowest Cost, Fastest and Balanced strategies' : 'Supplier comparison and Lowest Cost plan',
    'CSV, Excel and PDF list import',
  ]
  if (plan.aiMaterialLists) features.push('AI material lists from plain language')
  if (plan.priceAlerts) features.push('Price, stock and better-offer alerts')
  if (plan.purchaseOrders) features.push('Purchase orders with PDF and CSV export')
  if (plan.approvalWorkflows) features.push('Team approval workflows')
  if (plan.advancedReporting) features.push('Spend reporting')
  if (!plan.purchaseOrders) features.push('Manual shopping-list exports')
  return features
}

const fallbackPlans: Plan[] = [
  { code: 'free', name: 'Free', description: 'Try FindMyPrice on a few projects each month.', monthlyPriceCents: 0, maxProjectsPerMonth: 3, maxMembers: 1, aiMaterialLists: false, advancedOptimization: false, priceAlerts: false, purchaseOrders: false, advancedReporting: false, approvalWorkflows: false },
  { code: 'pro', name: 'Pro', description: 'For owner-operators who buy materials every week.', monthlyPriceCents: 2900, maxProjectsPerMonth: 50, maxMembers: 3, aiMaterialLists: true, advancedOptimization: true, priceAlerts: true, purchaseOrders: false, advancedReporting: false, approvalWorkflows: false },
  { code: 'business', name: 'Business', description: 'For teams that need purchase orders, approvals and reporting.', monthlyPriceCents: 7900, maxProjectsPerMonth: null, maxMembers: 25, aiMaterialLists: true, advancedOptimization: true, priceAlerts: true, purchaseOrders: true, advancedReporting: true, approvalWorkflows: true },
]

export function LandingPage() {
  const { session } = useAuth()
  const { data: config } = usePublicConfig()
  const [demoOpen, setDemoOpen] = useState(false)
  const [menuOpen, setMenuOpen] = useState(false)
  const [openFaq, setOpenFaq] = useState<number | null>(0)
  const plans = config?.plans.length ? config.plans : fallbackPlans
  const links = [['#benefits', 'Benefits'], ['#how-it-works', 'How it works'], ['#industries', 'Industries'], ['#pricing', 'Pricing'], ['#faq', 'FAQ'], ['#contact', 'Contact']] as const

  return (
    <div className="bg-canvas">
      <header className="sticky top-0 z-30 border-b border-slate-200/80 bg-white/90 backdrop-blur">
        <div className="mx-auto flex h-16 max-w-6xl items-center justify-between gap-4 px-6">
          <Link to="/" aria-label="FindMyPrice home"><Logo /></Link>
          <nav className="hidden items-center gap-7 text-sm font-medium text-slate-600 md:flex" aria-label="Sections">
            {links.map(([href, label]) => <a key={href} href={href} className="hover:text-navy-900">{label}</a>)}
          </nav>
          <div className="flex items-center gap-2">
            {session ? (
              <LinkButton to="/app" variant="primary" size="sm">Open dashboard</LinkButton>
            ) : (
              <>
                <LinkButton to="/login" variant="ghost" size="sm" className="hidden sm:inline-flex">Sign in</LinkButton>
                <LinkButton to="/register" variant="primary" size="sm">Start Free</LinkButton>
              </>
            )}
            <button type="button" className="rounded-lg p-2 text-slate-600 hover:bg-slate-100 md:hidden" aria-label="Toggle menu" aria-expanded={menuOpen} onClick={() => setMenuOpen((v) => !v)}>
              {menuOpen ? <X className="size-5" /> : <Menu className="size-5" />}
            </button>
          </div>
        </div>
        {menuOpen && (
          <nav className="border-t border-slate-200 bg-white px-6 py-3 md:hidden" aria-label="Sections">
            {links.map(([href, label]) => <a key={href} href={href} onClick={() => setMenuOpen(false)} className="block py-2 text-sm font-medium text-slate-700">{label}</a>)}
            {!session && <Link to="/login" className="block py-2 text-sm font-medium text-slate-700">Sign in</Link>}
          </nav>
        )}
      </header>

      <section className="relative overflow-hidden bg-navy-900 text-white">
        <div className="pointer-events-none absolute -left-40 top-0 size-[32rem] rounded-full bg-accent-600/25 blur-3xl" />
        <div className="pointer-events-none absolute -right-40 bottom-0 size-[28rem] rounded-full bg-accent-400/10 blur-3xl" />
        <div className="relative mx-auto max-w-6xl px-6 pb-24 pt-20 text-center sm:pt-28">
          <p className="inline-flex items-center gap-2 rounded-full border border-white/15 bg-white/5 px-4 py-1.5 text-sm text-navy-100">
            <Sparkles className="size-4 text-accent-400" /> One List. Every Supplier. The Smarter Way to Buy.
          </p>
          <h1 className="mx-auto mt-7 max-w-4xl text-4xl font-semibold leading-[1.1] tracking-tight text-white sm:text-6xl">
            Buy Smarter. Spend Less.<br className="hidden sm:block" /> Run Your Business Better.
          </h1>
          <p className="mx-auto mt-6 max-w-2xl text-lg text-navy-200">Compare materials across suppliers, optimize purchases, and reduce procurement costs using AI.</p>
          <div className="mt-9 flex flex-wrap items-center justify-center gap-3">
            <LinkButton to={session ? '/app' : '/register'} variant="accent" size="lg" icon={<ArrowRight className="order-last size-4" />}>Start Free</LinkButton>
            <Button variant="secondary" size="lg" onClick={() => setDemoOpen(true)}>Try Demo</Button>
            <a href="#how-it-works" className="inline-flex h-12 items-center rounded-lg px-5 text-base font-medium text-white hover:bg-white/10">See How It Works</a>
          </div>
          <p className="mt-6 text-sm text-navy-300">Free plan available · No card required · Demo needs no sign-up</p>
        </div>
      </section>

      <div className="relative z-10 mx-auto -mt-12 max-w-6xl px-6">
        <SampleComparison />
      </div>

      <Section id="benefits" eyebrow="Benefits" title="Purchasing decisions you can defend" description="Built for contractors and small businesses that buy the same kinds of materials from several suppliers every week.">
        <div className="grid gap-6 sm:grid-cols-2 lg:grid-cols-3">
          {benefits.map((benefit) => (
            <div key={benefit.title} className="rounded-2xl border border-slate-200 bg-white p-6 shadow-card">
              <div className="flex size-11 items-center justify-center rounded-xl bg-accent-50 text-accent-600"><benefit.icon className="size-5" /></div>
              <h3 className="mt-4 font-semibold text-navy-900">{benefit.title}</h3>
              <p className="mt-2 text-sm leading-relaxed text-slate-600">{benefit.text}</p>
            </div>
          ))}
        </div>
      </Section>

      <Section id="how-it-works" eyebrow="How it works" title="From job description to purchase order" tinted>
        <ol className="grid gap-6 md:grid-cols-4">
          {steps.map((step, index) => (
            <li key={step.title} className="relative rounded-2xl border border-slate-200 bg-canvas p-6">
              <span className="absolute -top-3 left-6 rounded-full bg-navy-900 px-2.5 py-0.5 text-xs font-semibold text-white">Step {index + 1}</span>
              <step.icon className="size-6 text-accent-600" />
              <h3 className="mt-4 font-semibold text-navy-900">{step.title}</h3>
              <p className="mt-2 text-sm leading-relaxed text-slate-600">{step.text}</p>
            </li>
          ))}
        </ol>
        <div className="mt-10 rounded-2xl border border-slate-200 bg-canvas p-6 text-sm leading-relaxed text-slate-600">
          <p className="font-semibold text-navy-900">Example</p>
          <p className="mt-1 italic">"I need 50 feet of 3/4-inch copper pipe, 20 elbows, 10 shutoff valves, and 5 faucets for a residential plumbing project."</p>
          <p className="mt-2">FindMyPrice turns this into four structured lines, links each to a catalog product, asks you to confirm the quantities, then prices every supplier that carries them.</p>
        </div>
      </Section>

      <Section id="industries" eyebrow="Supported industries" title="Product specifications built for the trades" description="Each industry has its own categories and the specifications that decide whether two products are really the same thing.">
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
          {industries.map((industry) => (
            <div key={industry.name} className="flex items-start gap-3 rounded-2xl border border-slate-200 bg-white p-5 shadow-card">
              <div className="flex size-10 shrink-0 items-center justify-center rounded-xl bg-navy-50 text-navy-700"><industry.icon className="size-5" /></div>
              <div>
                <p className="font-semibold text-navy-900">{industry.name}</p>
                <p className="mt-0.5 text-sm text-slate-500">{industry.text}</p>
              </div>
            </div>
          ))}
        </div>
      </Section>

      <Section id="pricing" eyebrow="Pricing" title="Start free. Upgrade when it pays for itself." description="Proposed test pricing for this preview release." tinted>
        <div className="grid gap-6 lg:grid-cols-3">
          {plans.map((plan) => {
            const featured = plan.code === 'pro'
            return (
              <div key={plan.code} className={clsx('relative flex flex-col rounded-3xl border p-7', featured ? 'border-navy-900 bg-navy-900 text-white shadow-pop' : 'border-slate-200 bg-white shadow-card')}>
                {featured && <Badge tone="blue" className="absolute -top-3 left-7">Most popular</Badge>}
                <h3 className={clsx('text-lg font-semibold', featured ? 'text-white' : 'text-navy-900')}>{plan.name}</h3>
                <p className={clsx('mt-1 text-sm', featured ? 'text-navy-200' : 'text-slate-500')}>{plan.description}</p>
                <p className="mt-6 flex items-baseline gap-1">
                  <span className={clsx('text-4xl font-semibold tracking-tight tabular', featured ? 'text-white' : 'text-navy-900')}>{planPrice(plan.monthlyPriceCents)}</span>
                  <span className={clsx('text-sm', featured ? 'text-navy-200' : 'text-slate-500')}>/ month</span>
                </p>
                <ul className={clsx('mt-6 flex-1 space-y-2.5 text-sm', featured ? 'text-navy-100' : 'text-slate-600')}>
                  {planFeatures(plan).map((feature) => (
                    <li key={feature} className="flex items-start gap-2.5">
                      <Check className={clsx('mt-0.5 size-4 shrink-0', featured ? 'text-accent-400' : 'text-accent-600')} />
                      {feature}
                    </li>
                  ))}
                </ul>
                <LinkButton to={session ? '/app/billing' : '/register'} variant={featured ? 'accent' : 'secondary'} className="mt-8 w-full">
                  {session ? 'Manage plan' : plan.monthlyPriceCents === 0 ? 'Start Free' : `Start with ${plan.name}`}
                </LinkButton>
              </div>
            )
          })}
        </div>
        <p className="mt-6 text-center text-sm text-slate-500">These are proposed test prices, not established market prices. Every account starts on Free; paid plans are selected inside the app.</p>
      </Section>

      <Section id="faq" eyebrow="FAQ" title="Straight answers">
        <div className="mx-auto max-w-3xl divide-y divide-slate-200 rounded-2xl border border-slate-200 bg-white shadow-card">
          {faqs.map((faq, index) => (
            <div key={faq.q}>
              <button type="button" onClick={() => setOpenFaq(openFaq === index ? null : index)} aria-expanded={openFaq === index} className="flex w-full items-center justify-between gap-4 px-6 py-4 text-left">
                <span className="font-medium text-navy-900">{faq.q}</span>
                <ChevronDown className={clsx('size-5 shrink-0 text-slate-400 transition-transform', openFaq === index && 'rotate-180')} />
              </button>
              {openFaq === index && <p className="px-6 pb-5 text-sm leading-relaxed text-slate-600">{faq.a}</p>}
            </div>
          ))}
        </div>
      </Section>

      <ContactSection />

      <footer className="bg-navy-950 text-navy-300">
        <div className="mx-auto flex max-w-6xl flex-col gap-6 px-6 py-10 sm:flex-row sm:items-center sm:justify-between">
          <div>
            <Logo light />
            <p className="mt-2 text-sm">One List. Every Supplier. The Smarter Way to Buy.</p>
          </div>
          <p className="max-w-md text-xs leading-relaxed text-navy-400">
            FindMyPrice is an independent purchasing tool. Supplier names belong to their owners and appear for comparison only; no affiliation or endorsement is implied. Prices shown from demo data are not supplier quotes.
          </p>
        </div>
      </footer>

      <Modal open={demoOpen} onClose={() => setDemoOpen(false)} title="Try the demo" description="Open a sample business. No sign-up needed. Supplier prices are simulated and labelled DEMO." wide>
        <DemoBusinessCards />
      </Modal>
    </div>
  )
}

function ContactSection() {
  const [form, setForm] = useState({ name: '', email: '', message: '' })
  const contactEmail = import.meta.env.VITE_CONTACT_EMAIL as string | undefined
  const ready = form.name.trim() && /\S+@\S+\.\S+/.test(form.email) && form.message.trim()
  // No message is stored or sent by FindMyPrice: the form hands the text to the visitor's own email app.
  const mailto = contactEmail
    ? `mailto:${contactEmail}?subject=${encodeURIComponent(`FindMyPrice enquiry from ${form.name}`)}&body=${encodeURIComponent(`${form.message}\n\n${form.name}\n${form.email}`)}`
    : null

  return (
    <Section id="contact" eyebrow="Contact" title="Talk to us about your suppliers" description="Tell us which suppliers you buy from and what you would like to compare." tinted>
      <div className="mx-auto grid max-w-4xl gap-8 md:grid-cols-[1fr_1.2fr]">
        <div className="space-y-4 text-sm text-slate-600">
          <p className="flex items-start gap-3"><Mail className="mt-0.5 size-5 shrink-0 text-accent-600" /> <span>Use the form to write to us. It opens your email app with the message filled in, so you keep a copy and nothing is stored here.</span></p>
          <p className="flex items-start gap-3"><ShieldCheck className="mt-0.5 size-5 shrink-0 text-accent-600" /> <span>We only connect supplier data through official, permitted integrations.</span></p>
        </div>
        <form className="space-y-4 rounded-2xl border border-slate-200 bg-canvas p-6" onSubmit={(e) => e.preventDefault()}>
          <div className="grid gap-4 sm:grid-cols-2">
            <label className="block text-sm font-medium text-navy-800">Name
              <input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} className="mt-1.5 block h-10 w-full rounded-lg border border-slate-300 bg-white px-3 text-sm focus:border-accent-500 focus:outline-none" />
            </label>
            <label className="block text-sm font-medium text-navy-800">Email
              <input type="email" value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} className="mt-1.5 block h-10 w-full rounded-lg border border-slate-300 bg-white px-3 text-sm focus:border-accent-500 focus:outline-none" />
            </label>
          </div>
          <label className="block text-sm font-medium text-navy-800">How can we help?
            <textarea rows={4} value={form.message} onChange={(e) => setForm({ ...form, message: e.target.value })} className="mt-1.5 block w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm focus:border-accent-500 focus:outline-none" />
          </label>
          {mailto ? (
            <a href={ready ? mailto : undefined} aria-disabled={!ready} className={clsx('inline-flex h-10 items-center justify-center rounded-lg px-4 text-sm font-medium', ready ? 'bg-navy-900 text-white hover:bg-navy-800' : 'pointer-events-none bg-navy-300 text-white')}>
              Open in my email app
            </a>
          ) : (
            <div>
              <button type="button" disabled className="inline-flex h-10 items-center rounded-lg border border-dashed border-slate-300 bg-slate-50 px-4 text-sm font-medium text-slate-400">Send message</button>
              <p className="mt-1.5 text-xs text-slate-500">Unavailable: no contact address is configured for this site yet (set VITE_CONTACT_EMAIL at build time).</p>
            </div>
          )}
        </form>
      </div>
    </Section>
  )
}
