// FindMyPrice API for the Netlify deployment.
//
// Netlify cannot run the ASP.NET Core API, so this function provides what the public site needs on its own:
// product search, accounts (email/password, Google, Facebook) and saved products. Data is kept in Netlify Blobs.
// The business workspace (projects, purchase orders, admin) is only available from the full API; when you host it,
// set VITE_API_BASE_URL and the site talks to that instead of this function.
//
// Optional environment variables (Netlify > Site configuration > Environment variables):
//   JWT_SECRET            signing secret for sessions (one is generated and stored if unset)
//   GOOGLE_CLIENT_ID      enables Google sign-in
//   FACEBOOK_APP_ID, FACEBOOK_APP_SECRET, FACEBOOK_GRAPH_VERSION   enable Facebook sign-in
//   EBAY_CLIENT_ID + EBAY_CLIENT_SECRET, BESTBUY_API_KEY, WALMART_CONSUMER_ID + WALMART_PRIVATE_KEY
//                         each switches search from the demo catalog to that store's live listings and prices
import { getStore } from '@netlify/blobs'
import { createHash, createHmac, createSign, randomBytes, randomUUID, scryptSync, timingSafeEqual } from 'node:crypto'
import demo from './demo-data.json'

export const config = { path: '/api/*' }

type User = {
  id: string
  email: string
  fullName: string
  passwordHash: string | null
  googleSubject?: string
  facebookSubject?: string
  createdAt: string
  failedLogins: number
  lockedUntil: number
}

const ACCESS_MINUTES = 30
const REFRESH_DAYS = 14
const store = () => getStore({ name: 'findmyprice', consistency: 'strong' })

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' } })
const problem = (status: number, title: string, code = 'error') => json({ status, title, code }, status)

class HttpError extends Error {
  status: number
  code: string
  constructor(status: number, message: string, code = 'error') {
    super(message)
    this.status = status
    this.code = code
  }
}

// ---------- Tokens and passwords ----------

const b64url = (input: Buffer | string) => Buffer.from(input).toString('base64url')
const sha256 = (text: string) => createHash('sha256').update(text).digest('hex')

async function jwtSecret(): Promise<string> {
  const fromEnv = Netlify.env.get('JWT_SECRET')
  if (fromEnv && fromEnv.length >= 32) return fromEnv
  const blobs = store()
  const existing = await blobs.get('config/jwt-secret')
  if (existing) return existing
  const created = randomBytes(48).toString('base64url')
  await blobs.set('config/jwt-secret', created)
  return created
}

async function signAccessToken(userId: string, email: string) {
  const expires = Math.floor(Date.now() / 1000) + ACCESS_MINUTES * 60
  const head = b64url(JSON.stringify({ alg: 'HS256', typ: 'JWT' }))
  const body = b64url(JSON.stringify({ sub: userId, email, exp: expires }))
  const signature = createHmac('sha256', await jwtSecret()).update(`${head}.${body}`).digest('base64url')
  return { token: `${head}.${body}.${signature}`, expiresAt: new Date(expires * 1000).toISOString() }
}

async function verifyAccessToken(request: Request): Promise<{ sub: string; email: string } | null> {
  const header = request.headers.get('authorization') ?? ''
  const [head, body, signature] = header.replace(/^Bearer\s+/i, '').split('.')
  if (!head || !body || !signature) return null
  const expected = createHmac('sha256', await jwtSecret()).update(`${head}.${body}`).digest()
  const given = Buffer.from(signature, 'base64url')
  if (given.length !== expected.length || !timingSafeEqual(given, expected)) return null
  try {
    const claims = JSON.parse(Buffer.from(body, 'base64url').toString())
    return typeof claims.exp === 'number' && claims.exp > Date.now() / 1000 ? claims : null
  } catch {
    return null
  }
}

function hashPassword(password: string) {
  const salt = randomBytes(16)
  return `scrypt$${salt.toString('base64')}$${scryptSync(password, salt, 64).toString('base64')}`
}

