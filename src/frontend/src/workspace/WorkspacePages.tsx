import { useState, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { request } from '../api/client';
import { useAuth } from '../auth/AuthProvider';

type Channel = { id: string; displayName: string; platform: string; status: string; isHealthy: boolean; lastWebhookAt?: string };
type Member = { id: string; displayName: string; email: string; role: number; isActive: boolean };
type Canned = { id: string; title: string; shortcut: string; content: string };
type Audit = { id: string; action: string; resource: string; createdAt: string };
type Workspace = { id: string; name: string; slug: string; retentionDays: number };

function useApi<T>(path: string, key: readonly unknown[]) {
  const { token } = useAuth();
  return useQuery({ queryKey: key, queryFn: () => request<T>(fetch, `/api/v1${path}`, { headers: { Authorization: `Bearer ${token}` }, credentials: 'include' }) });
}

export function OverviewPage() {
  const channels = useApi<Channel[]>('/channels', ['channels']);
  const users = useApi<Member[]>('/users', ['users']);
  const connected = channels.data?.filter(item => item.isHealthy).length ?? 0;
  return <WorkspacePage title="Overview"><div className="metric-grid">{[['Team members', String(users.data?.length ?? '—')], ['Connected channels', String(connected)], ['Channel reliability', channels.isError ? 'Unavailable' : channels.isPending ? 'Loading' : 'Live']].map(([label, value]) => <article className="metric-card" key={label}><span>{label}</span><strong>{value}</strong></article>)}</div><section className="workspace-card"><h2>Operations</h2><p>Metrics are calculated from your tenant’s persisted workspace data.</p></section></WorkspacePage>;
}

export function ChannelsPage() {
  const query = useApi<Channel[]>('/channels', ['channels']);
  return <WorkspacePage title="Channels"><LoadState query={query}>{query.data && <div className="channel-grid">{query.data.map(channel => <article className="workspace-card" key={channel.id}><h2>{channel.displayName || channel.platform}</h2><p><span className="health-dot" /> {channel.status}</p><small>{channel.lastWebhookAt ? `Last webhook ${new Date(channel.lastWebhookAt).toLocaleString()}` : 'Waiting for the first webhook'}</small></article>)}</div>}</LoadState></WorkspacePage>;
}

export function TeamPage() {
  const query = useApi<Member[]>('/users', ['users']);
  const role = (value: number) => ['Owner', 'Admin', 'Agent'][value] ?? 'Agent';
  return <WorkspacePage title="Team"><LoadState query={query}>{query.data && <table><thead><tr><th>Member</th><th>Role</th><th>Status</th></tr></thead><tbody>{query.data.map(member => <tr key={member.id}><td><strong>{member.displayName}</strong><br /><small>{member.email}</small></td><td>{role(member.role)}</td><td>{member.isActive ? 'Active' : 'Disabled'}</td></tr>)}</tbody></table>}</LoadState></WorkspacePage>;
}

export function CannedPage() {
  const [search, setSearch] = useState(''); const { token } = useAuth(); const client = useQueryClient();
  const query = useApi<Canned[]>(`/canned-responses${search ? `?q=${encodeURIComponent(search)}` : ''}`, ['canned', search]);
  const create = useMutation({ mutationFn: (data: Omit<Canned, 'id'>) => request<Canned>(fetch, '/api/v1/canned-responses', { method: 'POST', headers: { Authorization: `Bearer ${token}` }, body: data }), onSuccess: () => client.invalidateQueries({ queryKey: ['canned'] }) });
  const submit = (event: FormEvent<HTMLFormElement>) => { event.preventDefault(); const data = new FormData(event.currentTarget); create.mutate({ title: String(data.get('title')), shortcut: String(data.get('shortcut')), content: String(data.get('content')) }); event.currentTarget.reset(); };
  return <WorkspacePage title="Canned Responses"><label>Search responses<input aria-label="Search responses" value={search} onChange={event => setSearch(event.target.value)} /></label><form className="workspace-card" onSubmit={submit}><h2>New response</h2><input name="title" aria-label="Title" required placeholder="Welcome" /><input name="shortcut" aria-label="Shortcut" required placeholder="/welcome" /><textarea name="content" aria-label="Content" required /><button disabled={create.isPending}>Create response</button></form><LoadState query={query}>{query.data && <ul className="local-list">{query.data.map(item => <li key={item.id}><span><strong>{item.title}</strong> <small>{item.shortcut}</small><br />{item.content}</span></li>)}</ul>}</LoadState></WorkspacePage>;
}

export function AuditPage() {
  const [search, setSearch] = useState(''); const query = useApi<Audit[]>(`/audit-logs${search ? `?q=${encodeURIComponent(search)}` : ''}`, ['audit', search]);
  return <WorkspacePage title="Audit Log"><label>Search events<input value={search} onChange={event => setSearch(event.target.value)} /></label><LoadState query={query}>{query.data && <table><thead><tr><th>When</th><th>Action</th><th>Resource</th></tr></thead><tbody>{query.data.map(item => <tr key={item.id}><td>{new Date(item.createdAt).toLocaleString()}</td><td>{item.action}</td><td>{item.resource}</td></tr>)}</tbody></table>}</LoadState></WorkspacePage>;
}

export function SettingsPage() {
  const query = useApi<Workspace>('/workspace', ['workspace']); const { token } = useAuth(); const client = useQueryClient();
  const save = useMutation({ mutationFn: (data: Pick<Workspace, 'name' | 'retentionDays'>) => request<Workspace>(fetch, '/api/v1/workspace', { method: 'PUT', headers: { Authorization: `Bearer ${token}` }, body: data }), onSuccess: data => client.setQueryData(['workspace'], data) });
  const submit = (event: FormEvent<HTMLFormElement>) => { event.preventDefault(); const data = new FormData(event.currentTarget); save.mutate({ name: String(data.get('name')), retentionDays: Number(data.get('retentionDays')) }); };
  return <WorkspacePage title="Workspace Settings"><LoadState query={query}>{query.data && <form className="workspace-card" onSubmit={submit}><h2>Workspace profile</h2><label>Workspace name<input name="name" defaultValue={query.data.name} /></label><label>Retention days<input name="retentionDays" type="number" min="30" max="3650" defaultValue={query.data.retentionDays} /></label><button disabled={save.isPending}>Save settings</button>{save.isSuccess && <p role="status">Settings saved.</p>}</form>}</LoadState></WorkspacePage>;
}

function LoadState({ query, children }: { query: { isPending: boolean; isError: boolean }; children: React.ReactNode }) { if (query.isPending) return <p role="status">Loading…</p>; if (query.isError) return <p role="alert">This workspace data could not be loaded.</p>; return children; }
function WorkspacePage({ title, children }: { title: string; children: React.ReactNode }) { return <section className="workspace-page"><p className="eyebrow">Workspace</p><h1>{title}</h1>{children}</section>; }
