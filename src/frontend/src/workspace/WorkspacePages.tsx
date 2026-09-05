import { useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { request } from '../api/client';
import { useAuth } from '../auth/AuthProvider';
import { canAdmin, isOwner, useClients, useMe } from '../api/hooks';
import type { UserRole } from '../api/auth';
import type { ConnectionAttempt } from '../api/admin';
import { EmbeddedSignupButton } from '../channels/EmbeddedSignupButton';
import { useAction } from '../components/useAction';
import { Dialog } from '../components/Dialog';

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
const eventLabels: Record<string, string> = { 'message.received': 'New customer messages', 'message.failed': 'Message delivery failures', 'channel.unhealthy': 'Channel connection issues', 'invitation.created': 'Team invitations', 'auth.login.succeeded': 'Signed in', 'auth.email.verified': 'Email verified', 'tenant.registered': 'Workspace created', 'canned-response.created': 'Saved response created' };
const eventLabel = (kind: string) => eventLabels[kind] ?? kind.replace(/[._-]/g, ' ').replace(/^./, c => c.toUpperCase());

const preferenceHelp: Record<string, string> = {
  'message.received': 'Alert when a customer message arrives.',
  'message.failed': 'Alert when an outbound message fails to deliver.',
  'channel.unhealthy': 'Alert when a channel connection has issues.',
  'invitation.created': 'Alert when a teammate is invited.',
};

function timeAgo(iso: string): string {
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return '';
  const seconds = Math.max(0, Math.floor((Date.now() - then) / 1000));
  if (seconds < 60) return 'Just now';
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  if (days === 1) return 'Yesterday';
  if (days < 7) return `${days}d ago`;
  return new Date(then).toLocaleDateString();
}

function useConfirmation() {
  const [confirmation, setConfirmation] = useState<{ title: string; description: string; action(): void } | null>(null);
  return { ask: (title: string, description: string, action: () => void) => setConfirmation({ title, description, action }), dialog: confirmation && <Dialog title={confirmation.title} onClose={() => setConfirmation(null)}><p>{confirmation.description}</p><div className="button-row"><button onClick={() => setConfirmation(null)}>Cancel</button><button className="danger" onClick={() => { confirmation.action(); setConfirmation(null); }}>Confirm</button></div></Dialog> };
}

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
  const [wizard, setWizard] = useState<ConnectionAttempt | null>(null);
  const [result, setResult] = useState('');
  const [error, setError] = useState('');
  const [testingId, setTestingId] = useState<string | null>(null);
  const [completing, setCompleting] = useState(false);
  const [connectOpen, setConnectOpen] = useState(false);
  const action = useAction();
  const confirmation = useConfirmation();

  const begin = async (event: FormEvent) => {
    event.preventDefault();
    setResult('');
    setError('');
    await action.run(async () => { try {
      setWizard(await admin.beginConnect(displayName.trim()));
    } catch (err) {
      setError(err instanceof Error ? err.message : 'The connection attempt could not be started.');
    } });
  };
  const finish = async (session: { code: string; phoneNumberId: string; businessId: string }) => {
    if (!wizard || completing) return;
    setError('');
    setCompleting(true);
    try {
      const channel = await admin.completeConnect({ state: wizard.state, nonce: wizard.nonce, code: session.code, phoneNumberId: session.phoneNumberId, businessId: session.businessId, displayName: displayName.trim() || 'WhatsApp' });
      setWizard(null);
      setResult(`Connected ${channel.displayName || channel.externalAccountId}.`);
      await queryClient.invalidateQueries({ queryKey: ['channels'] });
    } catch (err) {
      setWizard(null);
      setError(err instanceof Error ? err.message : 'The connection could not be completed. Try again.');
    } finally {
      setCompleting(false);
      setConnectOpen(false);
    }
  };
  const test = async (id: string) => {
    setTestingId(id);
    try { await action.run(async () => { setResult((await admin.testChannel(id)).detail); await queryClient.invalidateQueries({ queryKey: ['channels'] }); }); }
    finally { setTestingId(null); }
  };
  const toggle = async (id: string, enabled: boolean) => { await action.run(async () => { await admin.setChannelEnabled(id, enabled); await queryClient.invalidateQueries({ queryKey: ['channels'] }); }, 'Channel updated.'); };
  const disconnect = async (id: string) => {
    confirmation.ask('Disconnect channel?', `Disconnect ${query.data?.find(channel => channel.id === id)?.displayName || 'this channel'}? Message history is retained.`, () => { void action.run(async () => { await admin.disconnectChannel(id); await queryClient.invalidateQueries({ queryKey: ['channels'] }); }, 'Channel disconnected.'); });
  };

  return <WorkspacePage title="Channels" actions={canAdmin(user) && <button className="primary" onClick={() => { setWizard(null); setConnectOpen(true); }}>Connect channel</button>}>
    {action.feedback}{confirmation.dialog}
    <LoadState query={query}>{query.data && (query.data.length ? <div className="channel-grid">{query.data.map((channel) => <ChannelCard key={channel.id} channel={channel} testing={testingId === channel.id} busy={action.pending} onTest={() => test(channel.id)} onToggle={(enabled) => toggle(channel.id, enabled)} onDisconnect={() => disconnect(channel.id)} />)}</div> : <div className="empty-state"><h2>Connect your first channel</h2><p>{canAdmin(user) ? 'Choose Connect channel to add a WhatsApp number and start receiving customer messages.' : 'No channels are connected yet. Ask an admin to connect a WhatsApp number.'}</p></div>)}</LoadState>
    {canAdmin(user) && connectOpen && <Dialog title="Connect a WhatsApp number" onClose={() => setConnectOpen(false)}>
      {!wizard
        ? <form className="form-stack" onSubmit={begin}><p>Connect securely through Meta. Your team will receive messages in the shared inbox.</p><label>Display name<input aria-label="Channel display name" value={displayName} onChange={(event) => setDisplayName(event.target.value)} required autoFocus placeholder="e.g. Customer support" /></label><div className="button-row"><button type="button" onClick={() => setConnectOpen(false)}>Cancel</button><button type="submit" disabled={action.pending}>{action.pending ? 'Starting connection…' : 'Start Embedded Signup'}</button></div></form>
        : <div>
            <p role="status">Complete Meta Embedded Signup in the popup. This attempt expires at {new Date(wizard.expiresAt).toLocaleTimeString()}.</p>
            {completing && <p role="status">Completing connection…</p>}
            <EmbeddedSignupButton attempt={wizard} onSession={(session) => void finish(session)} onError={setError} />
          </div>}
    </Dialog>}
    {result && <p role="status">{result}</p>}
    {error && <p role="alert">{error}</p>}
  </WorkspacePage>;
}

