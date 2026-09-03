import { useState, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { request } from '../api/client';
import { useAuth } from '../auth/AuthProvider';
import { canAdmin, isOwner, useClients, useMe } from '../api/hooks';
import type { UserRole } from '../api/auth';

type Channel = { id: string; displayName: string; platform: string; status: string; isHealthy: boolean; isEnabled: boolean; lastWebhookAt?: string | null };
type Member = { id: string; displayName: string; email: string; role: number | UserRole; isActive: boolean };
type Canned = { id: string; title: string; shortcut: string; content: string };
type Audit = { id: string; action: string; resource: string; createdAt: string };
type Workspace = { id: string; name: string; slug: string; retentionDays: number };

function useApi<T>(path: string, key: readonly unknown[], enabled = true) {
  const { token } = useAuth();
  return useQuery({ queryKey: key, enabled: enabled && Boolean(token), queryFn: () => request<T>(fetch, `/api/v1${path}`, { headers: { Authorization: `Bearer ${token}` }, credentials: 'include' }) });
}

const roleLabel = (value: number | UserRole) => typeof value === 'number' ? ['Owner', 'Admin', 'Agent'][value] ?? 'Agent' : value;

export function OverviewPage() {
  const [days, setDays] = useState(30);
  const metrics = useApi<{ conversationsOpened: number; openConversations: number; messagesInbound: number; messagesOutbound: number; notesCreated: number }>(`/metrics/overview?days=${days}`, ['metrics', days]);
  return <WorkspacePage title="Overview">
    <label>Window<select aria-label="Metrics window" value={days} onChange={(event) => setDays(Number(event.target.value))}><option value={7}>Last 7 days</option><option value={30}>Last 30 days</option><option value={90}>Last 90 days</option></select></label>
    <LoadState query={metrics}>{metrics.data && <div className="metric-grid">
      {[['Conversations opened', metrics.data.conversationsOpened], ['Open now', metrics.data.openConversations], ['Inbound messages', metrics.data.messagesInbound], ['Outbound messages', metrics.data.messagesOutbound], ['Internal notes', metrics.data.notesCreated]].map(([label, value]) => <article className="metric-card" key={String(label)}><span>{label}</span><strong>{value}</strong></article>)}
    </div>}</LoadState>
  </WorkspacePage>;
}

export function ChannelsPage() {
  const { admin } = useClients();
  const { user } = useMe();
  const queryClient = useQueryClient();
  const query = useQuery({ queryKey: ['channels'], queryFn: () => admin.channels() });
  const [displayName, setDisplayName] = useState('');
  const [wizard, setWizard] = useState<{ attemptId: string; state: string; expiresAt: string } | null>(null);
  const [complete, setComplete] = useState({ code: '', phoneNumberId: '', businessId: '' });
  const [result, setResult] = useState('');
  const [testingId, setTestingId] = useState<string | null>(null);

  const begin = async (event: FormEvent) => {
    event.preventDefault();
    setResult('');
    setWizard(await admin.beginConnect(displayName.trim()));
  };
  const finish = async (event: FormEvent) => {
    event.preventDefault();
    if (!wizard) return;
    setResult('');
    const channel = await admin.completeConnect({ state: wizard.state, code: complete.code.trim(), phoneNumberId: complete.phoneNumberId.trim(), businessId: complete.businessId.trim(), displayName: displayName.trim() || 'WhatsApp' });
    setWizard(null);
    setComplete({ code: '', phoneNumberId: '', businessId: '' });
    setResult(`Connected ${channel.displayName || channel.externalAccountId}.`);
    await queryClient.invalidateQueries({ queryKey: ['channels'] });
  };
  const test = async (id: string) => {
    setTestingId(id);
    setResult((await admin.testChannel(id)).detail);
    setTestingId(null);
    await queryClient.invalidateQueries({ queryKey: ['channels'] });
  };
  const toggle = async (id: string, enabled: boolean) => { await admin.setChannelEnabled(id, enabled); await queryClient.invalidateQueries({ queryKey: ['channels'] }); };
  const disconnect = async (id: string) => {
    if (!window.confirm('Disconnect this channel? Message history is retained.')) return;
    await admin.disconnectChannel(id);
    await queryClient.invalidateQueries({ queryKey: ['channels'] });
  };
  const reauthorize = async (id: string, name: string) => {
    setDisplayName(name);
    setResult('');
    setWizard(await admin.beginReauthorize(id));
  };

  return <WorkspacePage title="Channels">
    <LoadState query={query}>{query.data && <div className="channel-grid">{query.data.map((channel) => <ChannelCard key={channel.id} channel={channel} testing={testingId === channel.id} onTest={() => test(channel.id)} onToggle={(enabled) => toggle(channel.id, enabled)} onDisconnect={() => disconnect(channel.id)} onReauthorize={() => reauthorize(channel.id, channel.displayName)} />)}</div>}</LoadState>
    {canAdmin(user) && <section className="workspace-card"><h2>Connect a WhatsApp number</h2>
      {!wizard ? <form onSubmit={begin}><label>Display name<input aria-label="Channel display name" value={displayName} onChange={(event) => setDisplayName(event.target.value)} required /></label><button>Start Embedded Signup</button></form>
        : <form onSubmit={finish}><p role="status">Complete Meta Embedded Signup in a popup, then paste the authorization code, phone number ID, and business ID here. This attempt expires at {new Date(wizard.expiresAt).toLocaleTimeString()}.</p>
          <label>Authorization code<input aria-label="Authorization code" value={complete.code} onChange={(event) => setComplete({ ...complete, code: event.target.value })} required /></label>
          <label>Phone number ID<input aria-label="Phone number ID" value={complete.phoneNumberId} onChange={(event) => setComplete({ ...complete, phoneNumberId: event.target.value })} required /></label>
          <label>Business ID<input aria-label="Business ID" value={complete.businessId} onChange={(event) => setComplete({ ...complete, businessId: event.target.value })} required /></label>
          <button>Complete connection</button></form>}
    </section>}
    {result && <p role="status">{result}</p>}
  </WorkspacePage>;
}

function ChannelCard({ channel, testing, onTest, onToggle, onDisconnect, onReauthorize }: { channel: Channel; testing: boolean; onTest(): void; onToggle(enabled: boolean): void; onDisconnect(): void; onReauthorize(): void }) {
  const { admin } = useClients();
  const [healthOpen, setHealthOpen] = useState(false);
  const health = useQuery({ queryKey: ['channel-health', channel.id], queryFn: () => admin.channelHealth(channel.id), enabled: healthOpen });
  return <article className="workspace-card">
    <h2>{channel.displayName || channel.platform}</h2>
    <p><span className="health-dot" /> {channel.status}{channel.isHealthy ? '' : ' · unhealthy'}{channel.isEnabled ? '' : ' · disabled'}</p>
    <small>{channel.lastWebhookAt ? `Last webhook ${new Date(channel.lastWebhookAt).toLocaleString()}` : 'Waiting for the first webhook'}</small>
    <div>
      <button onClick={onTest} disabled={testing}>{testing ? 'Testing…' : 'Test connection'}</button>
      <button onClick={() => onToggle(!channel.isEnabled)}>{channel.isEnabled ? 'Disable' : 'Enable'}</button>
      <button onClick={onReauthorize}>Repair access</button>
      <button onClick={() => setHealthOpen((open) => !open)}>{healthOpen ? 'Hide health' : 'Health history'}</button>
      <button onClick={onDisconnect}>Disconnect</button>
    </div>
    {healthOpen && (health.isPending ? <p role="status">Loading…</p> : <ul>{health.data?.map((entry) => <li key={entry.id}>{new Date(entry.createdAt).toLocaleString()} — {entry.isHealthy ? 'healthy' : `unhealthy: ${entry.reason}`}</li>)}</ul>)}
  </article>;
}

export function TeamPage() {
  const { admin } = useClients();
  const { user } = useMe();
  const queryClient = useQueryClient();
  const members = useQuery({ queryKey: ['users'], queryFn: () => admin.users() });
  const invites = useQuery({ queryKey: ['invitations'], queryFn: () => admin.invitations(), enabled: canAdmin(user) });
  const [email, setEmail] = useState('');
  const [role, setRole] = useState<UserRole>('Agent');
  const [notice, setNotice] = useState('');

  const refresh = () => { void queryClient.invalidateQueries({ queryKey: ['users'] }); void queryClient.invalidateQueries({ queryKey: ['invitations'] }); };
  const invite = async (event: FormEvent) => {
    event.preventDefault();
    setNotice('');
    await admin.invite(email.trim(), role);
    setEmail('');
    setNotice(`Invitation sent to ${email.trim()}.`);
    refresh();
  };
  const changeRole = async (id: string, next: UserRole) => { await admin.setRole(id, next); refresh(); };
  const changeActive = async (id: string, active: boolean) => { await admin.setActive(id, active); refresh(); };
  const revoke = async (id: string) => { await admin.revokeInvitation(id); refresh(); };

  return <WorkspacePage title="Team">
    {canAdmin(user) && <form className="workspace-card" onSubmit={invite}><h2>Invite a member</h2>
      <label>Email<input aria-label="Invite email" type="email" value={email} onChange={(event) => setEmail(event.target.value)} required /></label>
      <label>Role<select aria-label="Invite role" value={role} onChange={(event) => setRole(event.target.value as UserRole)}><option value="Agent">Agent</option><option value="Admin">Admin</option>{isOwner(user) && <option value="Owner">Owner</option>}</select></label>
      <button>Send invitation</button></form>}
    {notice && <p role="status">{notice}</p>}
    <LoadState query={members}>{members.data && <table><thead><tr><th>Member</th><th>Role</th><th>Status</th>{canAdmin(user) && <th>Actions</th>}</tr></thead><tbody>
      {members.data.map((member) => <tr key={member.id}><td><strong>{member.displayName}</strong><br /><small>{member.email}</small></td>
        <td>{isOwner(user) && member.id !== user?.id ? <select aria-label={`Role for ${member.email}`} value={roleLabel(member.role)} onChange={(event) => changeRole(member.id, event.target.value as UserRole)}><option value="Owner">Owner</option><option value="Admin">Admin</option><option value="Agent">Agent</option></select> : roleLabel(member.role)}</td>
        <td>{member.isActive ? 'Active' : 'Disabled'}</td>
        {canAdmin(user) && <td>{member.id !== user?.id && <button onClick={() => changeActive(member.id, !member.isActive)}>{member.isActive ? 'Deactivate' : 'Reactivate'}</button>}</td>}</tr>)}
    </tbody></table>}</LoadState>
    {canAdmin(user) && <section><h2>Pending invitations</h2><LoadState query={invites}>{invites.data && (invites.data.length === 0 ? <p>No pending invitations.</p> : <ul className="local-list">{invites.data.map((invitation) => <li key={invitation.id}><span>{invitation.email} · {roleLabel(invitation.role)} · expires {new Date(invitation.expiresAt).toLocaleString()}</span><button onClick={() => revoke(invitation.id)}>Revoke</button></li>)}</ul>)}</LoadState></section>}
  </WorkspacePage>;
}

export function CannedPage() {
  const [search, setSearch] = useState(''); const { token } = useAuth(); const client = useQueryClient();
  const query = useApi<Canned[]>(`/canned-responses${search ? `?q=${encodeURIComponent(search)}` : ''}`, ['canned', search]);
  const [editing, setEditing] = useState<Canned | null>(null);
  const create = useMutation({ mutationFn: (data: Omit<Canned, 'id'>) => request<Canned>(fetch, '/api/v1/canned-responses', { method: 'POST', headers: { Authorization: `Bearer ${token}` }, body: data }), onSuccess: () => client.invalidateQueries({ queryKey: ['canned'] }) });
  const update = useMutation({ mutationFn: (data: Canned) => request<Canned>(fetch, `/api/v1/canned-responses/${data.id}`, { method: 'PUT', headers: { Authorization: `Bearer ${token}` }, body: data }), onSuccess: () => { setEditing(null); client.invalidateQueries({ queryKey: ['canned'] }); } });
  const remove = useMutation({ mutationFn: (id: string) => request<void>(fetch, `/api/v1/canned-responses/${id}`, { method: 'DELETE', headers: { Authorization: `Bearer ${token}` } }), onSuccess: () => client.invalidateQueries({ queryKey: ['canned'] }) });
  const submit = (event: FormEvent<HTMLFormElement>) => { event.preventDefault(); const data = new FormData(event.currentTarget); create.mutate({ title: String(data.get('title')), shortcut: String(data.get('shortcut')), content: String(data.get('content')) }); event.currentTarget.reset(); };
  return <WorkspacePage title="Canned Responses"><label>Search responses<input aria-label="Search responses" value={search} onChange={event => setSearch(event.target.value)} /></label>
    <form className="workspace-card" onSubmit={submit}><h2>New response</h2><input name="title" aria-label="Title" required placeholder="Welcome" /><input name="shortcut" aria-label="Shortcut" required placeholder="/welcome" /><textarea name="content" aria-label="Content" required /><button disabled={create.isPending}>Create response</button></form>
    <LoadState query={query}>{query.data && <ul className="local-list">{query.data.map(item => <li key={item.id}>
      {editing?.id === item.id
        ? <CannedEditor item={editing} onChange={setEditing} onSave={() => update.mutate(editing)} onCancel={() => setEditing(null)} saving={update.isPending} />
        : <span><strong>{item.title}</strong> <small>{item.shortcut}</small><br />{item.content}</span>}
      {editing?.id !== item.id && <span><button onClick={() => setEditing(item)}>Edit</button><button onClick={() => remove.mutate(item.id)}>Delete</button></span>}
    </li>)}</ul>}</LoadState></WorkspacePage>;
}

function CannedEditor({ item, onChange, onSave, onCancel, saving }: { item: Canned; onChange(item: Canned): void; onSave(): void; onCancel(): void; saving: boolean }) {
  return <span><input aria-label="Edit title" value={item.title} onChange={(event) => onChange({ ...item, title: event.target.value })} /><input aria-label="Edit shortcut" value={item.shortcut} onChange={(event) => onChange({ ...item, shortcut: event.target.value })} /><textarea aria-label="Edit content" value={item.content} onChange={(event) => onChange({ ...item, content: event.target.value })} /><button onClick={onSave} disabled={saving}>Save</button><button onClick={onCancel}>Cancel</button></span>;
}

export function AuditPage() {
  const [search, setSearch] = useState(''); const { token } = useAuth();
  const query = useApi<Audit[]>(`/audit-logs${search ? `?q=${encodeURIComponent(search)}` : ''}`, ['audit', search]);
  return <WorkspacePage title="Audit Log"><label>Search events<input aria-label="Search events" value={search} onChange={event => setSearch(event.target.value)} /></label>
    <p><a href={`/api/v1/audit-logs/export${search ? `?q=${encodeURIComponent(search)}` : ''}`} onClick={(event) => { event.preventDefault(); void downloadCsv(token, search); }}>Export CSV</a></p>
    <LoadState query={query}>{query.data && <table><thead><tr><th>When</th><th>Action</th><th>Resource</th></tr></thead><tbody>{query.data.map(item => <tr key={item.id}><td>{new Date(item.createdAt).toLocaleString()}</td><td>{item.action}</td><td>{item.resource}</td></tr>)}</tbody></table>}</LoadState></WorkspacePage>;
}

async function downloadCsv(token: string | null, search: string) {
  const response = await fetch(`/api/v1/audit-logs/export${search ? `?q=${encodeURIComponent(search)}` : ''}`, { headers: token ? { Authorization: `Bearer ${token}` } : {} });
  const blob = await response.blob();
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url; link.download = 'audit-logs.csv'; link.click();
  URL.revokeObjectURL(url);
}

export function SettingsPage() {
  const query = useApi<Workspace>('/workspace', ['workspace']); const { token } = useAuth(); const client = useQueryClient();
  const save = useMutation({ mutationFn: (data: Pick<Workspace, 'name' | 'retentionDays'>) => request<Workspace>(fetch, '/api/v1/workspace', { method: 'PUT', headers: { Authorization: `Bearer ${token}` }, body: data }), onSuccess: data => client.setQueryData(['workspace'], data) });
  const submit = (event: FormEvent<HTMLFormElement>) => { event.preventDefault(); const data = new FormData(event.currentTarget); save.mutate({ name: String(data.get('name')), retentionDays: Number(data.get('retentionDays')) }); };
  return <WorkspacePage title="Workspace Settings"><LoadState query={query}>{query.data && <form className="workspace-card" onSubmit={submit}><h2>Workspace profile</h2><label>Workspace name<input name="name" defaultValue={query.data.name} /></label><label>Retention days<input name="retentionDays" type="number" min="30" max="3650" defaultValue={query.data.retentionDays} /></label><button disabled={save.isPending}>Save settings</button>{save.isSuccess && <p role="status">Settings saved.</p>}</form>}</LoadState></WorkspacePage>;
}

export function NotificationsPage() {
  const { admin } = useClients();
  const queryClient = useQueryClient();
  const [unreadOnly, setUnreadOnly] = useState(false);
  const list = useQuery({ queryKey: ['notifications', unreadOnly], queryFn: () => admin.notifications(unreadOnly) });
  const prefs = useQuery({ queryKey: ['notification-preferences'], queryFn: () => admin.preferences() });
  const refresh = () => { void queryClient.invalidateQueries({ queryKey: ['notifications'] }); void queryClient.invalidateQueries({ queryKey: ['unread-count'] }); };
  const read = async (id: string) => { await admin.markNotificationRead(id); refresh(); };
  const readAll = async () => { await admin.markAllNotificationsRead(); refresh(); };
  const togglePref = async (kind: string, enabled: boolean) => { await admin.setPreference(kind, enabled); await queryClient.invalidateQueries({ queryKey: ['notification-preferences'] }); };
  return <WorkspacePage title="Notifications">
    <label><input type="checkbox" checked={unreadOnly} onChange={(event) => setUnreadOnly(event.target.checked)} /> Unread only</label>
    <button onClick={readAll}>Mark all read</button>
    <LoadState query={list}>{list.data && (list.data.length === 0 ? <p>No notifications.</p> : <ul className="local-list">{list.data.map((item) => <li key={item.id}><span><strong>{item.type}</strong><br />{item.text}<br /><small>{new Date(item.createdAt).toLocaleString()}</small></span>{!item.isRead && <button onClick={() => read(item.id)}>Mark read</button>}</li>)}</ul>)}</LoadState>
    <section><h2>Preferences</h2><LoadState query={prefs}>{prefs.data && <ul className="local-list">{['message.received', 'message.failed', 'channel.unhealthy', 'invitation.created'].map((kind) => {
      const current = prefs.data.find((pref) => pref.kind === kind);
      return <li key={kind}><span>{kind}</span><button onClick={() => togglePref(kind, !(current?.enabled ?? true))}>{current?.enabled === false ? 'Enable' : 'Disable'}</button></li>;
    })}</ul>}</LoadState></section>
  </WorkspacePage>;
}

export function LoadState({ query, children }: { query: { isPending: boolean; isError: boolean }; children: React.ReactNode }) { if (query.isPending) return <p role="status">Loading…</p>; if (query.isError) return <p role="alert">This workspace data could not be loaded.</p>; return children; }
export function WorkspacePage({ title, children }: { title: string; children: React.ReactNode }) { return <section className="workspace-page"><p className="eyebrow">Workspace</p><h1>{title}</h1>{children}</section>; }
