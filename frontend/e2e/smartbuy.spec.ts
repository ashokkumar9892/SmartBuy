import { expect, test, type Page } from '@playwright/test'

const ADMIN = { email: 'admin@findmyprice.local', password: 'ChangeMe!Admin2026' }
const shot = (page: Page, name: string) => page.screenshot({ path: `e2e/screenshots/${name}.png`, fullPage: true })

async function openDemo(page: Page, business: string) {
  await page.goto('/demo')
  await page.getByRole('button', { name: new RegExp(business) }).click()
  await expect(page.getByText('Demo workspace')).toBeVisible()
}

async function register(page: Page) {
  const id = Date.now().toString(36) + Math.random().toString(36).slice(2, 6)
  await page.goto('/register')
  await page.getByLabel('Your name').fill('Erin Tester')
  await page.getByLabel('Work email').fill('e2e-' + id + '@example.test')
  await page.getByLabel('Password').fill('Sup3r-secret-pass')
  await page.getByRole('button', { name: 'Create account' }).click()
  await expect(page).toHaveURL(/\/app\/projects\/new/)
}

test.describe('public site', () => {
  test('landing page presents the product and an interactive sample comparison', async ({ page }) => {
    await page.goto('/')
    await expect(page.getByRole('heading', { level: 1 })).toContainText('Buy Smarter. Spend Less.')
    await expect(page.getByText('Compare materials across suppliers, optimize purchases, and reduce procurement costs using AI.')).toBeVisible()
    await expect(page.getByRole('link', { name: 'Start Free' }).first()).toBeVisible()
    await expect(page.getByRole('button', { name: 'Try Demo' })).toBeVisible()
    await expect(page.getByRole('link', { name: 'See How It Works' })).toBeVisible()
    for (const id of ['benefits', 'how-it-works', 'industries', 'pricing', 'faq', 'contact']) await expect(page.locator('#' + id)).toBeAttached()

    // The sample comparison recalculates in the browser.
    const baseline = page.getByText('Baseline: all from Supplier A').locator('..')
    const before = await baseline.innerText()
    await page.getByLabel('Quantity of 3/4 in. Type L copper pipe').fill('200')
    await expect(baseline).not.toHaveText(before)
    await page.getByRole('button', { name: 'Fastest delivery' }).click()
    await expect(page.getByText(/Optimized plan · \d suppliers? · \d days?/)).toBeVisible()

    await expect(page.locator('#pricing').getByText('$29')).toBeVisible()
    await expect(page.locator('#pricing').getByText('$79')).toBeVisible()
    await expect(page.getByText(/These are proposed test prices, not established market prices/)).toBeVisible()
    await shot(page, '01-landing')
  })

  test('the app requires sign-in and rejects wrong credentials', async ({ page }) => {
    await page.goto('/app/projects')
    await expect(page).toHaveURL(/\/login/)
    await page.getByLabel('Work email').fill('nobody@example.test')
    await page.getByLabel('Password').fill('wrong-password-1')
    await page.getByRole('button', { name: 'Sign in' }).click()
    await expect(page.getByRole('alert')).toContainText('The email or password is incorrect.')
    await expect(page.getByText('Google sign-in is not configured on this server yet.')).toBeVisible()
    await expect(page.getByText('Facebook sign-in is not configured on this server yet.')).toBeVisible()
  })

  test('Try Demo opens a sample business without sign-up', async ({ page }) => {
    await page.goto('/')
    await page.getByRole('button', { name: 'Try Demo' }).click()
    await page.getByRole('button', { name: /ABC Plumbing/ }).click()
    await expect(page).toHaveURL(/\/app$/)
    await expect(page.getByText('Demo workspace')).toBeVisible()
    await expect(page.getByRole('heading', { name: /Welcome back, Alex/ })).toBeVisible()
    await expect(page.getByText('Estimated savings opportunity')).toBeVisible()
    await expect(page.getByText('How this is measured:')).toBeVisible()
    await expect(page.getByText('Based on DEMO prices')).toBeVisible()
    await expect(page.getByText('Supplier integrations')).toBeVisible()
    await shot(page, '02-dashboard-demo')
  })
})

