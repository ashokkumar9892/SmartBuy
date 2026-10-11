import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Trash2, UserPlus, Users } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { Badge, Button, Card, CardHeader, ConfirmDialog, EmptyState, ErrorState, Input, Modal, Notice, PageHeader, Select, SkeletonRows, Table, Td, Textarea, Th, useToast } from '../../components/ui'
import { api, ApiError, errorMessage } from '../../lib/api'
import { useAuth, useSession } from '../../lib/auth'
import { formatDate, timeAgo } from '../../lib/format'
import { INDUSTRIES, ROLE_LABELS, industryLabel, type Member, type Organization, type Role } from '../../lib/types'

const ROLE_ORDER: Role[] = ['Viewer', 'Employee', 'PurchasingManager', 'Administrator', 'Owner']
const rank = (role: Role) => ROLE_ORDER.indexOf(role)

const ROLE_REFERENCE: { role: Role; can: string }[] = [
  { role: 'Viewer', can: 'Read only: can see projects, lists, comparisons and purchase orders.' },
  { role: 'Employee', can: 'Create projects and shopping lists, build carts and draft purchase orders.' },
  { role: 'PurchasingManager', can: 'Everything an Employee can do, plus approve and send purchase orders, approve substitutions and manage suppliers.' },
  { role: 'Administrator', can: 'Everything a Purchasing Manager can do, plus organization settings and members.' },
  { role: 'Owner', can: 'Everything an Administrator can do, plus plan and billing.' },
]

export function OrganizationPage() {
  const session = useSession()
  const canManage = session.permissions.canManageOrganization

  return (
    <>
      <PageHeader title="Organization" description="Company details used across projects and purchase orders, and the people who can work in this organization." />
      <div className="space-y-6">
        <SettingsSection canManage={canManage} />
        <MembersSection canManage={canManage} />
        <RoleReference />
      </div>
    </>
  )
}

// ---------- Organization settings ----------

function SettingsSection({ canManage }: { canManage: boolean }) {
  const query = useQuery({ queryKey: ['organization'], queryFn: () => api.get<Organization>('/api/organization') })
  const [conflict, setConflict] = useState<string | null>(null)

  return (
    <Card>
      <CardHeader title="Organization settings" description="These defaults are applied to new projects and purchase orders." />
      {!canManage && (
        <Notice className="mb-4">Only administrators and owners can change organization settings. You can view them here.</Notice>
      )}
      {conflict && (
        <Notice tone="warning" className="mb-4" title="Your changes were not saved">
          {conflict} The latest saved settings are shown below.
        </Notice>
      )}
      {query.isPending ? (
        <SkeletonRows rows={3} />
      ) : query.isError ? (
        <ErrorState error={query.error} onRetry={() => query.refetch()} />
      ) : (
        // Keyed by version so the form picks up the latest saved values after a save or a conflict.
        <SettingsForm key={query.data.version} organization={query.data} canManage={canManage} onConflict={setConflict} />
      )}
    </Card>
  )
}