function verifyPassword(password: string, stored: string | null) {
  const [scheme, salt, hash] = (stored ?? '').split('$')
  // Always do the work, so a missing account takes as long as a wrong password.
  const expected = scheme === 'scrypt' && hash ? Buffer.from(hash, 'base64') : Buffer.alloc(64)
  const actual = scryptSync(password, salt ? Buffer.from(salt, 'base64') : Buffer.alloc(16), 64)
  return timingSafeEqual(actual, expected) && scheme === 'scrypt'
}

// ---------- Accounts ----------

const userKey = (email: string) => `users/${sha256(email.trim().toLowerCase())}`
const getUser = async (email: string) => (await store().get(userKey(email), { type: 'json' })) as User | null
const saveUser = (user: User) => store().setJSON(userKey(user.email), user)

const plan = {
  code: 'free', name: 'Free', description: 'Search and compare prices.', monthlyPriceCents: 0, maxProjectsPerMonth: 0, maxMembers: 1,
  aiMaterialLists: false, advancedOptimization: false, priceAlerts: false, purchaseOrders: false, advancedReporting: false, approvalWorkflows: false,
}

function sessionFor(user: User) {
  const organization = { id: user.id, name: `${user.fullName.split(' ')[0]}'s workspace`, slug: user.id, industry: 'General', isDemo: false, role: 'Owner' }
  return {
    user: { id: user.id, email: user.email, fullName: user.fullName, isPlatformAdmin: false, isDemo: false },
    organization,
    organizations: [organization],
    plan,
    permissions: { canEditLists: false, canManagePurchasing: false, canApprovePurchaseOrders: false, canManageOrganization: false, canManageBilling: false },
    aiProvider: 'none',
  }
}

async function issue(user: User) {
  const access = await signAccessToken(user.id, user.email)
  const refreshToken = randomBytes(48).toString('base64url')
  await store().setJSON(`refresh/${sha256(refreshToken)}`, { email: user.email, expires: Date.now() + REFRESH_DAYS * 86_400_000 })
  return json({ accessToken: access.token, expiresAt: access.expiresAt, refreshToken, session: sessionFor(user) })
}

function validEmail(value: unknown): value is string {
  return typeof value === 'string' && value.length <= 320 && /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value)
}

async function register(body: Record<string, unknown>) {
  const { email, password, fullName } = body
  if (!validEmail(email)) throw new HttpError(400, 'Enter a valid email address.', 'validation')
  if (typeof fullName !== 'string' || !fullName.trim() || fullName.length > 200) throw new HttpError(400, 'Enter your name.', 'validation')
  if (typeof password !== 'string' || password.length < 10 || password.length > 200) throw new HttpError(400, 'Use a password of at least 10 characters.', 'validation')
  if (!/[a-z]/i.test(password) || !/[^a-z]/i.test(password)) throw new HttpError(400, 'Use a password that mixes letters with numbers or symbols.', 'validation')
  if (await getUser(email)) throw new HttpError(409, 'An account with this email already exists. Sign in instead.', 'email_in_use')
  const user: User = { id: randomUUID(), email: email.trim(), fullName: fullName.trim(), passwordHash: hashPassword(password), createdAt: new Date().toISOString(), failedLogins: 0, lockedUntil: 0 }
  await saveUser(user)
  return issue(user)
}

async function login(body: Record<string, unknown>) {
  const { email, password } = body
  if (typeof email !== 'string' || typeof password !== 'string') throw new HttpError(400, 'Enter your email and password.', 'validation')
  const user = await getUser(email)
  if (user && user.lockedUntil > Date.now()) throw new HttpError(423, 'Too many failed sign-in attempts. Try again in a few minutes.')
  if (!verifyPassword(password, user?.passwordHash ?? null) || !user) {
    if (user) {
      user.failedLogins += 1
      if (user.failedLogins >= 5) {
        user.failedLogins = 0
        user.lockedUntil = Date.now() + 15 * 60_000
      }
      await saveUser(user)
    }
    throw new HttpError(401, 'The email or password is incorrect.')
  }
  if (user.failedLogins || user.lockedUntil) {
    user.failedLogins = 0
    user.lockedUntil = 0
    await saveUser(user)
  }
  return issue(user)
}

