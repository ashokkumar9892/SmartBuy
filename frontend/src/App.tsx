import { lazy, Suspense, useEffect } from 'react'
import { Route, Routes, useLocation } from 'react-router-dom'
import { AppLayout, RequireAuth } from './components/AppLayout'
import { LinkButton, Spinner } from './components/ui'
import { AlertsPage } from './pages/app/AlertsPage'
import { BillingPage } from './pages/app/BillingPage'
import { DashboardPage } from './pages/app/DashboardPage'
import { NewProjectPage } from './pages/app/NewProjectPage'
import { OrganizationPage } from './pages/app/OrganizationPage'
import { ProjectDetailPage } from './pages/app/ProjectDetailPage'
import { ProjectsPage } from './pages/app/ProjectsPage'
import { PurchaseOrderDetailPage } from './pages/app/PurchaseOrderDetailPage'
import { PurchaseOrdersPage } from './pages/app/PurchaseOrdersPage'
import { ReportsPage } from './pages/app/ReportsPage'
import { SuppliersPage } from './pages/app/SuppliersPage'
import { DemoPage, LoginPage, RegisterPage } from './pages/AuthPages'
import { LandingPage } from './pages/LandingPage'

// The admin panel is large and used by very few people, so it is loaded on demand.
const AdminPage = lazy(() => import('./pages/admin/AdminPage').then((m) => ({ default: m.AdminPage })))

function ScrollToTop() {
  const { pathname, hash } = useLocation()
  useEffect(() => {
    if (!hash) window.scrollTo(0, 0)
  }, [pathname, hash])
  return null
}

function NotFound() {
  return (
    <div className="flex min-h-screen flex-col items-center justify-center gap-4 px-6 text-center">
      <p className="text-sm font-semibold uppercase tracking-widest text-accent-600">404</p>
      <h1 className="text-3xl font-semibold text-navy-900">This page does not exist</h1>
      <p className="max-w-md text-slate-500">The link may be out of date, or the item may belong to a different organization.</p>
      <LinkButton to="/" variant="primary">Back to FindMyPrice</LinkButton>
    </div>
  )
}

export default function App() {
  return (
    <>
      <ScrollToTop />
      <Routes>
        <Route path="/" element={<LandingPage />} />
        <Route path="/login" element={<LoginPage />} />
        <Route path="/register" element={<RegisterPage />} />
        <Route path="/demo" element={<DemoPage />} />
        <Route path="/app" element={<RequireAuth><AppLayout /></RequireAuth>}>
          <Route index element={<DashboardPage />} />
          <Route path="projects" element={<ProjectsPage />} />
          <Route path="projects/new" element={<NewProjectPage />} />
          <Route path="projects/:id" element={<ProjectDetailPage />} />
          <Route path="purchase-orders" element={<PurchaseOrdersPage />} />
          <Route path="purchase-orders/:id" element={<PurchaseOrderDetailPage />} />
          <Route path="alerts" element={<AlertsPage />} />
          <Route path="suppliers" element={<SuppliersPage />} />
          <Route path="reports" element={<ReportsPage />} />
          <Route path="team" element={<OrganizationPage />} />
          <Route path="billing" element={<BillingPage />} />
        </Route>
        <Route path="/admin" element={<RequireAuth><Suspense fallback={<Spinner label="Loading administration" />}><AdminPage /></Suspense></RequireAuth>} />
        <Route path="*" element={<NotFound />} />
      </Routes>
    </>
  )
}
