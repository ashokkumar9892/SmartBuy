const currency = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' })
const preciseCurrency = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', minimumFractionDigits: 2, maximumFractionDigits: 4 })
const number = new Intl.NumberFormat('en-US', { maximumFractionDigits: 2 })
const dateFormat = new Intl.DateTimeFormat('en-US', { month: 'short', day: 'numeric', year: 'numeric' })
const dateTimeFormat = new Intl.DateTimeFormat('en-US', { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' })

/** Null and undefined are shown as "Unknown", never as $0.00. */
export const money = (value: number | null | undefined, unknown = 'Unknown') => (value === null || value === undefined ? unknown : currency.format(value))

export const unitMoney = (value: number | null | undefined) => (value === null || value === undefined ? '—' : preciseCurrency.format(value))

export const qty = (value: number | null | undefined) => (value === null || value === undefined ? '—' : number.format(value))

export const planPrice = (cents: number) => (cents === 0 ? '$0' : `$${(cents / 100).toFixed(0)}`)

export const formatDate = (value: string | null | undefined) => (value ? dateFormat.format(new Date(value)) : '—')

export const formatDateTime = (value: string | null | undefined) => (value ? dateTimeFormat.format(new Date(value)) : '—')

export function timeAgo(value: string | null | undefined) {
  if (!value) return '—'
  const seconds = Math.max(0, Math.round((Date.now() - new Date(value).getTime()) / 1000))
  if (seconds < 60) return 'just now'
  const minutes = Math.round(seconds / 60)
  if (minutes < 60) return `${minutes} min ago`
  const hours = Math.round(minutes / 60)
  if (hours < 24) return `${hours} hr ago`
  const days = Math.round(hours / 24)
  return days < 30 ? `${days} day${days === 1 ? '' : 's'} ago` : formatDate(value)
}

export function deliveryLabel(min: number | null, max: number | null) {
  if (min === null && max === null) return 'Unknown'
  const low = min ?? max!
  const high = max ?? min!
  if (high === 0) return 'Today'
  return low === high ? `${high} day${high === 1 ? '' : 's'}` : `${low}–${high} days`
}

export const UNIT_LABELS: Record<string, string> = { each: 'each', ft: 'ft', in: 'in', gal: 'gal', qt: 'qt', lb: 'lb', oz: 'oz', sqft: 'sq ft', sqyd: 'sq yd', cuft: 'cu ft', cuyd: 'cu yd', floz: 'fl oz', yd: 'yd' }

export const unitLabel = (code: string) => UNIT_LABELS[code] ?? code

export function packLabel(quantity: number, unit: string) {
  if (unit === 'each') return quantity === 1 ? 'Single' : `${qty(quantity)}-pack`
  return `${qty(quantity)} ${unitLabel(unit)}`
}

/** Date input value (yyyy-mm-dd) from an ISO string. */
export const toDateInput = (value: string | null | undefined) => (value ? value.slice(0, 10) : '')