async function refresh(body: Record<string, unknown>) {
  const expired = new HttpError(401, 'Your session has expired. Sign in again.')
  if (typeof body.refreshToken !== 'string') throw expired
  const key = `refresh/${sha256(body.refreshToken)}`
  const record = (await store().get(key, { type: 'json' })) as { email: string; expires: number } | null
  if (!record) throw expired
  // Refresh tokens are single use.
  await store().delete(key)
  const user = record.expires > Date.now() ? await getUser(record.email) : null
  if (!user) throw expired
  return issue(user)
}

/** Signs in, or creates, the account behind an identity the provider has already verified. */
async function externalSignIn(provider: 'google' | 'facebook', subject: string, email: string, name: string | undefined) {
  let user = await getUser(email)
  if (!user) user = { id: randomUUID(), email: email.trim(), fullName: (name || email).trim(), passwordHash: null, createdAt: new Date().toISOString(), failedLogins: 0, lockedUntil: 0 }
  if (provider === 'google') user.googleSubject ??= subject
  else user.facebookSubject ??= subject
  await saveUser(user)
  return issue(user)
}

async function googleSignIn(body: Record<string, unknown>) {
  const clientId = Netlify.env.get('GOOGLE_CLIENT_ID')
  if (!clientId) throw new HttpError(501, 'Google sign-in is not configured on this server.')
  if (typeof body.idToken !== 'string') throw new HttpError(400, 'Missing Google token.', 'validation')
  // Google validates the signature and expiry; the audience check ensures the token was issued for this site.
  const response = await fetch(`https://oauth2.googleapis.com/tokeninfo?id_token=${encodeURIComponent(body.idToken)}`)
  const info = (await response.json().catch(() => ({}))) as Record<string, string>
  if (!response.ok || info.aud !== clientId || !info.sub || !validEmail(info.email)) throw new HttpError(401, 'Google sign-in could not be verified.')
  if (String(info.email_verified) !== 'true') throw new HttpError(401, 'Your Google email address is not verified.')
  return externalSignIn('google', info.sub, info.email, info.name)
}

async function facebookSignIn(body: Record<string, unknown>) {
  const appId = Netlify.env.get('FACEBOOK_APP_ID')
  const secret = Netlify.env.get('FACEBOOK_APP_SECRET')
  if (!appId || !secret) throw new HttpError(501, 'Facebook sign-in is not configured on this server.')
  if (typeof body.accessToken !== 'string') throw new HttpError(400, 'Missing Facebook token.', 'validation')
  const token = encodeURIComponent(body.accessToken)
  const debug = (await (await fetch(`https://graph.facebook.com/debug_token?input_token=${token}&access_token=${encodeURIComponent(`${appId}|${secret}`)}`)).json().catch(() => ({}))) as { data?: { is_valid?: boolean; app_id?: string } }
  // A token issued for some other Facebook app must never sign anyone in here.
  if (!debug.data?.is_valid || debug.data.app_id !== appId) throw new HttpError(401, 'Facebook sign-in could not be verified.')
  const me = (await (await fetch(`https://graph.facebook.com/me?fields=id,name,email&access_token=${token}`)).json().catch(() => ({}))) as { id?: string; name?: string; email?: string }
  if (!me.id) throw new HttpError(401, 'Facebook sign-in could not be verified.')
  if (!validEmail(me.email)) throw new HttpError(400, 'Facebook did not share an email address. Allow email access, or sign up with your email instead.')
  return externalSignIn('facebook', me.id, me.email, me.name)
}

// ---------- Search ----------

const stopWords = new Set(['the', 'a', 'an', 'of', 'for', 'with', 'and', 'in', 'x', 'by', 'pack', 'each'])

