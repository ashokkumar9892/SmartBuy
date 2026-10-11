import clsx from 'clsx'
import { AlertTriangle, CheckCircle2, Info, Loader2, X, XCircle } from 'lucide-react'
import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useId,
  useRef,
  useState,
  type ButtonHTMLAttributes,
  type InputHTMLAttributes,
  type ReactNode,
  type SelectHTMLAttributes,
  type TextareaHTMLAttributes,
} from 'react'
import { Link, type LinkProps } from 'react-router-dom'
import type { DataStatus } from '../lib/types'

// ---------- Buttons ----------

type Variant = 'primary' | 'accent' | 'secondary' | 'ghost' | 'danger'
type Size = 'sm' | 'md' | 'lg'

const variantClass: Record<Variant, string> = {
  primary: 'bg-navy-900 text-white hover:bg-navy-800 shadow-sm disabled:bg-navy-300',
  accent: 'bg-accent-600 text-white hover:bg-accent-700 shadow-sm disabled:bg-accent-200',
  secondary: 'bg-white text-navy-900 border border-slate-300 hover:bg-slate-50 shadow-sm disabled:text-slate-400 disabled:bg-slate-50',
  ghost: 'text-navy-700 hover:bg-navy-50 disabled:text-slate-400 disabled:hover:bg-transparent',
  danger: 'bg-white text-red-700 border border-red-200 hover:bg-red-50 disabled:text-red-300',
}
const sizeClass: Record<Size, string> = { sm: 'h-8 px-3 text-sm gap-1.5', md: 'h-10 px-4 text-sm gap-2', lg: 'h-12 px-6 text-base gap-2' }
const buttonBase = 'inline-flex items-center justify-center rounded-lg font-medium transition-colors whitespace-nowrap select-none'

type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & { variant?: Variant; size?: Size; loading?: boolean; icon?: ReactNode }

export function Button({ variant = 'secondary', size = 'md', loading, icon, children, className, disabled, type = 'button', ...rest }: ButtonProps) {
  return (
    <button type={type} disabled={disabled || loading} className={clsx(buttonBase, variantClass[variant], sizeClass[size], className)} {...rest}>
      {loading ? <Loader2 className="size-4 animate-spin" aria-hidden /> : icon}
      {children}
    </button>
  )
}

export function LinkButton({ variant = 'secondary', size = 'md', icon, children, className, ...rest }: LinkProps & { variant?: Variant; size?: Size; icon?: ReactNode }) {
  return (
    <Link className={clsx(buttonBase, variantClass[variant], sizeClass[size], className)} {...rest}>
      {icon}
      {children}
    </Link>
  )
}

/** A control that exists in the design but cannot be used right now. It always says why. */
export function Unavailable({ label, reason, size = 'md' }: { label: string; reason: string; size?: Size }) {
  return (
    <span className="inline-flex flex-col items-start gap-1">
      <button type="button" disabled aria-disabled className={clsx(buttonBase, sizeClass[size], 'border border-dashed border-slate-300 bg-slate-50 text-slate-400')} title={reason}>
        {label}
      </button>
      <span className="text-xs text-slate-500">{reason}</span>
    </span>
  )
}

// ---------- Layout primitives ----------

export function Card({ children, className, padded = true }: { children: ReactNode; className?: string; padded?: boolean }) {
  return <section className={clsx('rounded-2xl border border-slate-200/80 bg-white shadow-card', padded && 'p-5 sm:p-6', className)}>{children}</section>
}

export function CardHeader({ title, description, action }: { title: ReactNode; description?: ReactNode; action?: ReactNode }) {
  return (
    <div className="mb-4 flex flex-wrap items-start justify-between gap-3">
      <div className="min-w-0">
        <h2 className="text-base font-semibold text-navy-900">{title}</h2>
        {description && <p className="mt-0.5 text-sm text-slate-500">{description}</p>}
      </div>
      {action}
    </div>
  )
}