test.describe('purchasing workflow', () => {
  test('a new account builds a list, approves it, compares suppliers and fills the cart', async ({ page }) => {
    await register(page)

    // Free plan: describing a job is an upgrade, so manual entry is preselected.
    await page.getByLabel('Project name').fill('Hall bathroom repipe')
    await page.getByLabel('Material 1').fill('3/4 in. Type L Copper Pipe')
    await page.getByLabel('Quantity 1').fill('50')
    await page.getByLabel('Unit 1').selectOption('ft')
    await page.getByRole('button', { name: /Choose a product/ }).click()
    await page.getByRole('dialog').getByRole('button', { name: /3\/4 in\. Type L Copper Pipe/ }).first().click()
    await page.getByRole('button', { name: 'Add line' }).click()
    await page.getByLabel('Material 2').fill('3/4 in. Copper 90-Degree Elbow')
    await page.getByLabel('Quantity 2').fill('20')
    await page.getByRole('button', { name: /Choose a product/ }).click()
    await page.getByRole('dialog').getByRole('button', { name: /3\/4 in\. Copper 90-Degree Elbow/ }).first().click()
    await page.getByRole('button', { name: 'Create project' }).click()

    await expect(page.getByRole('heading', { name: /Hall bathroom repipe/ })).toBeVisible()
    await expect(page.getByText('Review the quantities before pricing')).toBeVisible()

    // Comparison is blocked until a person approves the list.
    await page.getByRole('tab', { name: 'Compare suppliers' }).click()
    await expect(page.getByText('Approve a shopping list first')).toBeVisible()
    await page.getByRole('button', { name: 'Go to the material list' }).click()
    await page.getByRole('button', { name: 'Approve quantities' }).click()
    await expect(page.getByText('This list is approved and ready to compare.')).toBeVisible()

    await page.getByRole('tab', { name: 'Compare suppliers' }).click()
    await expect(page.getByText('These are demo prices')).toBeVisible()
    await expect(page.getByText(/2 of 2 items priced/)).toBeVisible()
    await expect(page.getByText('DEMO', { exact: true }).first()).toBeVisible()
    // No tax rate on a new organization: tax is shown as unknown, not zero.
    await expect(page.getByText(/tax is shown as unknown/)).toBeVisible()
    await expect(page.getByRole('columnheader', { name: 'Unit price' }).first()).toBeVisible()
    await shot(page, '03-compare')

    await page.getByRole('button', { name: 'See purchasing plan' }).click()
    await expect(page.getByText('Baseline for comparison')).toBeVisible()
    await expect(page.getByRole('radio', { name: /Lowest Cost/ })).toBeVisible()
    await expect(page.getByText('Advanced optimization is not included in the Free plan.').first()).toBeVisible()
    await expect(page.getByText('Excludes amounts that are unknown.').first()).toBeVisible()
    await page.getByRole('button', { name: 'Explain', exact: true }).click()
    await expect(page.getByText('Generated from the plan data (no language model)')).toBeVisible()
    await shot(page, '04-plan-free')

    await page.getByRole('button', { name: 'Add this plan to the cart' }).click()
    await expect(page.getByText('You complete each purchase yourself')).toBeVisible()
    await expect(page.getByRole('heading', { name: 'Cart summary' })).toBeVisible()
    await expect(page.getByText('Purchase orders is not included in your plan')).toBeVisible()
    const download = page.waitForEvent('download')
    await page.getByRole('button', { name: 'Export full list (CSV)' }).click()
    expect((await download).suggestedFilename()).toContain('.csv')
    await shot(page, '05-cart')

    // Everything persisted.
    await page.reload()
    await expect(page.getByRole('heading', { name: 'Cart summary' })).toBeVisible()
    await page.goto('/app/projects')
    await expect(page.getByRole('link', { name: 'Hall bathroom repipe' })).toBeVisible()
  })

  test('a demo business drafts a list from plain language and it must be reviewed', async ({ page }) => {
    await openDemo(page, 'ABC Plumbing')
    await page.goto('/app/projects/new')
    await page.getByLabel('Project name').fill('E2E described job')
    await page.getByLabel('What does this job need?').fill('I need 50 feet of 3/4-inch copper pipe, 20 elbows, 10 shutoff valves, and 5 faucets for a residential plumbing project.')
    await page.getByRole('button', { name: 'Generate draft list' }).click()
    await expect(page.getByText('Review required before pricing')).toBeVisible()
    await expect(page.getByText('Assumptions made')).toBeVisible()
    await expect(page.getByLabel('Material 4')).toHaveValue(/Faucets/i)
    await expect(page.getByLabel('Quantity 1')).toHaveValue('50')
    await shot(page, '06-ai-draft')
    await page.getByRole('button', { name: 'Create project' }).click()
    await expect(page.getByText('Needs review').first()).toBeVisible()
    await expect(page.getByText('AI draft').first()).toBeVisible()
  })

  test('a plan becomes purchase orders that follow approval and honest sending', async ({ page }) => {
    await openDemo(page, 'ABC Plumbing')
    await page.goto('/app/projects')
    await page.getByRole('link', { name: 'Henderson Residence Repipe' }).click()
    await page.getByRole('tab', { name: 'Purchasing plan' }).click()
    await expect(page.getByRole('radio', { name: /Fastest Delivery/ })).toBeVisible()
    await expect(page.getByRole('radio', { name: /Balanced/ })).toBeVisible()
    await page.getByRole('radio', { name: /Fastest Delivery/ }).click()
    await expect(page.getByText('Fastest Delivery: what to buy where')).toBeVisible()
    await page.getByRole('radio', { name: /Lowest Cost/ }).click()
    await shot(page, '07-plan-business')
    await page.getByRole('button', { name: 'Add this plan to the cart' }).click()
    await expect(page.getByRole('heading', { name: 'Cart summary' })).toBeVisible()
    await shot(page, '08-cart-business')

    await page.getByRole('button', { name: 'Create purchase orders for all suppliers' }).click()
    await expect(page.getByRole('tab', { name: /Purchase orders/, selected: true })).toBeVisible()
    await page.getByRole('link', { name: /^PO-\d{4}-\d{4}$/ }).first().click()
    await expect(page.getByRole('heading', { level: 1 })).toContainText(/PO-\d{4}-\d{4}/)
    await shot(page, '09-po-draft')

    await page.getByRole('button', { name: /Submit for approval/i }).click()
    await expect(page.getByText('Pending Approval').first()).toBeVisible()
    await page.getByRole('button', { name: /^Approve/ }).first().click()
    const dialog = page.getByRole('dialog')
    if (await dialog.isVisible().catch(() => false)) await dialog.getByRole('button', { name: /^Approve/ }).click()
    await expect(page.getByRole('button', { name: /Record as sent/i })).toBeVisible()

    // No email channel is configured: sending is shown as unavailable rather than faked.
    await expect(page.getByText('No email delivery channel is configured on this server.')).toBeVisible()
    await page.getByRole('button', { name: /Record as sent/i }).click()
    await page.getByRole('dialog').getByRole('button', { name: /Record as sent/i }).click()
    await expect(page.getByText(/Recorded manually/).first()).toBeVisible()
    await shot(page, '10-po-sent')
  })

  test('alternatives explain differences and require acknowledgement', async ({ page }) => {
    await openDemo(page, 'ABC Plumbing')
    await page.goto('/app/projects')
    await page.getByRole('link', { name: 'Henderson Residence Repipe' }).click()
    await page.getByRole('row', { name: /3\/4 in\. Type L Copper Pipe/ }).getByRole('button', { name: 'Alternatives' }).click()
    const dialog = page.getByRole('dialog')
    await expect(dialog.getByText('3/4 in. Type M Copper Pipe').first()).toBeVisible()
    await expect(dialog.getByText('Specification differences').first()).toBeVisible()
    await expect(dialog.getByRole('button', { name: 'Approve substitution' }).first()).toBeDisabled()
    await shot(page, '11-alternatives')
  })
})

