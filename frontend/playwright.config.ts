import { defineConfig, devices } from '@playwright/test'

// End-to-end tests drive the real frontend against the real API with the demo seed.
// Both servers are started automatically unless they are already running.
export default defineConfig({
  testDir: './e2e',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list']],
  use: {
    baseURL: 'http://localhost:5173',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } } }],
  webServer: [
    {
      command: 'dotnet run --project ../backend/src/SmartBuy.Api --no-launch-profile --urls http://localhost:5080',
      url: 'http://localhost:5080/health/ready',
      reuseExistingServer: true,
      timeout: 180_000,
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        Database__ConnectionString: 'Data Source=smartbuy-e2e.db',
        RateLimits__AuthPerMinute: '1000',
        RateLimits__GlobalPerMinute: '100000',
      },
    },
    { command: 'npm run dev', url: 'http://localhost:5173', reuseExistingServer: true, timeout: 60_000 },
  ],
})