function ChannelCard({ channel, testing, busy, onTest, onToggle, onDisconnect }: { channel: Channel; testing: boolean; busy: boolean; onTest(): void; onToggle(enabled: boolean): void; onDisconnect(): void }) {
  const { admin } = useClients();
  const navigate = useNavigate();
  const [healthOpen, setHealthOpen] = useState(false);
  const health = useQuery({ queryKey: ['channel-health', channel.id], queryFn: () => admin.channelHealth(channel.id), enabled: healthOpen });
  const statusBadge = !channel.isEnabled ? 'is-disabled' : !channel.isHealthy ? 'is-unhealthy' : 'is-active';
  const statusText = `${channel.status}${channel.isHealthy ? '' : ' · unhealthy'}${channel.isEnabled ? '' : ' · disabled'}`;
  return <article className="workspace-card">
    <h2>{channel.displayName || channel.platform}</h2>
    <p><span className={`badge ${statusBadge}`}>{statusText}</span></p>
    <small>{channel.lastWebhookAt ? `Last webhook ${new Date(channel.lastWebhookAt).toLocaleString()}` : 'Waiting for the first webhook'}</small>
    <div className="button-row">
      <button onClick={onTest} disabled={busy}>{testing ? 'Testing…' : 'Test connection'}</button>
      <button disabled={busy} onClick={() => onToggle(!channel.isEnabled)}>{channel.isEnabled ? 'Disable' : 'Enable'}</button>
      <button onClick={() => navigate(`/channels/${channel.id}/repair`)}>Repair access</button>
      <button onClick={() => setHealthOpen((open) => !open)}>{healthOpen ? 'Hide health' : 'Health history'}</button>
      <button className="danger" disabled={busy} onClick={onDisconnect}>Disconnect</button>
    </div>
    {healthOpen && <LoadState query={health}>{health.data?.length ? <ul>{health.data.map((entry) => <li key={entry.id}>{new Date(entry.createdAt).toLocaleString()} — {entry.isHealthy ? 'healthy' : `unhealthy: ${entry.reason}`}</li>)}</ul> : <p>No health checks recorded yet.</p>}</LoadState>}
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
  const [inviteOpen, setInviteOpen] = useState(false);
  const action = useAction();
  const confirmation = useConfirmation();

  const refresh = () => { void queryClient.invalidateQueries({ queryKey: ['users'] }); void queryClient.invalidateQueries({ queryKey: ['invitations'] }); };
  const invite = async (event: FormEvent) => {
    event.preventDefault();
    setNotice('');
    await action.run(async () => { await admin.invite(email.trim(), role); setEmail(''); setNotice(`Invitation sent to ${email.trim()}.`); refresh(); setInviteOpen(false); });
  };
  const changeRole = async (id: string, next: UserRole) => { await action.run(async () => { await admin.setRole(id, next); refresh(); }, 'Role updated.'); };
  const changeActive = async (id: string, active: boolean) => { const perform = () => { void action.run(async () => { await admin.setActive(id, active); refresh(); }, 'Member access updated.'); }; if (active) perform(); else confirmation.ask('Deactivate member?', `${members.data?.find(member => member.id === id)?.email ?? 'This member'} will lose access to the workspace.`, perform); };
  const revoke = async (id: string) => { confirmation.ask('Revoke invitation?', `The invitation for ${invites.data?.find(invite => invite.id === id)?.email ?? 'this member'} will no longer work.`, () => { void action.run(async () => { await admin.revokeInvitation(id); refresh(); }, 'Invitation revoked.'); }); };

  return <WorkspacePage title="Team" actions={canAdmin(user) && <button className="primary" onClick={() => setInviteOpen(true)}>Invite member</button>}>
    {confirmation.dialog}
    {canAdmin(user) && inviteOpen && <Dialog title="Invite a member" onClose={() => setInviteOpen(false)}>
      <form className="form-stack" onSubmit={invite}>
        <label>Email<input aria-label="Invite email" type="email" value={email} onChange={(event) => setEmail(event.target.value)} required autoFocus aria-describedby={action.error ? 'team-error' : undefined} /></label>
        <label>Role<select aria-label="Invite role" value={role} onChange={(event) => setRole(event.target.value as UserRole)}><option value="Agent">Agent</option><option value="Admin">Admin</option>{isOwner(user) && <option value="Owner">Owner</option>}</select></label>
        <p className="field-help">Agents respond to customers. Admins also manage channels and workspace settings.</p>
        <div className="button-row"><button type="button" onClick={() => setInviteOpen(false)}>Cancel</button><button type="submit" disabled={action.pending}>{action.pending ? 'Working…' : 'Send invitation'}</button></div>
        <div id="team-error">{action.feedback}</div>
      </form>
    </Dialog>}
    {notice && <p role="status">{notice}</p>}
    <LoadState query={members}>{members.data && <div className="table-wrap" role="region" aria-label="Team members" tabIndex={0}><table><thead><tr><th>Member</th><th>Role</th><th>Status</th>{canAdmin(user) && <th>Actions</th>}</tr></thead><tbody>
      {members.data.map((member) => <tr key={member.id}><td><strong>{member.displayName}</strong><br /><small>{member.email}</small></td>
        <td>{isOwner(user) && member.id !== user?.id ? <select disabled={action.pending} aria-label={`Role for ${member.email}`} value={roleLabel(member.role)} onChange={(event) => changeRole(member.id, event.target.value as UserRole)}><option value="Owner">Owner</option><option value="Admin">Admin</option><option value="Agent">Agent</option></select> : roleLabel(member.role)}</td>
        <td><span className={member.isActive ? 'badge is-active' : 'badge is-disabled'}>{member.isActive ? 'Active' : 'Disabled'}</span></td>
        {canAdmin(user) && <td>{member.id !== user?.id && <button disabled={action.pending} className={member.isActive ? 'danger' : ''} onClick={() => changeActive(member.id, !member.isActive)}>{member.isActive ? 'Deactivate' : 'Reactivate'}</button>}</td>}</tr>)}
    </tbody></table></div>}</LoadState>
    {canAdmin(user) && <section><h2>Pending invitations</h2><LoadState query={invites}>{invites.data && (invites.data.length === 0 ? <p>No pending invitations.</p> : <ul className="local-list">{invites.data.map((invitation) => <li key={invitation.id}><span>{invitation.email} · {roleLabel(invitation.role)} · expires {new Date(invitation.expiresAt).toLocaleString()}</span><button disabled={action.pending} onClick={() => revoke(invitation.id)}>Revoke</button></li>)}</ul>)}</LoadState></section>}
  </WorkspacePage>;
}