function tokens(text: string): string[] {
  const normalized = text
    .toLowerCase()
    .replace(/"/g, ' in ')
    .replace(/(\d+)\s*\/\s*(\d+)/g, (match, a, b) => (Number(b) ? String(Number((Number(a) / Number(b)).toFixed(4))) : match))
    .replace(/inches|inch/g, 'in')
    .replace(/feet|foot/g, 'ft')
    .replace(/[^a-z0-9.]+/g, ' ')
  const result = new Set<string>()
  for (let token of normalized.split(' ')) {
    token = token.replace(/^\.+|\.+$/g, '')
    if (!token || stopWords.has(token)) continue
    if (token.length > 3 && token.endsWith('s') && !token.endsWith('ss') && !/^\d/.test(token)) token = token.slice(0, -1)
    result.add(token)
  }
  return [...result]
}

const productIndex = demo.products.map((product) => ({
  product,
  nameTokens: new Set(tokens(product.name)),
  allTokens: new Set(tokens(`${product.name} ${product.category} ${product.keywords.join(' ')} ${product.brand ?? ''}`)),
}))
const supplierByCode = new Map(demo.suppliers.map((s) => [s.code, s]))

function search(query: string) {
  const clean = query.replace(/[\u0000-\u001f]/g, ' ').trim().slice(0, 120)
  const queryTokens = tokens(clean)
  if (clean.length < 2 || queryTokens.length === 0) return { query: clean, demoData: false, results: [], suggestions: demo.suggestions }

  const ranked = productIndex
    .map((entry) => {
      const words = queryTokens.filter((t) => !/^[\d.]/.test(t))
      if (words.length > 0 && !words.some((t) => entry.allTokens.has(t))) return { entry, score: 0 }
      const recall = queryTokens.filter((t) => entry.allTokens.has(t)).length / queryTokens.length
      const precision = [...entry.nameTokens].filter((t) => queryTokens.includes(t)).length / Math.max(1, entry.nameTokens.size)
      return { entry, score: 0.65 * recall + 0.35 * precision }
    })
    .filter((r) => r.score >= 0.35)
    .sort((a, b) => b.score - a.score || a.entry.product.name.localeCompare(b.entry.product.name))
    .slice(0, 6)

  const results = ranked
    .map(({ entry }) => {
      const offers = demo.listings
        // Marketplace listings with only a title cannot be matched with confidence, so they are not priced.
        // Shoppers come here to click through and buy, so offers with no supplier link are left out as well.
        .filter((l) => l.productKey === entry.product.key && l.supplier !== 'ebay' && l.buyUrl)
        .map((l) => {
          const supplier = supplierByCode.get(l.supplier)!
          return {
            supplier: supplier.name, title: l.title, packQuantity: l.packQuantity, packUnit: l.packUnit, price: l.price, unitPrice: l.unitPrice,
            stockStatus: l.stockStatus, deliveryDaysMin: l.deliveryDaysMin, deliveryDaysMax: l.deliveryDaysMax,
            shippingFlatFee: supplier.shippingFlatFee, freeShippingThreshold: supplier.freeShippingThreshold, dataStatus: 'DEMO', buyUrl: l.buyUrl, buyNote: l.buyNote,
          }
        })
        .sort((a, b) => Number(a.stockStatus === 'out_of_stock') - Number(b.stockStatus === 'out_of_stock') || (a.unitPrice ?? Infinity) - (b.unitPrice ?? Infinity))
      return { productId: entry.product.id, name: entry.product.name, category: entry.product.category, unit: entry.product.unit, isSafetyCritical: entry.product.safetyCritical, offers }
    })
    .filter((r) => r.offers.length > 0)
  return { query: clean, demoData: results.length > 0, results, suggestions: demo.suggestions }
}

// ---------- Live prices (official store APIs) ----------
//
// Real listings and prices come only from each store's official API, and a store is searched only when its keys are
// set. Nothing is scraped. Stores are asked in parallel with a short timeout, so one slow store cannot hold up a search.
//   EBAY_CLIENT_ID, EBAY_CLIENT_SECRET     eBay Buy Browse API (free eBay developer account)
//   EBAY_MARKETPLACE                       marketplace to search, default EBAY_US
//   EBAY_CAMPAIGN_ID                       optional eBay Partner Network campaign ID; links then are affiliate links and say so
//   BESTBUY_API_KEY                        Best Buy Products API
//   WALMART_CONSUMER_ID, WALMART_PRIVATE_KEY, WALMART_KEY_VERSION   Walmart affiliate product API (key version defaults to 1)

const LIVE_TIMEOUT_MS = 4000
const LIVE_CACHE_MS = 5 * 60_000
const LIVE_PER_STORE = 12

type LiveOffer = {
  supplier: string; title: string; packQuantity: number; packUnit: string; price: number; unitPrice: number; stockStatus: string
  deliveryDaysMin: null; deliveryDaysMax: null; shippingFlatFee: number | null; freeShippingThreshold: null; dataStatus: 'LIVE'
  buyUrl: string; buyNote: string | null; seller: string | null; condition: string | null; imageUrl: string | null; isAffiliate: boolean
}
type LiveStore = { name: string; configured: () => boolean; search: (query: string) => Promise<LiveOffer[]> }

/** Only HTTPS links on the store's own domain are ever handed to the browser. */
function safeUrl(value: string | undefined, domain: string): string | null {
  try {
    const url = new URL(value ?? '')
    return url.protocol === 'https:' && (url.hostname === domain || url.hostname.endsWith(`.${domain}`)) ? url.toString() : null
  } catch {
    return null
  }
}

/** Builds one offer, or null when the store's answer lacks a usable title, price or link. */
function liveOffer(supplier: string, fields: { title?: string; price: unknown; buyUrl: string | null; imageUrl?: string | null; shipping?: number | null; inStock?: boolean; seller?: string | null; condition?: string | null; affiliateNote?: string | null }): LiveOffer | null {
  const price = Number(fields.price)
  if (!fields.title || !Number.isFinite(price) || price <= 0 || !fields.buyUrl) return null
  const shipping = fields.shipping
  return {
    supplier,
    // Listing titles are written by sellers: shown as plain text only, never interpreted.
    title: fields.title.replace(/[\u0000-\u001f]/g, ' ').slice(0, 200),
    packQuantity: 1, packUnit: 'each', price, unitPrice: price, stockStatus: fields.inStock === false ? 'out_of_stock' : 'in_stock',
    deliveryDaysMin: null, deliveryDaysMax: null,
    shippingFlatFee: typeof shipping === 'number' && Number.isFinite(shipping) && shipping >= 0 ? shipping : null, freeShippingThreshold: null,
    dataStatus: 'LIVE', buyUrl: fields.buyUrl, buyNote: fields.affiliateNote ?? null,
    seller: fields.seller ?? null, condition: fields.condition ?? null, imageUrl: fields.imageUrl ?? null, isAffiliate: !!fields.affiliateNote,
  }
}

async function storeJson<T>(store: string, url: string, headers: Record<string, string> = {}): Promise<T> {
  const response = await fetch(url, { headers, signal: AbortSignal.timeout(LIVE_TIMEOUT_MS) })
  if (!response.ok) throw new Error(`${store} search failed with HTTP ${response.status}`)
  return (await response.json()) as T
}

let ebayToken: { value: string; expires: number } | null = null

async function ebayAccessToken(): Promise<string> {
  if (ebayToken && ebayToken.expires > Date.now() + 60_000) return ebayToken.value
  const basic = Buffer.from(`${Netlify.env.get('EBAY_CLIENT_ID')}:${Netlify.env.get('EBAY_CLIENT_SECRET')}`).toString('base64')
  const response = await fetch('https://api.ebay.com/identity/v1/oauth2/token', {
    method: 'POST',
    headers: { Authorization: `Basic ${basic}`, 'Content-Type': 'application/x-www-form-urlencoded' },
    body: 'grant_type=client_credentials&scope=' + encodeURIComponent('https://api.ebay.com/oauth/api_scope'),
    signal: AbortSignal.timeout(LIVE_TIMEOUT_MS),
  })
  if (!response.ok) throw new Error(`eBay token request failed with HTTP ${response.status}`)
  const body = (await response.json()) as { access_token: string; expires_in: number }
  ebayToken = { value: body.access_token, expires: Date.now() + body.expires_in * 1000 }
  return ebayToken.value
}

type EbayItem = {
  title?: string
  price?: { value?: string; currency?: string }
  itemWebUrl?: string
  itemAffiliateWebUrl?: string
  condition?: string
  image?: { imageUrl?: string }
  seller?: { username?: string }
  shippingOptions?: { shippingCost?: { value?: string } }[]
}

const ebay: LiveStore = {
  name: 'eBay',
  configured: () => !!Netlify.env.get('EBAY_CLIENT_ID') && !!Netlify.env.get('EBAY_CLIENT_SECRET'),
  async search(query) {
    const campaign = Netlify.env.get('EBAY_CAMPAIGN_ID')
    const headers: Record<string, string> = {
      Authorization: `Bearer ${await ebayAccessToken()}`,
      'X-EBAY-C-MARKETPLACE-ID': Netlify.env.get('EBAY_MARKETPLACE') ?? 'EBAY_US',
    }
    if (campaign) headers['X-EBAY-C-ENDUSERCTX'] = `affiliateCampaignId=${campaign}`
    const url = `https://api.ebay.com/buy/browse/v1/item_summary/search?q=${encodeURIComponent(query)}&limit=${LIVE_PER_STORE}&filter=${encodeURIComponent('buyingOptions:{FIXED_PRICE},conditions:{NEW}')}`
    const body = await storeJson<{ itemSummaries?: EbayItem[] }>('eBay', url, headers)
    return (body.itemSummaries ?? []).flatMap((item) => {
      if (item.price?.currency !== 'USD') return []
      const affiliate = campaign ? safeUrl(item.itemAffiliateWebUrl, 'ebay.com') : null
      const shipping = item.shippingOptions?.[0]?.shippingCost?.value
      return liveOffer('eBay', {
        title: item.title, price: item.price.value, buyUrl: affiliate ?? safeUrl(item.itemWebUrl, 'ebay.com'),
        imageUrl: safeUrl(item.image?.imageUrl, 'ebayimg.com'), shipping: shipping === undefined ? null : Number(shipping),
        seller: item.seller?.username, condition: item.condition,
        affiliateNote: affiliate ? 'Affiliate link: FindMyPrice may earn a commission from eBay at no cost to you.' : null,
      }) ?? []
    })
  },
}

type BestBuyProduct = { name?: string; salePrice?: number; url?: string; image?: string; onlineAvailability?: boolean; freeShipping?: boolean; shippingCost?: number | string }

const bestBuy: LiveStore = {
  name: 'Best Buy',
  configured: () => !!Netlify.env.get('BESTBUY_API_KEY'),
  async search(query) {
    // Best Buy matches products that contain every search term.
    const terms = (query.toLowerCase().match(/[a-z0-9]+/g) ?? []).slice(0, 6)
    if (terms.length === 0) return []
    const url = `https://api.bestbuy.com/v1/products(${terms.map((t) => `search=${t}`).join('&')})?format=json&pageSize=${LIVE_PER_STORE}&sort=bestSellingRank.asc`
      + `&show=name,salePrice,url,image,onlineAvailability,freeShipping,shippingCost&apiKey=${encodeURIComponent(Netlify.env.get('BESTBUY_API_KEY')!)}`
    const body = await storeJson<{ products?: BestBuyProduct[] }>('Best Buy', url)
    return (body.products ?? []).flatMap((item) => liveOffer('Best Buy', {
      title: item.name, price: item.salePrice, buyUrl: safeUrl(item.url, 'bestbuy.com'), imageUrl: safeUrl(item.image, 'bbystatic.com'),
      shipping: item.freeShipping ? 0 : typeof item.shippingCost === 'number' ? item.shippingCost : null, inStock: item.onlineAvailability !== false, condition: 'New',
    }) ?? [])
  },
}

type WalmartItem = { itemId?: number; name?: string; salePrice?: number; mediumImage?: string; thumbnailImage?: string; stock?: string }

const walmart: LiveStore = {
  name: 'Walmart',
  configured: () => !!Netlify.env.get('WALMART_CONSUMER_ID') && !!Netlify.env.get('WALMART_PRIVATE_KEY'),
  async search(query) {
    const consumerId = Netlify.env.get('WALMART_CONSUMER_ID')!
    const keyVersion = Netlify.env.get('WALMART_KEY_VERSION') ?? '1'
    const timestamp = String(Date.now())
    // Walmart authenticates each request with an RSA signature over these three values. The key may be pasted as a
    // full PEM (with real or escaped line breaks) or as the bare base64 body.
    let pem = Netlify.env.get('WALMART_PRIVATE_KEY')!.replace(/\\n/g, '\n').trim()
    if (!pem.includes('BEGIN')) pem = `-----BEGIN PRIVATE KEY-----\n${pem}\n-----END PRIVATE KEY-----`
    const signature = createSign('RSA-SHA256').update(`${consumerId}\n${timestamp}\n${keyVersion}\n`).sign(pem, 'base64')
    const url = `https://developer.api.walmart.com/api-proxy/service/affil/product/v2/search?query=${encodeURIComponent(query)}&numItems=${LIVE_PER_STORE}`
    const body = await storeJson<{ items?: WalmartItem[] }>('Walmart', url, {
      'WM_CONSUMER.ID': consumerId, 'WM_CONSUMER.INTIMESTAMP': timestamp, 'WM_SEC.KEY_VERSION': keyVersion, 'WM_SEC.AUTH_SIGNATURE': signature, Accept: 'application/json',
    })
    return (body.items ?? []).flatMap((item) => liveOffer('Walmart', {
      title: item.name, price: item.salePrice, buyUrl: item.itemId ? `https://www.walmart.com/ip/${encodeURIComponent(item.itemId)}` : null,
      imageUrl: safeUrl(item.mediumImage ?? item.thumbnailImage, 'walmartimages.com'), inStock: item.stock !== 'Not available', condition: 'New',
    }) ?? [])
  },
}

const liveStores = [ebay, bestBuy, walmart]
const liveEnabled = () => liveStores.some((store) => store.configured())
const listNames = (names: string[]) => (names.length <= 1 ? names.join('') : `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}`)

// Repeat searches are answered from memory while this function instance stays warm.
const liveCache = new Map<string, { expires: number; value: unknown }>()

/** Live results when a store is configured and answers; otherwise the labelled demo catalog. */
async function searchAny(query: string) {
  const fallback = search(query)
  const stores = liveStores.filter((store) => store.configured())
  if (stores.length === 0 || fallback.query.length < 2) return { ...fallback, liveSource: null, notice: null }
  const key = fallback.query.toLowerCase()
  const cached = liveCache.get(key)
  if (cached && cached.expires > Date.now()) return cached.value

  const settled = await Promise.allSettled(stores.map((store) => store.search(fallback.query)))
  const answered = stores.filter((_, index) => settled[index].status === 'fulfilled')
  const failed = stores.filter((_, index) => settled[index].status === 'rejected')
  settled.forEach((result) => result.status === 'rejected' && console.error(result.reason))
  if (answered.length === 0) return { ...fallback, liveSource: null, notice: 'Live prices could not be loaded just now, so sample prices are shown instead.' }

  const offers = settled
    .flatMap((result) => (result.status === 'fulfilled' ? result.value : []))
    .sort((a, b) => Number(a.stockStatus === 'out_of_stock') - Number(b.stockStatus === 'out_of_stock') || a.price + (a.shippingFlatFee ?? 0) - (b.price + (b.shippingFlatFee ?? 0)))
  const source = listNames(answered.map((store) => store.name))
  const results = offers.length === 0 ? [] : [{ productId: `live:${key}`, name: `Live listings for "${fallback.query}"`, category: `Live prices from ${source}`, unit: 'each', isSafetyCritical: false, comparable: false, offers }]
  const value = {
    query: fallback.query, demoData: false, results, suggestions: fallback.suggestions, liveSource: source,
    notice: failed.length > 0 ? `${listNames(failed.map((store) => store.name))} could not be reached just now, so ${failed.length === 1 ? 'its' : 'their'} prices are missing.` : null,
  }
  // Only complete answers are remembered, so a store that failed is retried on the next search.
  if (failed.length === 0) {
    if (liveCache.size >= 300) liveCache.clear()
    liveCache.set(key, { expires: Date.now() + LIVE_CACHE_MS, value })
  }
  return value
}

// ---------- Saved products ----------

type Saved = { productId: string; name: string; query: string; bestPrice: number | null; bestSupplier: string | null; unit: string; savedAt: string }

async function saved(request: Request, userId: string, productId: string | undefined) {
  const key = `saved/${userId}`
  const items = ((await store().get(key, { type: 'json' })) as Saved[] | null) ?? []
  if (request.method === 'GET') return json(items)
  if (request.method === 'DELETE' && productId) {
    const next = items.filter((i) => i.productId !== productId)
    await store().setJSON(key, next)
    return json(next)
  }
  if (request.method === 'POST') {
    const body = (await request.json().catch(() => ({}))) as Record<string, unknown>
    const product = demo.products.find((p) => p.id === body.productId)
    if (!product) throw new HttpError(400, 'Unknown product.', 'validation')
    const best = search(product.name).results.find((r) => r.productId === product.id)?.offers.find((o) => o.stockStatus !== 'out_of_stock')
    const entry: Saved = { productId: product.id, name: product.name, query: product.name, bestPrice: best?.unitPrice ?? null, bestSupplier: best?.supplier ?? null, unit: product.unit, savedAt: new Date().toISOString() }
    const next = [entry, ...items.filter((i) => i.productId !== product.id)].slice(0, 100)
    await store().setJSON(key, next)
    return json(next)
  }
  throw new HttpError(405, 'Method not allowed.')
}

// ---------- Router ----------

export default async (request: Request) => {
  const url = new URL(request.url)
  const path = url.pathname.replace(/\/+$/, '')
  const body = async () => (await request.json().catch(() => ({}))) as Record<string, unknown>
  try {
    if (request.method === 'GET' && path === '/api/public/search') return json(await searchAny(url.searchParams.get('q') ?? ''))
    if (request.method === 'GET' && path === '/api/public/catalog') return json(demo.products.map((p) => ({ name: p.name, category: p.category })).sort((a, b) => a.name.localeCompare(b.name)))
    if (request.method === 'GET' && path === '/api/auth/config') {
      const facebookAppId = Netlify.env.get('FACEBOOK_APP_ID') && Netlify.env.get('FACEBOOK_APP_SECRET') ? Netlify.env.get('FACEBOOK_APP_ID') : null
      return json({
        demoEnabled: false, selfSignupEnabled: true,
        googleEnabled: !!Netlify.env.get('GOOGLE_CLIENT_ID'), googleClientId: Netlify.env.get('GOOGLE_CLIENT_ID') ?? null,
        facebookEnabled: !!facebookAppId, facebookAppId: facebookAppId ?? null, facebookGraphVersion: Netlify.env.get('FACEBOOK_GRAPH_VERSION') ?? 'v21.0',
        workspaceEnabled: false, savedItemsEnabled: true, liveSearchEnabled: liveEnabled(), demoBusinesses: [], plans: [],
      })
    }
    if (request.method === 'POST') {
      if (path === '/api/auth/register') return await register(await body())
      if (path === '/api/auth/login') return await login(await body())
      if (path === '/api/auth/refresh') return await refresh(await body())
      if (path === '/api/auth/google') return await googleSignIn(await body())
      if (path === '/api/auth/facebook') return await facebookSignIn(await body())
      if (path === '/api/auth/logout') {
        const { refreshToken } = await body()
        if (typeof refreshToken === 'string') await store().delete(`refresh/${sha256(refreshToken)}`)
        return new Response(null, { status: 204 })
      }
    }

    const savedMatch = /^\/api\/saved(?:\/([0-9a-f-]{36}))?$/.exec(path)
    if (path === '/api/auth/me' || savedMatch) {
      const claims = await verifyAccessToken(request)
      const user = claims ? await getUser(claims.email) : null
      if (!user || user.id !== claims!.sub) throw new HttpError(401, 'Sign in to continue.')
      if (savedMatch) return await saved(request, user.id, savedMatch[1])
      return json(sessionFor(user))
    }

    return problem(404, 'This feature is part of the FindMyPrice business workspace, which is not available on this site yet.', 'not_available')
  } catch (error) {
    if (error instanceof HttpError) return problem(error.status, error.message, error.code)
    console.error(error)
    return problem(500, 'Something went wrong on our side. Please try again.', 'server_error')
  }
}