function SettingsForm({ organization, canManage, onConflict }: { organization: Organization; canManage: boolean; onConflict: (message: string | null) => void }) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const { refreshSession } = useAuth()
  const [name, setName] = useState(organization.name)
  const [industry, setIndustry] = useState(organization.industry)
  const [taxRate, setTaxRate] = useState(organization.estimatedTaxRatePercent === null ? '' : String(organization.estimatedTaxRatePercent))
  const [postalCode, setPostalCode] = useState(organization.postalCode ?? '')
  const [shippingAddress, setShippingAddress] = useState(organization.shippingAddress ?? '')
  const [error, setError] = useState<string | null>(null)

  const trimmedTax = taxRate.trim()
  const taxNumber = trimmedTax === '' ? null : Number(trimmedTax)
  const taxError = taxNumber !== null && (!Number.isFinite(taxNumber) || taxNumber < 0 || taxNumber > 30) ? 'Enter a rate between 0 and 30 percent, or leave blank.' : null

  const save = useMutation({
    mutationFn: () =>
      api.put<Organization>('/api/organization', {
        name: name.trim(),
        industry,
        estimatedTaxRatePercent: taxNumber,
        postalCode: postalCode.trim() || null,
        shippingAddress: shippingAddress.trim() || null,
        version: organization.version,
      }),
    onSuccess: async (updated) => {
      onConflict(null)
      toast.success('Organization settings saved.')
      queryClient.setQueryData(['organization'], updated)
      await queryClient.invalidateQueries({ queryKey: ['organization'] })
      await refreshSession()
    },
    onError: (err) => {
      if (err instanceof ApiError && err.status === 409 && err.code === 'concurrency') {
        onConflict(err.message)
        queryClient.invalidateQueries({ queryKey: ['organization'] })
        return
      }
      setError(errorMessage(err))
    },
  })

  const submit = (event: FormEvent) => {
    event.preventDefault()
    setError(null)
    if (!name.trim()) return setError('Enter the organization name.')
    if (taxError) return
    save.mutate()
  }

  return (
    <form onSubmit={submit} className="space-y-4" noValidate>
      {organization.isDemo && <Badge tone="violet">Demo business</Badge>}
      <div className="grid gap-4 sm:grid-cols-2">
        <Input label="Organization name" value={name} onChange={(e) => setName(e.target.value)} maxLength={200} required disabled={!canManage} />
        <Select label="Industry" value={industry} onChange={(e) => setIndustry(e.target.value)} disabled={!canManage}>
          {!INDUSTRIES.some((i) => i.value === industry) && <option value={industry}>{industryLabel(industry)}</option>}
          {INDUSTRIES.map((i) => (
            <option key={i.value} value={i.value}>
              {i.label}
            </option>
          ))}
        </Select>
        <Input
          label="Estimated sales-tax rate (%)"
          inputMode="decimal"
          placeholder="Unknown"
          value={taxRate}
          onChange={(e) => setTaxRate(e.target.value)}
          error={taxError}
          hint="Used only to estimate tax. Leave blank and tax is shown as Unknown rather than assumed to be zero."
          disabled={!canManage}
        />
        <Input label="Postal code" value={postalCode} onChange={(e) => setPostalCode(e.target.value)} maxLength={20} autoComplete="postal-code" disabled={!canManage} />
      </div>
      <Textarea
        label="Default ship-to address"
        rows={3}
        value={shippingAddress}
        onChange={(e) => setShippingAddress(e.target.value)}
        maxLength={500}
        hint="Pre-filled on new purchase orders. It can be changed on each order."
        disabled={!canManage}
      />
      {error && <Notice tone="error">{error}</Notice>}
      {canManage && (
        <div className="flex justify-end">
          <Button type="submit" variant="primary" loading={save.isPending}>
            Save settings
          </Button>
        </div>
      )}
    </form>
  )
}

// ---------- Members ----------