export function CannedPage() {
  const [search, setSearch] = useState(''); const { token } = useAuth(); const client = useQueryClient();
  const query = useApi<Canned[]>(`/canned-responses${search ? `?q=${encodeURIComponent(search)}` : ''}`, ['canned', search]);
  const [editing, setEditing] = useState<Canned | null>(null);
  const [createOpen, setCreateOpen] = useState(false);
  const confirmation = useConfirmation();
  const create = useMutation({ mutationFn: (data: Omit<Canned, 'id'>) => request<Canned>(fetch, '/api/v1/canned-responses', { method: 'POST', headers: { Authorization: `Bearer ${token}` }, body: data }), onSuccess: () => client.invalidateQueries({ queryKey: ['canned'] }) });
  const update = useMutation({ mutationFn: (data: Canned) => request<Canned>(fetch, `/api/v1/canned-responses/${data.id}`, { method: 'PUT', headers: { Authorization: `Bearer ${token}` }, body: data }), onSuccess: () => { setEditing(null); client.invalidateQueries({ queryKey: ['canned'] }); } });
  const remove = useMutation({ mutationFn: (id: string) => request<void>(fetch, `/api/v1/canned-responses/${id}`, { method: 'DELETE', headers: { Authorization: `Bearer ${token}` } }), onSuccess: () => client.invalidateQueries({ queryKey: ['canned'] }) });
  const submit = (event: FormEvent<HTMLFormElement>) => { event.preventDefault(); const form = event.currentTarget; const data = new FormData(form); create.mutate({ title: String(data.get('title')), shortcut: String(data.get('shortcut')), content: String(data.get('content')) }, { onSuccess: () => { form.reset(); setCreateOpen(false); } }); };
  return <WorkspacePage title="Canned Responses" actions={<button className="primary" onClick={() => { create.reset(); setCreateOpen(true); }}>New response</button>}><label>Search responses<input aria-label="Search responses" value={search} onChange={event => setSearch(event.target.value)} /></label>
    {confirmation.dialog}
    {createOpen && <Dialog title="New response" onClose={() => setCreateOpen(false)}>
      <form className="form-stack" onSubmit={submit}><div className="form-grid"><label>Title<input name="title" aria-label="Title" required autoFocus placeholder="Welcome" /></label><label>Shortcut<input name="shortcut" aria-label="Shortcut" required placeholder="/welcome" /></label></div><label>Message content<textarea name="content" aria-label="Content" required placeholder="Write a message your team can reuse…" /></label><p className="field-help">Saved responses are available to your team in the message composer.</p><div className="button-row"><button type="button" onClick={() => setCreateOpen(false)}>Cancel</button><button type="submit" disabled={create.isPending}>{create.isPending ? 'Creating…' : 'Create response'}</button></div>{create.isError && <p role="alert">{create.error.message}</p>}</form>
    </Dialog>}
    {create.isSuccess && <p role="status">Response created.</p>}
    {update.isError && <p role="alert">{update.error.message}</p>}{remove.isError && <p role="alert">{remove.error.message}</p>}
    {remove.isSuccess && <p role="status">Response deleted.</p>}
    {query.data?.length === 0 && <div className="empty-state"><h2>{search ? 'No matching responses' : 'No saved responses yet'}</h2><p>{search ? 'Try a different search.' : 'Create your first reusable reply above.'}</p></div>}
    <LoadState query={query}>{query.data && <ul className="local-list">{query.data.map(item => <li key={item.id}>
      {editing?.id === item.id
        ? <CannedEditor item={editing} onChange={setEditing} onSave={() => update.mutate(editing)} onCancel={() => setEditing(null)} saving={update.isPending} />
        : <span><strong>{item.title}</strong> <small>{item.shortcut}</small><br />{item.content}</span>}
      {editing?.id !== item.id && <span className="button-row"><button onClick={() => { update.reset(); setEditing(item); }}>Edit</button><button className="danger" disabled={remove.isPending} onClick={() => confirmation.ask(`Delete ${item.title}?`, 'This response will be removed from the team composer. Existing messages will not change.', () => remove.mutate(item.id))}>Delete</button></span>}
    </li>)}</ul>}</LoadState></WorkspacePage>;
}

