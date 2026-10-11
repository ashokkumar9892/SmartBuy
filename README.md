# FindMyPrice

**One List. Every Supplier. The Smarter Way to Buy.**

An AI-assisted purchasing and price-optimization platform for small and medium-sized businesses. A user describes a
job, enters items, or uploads a list; FindMyPrice links each line to a catalog product, compares offers across suppliers
with pack sizes normalized, builds an optimized purchasing plan, and turns it into a supplier-grouped cart and
purchase orders.

> **Status of supplier data.** No live supplier API is connected yet. Every supplier runs on a mock adapter and every
> price is labelled **DEMO**. The application, calculations and workflow are real; the prices are simulated.
> See [docs/SUPPLIER_INTEGRATIONS.md](docs/SUPPLIER_INTEGRATIONS.md).

## What is in the repository

| Path | Contents |
|---|---|
| `backend/src/SmartBuy.Domain` | Entities and pure business logic: unit conversion, pack normalization, product matching, pricing, the optimizer, purchase-order and alert rules. No dependencies. |
| `backend/src/SmartBuy.Infrastructure` | EF Core (PostgreSQL + SQLite), supplier adapters, AI providers, list parsing, purchase orders, billing, seeding, jobs. |
| `backend/src/SmartBuy.Api` | ASP.NET Core API: authentication, tenant resolution, rate limiting, controllers, Swagger. |
| `backend/tests/SmartBuy.Tests` | 149 unit and API integration tests. |
| `frontend` | React 19 + TypeScript + Vite + Tailwind + TanStack Query + React Router. Playwright tests in `frontend/e2e`. |
| `docs` | Deployment, supplier integration and security notes. |
| `scripts` | PowerShell helpers for setup, running, testing and publishing. |

## Run it locally

Requirements: **.NET 10 SDK** and **Node.js 22+**. Development uses SQLite, so no database server is needed.

```powershell
.\scripts\setup.ps1     # restore, build, npm install (once)
.\scripts\dev.ps1       # API on :5080 and frontend on :5173
```

Or by hand:

```powershell
# Terminal 1 - API
cd backend\src\SmartBuy.Api
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --no-launch-profile --urls http://localhost:5080

# Terminal 2 - frontend
cd frontend
npm install
npm run dev
```

Open <http://localhost:5173>.

- **Try Demo** opens one of three seeded businesses (ABC Plumbing, Premier Renovations, Green Valley Landscaping) with
  sample projects, comparisons, alerts and purchase orders. No sign-up needed.
- **Platform admin (development only):** `admin@findmyprice.local` / `ChangeMe!Admin2026`, then open `/admin`.
- **API documentation:** <http://localhost:5080/swagger>.
- Delete `backend/src/SmartBuy.Api/smartbuy-dev.db` to start again from a clean seed.

With Docker (PostgreSQL + API): `docker compose up --build`, then `npm run dev` in `frontend`. The Docker files were
written without a Docker engine available and have not been run; treat them as a starting point.

## Tests

```powershell
.\scripts\test.ps1
# or individually
dotnet test backend\SmartBuy.sln
cd frontend; npm run build; npx playwright test
```

- **Backend:** login and authorization, multi-tenant isolation, product matching, pack-size normalization, unit
  conversion, price calculation, optimization (the heuristic is checked against exhaustive search), supplier failures
  and timeouts, price-alert rules, purchase-order calculations and workflow, API validation, subscription limits.
- **Browser:** landing page, sign-in guard, demo, list → approve → compare → plan → cart, AI draft, purchase-order
  approval and sending, alternatives, secondary pages, admin panel.

## How the important parts work

**Reviewed lists.** AI or an upload only ever produces a *draft*. A person must approve the quantities before any
supplier is searched. With no AI key configured, a built-in rule-based parser stands in and says so in the UI.

**Product matching.** Supplier listings are matched to a supplier-independent catalog in this order: GTIN/UPC,
manufacturer part number, brand + model, verified specifications, then title similarity. Title similarity alone can
never auto-match; a conflict in a critical specification (size, type, voltage, rating…) rejects the match. Matches
under 85 % confidence wait in the admin review queue and are not priced.

**Pack normalization.** Every offer is converted to the unit the line was requested in. 20 elbows from a bag of 10 is
two bags; 3 cubic yards of mulch in 2 cu ft bags is 41 bags.

**Unknowns.** Unpublished shipping, an unset tax rate, or unreported stock are carried as *unknown*, shown as such,
and excluded from totals. They are never treated as zero. For ranking only, unknown shipping is assumed to cost as much
as the most expensive known flat fee, so an unknown charge never beats a known one.

**Optimizer.** Deterministic decimal arithmetic. Small lists are solved exhaustively; larger ones enumerate supplier
subsets and then apply a single-move local search. Three strategies: Lowest Cost, Fastest Delivery, Balanced.

**Savings baseline.** Savings are always measured against a stated baseline: buying from the single supplier that
carries the most of the list (the rest priced at the cheapest offer elsewhere).

**Purchasing.** FindMyPrice never places an order. The cart offers a plain link to the supplier, a CSV export, or a
purchase order. A PO is only marked *Sent* after the email channel confirms hand-off, or when a user explicitly records
that they delivered it themselves (stored as a manual record).

**Substitutions.** Alternatives come from a reviewed table, with price, specification and compatibility differences.
Each needs explicit approval. Safety-critical items only allow alternatives with the same rating or listing.

**Tenancy.** Every tenant-owned table has a global query filter on the active organization, and writes are checked the
same way in `SaveChanges`. Platform administration is a separate flag on the user, not an organization role.

## Configuration

All settings are environment variables; see [.env.example](.env.example). The ones that matter first:

| Setting | Purpose |
|---|---|
| `Database__Provider`, `Database__ConnectionString` | `postgres` in production (EF Core migrations run at start-up); `sqlite` for development. |
| `Jwt__Key` | Signing secret, 32+ characters. The API refuses to start in production without one. |
| `Cors__AllowedOrigins__0`, `App__FrontendUrl` | The frontend's origin. |
| `Seed__AdminEmail`, `Seed__AdminPassword` | Creates the first platform administrator. |
| `Ai__Provider`, `Ai__Claude__ApiKey` | `mock` (default), `claude` or `openai`. |
| `Smtp__*` | Enables sending purchase orders by email. |
| `Billing__Stripe*` | Enables Stripe Checkout and webhooks (use test keys first). |

Frontend build-time settings are in [frontend/.env.example](frontend/.env.example).

## Deployment

Frontend on Netlify, API on Windows/IIS or any server that runs .NET, PostgreSQL for data.
Step-by-step instructions, health checks, scheduled jobs and the production security checklist are in
[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md).

## Known gaps

- **Live supplier integrations (Phase 6)** are not built: no supplier API credentials were available. The adapter
  interface, registry and data-status labelling are in place.
- **Claude, OpenAI, Stripe, SMTP and Google sign-in** are implemented but were not exercised against the real services,
  because no credentials were configured. Each is off until its settings are present.
- **Email invitations** are not sent; an administrator adds a member with a temporary password.
- **Affiliate links:** clicks are logged and the flag exists, but no affiliate tag is ever attached.
- **Pricing** ($29 / $79) is proposed test pricing.
- The in-process alert scheduler and the catalog write lock assume a single API instance.
