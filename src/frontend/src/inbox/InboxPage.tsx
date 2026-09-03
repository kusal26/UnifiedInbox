import { useEffect, useMemo, useRef, useState } from 'react';
import { useInfiniteQuery, useMutation, useQuery, useQueryClient, type InfiniteData, type QueryKey } from '@tanstack/react-query';
import type { ActivityItem, ActivityPage, Conversation, ConversationPage, ConversationStatus, InboxApi } from '../api/inbox';
import type { AdminApi, CannedResponse } from '../api/admin';
import type { AttachmentsApi } from '../api/attachments';
import { useClients } from '../api/hooks';
import { ConversationTimeline } from './ConversationTimeline';

const statuses: Array<ConversationStatus | 'All'> = ['All', 'Open', 'Pending', 'Closed'];

function createIdempotencyKey() {
  return globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random()}`;
}

interface InboxPageProps { api?: InboxApi; admin?: AdminApi; attachments?: AttachmentsApi }

export function InboxPage(props: InboxPageProps) {
  const clients = useClients();
  const api = props.api ?? clients.inbox;
  const admin = props.admin ?? clients.admin;
  const attachments = props.attachments ?? clients.attachments;
  const queryClient = useQueryClient();

  const [search, setSearch] = useState('');
  const [filter, setFilter] = useState<ConversationStatus | 'All'>('All');
  const [unreadOnly, setUnreadOnly] = useState(false);
  const [selectedId, setSelectedId] = useState<string | null>(null);

  const listKey = ['conversations', search, filter, unreadOnly] as const;
  const conversationsQuery = useInfiniteQuery<ConversationPage, Error, InfiniteData<ConversationPage>, QueryKey, string | undefined>({
    queryKey: listKey,
    initialPageParam: undefined,
    queryFn: ({ pageParam }) => api.listConversations({
      search: search || undefined,
      status: filter === 'All' ? undefined : filter,
      unreadOnly: unreadOnly || undefined,
      cursor: pageParam ?? undefined,
      pageSize: 30,
    }),
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });
  const conversations = useMemo(
    () => (conversationsQuery.data?.pages ?? []).flatMap((page) => page.items),
    [conversationsQuery.data],
  );

  useEffect(() => {
    const refresh = () => { void queryClient.invalidateQueries({ queryKey: ['conversations'] }); };
    window.addEventListener('inbox:refresh', refresh);
    return () => window.removeEventListener('inbox:refresh', refresh);
  }, [queryClient]);

  useEffect(() => {
    if (!conversationsQuery.data) return;
    setSelectedId((current) => (current && conversations.some((item) => item.id === current) ? current : conversations[0]?.id ?? null));
  }, [conversations, conversationsQuery.data]);

  const selected = conversations.find((item) => item.id === selectedId) ?? null;

  const activityQuery = useInfiniteQuery<ActivityPage, Error, InfiniteData<ActivityPage>, QueryKey, string | undefined>({
    queryKey: ['activity', selectedId],
    initialPageParam: undefined,
    enabled: Boolean(selectedId),
    queryFn: ({ pageParam }) => api.getActivity(selectedId!, { before: pageParam ?? undefined, limit: 50 }),
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });
  const timeline: ActivityItem[] = useMemo(() => {
    const pages = activityQuery.data?.pages ?? [];
    return [...pages].reverse().flatMap((page) => page.items);
  }, [activityQuery.data]);

  const readThrough = useRef<string | null>(null);
  useEffect(() => {
    if (!selected || timeline.length === 0) return;
    const latest = timeline.reduce((max, item) => Math.max(max, item.sequence), 0);
    const readKey = `${selected.id}:${latest}`;
    if (latest > 0 && readThrough.current !== readKey) {
      readThrough.current = readKey;
      api.markRead(selected.id, latest)
        .then((updated) => queryClient.setQueriesData<unknown>({ queryKey: ['conversations'] }, (cached: unknown) => replaceConversation(cached, updated)))
        .catch(() => { readThrough.current = null; });
    }
  }, [api, queryClient, selected, timeline]);

  const statusMutation = useMutation({
    mutationFn: ({ id, status }: { id: string; status: ConversationStatus }) => api.setStatus(id, status),
    onMutate: async ({ id, status }) => {
      await queryClient.cancelQueries({ queryKey: ['conversations'] });
      const previous = queryClient.getQueriesData({ queryKey: ['conversations'] });
      queryClient.setQueriesData({ queryKey: ['conversations'] }, (cached: unknown) =>
        mapInfinite(cached, (item: Conversation) => (item.id === id ? { ...item, status } : item)));
      return { previous };
    },
    onError: (_error, _vars, context) => context?.previous.forEach(([key, data]) => queryClient.setQueryData(key, data)),
    onSuccess: (updated) => queryClient.setQueriesData({ queryKey: ['conversations'] }, (cached: unknown) => replaceConversation(cached, updated)),
  });

  const sendMutation = useMutation({
    mutationFn: (input: { id: string; body: string; key: string; templateName?: string; attachmentIds?: string[] }) =>
      api.sendMessage(input.id, input.body, input.key, { templateName: input.templateName, attachmentIds: input.attachmentIds }),
    onMutate: async (input) => {
      await queryClient.cancelQueries({ queryKey: ['activity', input.id] });
      const optimistic: ActivityItem = {
        id: `optimistic-${input.key}`, conversationId: input.id, kind: 'Message', body: input.body,
        createdAt: new Date().toISOString(), sequence: Number.MAX_SAFE_INTEGER, authorId: null, status: 'Sending',
      };
      queryClient.setQueryData(['activity', input.id], (cached: unknown) => appendActivity(cached, optimistic));
      return { optimisticId: optimistic.id, conversationId: input.id };
    },
    onError: (_error, _vars, context) => {
      if (context) queryClient.setQueryData(['activity', context.conversationId], (cached: unknown) => removeActivity(cached, context.optimisticId));
    },
    onSuccess: (item, _vars, context) => {
      if (context) queryClient.setQueryData(['activity', context.conversationId], (cached: unknown) => replaceActivity(cached, context.optimisticId, item));
      void queryClient.invalidateQueries({ queryKey: ['conversations'] });
    },
  });

  const noteMutation = useMutation({
    mutationFn: ({ id, body }: { id: string; body: string }) => api.addNote(id, body),
    onMutate: async ({ id, body }) => {
      await queryClient.cancelQueries({ queryKey: ['activity', id] });
      const optimistic: ActivityItem = {
        id: `optimistic-note-${Date.now()}`, conversationId: id, kind: 'InternalNote', body,
        createdAt: new Date().toISOString(), sequence: Number.MAX_SAFE_INTEGER, authorId: null, status: null,
      };
      queryClient.setQueryData(['activity', id], (cached: unknown) => appendActivity(cached, optimistic));
      return { optimisticId: optimistic.id, conversationId: id };
    },
    onError: (_error, _vars, context) => {
      if (context) queryClient.setQueryData(['activity', context.conversationId], (cached: unknown) => removeActivity(cached, context.optimisticId));
    },
    onSuccess: (item, _vars, context) => {
      if (context) queryClient.setQueryData(['activity', context.conversationId], (cached: unknown) => replaceActivity(cached, context.optimisticId, item));
      void queryClient.invalidateQueries({ queryKey: ['conversations'] });
    },
  });

  return <section className={`inbox-page ${selected ? 'has-selection' : ''}`}>
    <aside className="inbox-list" aria-label="Conversations">
      <header><p className="eyebrow">Shared inbox</p><h1>Conversations</h1></header>
      <label className="inbox-search">Search conversations<input aria-label="Search conversations" value={search} onChange={(event) => setSearch(event.target.value)} /></label>
      <div className="inbox-filters" aria-label="Conversation status filters">
        {statuses.map((status) => <button className={filter === status ? 'active' : ''} key={status} onClick={() => setFilter(status)}>{status}</button>)}
        <button className={unreadOnly ? 'active' : ''} onClick={() => setUnreadOnly((value) => !value)}>Unread</button>
      </div>
      <div className="conversation-list">
        {conversationsQuery.isPending && <p role="status">Loading conversations…</p>}
        {conversationsQuery.isError && <p role="alert">Conversations could not be loaded. <button onClick={() => conversationsQuery.refetch()}>Try again</button></p>}
        {conversations.map((conversation) => <button className={`conversation-row ${conversation.id === selectedId ? 'selected' : ''}`} key={conversation.id} onClick={() => setSelectedId(conversation.id)}>
          <span className="conversation-avatar">{conversation.contactName.slice(0, 1)}</span><span><strong>{conversation.contactName}</strong><small>{conversation.preview}</small></span>
          {conversation.unread && <i aria-label="Unread" />}
        </button>)}
        {conversationsQuery.hasNextPage && <button onClick={() => conversationsQuery.fetchNextPage()} disabled={conversationsQuery.isFetchingNextPage}>{conversationsQuery.isFetchingNextPage ? 'Loading…' : 'Load more'}</button>}
      </div>
    </aside>
    <main className="inbox-thread">
      {!selected ? <p className="timeline-state">Choose a conversation to get started.</p> : <>
        <ThreadHeader
          conversation={selected}
          onStatus={(status) => statusMutation.mutate({ id: selected.id, status })}
          statusError={statusMutation.isError}
        />
        <ConversationTimeline
          state={activityQuery.isPending ? 'loading' : activityQuery.isError ? 'error' : timeline.length ? 'ready' : 'empty'}
          items={timeline}
          onRetry={() => activityQuery.refetch()}
        />
        {activityQuery.hasNextPage && <button onClick={() => activityQuery.fetchNextPage()} disabled={activityQuery.isFetchingNextPage}>{activityQuery.isFetchingNextPage ? 'Loading…' : 'Load older messages'}</button>}
        <Composer
          conversationId={selected.id}
          admin={admin}
          attachments={attachments}
          sending={sendMutation.isPending || noteMutation.isPending}
          sendError={(sendMutation.isError || noteMutation.isError) ? 'The message could not be sent. Try again.' : null}
          onSend={(body, extra) => sendMutation.mutate({ id: selected.id, body, key: createIdempotencyKey(), ...extra })}
          onNote={(body) => noteMutation.mutate({ id: selected.id, body })}
        />
      </>}
    </main>
    {selected && <CustomerPanel conversationId={selected.id} api={api} />}
  </section>;
}

function ThreadHeader({ conversation, onStatus, statusError }: { conversation: Conversation; onStatus(status: ConversationStatus): void; statusError: boolean }) {
  const [open, setOpen] = useState(false);
  return <header className="thread-header">
    <div><h2 aria-label={`Conversation with ${conversation.contactName}`}>Conversation</h2><p>{conversation.platform}</p></div>
    <div className="status-control">
      <button aria-label={`Status: ${conversation.status}`} aria-expanded={open} onClick={() => setOpen((value) => !value)}>{conversation.status}</button>
      {open && <div role="menu">{(['Open', 'Pending', 'Closed'] as ConversationStatus[]).map((status) => <button role="menuitem" key={status} onClick={() => { onStatus(status); setOpen(false); }}>{status}</button>)}</div>}
      {statusError && <p role="alert">Status change failed.</p>}
    </div>
  </header>;
}

function Composer(props: {
  conversationId: string;
  admin: AdminApi;
  attachments: AttachmentsApi;
  sending: boolean;
  sendError: string | null;
  onSend(body: string, extra?: { templateName?: string; attachmentIds?: string[] }): void;
  onNote(body: string): void;
}) {
  const [mode, setMode] = useState<'reply' | 'note'>('reply');
  const [message, setMessage] = useState('');
  const [templateName, setTemplateName] = useState('');
  const [cannedOpen, setCannedOpen] = useState(false);
  const [cannedSearch, setCannedSearch] = useState('');
  const [attachedIds, setAttachedIds] = useState<string[]>([]);
  const [attachedNames, setAttachedNames] = useState<string[]>([]);
  const [uploadState, setUploadState] = useState<'idle' | 'uploading' | 'error'>('idle');
  const fileInput = useRef<HTMLInputElement>(null);

  const cannedQuery = useQuery({
    queryKey: ['canned', cannedSearch],
    queryFn: () => props.admin.cannedResponses(cannedSearch || undefined),
    enabled: cannedOpen,
  });
  const responses: CannedResponse[] = cannedQuery.data ?? [];

  const send = () => {
    const body = message.trim();
    if (!body || props.sending || uploadState === 'uploading') return;
    if (mode === 'note') props.onNote(body);
    else props.onSend(body, { templateName: templateName.trim() || undefined, attachmentIds: attachedIds.length ? attachedIds : undefined });
    setMessage('');
    setAttachedIds([]);
    setAttachedNames([]);
  };

  const attach = async (files: FileList | null) => {
    if (!files || files.length === 0) return;
    setUploadState('uploading');
    try {
      const ids: string[] = [];
      const names: string[] = [];
      for (const file of Array.from(files)) {
        ids.push(await props.attachments.upload(file));
        names.push(file.name);
      }
      setAttachedIds((current) => [...current, ...ids]);
      setAttachedNames((current) => [...current, ...names]);
      setUploadState('idle');
    } catch {
      setUploadState('error');
    }
  };

  return <section className={`composer ${mode === 'note' ? 'is-note' : ''}`} aria-label="Message composer">
    <div className="composer-modes"><button className={mode === 'reply' ? 'active' : ''} onClick={() => setMode('reply')}>Reply</button><button className={mode === 'note' ? 'active' : ''} onClick={() => setMode('note')}>Internal note</button></div>
    <textarea aria-label="Message" value={message} onChange={(event) => setMessage(event.target.value)} placeholder={mode === 'note' ? 'Write a private note' : 'Write a reply'} />
    {mode === 'reply' && <label>Template (for closed 24h windows)<input aria-label="Template name" value={templateName} onChange={(event) => setTemplateName(event.target.value)} placeholder="hello_world" /></label>}
    <div className="composer-actions">
      <button aria-label="Canned responses" onClick={() => setCannedOpen((open) => !open)}>Canned responses</button>
      <button aria-label="Add emoji" onClick={() => setMessage((current) => `${current}${current ? ' ' : ''}🙂`)}>🙂</button>
      <button aria-label="Add attachment" onClick={() => fileInput.current?.click()}>Attach</button>
      <input ref={fileInput} type="file" hidden multiple aria-label="Attach files" onChange={(event) => { void attach(event.target.files); event.target.value = ''; }} />
      <button className="send-button" onClick={send} disabled={props.sending || uploadState === 'uploading'}>{mode === 'note' ? 'Add note' : 'Send reply'}</button>
    </div>
    {attachedNames.length > 0 && <p>Attached: {attachedNames.join(', ')}</p>}
    {uploadState === 'uploading' && <p role="status">Uploading attachments…</p>}
    {uploadState === 'error' && <p role="alert">An attachment upload failed. Try again.</p>}
    {props.sendError && <p role="alert">{props.sendError}</p>}
    {cannedOpen && <div className="canned-menu">
      <input aria-label="Search canned responses" value={cannedSearch} onChange={(event) => setCannedSearch(event.target.value)} autoFocus />
      {responses.map((response) => <button key={response.id} onClick={() => { setMessage((current) => current ? `${current} ${response.content}` : response.content); setCannedOpen(false); }}>{response.title}</button>)}
    </div>}
  </section>;
}

function CustomerPanel({ conversationId, api }: { conversationId: string; api: InboxApi }) {
  const queryClient = useQueryClient();
  const detailsQuery = useQuery({ queryKey: ['conversation', conversationId], queryFn: () => api.getConversation(conversationId) });
  const [notes, setNotes] = useState<string | null>(null);
  useEffect(() => { setNotes(null); }, [conversationId]);
  const notesMutation = useMutation({
    mutationFn: (value: string | null) => api.updateCustomerNotes(conversationId, value),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['conversation', conversationId] }),
  });
  const details = detailsQuery.data;
  return <aside className="customer-panel" aria-label="Customer details">
    <h2>Customer</h2>
    {detailsQuery.isPending && <p role="status">Loading…</p>}
    {details && <>
      <strong aria-label={details.contactName}>Contact profile</strong>
      <p>{details.platform} conversation</p>
      <p>{details.phone}{details.email ? ` · ${details.email}` : ''}</p>
      <label>Customer notes<textarea aria-label="Customer notes" value={notes ?? details.customerNotes ?? ''} onChange={(event) => setNotes(event.target.value)} /></label>
      <button onClick={() => notesMutation.mutate(notes ?? details.customerNotes ?? '')} disabled={notesMutation.isPending}>Save notes</button>
      {notesMutation.isSuccess && <p role="status">Notes saved.</p>}
      {notesMutation.isError && <p role="alert">Notes could not be saved.</p>}
    </>}
  </aside>;
}

function mapInfinite(cached: unknown, map: (item: Conversation) => Conversation): unknown {
  const data = cached as { pages: ConversationPage[]; pageParams: unknown[] } | undefined;
  if (!data?.pages) return cached;
  return { ...data, pages: data.pages.map((page) => ({ ...page, items: page.items.map(map) })) };
}

function replaceConversation(cached: unknown, updated: Conversation): unknown {
  return mapInfinite(cached, (item) => (item.id === updated.id ? updated : item));
}

function appendActivity(cached: unknown, item: ActivityItem): unknown {
  const data = cached as { pages: Array<{ items: ActivityItem[]; nextCursor: string | null }>; pageParams: unknown[] } | undefined;
  if (!data?.pages?.length) return { pages: [{ items: [item], nextCursor: null }], pageParams: [undefined] };
  const pages = data.pages.map((page, index) => (index === 0 ? { ...page, items: [...page.items, item] } : page));
  return { ...data, pages };
}

function removeActivity(cached: unknown, id: string): unknown {
  const data = cached as { pages: Array<{ items: ActivityItem[]; nextCursor: string | null }>; pageParams: unknown[] } | undefined;
  if (!data?.pages) return cached;
  return { ...data, pages: data.pages.map((page) => ({ ...page, items: page.items.filter((item) => item.id !== id) })) };
}

function replaceActivity(cached: unknown, id: string, item: ActivityItem): unknown {
  const data = cached as { pages: Array<{ items: ActivityItem[]; nextCursor: string | null }>; pageParams: unknown[] } | undefined;
  if (!data?.pages) return cached;
  return { ...data, pages: data.pages.map((page) => ({ ...page, items: page.items.map((existing) => (existing.id === id ? item : existing)) })) };
}