function CannedEditor({ item, onChange, onSave, onCancel, saving }: { item: Canned; onChange(item: Canned): void; onSave(): void; onCancel(): void; saving: boolean }) {
  return <form className="form-stack" onSubmit={event => { event.preventDefault(); onSave(); }}><label>Title<input required aria-label="Edit title" value={item.title} onChange={(event) => onChange({ ...item, title: event.target.value })} /></label><label>Shortcut<input required aria-label="Edit shortcut" value={item.shortcut} onChange={(event) => onChange({ ...item, shortcut: event.target.value })} /></label><label>Message content<textarea required aria-label="Edit content" value={item.content} onChange={(event) => onChange({ ...item, content: event.target.value })} /></label><div className="button-row"><button type="submit" disabled={saving}>{saving ? 'Saving…' : 'Save'}</button><button type="button" onClick={onCancel}>Cancel</button></div></form>;
}

export function AuditPage() {
  const [search, setSearch] = useState(''); const { token } = useAuth();
  const action = useAction();
  const query = useApi<Audit[]>(`/audit-logs${search ? `?q=${encodeURIComponent(search)}` : ''}`, ['audit', search]);
  return <WorkspacePage title="Audit Log"><label>Search events<input aria-label="Search events" value={search} onChange={event => setSearch(event.target.value)} /></label>
    <button disabled={action.pending} onClick={() => action.run(() => downloadCsv(token, search))}>{action.pending ? 'Exporting…' : 'Export CSV'}</button>{action.feedback}
    <LoadState query={query}>{query.data && (query.data.length ? <div className="table-wrap" role="region" aria-label="Audit events" tabIndex={0}><table><thead><tr><th>When</th><th>Action</th><th>Resource</th></tr></thead><tbody>{query.data.map(item => <tr key={item.id}><td>{new Date(item.createdAt).toLocaleString()}</td><td title={item.action}>{eventLabel(item.action)}</td><td className="resource-id">{item.resource}</td></tr>)}</tbody></table></div> : <div className="empty-state"><h2>No events found</h2><p>{search ? 'Try a different search.' : 'Workspace activity will be recorded here.'}</p></div>)}</LoadState></WorkspacePage>;
}

