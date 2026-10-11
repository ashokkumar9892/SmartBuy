# Deploying SmartBuy AI

Three independent pieces:

| Piece | Where | Notes |
|---|---|---|
| Frontend | Netlify | Static files built by Vite. |
| API | Windows Server + IIS, or any host that runs .NET 10 | Stateless; all state is in the database. |
| Database | PostgreSQL 14+ | Schema is created and upgraded by EF Core migrations at API start-up. |

Deploy the database and API first: the frontend needs the API's public URL.

## 1. PostgreSQL

```sql
CREATE ROLE smartbuy LOGIN PASSWORD 'a-long-random-password';
CREATE DATABASE smartbuy OWNER smartbuy;
```

The API applies migrations itself on start. To apply them by hand instead:

```powershell
$env:SMARTBUY_MIGRATIONS_CONNECTION = 'Host=...;Database=smartbuy;Username=smartbuy;Password=...'
dotnet ef database update --project backend\src\SmartBuy.Infrastructure --startup-project backend\src\SmartBuy.Api
```

Take regular backups (`pg_dump`) and test a restore.

## 2. API on Windows + IIS

1. **Install on the server:** the *ASP.NET Core 10 Hosting Bundle* (it contains the runtime and the IIS module).
   Restart IIS afterwards (`iisreset`).
2. **Publish** on your machine and copy the folder to the server, for example `C:\inetpub\smartbuy-api`:
   ```powershell
   .\scripts\publish-iis.ps1 -Output C:\publish\smartbuy-api
   ```
3. **Create the site** in IIS Manager: new Application Pool with *.NET CLR version = No Managed Code*; new Website
   pointing at the folder; bind it to your API host name with an HTTPS certificate.
4. **Set configuration** as environment variables on the application pool (IIS Manager → the site → Configuration
   Editor → `system.webServer/aspNetCore` → `environmentVariables`), or as machine-level variables. Use
   [.env.example](../.env.example) as the list. At minimum:
   ```
   ASPNETCORE_ENVIRONMENT=Production
   Database__Provider=postgres
   Database__ConnectionString=Host=...;Database=smartbuy;Username=smartbuy;Password=...
   Jwt__Key=<random, 32+ characters>
   Cors__AllowedOrigins__0=https://<your-site>.netlify.app
   App__FrontendUrl=https://<your-site>.netlify.app
   Seed__AdminEmail=<you>
   Seed__AdminPassword=<strong password>
   ```
   Do not put secrets in `appsettings.json` or `web.config` under source control.
5. **Give the app-pool identity** read access to the folder. Stop the site before copying a new version over it
   (IIS locks the DLLs), or deploy to a new folder and repoint the site.
6. **Check:** `https://<api-host>/health/live` returns `Healthy`; `/health/ready` also checks the database.

After the first successful start, remove `Seed__AdminPassword` from the environment (the account already exists) and
decide whether to keep `Seed__DemoData=true`. Demo businesses are what the public **Try Demo** button opens.

### Other hosts

Any Linux VM works the same way: install the .NET 10 ASP.NET runtime, `dotnet publish`, run `dotnet SmartBuy.Api.dll`
behind a reverse proxy (nginx, Caddy) that terminates TLS and forwards `X-Forwarded-For` / `X-Forwarded-Proto`.
`backend/Dockerfile` builds a container image (not yet run against a Docker engine; verify before relying on it).

## 3. Frontend on Netlify

`netlify.toml` at the repository root already contains the build settings (`base = frontend`, `npm ci && npm run build`,
publish `dist`), the single-page-app fallback, security headers and asset caching.

1. In Netlify: **Add new site → Import an existing project** and pick this repository. Leave the build settings as
   detected from `netlify.toml`.
2. Point the site at the API, one of:
   - **Environment variable (simplest):** Site configuration → Environment variables → add
     `VITE_API_BASE_URL = https://<api-host>`. The API must list the Netlify site URL in `Cors__AllowedOrigins__0`.
   - **Same-origin proxy:** leave `VITE_API_BASE_URL` unset, and in `netlify.toml` uncomment the `/api/*` redirect and
     set its host. No CORS configuration is needed.
3. Optional: `VITE_CONTACT_EMAIL` enables the contact form on the landing page.
4. Deploy. `VITE_*` values are read at build time, so trigger a new deploy after changing them.