export function PageHeader({ title, description, actions, back }: { title: ReactNode; description?: ReactNode; actions?: ReactNode; back?: ReactNode }) {
  return (
    <header className="mb-6">
      {back && <div className="mb-2 text-sm">{back}</div>}
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div className="min-w-0">
          <h1 className="text-2xl font-semibold tracking-tight text-navy-900">{title}</h1>
          {description && <p className="mt-1 max-w-3xl text-sm text-slate-500">{description}</p>}
        </div>
        {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
      </div>
    </header>
  )
}

export function EmptyState({ icon, title, description, action }: { icon?: ReactNode; title: string; description?: ReactNode; action?: ReactNode }) {
  return (
    <div className="flex flex-col items-center justify-center rounded-2xl border border-dashed border-slate-300 bg-white/60 px-6 py-12 text-center">
      {icon && <div className="mb-3 flex size-12 items-center justify-center rounded-full bg-navy-50 text-navy-600">{icon}</div>}
      <h3 className="text-base font-semibold text-navy-900">{title}</h3>
      {description && <p className="mt-1 max-w-md text-sm text-slate-500">{description}</p>}
      {action && <div className="mt-5">{action}</div>}
    </div>
  )
}

export function Skeleton({ className }: { className?: string }) {
  return <div className={clsx('skeleton', className)} aria-hidden />
}

export function SkeletonRows({ rows = 4 }: { rows?: number }) {
  return (
    <div className="space-y-3" role="status" aria-label="Loading">
      {Array.from({ length: rows }, (_, i) => (
        <Skeleton key={i} className="h-12 w-full" />
      ))}
    </div>
  )
}

export function Spinner({ label = 'Loading' }: { label?: string }) {
  return (
    <div className="flex items-center justify-center gap-2 py-10 text-sm text-slate-500" role="status">
      <Loader2 className="size-5 animate-spin" aria-hidden />
      {label}
    </div>
  )
}

// ---------- Badges and notices ----------

type Tone = 'neutral' | 'blue' | 'green' | 'amber' | 'red' | 'navy' | 'violet'
const toneClass: Record<Tone, string> = {
  neutral: 'bg-slate-100 text-slate-700 ring-slate-200',
  blue: 'bg-accent-50 text-accent-700 ring-accent-200',
  green: 'bg-emerald-50 text-emerald-700 ring-emerald-200',
  amber: 'bg-amber-50 text-amber-800 ring-amber-200',
  red: 'bg-red-50 text-red-700 ring-red-200',
  navy: 'bg-navy-900 text-white ring-navy-900',
  violet: 'bg-violet-50 text-violet-700 ring-violet-200',
}

export function Badge({ tone = 'neutral', children, title, className }: { tone?: Tone; children: ReactNode; title?: string; className?: string }) {
  return (
    <span title={title} className={clsx('inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-xs font-medium ring-1 ring-inset whitespace-nowrap', toneClass[tone], className)}>
      {children}
    </span>
  )
}

const dataStatusInfo: Record<DataStatus, { tone: Tone; title: string }> = {
  LIVE: { tone: 'green', title: 'Live price returned by the supplier API within its cache window.' },
  DEMO: { tone: 'violet', title: 'Simulated demo data. Not a live supplier quote.' },
  STALE: { tone: 'amber', title: 'The supplier could not be refreshed. This price is from an earlier check and may have changed.' },
}

/** Every price in the product carries one of these so demo data is never mistaken for a live quote. */
export function DataStatusBadge({ status }: { status: DataStatus }) {
  const info = dataStatusInfo[status]
  return (
    <Badge tone={info.tone} title={info.title}>
      {status}
    </Badge>
  )
}

const statusTones: Record<string, Tone> = {
  Draft: 'neutral',
  Active: 'blue',
  Ordered: 'violet',
  Completed: 'green',
  Archived: 'neutral',
  'Pending Approval': 'amber',
  Approved: 'blue',
  Sent: 'violet',
  'Partially Received': 'amber',
  Received: 'green',
  Cancelled: 'red',
  Pending: 'amber',
  Rejected: 'red',
}

export function StatusBadge({ status }: { status: string }) {
  return <Badge tone={statusTones[status] ?? 'neutral'}>{status}</Badge>
}

/** Renders a value that may be unknown. Unknown is shown as a word, never as zero. */
export function Unknown({ children = 'Unknown', title }: { children?: ReactNode; title?: string }) {
  return (
    <span className="inline-flex items-center gap-1 text-amber-700" title={title ?? 'The supplier did not provide this, so it is not included in any total.'}>
      <AlertTriangle className="size-3.5" aria-hidden />
      {children}
    </span>
  )
}

const noticeStyles = {
  info: { box: 'border-accent-200 bg-accent-50 text-navy-800', icon: <Info className="size-5 shrink-0 text-accent-600" aria-hidden /> },
  warning: { box: 'border-amber-200 bg-amber-50 text-amber-900', icon: <AlertTriangle className="size-5 shrink-0 text-amber-600" aria-hidden /> },
  error: { box: 'border-red-200 bg-red-50 text-red-800', icon: <XCircle className="size-5 shrink-0 text-red-600" aria-hidden /> },
  success: { box: 'border-emerald-200 bg-emerald-50 text-emerald-900', icon: <CheckCircle2 className="size-5 shrink-0 text-emerald-600" aria-hidden /> },
}

export function Notice({ tone = 'info', title, children, action, className }: { tone?: keyof typeof noticeStyles; title?: ReactNode; children?: ReactNode; action?: ReactNode; className?: string }) {
  const style = noticeStyles[tone]
  return (
    <div role={tone === 'error' ? 'alert' : 'status'} className={clsx('flex items-start gap-3 rounded-xl border px-4 py-3 text-sm', style.box, className)}>
      {style.icon}
      <div className="min-w-0 flex-1">
        {title && <p className="font-semibold">{title}</p>}
        {children && <div className={clsx(title && 'mt-0.5', 'leading-relaxed')}>{children}</div>}
      </div>
      {action}
    </div>
  )
}

export function ErrorState({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  const message = error instanceof Error ? error.message : 'Something went wrong.'
  return (
    <Notice tone="error" title="This could not be loaded" action={onRetry && <Button size="sm" onClick={onRetry}>Try again</Button>}>
      {message}
    </Notice>
  )
}

// ---------- Form controls ----------

const fieldClass =
  'block w-full rounded-lg border border-slate-300 bg-white px-3 text-sm text-navy-900 shadow-sm placeholder:text-slate-400 focus:border-accent-500 focus:outline-none focus:ring-2 focus:ring-accent-500/25 disabled:bg-slate-50 disabled:text-slate-500 aria-[invalid=true]:border-red-400'

type FieldShell = { label?: string; hint?: ReactNode; error?: string | null; className?: string }

function Field({ id, label, hint, error, className, children }: FieldShell & { id: string; children: ReactNode }) {
  return (
    <div className={className}>
      {label && (
        <label htmlFor={id} className="mb-1.5 block text-sm font-medium text-navy-800">
          {label}
        </label>
      )}
      {children}
      {error ? (
        <p id={`${id}-error`} className="mt-1 text-xs text-red-600">
          {error}
        </p>
      ) : (
        hint && <p className="mt-1 text-xs text-slate-500">{hint}</p>
      )}
    </div>
  )
}

export function Input({ label, hint, error, className, id, ...rest }: InputHTMLAttributes<HTMLInputElement> & FieldShell) {
  const generated = useId()
  const fieldId = id ?? generated
  return (
    <Field id={fieldId} label={label} hint={hint} error={error} className={className}>
      <input id={fieldId} aria-invalid={!!error} aria-describedby={error ? `${fieldId}-error` : undefined} className={clsx(fieldClass, 'h-10')} {...rest} />
    </Field>
  )
}

export function Textarea({ label, hint, error, className, id, rows = 4, ...rest }: TextareaHTMLAttributes<HTMLTextAreaElement> & FieldShell) {
  const generated = useId()
  const fieldId = id ?? generated
  return (
    <Field id={fieldId} label={label} hint={hint} error={error} className={className}>
      <textarea id={fieldId} rows={rows} aria-invalid={!!error} className={clsx(fieldClass, 'py-2 leading-relaxed')} {...rest} />
    </Field>
  )
}

export function Select({ label, hint, error, className, id, children, ...rest }: SelectHTMLAttributes<HTMLSelectElement> & FieldShell) {
  const generated = useId()
  const fieldId = id ?? generated
  return (
    <Field id={fieldId} label={label} hint={hint} error={error} className={className}>
      <select id={fieldId} aria-invalid={!!error} className={clsx(fieldClass, 'h-10 pr-8')} {...rest}>
        {children}
      </select>
    </Field>
  )
}

export function Checkbox({ label, description, className, id, ...rest }: InputHTMLAttributes<HTMLInputElement> & { label: ReactNode; description?: ReactNode }) {
  const generated = useId()
  const fieldId = id ?? generated
  return (
    <div className={clsx('flex items-start gap-3', className)}>
      <input id={fieldId} type="checkbox" className="mt-0.5 size-4 rounded border-slate-300 text-accent-600 accent-accent-600" {...rest} />
      <label htmlFor={fieldId} className="text-sm text-navy-800">
        <span className="font-medium">{label}</span>
        {description && <span className="mt-0.5 block text-slate-500">{description}</span>}
      </label>
    </div>
  )
}

// ---------- Modal ----------

export function Modal({ open, onClose, title, description, children, footer, wide }: { open: boolean; onClose: () => void; title: string; description?: ReactNode; children: ReactNode; footer?: ReactNode; wide?: boolean }) {
  const ref = useRef<HTMLDialogElement>(null)
  useEffect(() => {
    const dialog = ref.current
    if (!dialog) return
    if (open && !dialog.open) dialog.showModal()
    if (!open && dialog.open) dialog.close()
  }, [open])

  return (
    <dialog
      ref={ref}
      onClose={onClose}
      onClick={(event) => event.target === ref.current && onClose()}
      className={clsx('m-auto w-[calc(100%-2rem)] rounded-2xl border border-slate-200 bg-white p-0 shadow-pop backdrop:bg-navy-950/50 backdrop:backdrop-blur-[2px]', wide ? 'max-w-3xl' : 'max-w-lg')}
    >
      {open && (
        <div className="flex max-h-[85vh] flex-col">
          <div className="flex items-start justify-between gap-4 border-b border-slate-200 px-6 py-4">
            <div>
              <h2 className="text-lg font-semibold text-navy-900">{title}</h2>
              {description && <p className="mt-0.5 text-sm text-slate-500">{description}</p>}
            </div>
            <button type="button" onClick={onClose} aria-label="Close" className="rounded-lg p-1.5 text-slate-500 hover:bg-slate-100">
              <X className="size-5" />
            </button>
          </div>
          <div className="overflow-y-auto px-6 py-5">{children}</div>
          {footer && <div className="flex flex-wrap justify-end gap-2 border-t border-slate-200 bg-slate-50/70 px-6 py-4">{footer}</div>}
        </div>
      )}
    </dialog>
  )
}

export function ConfirmDialog({ open, onClose, onConfirm, title, children, confirmLabel = 'Confirm', danger, loading }: { open: boolean; onClose: () => void; onConfirm: () => void; title: string; children: ReactNode; confirmLabel?: string; danger?: boolean; loading?: boolean }) {
  return (
    <Modal
      open={open}
      onClose={onClose}
      title={title}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant={danger ? 'danger' : 'primary'} onClick={onConfirm} loading={loading}>
            {confirmLabel}
          </Button>
        </>
      }
    >
      <div className="text-sm leading-relaxed text-slate-600">{children}</div>
    </Modal>
  )
}

// ---------- Tabs ----------

export function Tabs<T extends string>({ tabs, value, onChange }: { tabs: { id: T; label: string; count?: number | null }[]; value: T; onChange: (id: T) => void }) {
  return (
    <div className="-mx-1 mb-6 overflow-x-auto border-b border-slate-200" role="tablist">
      <div className="flex min-w-max gap-1 px-1">
        {tabs.map((tab) => (
          <button
            key={tab.id}
            type="button"
            role="tab"
            aria-selected={value === tab.id}
            onClick={() => onChange(tab.id)}
            className={clsx(
              '-mb-px flex items-center gap-2 border-b-2 px-3.5 py-2.5 text-sm font-medium transition-colors',
              value === tab.id ? 'border-accent-600 text-accent-700' : 'border-transparent text-slate-500 hover:border-slate-300 hover:text-navy-800',
            )}
          >
            {tab.label}
            {tab.count !== undefined && tab.count !== null && tab.count > 0 && (
              <span className={clsx('rounded-full px-1.5 text-xs tabular', value === tab.id ? 'bg-accent-100 text-accent-700' : 'bg-slate-100 text-slate-600')}>{tab.count}</span>
            )}
          </button>
        ))}
      </div>
    </div>
  )
}

// ---------- Tables ----------

export function Table({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <div className={clsx('overflow-x-auto', className)}>
      <table className="w-full min-w-max border-collapse text-left text-sm">{children}</table>
    </div>
  )
}

export function Th({ children, className, align }: { children?: ReactNode; className?: string; align?: 'right' | 'center' }) {
  return (
    <th scope="col" className={clsx('border-b border-slate-200 bg-slate-50/80 px-2.5 py-2.5 text-xs font-semibold uppercase tracking-wide text-slate-500 first:pl-5 last:pr-5', align === 'right' && 'text-right', align === 'center' && 'text-center', className)}>
      {children}
    </th>
  )
}

export function Td({ children, className, align, colSpan }: { children?: ReactNode; className?: string; align?: 'right' | 'center'; colSpan?: number }) {
  return (
    <td colSpan={colSpan} className={clsx('border-b border-slate-100 px-2.5 py-3 align-top first:pl-5 last:pr-5', align === 'right' && 'text-right tabular', align === 'center' && 'text-center', className)}>
      {children}
    </td>
  )
}

// ---------- Toasts ----------

type Toast = { id: number; tone: 'success' | 'error' | 'info'; message: string }
const ToastContext = createContext<(tone: Toast['tone'], message: string) => void>(() => undefined)

export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([])
  const nextId = useRef(1)
  const push = useCallback((tone: Toast['tone'], message: string) => {
    const id = nextId.current++
    setToasts((current) => [...current.slice(-3), { id, tone, message }])
    window.setTimeout(() => setToasts((current) => current.filter((t) => t.id !== id)), tone === 'error' ? 7000 : 4000)
  }, [])

  return (
    <ToastContext.Provider value={push}>
      {children}
      <div className="pointer-events-none fixed inset-x-0 bottom-4 z-50 flex flex-col items-center gap-2 px-4" aria-live="polite">
        {toasts.map((toast) => (
          <div
            key={toast.id}
            className={clsx(
              'pointer-events-auto flex max-w-md items-start gap-2.5 rounded-xl px-4 py-3 text-sm font-medium shadow-pop',
              toast.tone === 'error' ? 'bg-red-700 text-white' : 'bg-navy-900 text-white',
            )}
          >
            {toast.tone === 'error' ? <XCircle className="mt-0.5 size-4 shrink-0" aria-hidden /> : <CheckCircle2 className="mt-0.5 size-4 shrink-0 text-emerald-300" aria-hidden />}
            {toast.message}
          </div>
        ))}
      </div>
    </ToastContext.Provider>
  )
}