async function downloadCsv(token: string | null, search: string) {
  const response = await fetch(`/api/v1/audit-logs/export${search ? `?q=${encodeURIComponent(search)}` : ''}`, { headers: token ? { Authorization: `Bearer ${token}` } : {} });
  if (!response.ok) throw new Error('Audit export could not be downloaded. Please try again.');
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
  return <WorkspacePage title="Workspace Settings"><LoadState query={query}>{query.data && <form className="workspace-card" onSubmit={submit}><h2>Workspace profile</h2><label>Workspace name<input name="name" defaultValue={query.data.name} /></label><label>Retention days<input name="retentionDays" type="number" min="30" max="3650" defaultValue={query.data.retentionDays} /><span className="field-help">Choose a retention period between 30 and 3,650 days.</span></label><button type="submit" disabled={save.isPending}>{save.isPending ? 'Saving…' : 'Save settings'}</button>{save.isSuccess && <p role="status">Settings saved.</p>}{save.isError && <p role="alert">{save.error.message}</p>}</form>}</LoadState></WorkspacePage>;
}

export function NotificationsPage() {
  const { admin } = useClients();
  const queryClient = useQueryClient();
  const [unreadOnly, setUnreadOnly] = useState(false);
  const action = useAction();
  const list = useQuery({ queryKey: ['notifications', unreadOnly], queryFn: () => admin.notifications(unreadOnly) });
  const prefs = useQuery({ queryKey: ['notification-preferences'], queryFn: () => admin.preferences() });
  const refresh = () => { void queryClient.invalidateQueries({ queryKey: ['notifications'] }); void queryClient.invalidateQueries({ queryKey: ['unread-count'] }); };
  const read = async (id: string) => { await action.run(async () => { await admin.markNotificationRead(id); refresh(); }); };
  const readAll = async () => { await action.run(async () => { await admin.markAllNotificationsRead(); refresh(); }, 'All notifications marked as read.'); };
  const togglePref = async (kind: string, enabled: boolean) => { await action.run(async () => { await admin.setPreference(kind, enabled); await queryClient.invalidateQueries({ queryKey: ['notification-preferences'] }); }, 'Notification preference updated.'); };
  const hasUnread = list.data?.some((item) => !item.isRead) ?? false;
  return <WorkspacePage title="Notifications" actions={<button className="primary" disabled={action.pending || !hasUnread} onClick={readAll}>{action.pending ? 'Working…' : 'Mark all read'}</button>}>
    <div className="segmented" role="group" aria-label="Notification filter">
      <button aria-pressed={!unreadOnly} className={!unreadOnly ? 'active' : ''} onClick={() => setUnreadOnly(false)}>All</button>
      <button aria-pressed={unreadOnly} className={unreadOnly ? 'active' : ''} onClick={() => setUnreadOnly(true)}>Unread</button>
    </div>
    {action.feedback}
    <LoadState query={list}>{list.data && (list.data.length === 0 ? <div className="empty-state"><h2>{unreadOnly ? 'No unread notifications' : "You're all caught up"}</h2><p>{unreadOnly ? 'Everything here has been read.' : 'No notifications.'}</p></div> : <ul className="local-list notification-list">{list.data.map((item) => <li key={item.id} className={item.isRead ? '' : 'is-unread'}><span className="notification-dot" aria-hidden="true" /><span><strong>{eventLabel(item.type)}</strong><br />{item.text}<br /><small>{timeAgo(item.createdAt)}</small></span>{!item.isRead && <button disabled={action.pending} onClick={() => read(item.id)}>Mark read</button>}</li>)}</ul>)}</LoadState>
    <section><h2>Preferences</h2><LoadState query={prefs}>{prefs.data && <ul className="local-list">{['message.received', 'message.failed', 'channel.unhealthy', 'invitation.created'].map((kind) => {
      const current = prefs.data.find((pref) => pref.kind === kind);
      return <li key={kind}><span>{eventLabel(kind)}<br /><small>{preferenceHelp[kind]}</small></span><button disabled={action.pending} aria-label={`${current?.enabled === false ? 'Enable' : 'Disable'} ${eventLabel(kind)}`} onClick={() => togglePref(kind, !(current?.enabled ?? true))}>{current?.enabled === false ? 'Enable' : 'Disable'}</button></li>;
    })}</ul>}</LoadState></section>
  </WorkspacePage>;
}

