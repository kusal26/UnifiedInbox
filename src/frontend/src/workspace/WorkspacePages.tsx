import { useMemo, useState } from 'react';

const demo = <p className="demo-label"><strong>Demo-local data</strong> — API endpoints are not available yet.</p>;

export function OverviewPage() {
  return <WorkspacePage title="Overview">{demo}<div className="metric-grid">{[['Open conversations', '12'], ['First reply time', '4m 18s'], ['Reliability', '99.98%']].map(([label, value]) => <article className="metric-card" key={label}><span>{label}</span><strong>{value}</strong></article>)}</div><section className="workspace-card"><h2>Channel health</h2><p>WhatsApp and Email are delivering normally. Web chat reliability has held steady over the last 24 hours.</p></section></WorkspacePage>;
}

export function ChannelsPage() {
  const [reconnected, setReconnected] = useState(false);
  return <WorkspacePage title="Channels">{demo}<div className="channel-grid">{['WhatsApp', 'Email', 'Web chat'].map((channel) => <article className="workspace-card" key={channel}><h2>{channel}</h2><p><span className="health-dot" /> Healthy and receiving messages</p><button onClick={() => setReconnected(true)}>Reconnect</button></article>)}</div>{reconnected && <p role="status">Reconnect requested for demo channel.</p>}</WorkspacePage>;
}

export function TeamPage() {
  const [invited, setInvited] = useState(false);
  return <WorkspacePage title="Team">{demo}<button onClick={() => setInvited(true)}>Invite teammate</button>{invited && <p role="status">Invitation prepared for demo</p>}<table><thead><tr><th>Member</th><th>Role</th><th>Availability</th></tr></thead><tbody><tr><td>Alex Morgan</td><td>Admin</td><td>Online</td></tr><tr><td>Jordan Lee</td><td>Agent</td><td>Available</td></tr></tbody></table></WorkspacePage>;
}

export function CannedPage() {
  const [query, setQuery] = useState(''); const [responses, setResponses] = useState(['Welcome — how can we help?', 'Thanks for reaching out']);
  const visible = responses.filter((item) => item.toLowerCase().includes(query.toLowerCase()));
  return <WorkspacePage title="Canned Responses">{demo}<label>Search responses<input aria-label="Search responses" value={query} onChange={(event) => setQuery(event.target.value)} /></label><button onClick={() => setResponses((items) => [...items, 'New demo response'])}>Create response</button><ul className="local-list">{visible.map((item) => <li key={item}><span>{item}</span><button onClick={() => setResponses((items) => items.map((response) => response === item ? `${response} (edited)` : response))}>Edit</button></li>)}</ul></WorkspacePage>;
}

export function AuditPage() {
  const [filter, setFilter] = useState('All');
  return <WorkspacePage title="Audit Log">{demo}<label>Event filter<select aria-label="Event filter" value={filter} onChange={(event) => setFilter(event.target.value)}><option>All</option><option>Conversation</option><option>Team</option></select></label><table><thead><tr><th>When</th><th>Actor</th><th>Event</th></tr></thead><tbody><tr><td>Today, 09:14</td><td>Alex Morgan</td><td>{filter === 'All' ? 'Conversation updated' : `${filter} activity`}</td></tr></tbody></table></WorkspacePage>;
}

export function SettingsPage() {
  const [notifications, setNotifications] = useState(true);
  return <WorkspacePage title="Workspace Settings">{demo}<section className="workspace-card"><h2>Notifications</h2><label><input type="checkbox" checked={notifications} onChange={(event) => setNotifications(event.target.checked)} /> Send desktop alerts for unassigned conversations</label></section><section className="workspace-card"><h2>Workspace profile</h2><label>Workspace name<input defaultValue="Acme workspace" /></label></section></WorkspacePage>;
}

function WorkspacePage({ title, children }: { title: string; children: React.ReactNode }) { return <section className="workspace-page"><p className="eyebrow">Workspace</p><h1>{title}</h1>{children}</section>; }
