import { useEffect, useRef, useState } from 'react';
import type { ActivityItem, Conversation, ConversationStatus, InboxApi } from '../api/inbox';
import { ConversationTimeline, type TimelineState } from './ConversationTimeline';

const statuses: Array<ConversationStatus | 'All'> = ['All', 'Open', 'Pending', 'Closed'];
const cannedResponses = ['Thanks for reaching out', 'I am looking into this now', 'Could you share a little more detail?'];

function createIdempotencyKey() {
  return globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random()}`;
}

export function InboxPage({ api }: { api: InboxApi }) {
  const [conversations, setConversations] = useState<Conversation[]>([]);
  const [search, setSearch] = useState('');
  const [filter, setFilter] = useState<ConversationStatus | 'All'>('All');
  const [unreadOnly, setUnreadOnly] = useState(false);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [timeline, setTimeline] = useState<ActivityItem[]>([]);
  const [timelineState, setTimelineState] = useState<TimelineState>('loading');
  const [mode, setMode] = useState<'reply' | 'note'>('reply');
  const [message, setMessage] = useState('');
  const [cannedOpen, setCannedOpen] = useState(false);
  const [cannedSearch, setCannedSearch] = useState('');
  const [attachmentError, setAttachmentError] = useState(false);
  const [statusOpen, setStatusOpen] = useState(false);
  const [revision, setRevision] = useState(0);
  const readThrough = useRef<string | null>(null);

  useEffect(() => {
    let active = true;
    api.listConversations({ search: search || undefined, status: filter === 'All' ? undefined : filter })
      .then((items) => {
        if (!active) return;
        const visible = unreadOnly ? items.filter((item) => item.unread) : items;
        setConversations(visible);
        setSelectedId((current) => visible.some((item) => item.id === current) ? current : visible[0]?.id ?? null);
      })
      .catch(() => active && setConversations([]));
    return () => { active = false; };
  }, [api, search, filter, unreadOnly, revision]);

  useEffect(() => { const refresh = () => setRevision(value => value + 1); window.addEventListener('inbox:refresh', refresh); return () => window.removeEventListener('inbox:refresh', refresh); }, []);

  const selected = conversations.find((item) => item.id === selectedId) ?? null;
  const loadTimeline = () => {
    if (!selected) return;
    setTimelineState('loading');
    api.getActivity(selected.id).then(({ items }) => {
      setTimeline(items);
      setTimelineState(items.length ? 'ready' : 'empty');
      const latest = items.reduce((max, item) => Math.max(max, item.sequence), 0);
      const readKey = `${selected.id}:${latest}`;
      if (latest > 0 && readThrough.current !== readKey) {
        readThrough.current = readKey;
        api.markRead(selected.id, latest).catch(() => { readThrough.current = null; });
      }
    }).catch(() => setTimelineState('error'));
  };

  useEffect(() => { loadTimeline(); // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedId]);

  const selectConversation = (id: string) => { setSelectedId(id); setTimeline([]); };
  const changeStatus = async (status: ConversationStatus) => {
    if (!selected) return;
    const changed = await api.setStatus(selected.id, status);
    setConversations((items) => items.map((item) => item.id === changed.id ? changed : item));
    setStatusOpen(false);
  };
  const send = async () => {
    if (!selected || !message.trim()) return;
    const body = message.trim();
    const item = mode === 'note'
      ? await api.addNote(selected.id, body)
      : await api.sendMessage(selected.id, body, createIdempotencyKey());
    setTimeline((items) => [...items, item]);
    setTimelineState('ready');
    setMessage('');
  };

  return <section className={`inbox-page ${selected ? 'has-selection' : ''}`}>
    <aside className="inbox-list" aria-label="Conversations">
      <header><p className="eyebrow">Shared inbox</p><h1>Conversations</h1></header>
      <label className="inbox-search">Search conversations<input aria-label="Search conversations" value={search} onChange={(event) => setSearch(event.target.value)} /></label>
      <div className="inbox-filters" aria-label="Conversation status filters">
        {statuses.map((status) => <button className={filter === status ? 'active' : ''} key={status} onClick={() => setFilter(status)}>{status}</button>)}
        <button className={unreadOnly ? 'active' : ''} onClick={() => setUnreadOnly((value) => !value)}>Unread</button>
      </div>
      <div className="conversation-list">
        {conversations.map((conversation) => <button className={`conversation-row ${conversation.id === selectedId ? 'selected' : ''}`} key={conversation.id} onClick={() => selectConversation(conversation.id)}>
          <span className="conversation-avatar">{conversation.contactName.slice(0, 1)}</span><span><strong>{conversation.contactName}</strong><small>{conversation.preview}</small></span>
          {conversation.unread && <i aria-label="Unread" />}
        </button>)}
      </div>
    </aside>
    <main className="inbox-thread">
      {!selected ? <p className="timeline-state">Choose a conversation to get started.</p> : <>
        <header className="thread-header"><div><button className="mobile-back" aria-label="Back to conversations" onClick={() => setSelectedId(null)}>←</button><h2 aria-label={`Conversation with ${selected.contactName}`}>Conversation</h2><p>{selected.platform}</p></div><div className="status-control"><button aria-label={`Status: ${selected.status}`} aria-expanded={statusOpen} onClick={() => setStatusOpen((open) => !open)}>{selected.status}</button>{statusOpen && <div role="menu">{(['Open', 'Pending', 'Closed'] as ConversationStatus[]).map((status) => <button role="menuitem" key={status} onClick={() => changeStatus(status)}>{status}</button>)}</div>}</div></header>
        <ConversationTimeline state={timelineState} items={timeline} onRetry={loadTimeline} />
        <Composer mode={mode} message={message} cannedOpen={cannedOpen} cannedSearch={cannedSearch} attachmentError={attachmentError}
          onMode={setMode} onMessage={setMessage} onCannedOpen={() => setCannedOpen((open) => !open)} onCannedSearch={setCannedSearch}
          onInsert={(text) => { setMessage((current) => current ? `${current} ${text}` : text); setCannedOpen(false); }} onEmoji={() => setMessage((current) => `${current}${current ? ' ' : ''}🙂`)}
          onAttachment={() => setAttachmentError(true)} onSend={send} />
      </>}
    </main>
    {selected && <aside className="customer-panel" aria-label={`Customer details for ${selected.contactName}`}><h2>Customer</h2><strong aria-label={selected.contactName}>Contact profile</strong><p>{selected.platform} conversation</p><p>Status: {selected.status}</p></aside>}
  </section>;
}

function Composer(props: { mode: 'reply' | 'note'; message: string; cannedOpen: boolean; cannedSearch: string; attachmentError: boolean; onMode(mode: 'reply' | 'note'): void; onMessage(value: string): void; onCannedOpen(): void; onCannedSearch(value: string): void; onInsert(value: string): void; onEmoji(): void; onAttachment(): void; onSend(): void }) {
  const responses = cannedResponses.filter((response) => response.toLowerCase().includes(props.cannedSearch.toLowerCase()));
  return <section className={`composer ${props.mode === 'note' ? 'is-note' : ''}`} aria-label="Message composer">
    <div className="composer-modes"><button className={props.mode === 'reply' ? 'active' : ''} onClick={() => props.onMode('reply')}>Reply</button><button className={props.mode === 'note' ? 'active' : ''} onClick={() => props.onMode('note')}>Internal note</button></div>
    <textarea aria-label="Message" value={props.message} onChange={(event) => props.onMessage(event.target.value)} placeholder={props.mode === 'note' ? 'Write a private note' : 'Write a reply'} />
    <div className="composer-actions"><button aria-label="Canned responses" onClick={props.onCannedOpen}>Canned responses</button><button aria-label="Add emoji" onClick={props.onEmoji}>🙂</button><button aria-label="Add attachment" onClick={props.onAttachment}>Attach</button><button className="send-button" onClick={props.onSend}>{props.mode === 'note' ? 'Add note' : 'Send reply'}</button></div>
    {props.cannedOpen && <div className="canned-menu"><input aria-label="Search canned responses" value={props.cannedSearch} onChange={(event) => props.onCannedSearch(event.target.value)} autoFocus />{responses.map((response) => <button key={response} onClick={() => props.onInsert(response)}>{response}</button>)}</div>}
    {props.attachmentError && <p role="alert">Attachments are not available yet.</p>}
  </section>;
}