function MembersSection({ canManage }: { canManage: boolean }) {
  const session = useSession()
  const toast = useToast()
  const queryClient = useQueryClient()
  const query = useQuery({ queryKey: ['organization', 'members'], queryFn: () => api.get<Member[]>('/api/organization/members') })
  const [adding, setAdding] = useState(false)
  const [removing, setRemoving] = useState<Member | null>(null)
  const [removeError, setRemoveError] = useState<string | null>(null)

  const myRank = rank(session.organization.role)
  const assignable = ROLE_ORDER.filter((role) => rank(role) <= myRank)
  const maxMembers = session.plan.maxMembers
  const count = query.data?.length
  const atLimit = maxMembers !== null && count !== undefined && count >= maxMembers

  const refresh = () => queryClient.invalidateQueries({ queryKey: ['organization', 'members'] })

  const changeRole = useMutation({
    mutationFn: ({ member, role }: { member: Member; role: Role }) => api.put(`/api/organization/members/${member.userId}`, { role }),
    onSuccess: (_, { member, role }) => {
      toast.success(`${member.fullName} is now ${ROLE_LABELS[role]}.`)
      refresh()
    },
    onError: (err) => {
      toast.error(errorMessage(err))
      refresh()
    },
  })

  const remove = useMutation({
    mutationFn: (member: Member) => api.delete(`/api/organization/members/${member.userId}`),
    onSuccess: (_, member) => {
      toast.success(`${member.fullName} was removed from the organization.`)
      setRemoving(null)
      refresh()
    },
    onError: (err) => setRemoveError(errorMessage(err)),
  })

  const seats =
    count === undefined ? null : maxMembers === null ? `${count} ${count === 1 ? 'member' : 'members'} · unlimited seats on the ${session.plan.name} plan` : `${count} of ${maxMembers} seats used on the ${session.plan.name} plan`

  return (
    <Card padded={false}>
      <div className="px-5 pt-5 sm:px-6 sm:pt-6">
        <CardHeader
          title="Team members"
          description={seats ?? 'People who can sign in to this organization.'}
          action={
            canManage && (
              <Button variant="primary" icon={<UserPlus className="size-4" aria-hidden />} onClick={() => setAdding(true)}>
                Add member
              </Button>
            )
          }
        />
        {!canManage && <Notice className="mb-4">Only administrators and owners can add, remove or change the role of members.</Notice>}
        {canManage && atLimit && (
          <Notice tone="warning" className="mb-4">
            All {maxMembers} seats on the {session.plan.name} plan are in use. Remove a member or change the plan before adding another.
          </Notice>
        )}
      </div>

      {query.isPending ? (
        <div className="px-5 pb-5 sm:px-6 sm:pb-6">
          <SkeletonRows rows={3} />
        </div>
      ) : query.isError ? (
        <div className="px-5 pb-5 sm:px-6 sm:pb-6">
          <ErrorState error={query.error} onRetry={() => query.refetch()} />
        </div>
      ) : query.data.length === 0 ? (
        <div className="px-5 pb-5 sm:px-6 sm:pb-6">
          <EmptyState icon={<Users className="size-6" />} title="No members yet" description="Add the people who buy for this organization." />
        </div>
      ) : (
        <Table>
          <thead>
            <tr>
              <Th>Name</Th>
              <Th>Email</Th>
              <Th>Role</Th>
              <Th>Joined</Th>
              <Th>Last sign-in</Th>
              {canManage && <Th align="right">Actions</Th>}
            </tr>
          </thead>
          <tbody>
            {query.data.map((member) => {
              // The server refuses changes to anyone ranked above the current user.
              const editable = canManage && !member.isYou && rank(member.role) <= myRank
              return (
                <tr key={member.userId}>
                  <Td>
                    <span className="flex flex-wrap items-center gap-2 font-medium text-navy-900">
                      {member.fullName}
                      {member.isYou && <Badge tone="blue">You</Badge>}
                      {!member.isActive && <Badge tone="red" title="This account has been deactivated and cannot sign in.">Deactivated</Badge>}
                    </span>
                  </Td>
                  <Td className="text-slate-600">{member.email}</Td>
                  <Td>
                    {editable ? (
                      <Select
                        aria-label={`Role for ${member.fullName}`}
                        className="w-52"
                        value={member.role}
                        disabled={changeRole.isPending}
                        onChange={(e) => changeRole.mutate({ member, role: e.target.value as Role })}
                      >
                        {assignable.map((role) => (
                          <option key={role} value={role}>
                            {ROLE_LABELS[role]}
                          </option>
                        ))}
                      </Select>
                    ) : (
                      <Badge tone={member.role === 'Owner' ? 'navy' : 'neutral'}>{ROLE_LABELS[member.role] ?? member.role}</Badge>
                    )}
                  </Td>
                  <Td className="text-slate-600">{formatDate(member.joinedAt)}</Td>
                  <Td className="text-slate-600">{member.lastLoginAt ? timeAgo(member.lastLoginAt) : 'Never signed in'}</Td>
                  {canManage && (
                    <Td align="right">
                      {editable ? (
                        <Button
                          size="sm"
                          variant="danger"
                          icon={<Trash2 className="size-4" aria-hidden />}
                          onClick={() => {
                            setRemoveError(null)
                            setRemoving(member)
                          }}
                        >
                          Remove
                        </Button>
                      ) : (
                        <span className="text-xs text-slate-400">{member.isYou ? 'Your own account' : 'Higher role than yours'}</span>
                      )}
                    </Td>
                  )}
                </tr>
              )
            })}
          </tbody>
        </Table>
      )}

      <AddMemberModal open={adding} onClose={() => setAdding(false)} roles={assignable} onAdded={refresh} />

      <ConfirmDialog
        open={!!removing}
        onClose={() => setRemoving(null)}
        onConfirm={() => removing && remove.mutate(removing)}
        title="Remove this member?"
        confirmLabel="Remove member"
        danger
        loading={remove.isPending}
      >
        <p>
          <strong className="text-navy-900">{removing?.fullName}</strong> ({removing?.email}) will lose access to this organization and be signed out of it. Their FindMyPrice account is not deleted, and the projects and
          purchase orders they created stay in place.
        </p>
        {removeError && (
          <Notice tone="error" className="mt-3">
            {removeError}
          </Notice>
        )}
      </ConfirmDialog>
    </Card>
  )
}