test.describe('workspace and administration', () => {
  test('secondary pages load for a demo business', async ({ page }) => {
    await openDemo(page, 'ABC Plumbing')
    for (const path of ['/app/purchase-orders', '/app/alerts', '/app/suppliers', '/app/reports', '/app/team', '/app/billing']) {
      await page.goto(path)
      await expect(page.getByRole('heading', { level: 1 })).toBeVisible()
      await page.waitForLoadState('networkidle')
      await expect(page.getByText('This could not be loaded')).toHaveCount(0)
      await shot(page, '12-' + path.split('/').pop())
    }
    await page.goto('/app/suppliers')
    await expect(page.getByText('DEMO DATA').first()).toBeVisible()
    await page.goto('/app/billing')
    await expect(page.getByText(/proposed test prices/i).first()).toBeVisible()
  })

  test('organization owners cannot open platform administration', async ({ page }) => {
    await openDemo(page, 'ABC Plumbing')
    await page.goto('/admin')
    await expect(page.getByText(/Platform administrator access is required/i)).toBeVisible()
  })

  test('platform administrator can use the admin panel', async ({ page }) => {
    await page.goto('/login')
    await page.getByLabel('Work email').fill(ADMIN.email)
    await page.getByLabel('Password').fill(ADMIN.password)
    await page.getByRole('button', { name: 'Sign in' }).click()
    await expect(page).toHaveURL(/\/app$/)
    await page.goto('/admin')
    await expect(page.getByText('Platform administration').first()).toBeVisible()
    await page.waitForLoadState('networkidle')
    await shot(page, '13-admin-overview')
    for (const tab of ['Organizations', 'Users', 'Suppliers', 'Catalog', 'Match review', 'API usage', 'AI usage', 'Analytics', 'Affiliate clicks', 'Errors', 'Audit log', 'Feature flags']) {
      await page.getByRole('tab', { name: new RegExp('^' + tab) }).click()
      await page.waitForLoadState('networkidle')
      await expect(page.getByText('This could not be loaded')).toHaveCount(0)
    }
    await page.getByRole('tab', { name: /^Match review/ }).click()
    await expect(page.getByText(/Name similarity alone never auto-matches/)).toBeVisible()
    await shot(page, '14-admin-matches')
  })
})