Without Git: `cd frontend; npm ci; npm run build`, then `npx netlify deploy --prod --dir=dist` (set the variables in the
shell first, and run from the repository root so `netlify.toml` is picked up).

## 4. Background job: price alerts

Pick **one**:

- **Windows Task Scheduler (recommended on IIS):** IIS may suspend an idle site, so run the job out of process.
  Create a task that runs hourly, with the same environment variables as the site:
  ```
  Program:   C:\Program Files\dotnet\dotnet.exe
  Arguments: C:\inetpub\smartbuy-api\SmartBuy.Api.dll --run-job price-alerts
  Start in:  C:\inetpub\smartbuy-api
  ```
  The process runs the job once, prints a one-line summary and exits (code 0 on success).
- **cron** on Linux: the same command.
- **In-process scheduler:** `Jobs__PriceAlertSchedulerEnabled=true` and `Jobs__PriceAlertIntervalMinutes=60`. Only for a
  single, always-running instance.

For local development, the Price alerts page has **Run alert check now**, and the admin panel can run it for all
organizations.

If you later run several API instances or need retries and history, move this to a persistent job runner such as
Hangfire or Quartz.NET with a database store; `PriceAlertJob.RunAsync` is the single entry point to call.

## 5. Optional services

| Feature | Settings | Behaviour when unset |
|---|---|---|
| AI lists and explanations | `Ai__Provider=claude`, `Ai__Claude__ApiKey` (or `openai` + key + model) | Rule-based demo parser; explanations use a template. |
| Purchase-order email | `Smtp__Host`, `Smtp__FromAddress`, credentials | "Send" is shown as unavailable; POs can be exported or recorded as sent manually. |
| Stripe billing | `Billing__StripeSecretKey`, `Billing__StripeWebhookSecret`, `Billing__Prices__pro`, `Billing__Prices__business` | Checkout unavailable. Webhook URL: `https://<api-host>/api/billing/webhook` (events: `checkout.session.completed`, `customer.subscription.updated`, `customer.subscription.deleted`). Start with test keys. |
| Google sign-in | `Google__ClientId` (add the frontend origin as an authorized JavaScript origin) | Button shown as unavailable. |

None of these integrations has been run against the real service yet. Test each in a non-production environment first.

## 6. Production security checklist

- [ ] HTTPS only on both the site and the API; HSTS is sent by both.
- [ ] `Jwt__Key` is a unique random secret of 32+ characters, stored outside source control. Rotating it signs
      everyone out.
- [ ] `ASPNETCORE_ENVIRONMENT=Production` (disables the development key, Swagger, and manual plan switching).
- [ ] `Cors__AllowedOrigins` lists only your real frontend origin(s).
- [ ] `Billing__AllowManualPlanChange=false`.
- [ ] `Swagger__Enabled=false` unless you intend the API reference to be public.
- [ ] The database user owns only the SmartBuy database; the database is not reachable from the internet.
- [ ] Platform administrator password is strong, and `Seed__AdminPassword` is removed after first start.
- [ ] Decide whether the public demo should be on (`demo_login` feature flag in the admin panel). Demo workspaces are
      shared and writable by anyone; reset them from Admin → Operations.
- [ ] Tighten `connect-src` in `netlify.toml` from `https:` to your API origin.
- [ ] If the API is behind a proxy or load balancer, configure trusted proxies so the client IP used for rate limiting
      is the real one.
- [ ] Rate limits (`RateLimits__*`) suit your traffic. Sign-in is limited per IP and accounts lock for 15 minutes after
      5 failed attempts.
- [ ] Stripe webhook secret set; only test keys outside production.
- [ ] Backups scheduled and a restore tested.
- [ ] Review Admin → Errors and Audit log after launch.

What the application already does: PBKDF2-SHA256 password hashing (600,000 iterations), short-lived access tokens with
rotating single-use refresh tokens and reuse detection, per-request membership checks, tenant isolation on reads and
writes, input validation, upload type/size/signature checks with in-memory parsing only, CSV formula-injection
neutralization, outbound-link allow-listing to the supplier's own HTTPS domain, prompt-injection containment for AI
input, and an audit log of sensitive actions. Tokens are sent in the `Authorization` header (never cookies), so the API
has no CSRF surface; the trade-off is that the refresh token lives in browser storage, which makes a strict
Content-Security-Policy on the frontend important.