function AddMemberModal({ open, onClose, roles, onAdded }: { open: boolean; onClose: () => void; roles: Role[]; onAdded: () => void }) {
  const toast = useToast()
  const [email, setEmail] = useState('')
  const [fullName, setFullName] = useState('')
  const [role, setRole] = useState<Role>('Employee')
  const [temporaryPassword, setTemporaryPassword] = useState('')
  const [error, setError] = useState<string | null>(null)

  const selectedRole = roles.includes(role) ? role : roles[roles.length - 1]

  const close = () => {
    setEmail('')
    setFullName('')
    setRole('Employee')
    setTemporaryPassword('')
    setError(null)
    onClose()
  }

  const add = useMutation({
    mutationFn: () => api.post<Member>('/api/organization/members', { email: email.trim(), fullName: fullName.trim(), role: selectedRole, temporaryPassword: temporaryPassword || null }),
    onSuccess: (member) => {
      toast.success(`${member.fullName} was added as ${ROLE_LABELS[member.role] ?? member.role}.`)
      onAdded()
      close()
    },
    onError: (err) => setError(errorMessage(err)),
  })

  const submit = (event: FormEvent) => {
    event.preventDefault()
    setError(null)
    if (!email.trim() || !fullName.trim()) return setError('Enter the email address and full name.')
    add.mutate()
  }

  return (
    <Modal
      open={open}
      onClose={close}
      title="Add member"
      description="Give someone access to this organization."
      footer={
        <>
          <Button onClick={close}>Cancel</Button>
          <Button variant="primary" type="submit" form="add-member-form" loading={add.isPending}>
            Add member
          </Button>
        </>
      }
    >
      <form id="add-member-form" onSubmit={submit} className="space-y-4" noValidate>
        <Input label="Email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} maxLength={320} autoComplete="off" required />
        <Input label="Full name" value={fullName} onChange={(e) => setFullName(e.target.value)} maxLength={200} autoComplete="off" required />
        <Select label="Role" value={selectedRole} onChange={(e) => setRole(e.target.value as Role)} hint="You can grant roles up to your own.">
          {roles.map((r) => (
            <option key={r} value={r}>
              {ROLE_LABELS[r]}
            </option>
          ))}
        </Select>
        <Input
          label="Temporary password"
          type="text"
          value={temporaryPassword}
          onChange={(e) => setTemporaryPassword(e.target.value)}
          maxLength={200}
          autoComplete="off"
          hint="If this person has no FindMyPrice account yet, one is created with this temporary password for you to pass on. Email invitations are not sent."
        />
        {error && <Notice tone="error">{error}</Notice>}
      </form>
    </Modal>
  )
}

// ---------- Role reference ----------

function RoleReference() {
  return (
    <Card padded={false}>
      <div className="px-5 pt-5 sm:px-6 sm:pt-6">
        <CardHeader title="What each role can do" description="Each role includes everything the roles above it can do." />
      </div>
      <Table>
        <thead>
          <tr>
            <Th>Role</Th>
            <Th>Access</Th>
          </tr>
        </thead>
        <tbody>
          {ROLE_REFERENCE.map((row) => (
            <tr key={row.role}>
              <Td className="font-medium text-navy-900">{ROLE_LABELS[row.role]}</Td>
              <Td className="min-w-64 whitespace-normal text-slate-600">{row.can}</Td>
            </tr>
          ))}
        </tbody>
      </Table>
      <p className="px-5 py-4 text-xs text-slate-500 sm:px-6">
        Platform administration is separate from these roles. It is granted to FindMyPrice operators only, and an organization owner does not have it.
      </p>
    </Card>
  )
}
