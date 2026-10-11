import { useQuery, useQueryClient } from '@tanstack/react-query'
import { createContext, useCallback, useContext, useEffect, useMemo, type ReactNode } from 'react'
import { api, tokens } from './api'
import type { AuthResponse, PublicConfig, Session } from './types'

type AuthContextValue = {
  session: Session | null
  loading: boolean
  /** Set when the session could not be loaded for a reason other than being signed out (network, rate limit, server error). */
  loadError: Error | null
  retry: () => void
  login: (email: string, password: string) => Promise<Session>
  register: (input: { email: string; password: string; fullName: string }) => Promise<Session>
  openDemo: (business: string) => Promise<Session>
  loginWithGoogle: (idToken: string) => Promise<Session>
  loginWithFacebook: (accessToken: string) => Promise<Session>
  switchOrganization: (organizationId: string) => Promise<void>
  logout: () => Promise<void>
  refreshSession: () => Promise<void>
}

const AuthContext = createContext<AuthContextValue | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()

  const sessionQuery = useQuery({
    queryKey: ['session'],
    queryFn: () => api.get<Session>('/api/auth/me'),
    enabled: tokens.has(),
    retry: false,
    staleTime: 60_000,
  })

  // Drops everything cached for the previous identity. The session query itself is kept and overwritten in place:
  // removing it would detach the observer that the whole app reads the session from.
  const resetCache = useCallback(
    (session: Session | null) => {
      queryClient.removeQueries({ predicate: (query) => query.queryKey[0] !== 'session' && query.queryKey[0] !== 'public-config' })
      queryClient.setQueryData(['session'], session)
    },
    [queryClient],
  )

  const accept = useCallback(
    (response: AuthResponse) => {
      tokens.set(response)
      resetCache(response.session)
      return response.session
    },
    [resetCache],
  )

  useEffect(
    () =>
      tokens.onExpired(() => resetCache(null)) as () => void,
    [resetCache],
  )

  const value = useMemo<AuthContextValue>(
    () => ({
      session: tokens.has() ? (sessionQuery.data ?? null) : null,
      loading: tokens.has() && sessionQuery.isLoading,
      loadError: tokens.has() && sessionQuery.isError ? sessionQuery.error : null,
      retry: () => void sessionQuery.refetch(),
      login: async (email, password) => accept(await api.anonymous.post<AuthResponse>('/api/auth/login', { email, password })),
      register: async (input) => accept(await api.anonymous.post<AuthResponse>('/api/auth/register', input)),
      openDemo: async (business) => accept(await api.anonymous.post<AuthResponse>('/api/auth/demo', { business })),
      loginWithGoogle: async (idToken) => accept(await api.anonymous.post<AuthResponse>('/api/auth/google', { idToken })),
      loginWithFacebook: async (accessToken) => accept(await api.anonymous.post<AuthResponse>('/api/auth/facebook', { accessToken })),
      switchOrganization: async (organizationId) => {
        accept(await api.post<AuthResponse>('/api/auth/switch-organization', { organizationId }))
      },
      logout: async () => {
        const refreshToken = tokens.refresh()
        tokens.clear()
        resetCache(null)
        if (refreshToken) await api.anonymous.post('/api/auth/logout', { refreshToken }).catch(() => undefined)
      },
      refreshSession: async () => {
        await queryClient.invalidateQueries({ queryKey: ['session'] })
      },
    }),
    [accept, queryClient, resetCache, sessionQuery],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth() {
  const context = useContext(AuthContext)
  if (!context) throw new Error('useAuth must be used inside AuthProvider')
  return context
}

/** Session for pages rendered inside the authenticated app shell, where it is guaranteed to exist. */
export function useSession(): Session {
  const { session } = useAuth()
  if (!session) throw new Error('No active session')
  return session
}

export function usePublicConfig() {
  return useQuery({ queryKey: ['public-config'], queryFn: () => api.anonymous.get<PublicConfig>('/api/auth/config'), staleTime: 5 * 60_000, retry: 1 })
}