export function useToast() {
  const push = useContext(ToastContext)
  return {
    success: (message: string) => push('success', message),
    error: (message: string) => push('error', message),
    info: (message: string) => push('info', message),
  }
}

// ---------- Small helpers ----------

export function Stat({ label, value, hint, icon, tone = 'navy' }: { label: string; value: ReactNode; hint?: ReactNode; icon?: ReactNode; tone?: 'navy' | 'blue' | 'green' | 'amber' }) {
  const tones = { navy: 'bg-navy-50 text-navy-700', blue: 'bg-accent-50 text-accent-600', green: 'bg-emerald-50 text-emerald-600', amber: 'bg-amber-50 text-amber-600' }
  return (
    <Card className="flex items-start gap-4">
      {icon && <div className={clsx('flex size-10 shrink-0 items-center justify-center rounded-xl', tones[tone])}>{icon}</div>}
      <div className="min-w-0">
        <p className="text-sm text-slate-500">{label}</p>
        <p className="mt-0.5 text-2xl font-semibold tracking-tight text-navy-900 tabular">{value}</p>
        {hint && <p className="mt-0.5 text-xs text-slate-500">{hint}</p>}
      </div>
    </Card>
  )
}

export function DefinitionList({ items }: { items: { label: string; value: ReactNode }[] }) {
  return (
    <dl className="grid grid-cols-1 gap-x-6 gap-y-3 text-sm sm:grid-cols-2">
      {items.map((item) => (
        <div key={item.label}>
          <dt className="text-xs font-medium uppercase tracking-wide text-slate-500">{item.label}</dt>
          <dd className="mt-0.5 text-navy-900">{item.value}</dd>
        </div>
      ))}
    </dl>
  )
}

/** Shown wherever a plan does not include a feature. Explains instead of hiding. */
export function UpgradeNotice({ feature, reason, canManageBilling }: { feature: string; reason?: string; canManageBilling: boolean }) {
  return (
    <Notice
      tone="info"
      title={`${feature} is not included in your plan`}
      action={canManageBilling ? <LinkButton to="/app/billing" size="sm" variant="accent">View plans</LinkButton> : undefined}
    >
      {reason ?? 'Upgrade to unlock this feature.'}
      {!canManageBilling && ' Ask your organization owner to change the plan.'}
    </Notice>
  )
}
