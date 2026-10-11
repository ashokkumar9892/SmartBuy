// Base URL of the SmartBuy API. Empty means "same origin" (Vite dev proxy, or a Netlify /api proxy rule).
const API_BASE = (import.meta.env.VITE_API_BASE_URL ?? '').replace(/\/$/, '')

const ACCESS_KEY = 'smartbuy.access'
const REFRESH_KEY = 'smartbuy.refresh'

export class ApiError extends Error {
  status: number
  code: string
  constructor(status: number, message: string, code: string) {
    super(message)
    this.status = status
    this.code = code
  }
}

type Tokens = { accessToken: string; refreshToken: string }
type AuthListener = () => void

let accessToken: string | null = sessionStorage.getItem(ACCESS_KEY)
let refreshInFlight: Promise<boolean> | null = null
const listeners = new Set<AuthListener>()

export const tokens = {
  has: () => !!localStorage.getItem(REFRESH_KEY),
  refresh: () => localStorage.getItem(REFRESH_KEY),
  set(next: Tokens) {
    accessToken = next.accessToken
    sessionStorage.setItem(ACCESS_KEY, next.accessToken)
    localStorage.setItem(REFRESH_KEY, next.refreshToken)
  },
  clear() {
    accessToken = null
    sessionStorage.removeItem(ACCESS_KEY)
    localStorage.removeItem(REFRESH_KEY)
  },
  /** Called when the session can no longer be refreshed. */
  onExpired(listener: AuthListener) {
    listeners.add(listener)
    return () => listeners.delete(listener)
  },
}

async function parseError(response: Response): Promise<ApiError> {
  let message = `Request failed (${response.status})`
  let code = 'error'
  try {
    const body = await response.json()
    if (typeof body?.title === 'string') message = body.title
    if (typeof body?.code === 'string') code = body.code
  } catch {
    // Non-JSON error body: keep the generic message.
  }
  if (response.status === 429) message = 'Too many requests. Wait a minute and try again.'
  return new ApiError(response.status, message, code)
}

async function tryRefresh(): Promise<boolean> {
  const refreshToken = tokens.refresh()
  if (!refreshToken) return false
  // Concurrent 401s share one refresh call: refresh tokens are single-use, so a second call would end the session.
  refreshInFlight ??= (async () => {
    try {
      const response = await fetch(`${API_BASE}/api/auth/refresh`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshToken }),
      })
      if (!response.ok) return false
      tokens.set(await response.json())
      return true
    } catch {
      return false
    } finally {
      refreshInFlight = null
    }
  })()
  return refreshInFlight
}

type Options = { method?: string; body?: unknown; form?: FormData; anonymous?: boolean }

async function send(path: string, options: Options): Promise<Response> {
  const headers: Record<string, string> = {}
  if (options.body !== undefined) headers['Content-Type'] = 'application/json'
  if (accessToken && !options.anonymous) headers.Authorization = `Bearer ${accessToken}`
  return fetch(`${API_BASE}${path}`, {
    method: options.method ?? 'GET',
    headers,
    body: options.form ?? (options.body !== undefined ? JSON.stringify(options.body) : undefined),
  })
}

async function request(path: string, options: Options = {}): Promise<Response> {
  let response: Response
  try {
    if (!accessToken && !options.anonymous && tokens.has()) await tryRefresh()
    response = await send(path, options)
    if (response.status === 401 && !options.anonymous && tokens.has()) {
      if (await tryRefresh()) response = await send(path, options)
      else {
        tokens.clear()
        listeners.forEach((listener) => listener())
      }
    }
  } catch {
    throw new ApiError(0, 'Cannot reach the SmartBuy server. Check your connection and try again.', 'network')
  }
  if (!response.ok) throw await parseError(response)
  return response
}

async function json<T>(path: string, options?: Options): Promise<T> {
  const response = await request(path, options)
  if (response.status === 204) return undefined as T
  const text = await response.text()
  return (text ? JSON.parse(text) : undefined) as T
}

export const api = {
  get: <T>(path: string) => json<T>(path),
  post: <T>(path: string, body: unknown = {}) => json<T>(path, { method: 'POST', body }),
  put: <T>(path: string, body: unknown) => json<T>(path, { method: 'PUT', body }),
  delete: <T = void>(path: string) => json<T>(path, { method: 'DELETE' }),
  upload: <T>(path: string, file: File) => {
    const form = new FormData()
    form.append('file', file)
    return json<T>(path, { method: 'POST', form })
  },
  anonymous: {
    get: <T>(path: string) => json<T>(path, { anonymous: true }),
    post: <T>(path: string, body: unknown = {}) => json<T>(path, { method: 'POST', body, anonymous: true }),
  },
  /** Fetches a protected file and hands it to the browser as a download. */
  async download(path: string, fallbackName: string) {
    const response = await request(path)
    const disposition = response.headers.get('Content-Disposition') ?? ''
    const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition)
    const url = URL.createObjectURL(await response.blob())
    const link = document.createElement('a')
    link.href = url
    link.download = match ? decodeURIComponent(match[1]) : fallbackName
    document.body.appendChild(link)
    link.click()
    link.remove()
    URL.revokeObjectURL(url)
  },
}

export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) return error.message
  if (error instanceof Error) return error.message
  return 'Something went wrong.'
}