export function LoadState({ query, children }: { query: { isPending: boolean; isError: boolean; refetch?: () => unknown }; children: React.ReactNode }) { if (query.isPending) return <p role="status">Loading…</p>; if (query.isError) return <div role="alert"><p>This workspace data could not be loaded.</p>{query.refetch && <button onClick={() => query.refetch?.()}>Try again</button>}</div>; return children; }
const pageDescriptions: Record<string, string> = { Overview: 'A shared view of your team’s workload and customer activity.', Channels: 'Manage the business accounts connected to your shared inbox.', Team: 'Manage workspace access and invite the people who support your customers.', 'Canned Responses': 'Keep common answers consistent. Create replies your whole team can reuse.', 'Audit Log': 'Review activity and changes across your workspace.', 'Workspace Settings': 'Manage your workspace identity and data retention.', Notifications: 'Stay up to date with customer activity and the health of your channels.' };
export function WorkspacePage({ title, actions, children }: { title: string; actions?: React.ReactNode; children: React.ReactNode }) { return <section className="workspace-page"><header className={`page-header${actions ? ' has-actions' : ''}`}><div><p className="eyebrow">Workspace</p><h1>{title}</h1>{pageDescriptions[title] && <p>{pageDescriptions[title]}</p>}</div>{actions && <div className="page-actions">{actions}</div>}</header>{children}</section>; }
