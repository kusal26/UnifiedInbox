import { useEffect, useMemo, useRef, useState } from 'react';
import { useInfiniteQuery, useMutation, useQuery, useQueryClient, type InfiniteData, type QueryKey } from '@tanstack/react-query';
import type { ActivityItem, ActivityPage, Conversation, ConversationPage, ConversationStatus, InboxApi, OutboundTemplateSelection } from '../api/inbox';
import type { AdminApi, CannedResponse, WhatsAppTemplateInfo } from '../api/admin';
import type { AttachmentsApi } from '../api/attachments';
import { ApiError } from '../api/client';
import { useClients } from '../api/hooks';
import { ConversationTimeline } from './ConversationTimeline';
import { AttachmentComposer } from './AttachmentComposer';
import { TemplatePicker } from './TemplatePicker';
import type { Fetcher } from '../api/client';
import { Dialog } from '../components/Dialog';

const statuses: Array<ConversationStatus | 'All'> = ['All', 'Open', 'Pending', 'Closed'];

function createIdempotencyKey() {
  return globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random()}`;
}

interface InboxPageProps { api?: InboxApi; admin?: AdminApi; attachments?: AttachmentsApi; attachmentPut?: Fetcher }

export function InboxPage(props: InboxPageProps) {
  const clients = useClients();
  const api = props.api ?? clients.inbox;
  const admin = props.admin ?? clients.admin;
  const attachments = props.attachments ?? clients.attachments;
  const queryClient = useQueryClient();

  const [search, setSearch] = useState(() => new URLSearchParams(window.location.search).get('q') ?? '');
  const [mobileThread, setMobileThread] = useState(false);
  const [detailsOpen, setDetailsOpen] = useState(false);
  const listRef = useRef<HTMLDivElement>(null);
  const searchFromUrl = new URLSearchParams(window.location.search).get('q') ?? '';
  useEffect(() => { setSearch(searchFromUrl); }, [searchFromUrl]);
  // Back/forward navigation does not re-render on its own; re-sync the filter.
  useEffect(() => {
    const sync = () => setSearch(new URLSearchParams(window.location.search).get('q') ?? '');
    window.addEventListener('popstate', sync);
    return () => window.removeEventListener('popstate', sync);
  }, []);
  const [filter, setFilter] = useState<ConversationStatus | 'All'>('All');
  const [unreadOnly, setUnreadOnly] = useState(false);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  // One shared customer-notes draft: the inline panel and the dialog panel are two
  // instances of the same editor, so an unsaved draft must survive switching between them.
  const [notesDraft, setNotesDraft] = useState<string | null>(null);
  useEffect(() => { setNotesDraft(null); }, [selectedId]);

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
    mutationFn: (input: { id: string; body: string; key: string; template?: OutboundTemplateSelection; attachmentIds?: string[] }) =>
      api.sendMessage(input.id, input.body, input.key, { template: input.template, attachmentIds: input.attachmentIds }),
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

  return <section className={`inbox-page ${mobileThread && selected ? 'mobile-thread-open' : ''}`}>
    <aside className="inbox-list" aria-label="Conversations">
      <header><p className="eyebrow">Shared inbox</p><h1>Conversations</h1></header>
      <label className="inbox-search">Search conversations<input aria-label="Search conversations" value={search} onChange={(event) => setSearch(event.target.value)} /></label>
      <div className="inbox-filters" aria-label="Conversation status filters">
        {statuses.map((status) => <button aria-pressed={filter === status} className={filter === status ? 'active' : ''} key={status} onClick={() => setFilter(status)}>{status}</button>)}
        <button aria-pressed={unreadOnly} className={unreadOnly ? 'active' : ''} onClick={() => setUnreadOnly((value) => !value)}>Unread</button>
      </div>
      <div className="conversation-list" ref={listRef}>
        {conversationsQuery.isPending && <p role="status">Loading conversations…</p>}
        {conversationsQuery.isError && <p role="alert">Conversations could not be loaded. <button onClick={() => conversationsQuery.refetch()}>Try again</button></p>}
        {!conversationsQuery.isPending && !conversationsQuery.isError && conversations.length === 0 && <div className="empty-state"><h2>{search || filter !== 'All' || unreadOnly ? 'No matching conversations' : 'Your inbox is ready'}</h2><p>{search || filter !== 'All' || unreadOnly ? 'Try another search or clear your filters.' : 'New customer messages will appear here once a channel is connected.'}</p>{(search || filter !== 'All' || unreadOnly) && <button onClick={() => { setSearch(''); setFilter('All'); setUnreadOnly(false); }}>Clear filters</button>}</div>}
        {conversations.map((conversation) => <button aria-pressed={conversation.id === selectedId} className={`conversation-row ${conversation.id === selectedId ? 'selected' : ''}`} key={conversation.id} onClick={() => { setSelectedId(conversation.id); setMobileThread(true); }}>
          <span className="conversation-avatar">{conversation.contactName.slice(0, 1)}</span><span><strong>{conversation.contactName}</strong><small>{conversation.preview}</small></span>
          {conversation.unread && <i aria-label="Unread" />}
        </button>)}
        {conversationsQuery.hasNextPage && <button onClick={() => conversationsQuery.fetchNextPage()} disabled={conversationsQuery.isFetchingNextPage}>{conversationsQuery.isFetchingNextPage ? 'Loading…' : 'Load more'}</button>}
      </div>
    </aside>
    <section className="inbox-thread" aria-label="Conversation thread">
      {!selected ? <div className="timeline-state"><h2>A clear view of every conversation</h2><p>{conversations.length ? 'Select a customer to view their messages.' : 'Messages and private team notes will appear here.'}</p></div> : <>
        <ThreadHeader
          conversation={selected}
          onStatus={(status) => statusMutation.mutate({ id: selected.id, status })}
          statusError={statusMutation.isError}
          onBack={() => { setMobileThread(false); requestAnimationFrame(() => listRef.current?.querySelector<HTMLButtonElement>('.selected')?.focus()); }}
          onDetails={() => setDetailsOpen(true)}
        />
        <div className="timeline-scroll">
        <ConversationTimeline
          state={activityQuery.isPending ? 'loading' : activityQuery.isError ? 'error' : timeline.length ? 'ready' : 'empty'}
          items={timeline}
          onRetry={() => activityQuery.refetch()}
        />
        {activityQuery.hasNextPage && <button onClick={() => activityQuery.fetchNextPage()} disabled={activityQuery.isFetchingNextPage}>{activityQuery.isFetchingNextPage ? 'Loading…' : 'Load older messages'}</button>}
        </div>
        <Composer
          key={selected.id}
          conversationId={selected.id}
          api={api}
          admin={admin}
          attachments={attachments}
          attachmentPut={props.attachmentPut}
          sending={sendMutation.isPending || noteMutation.isPending}
          onSend={(body, extra) => sendMutation.mutateAsync({ id: selected.id, body, key: createIdempotencyKey(), ...extra })}
          onNote={(body) => noteMutation.mutateAsync({ id: selected.id, body })}
        />
      </>}
    </section>
    {selected && <CustomerPanel conversationId={selected.id} api={api} notes={notesDraft} onNotesChange={setNotesDraft} />}
    {selected && detailsOpen && <Dialog title="Customer details" onClose={() => setDetailsOpen(false)}><CustomerPanel conversationId={selected.id} api={api} notes={notesDraft} onNotesChange={setNotesDraft} /></Dialog>}
  </section>;
}

function ThreadHeader({ conversation, onStatus, statusError, onBack, onDetails }: { conversation: Conversation; onStatus(status: ConversationStatus): void; statusError: boolean; onBack(): void; onDetails(): void }) {
  const [open, setOpen] = useState(false);
  const menu = useRef<HTMLDivElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  useEffect(() => { if (open) menu.current?.querySelector<HTMLButtonElement>('button')?.focus(); }, [open]);
  return <header className="thread-header">
    <button className="mobile-back" onClick={onBack} aria-label="Back to conversations">←</button>
    <div><h2 aria-label={`Conversation with ${conversation.contactName}`}>{conversation.contactName}</h2><p>{conversation.platform}</p></div>
    <div className="thread-actions"><button onClick={onDetails}>Customer details</button><div className="status-control" onBlur={event => { if (!event.currentTarget.contains(event.relatedTarget)) setOpen(false); }}>
      <button ref={trigger} aria-haspopup="menu" aria-label={`Status: ${conversation.status}`} aria-expanded={open} onClick={() => setOpen((value) => !value)}>{conversation.status}</button>
      {open && <div ref={menu} role="menu" aria-label="Conversation status" onKeyDown={event => {
        const options = Array.from(menu.current?.querySelectorAll<HTMLButtonElement>('button') ?? []);
        const index = options.indexOf(document.activeElement as HTMLButtonElement);
        if (event.key === 'Escape') { setOpen(false); trigger.current?.focus(); }
        if (['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) { event.preventDefault(); options[event.key === 'Home' ? 0 : event.key === 'End' ? options.length - 1 : (index + (event.key === 'ArrowDown' ? 1 : -1) + options.length) % options.length]?.focus(); }
      }}>{(['Open', 'Pending', 'Closed'] as ConversationStatus[]).map((status) => <button role="menuitem" key={status} onClick={() => { onStatus(status); setOpen(false); trigger.current?.focus(); }}>{status}</button>)}</div>}
      {statusError && <p role="alert">Status change failed.</p>}
    </div></div>
  </header>;
}

interface ComposerProps {
  conversationId: string;
  api: InboxApi;
  admin: AdminApi;
  attachments: AttachmentsApi;
  attachmentPut?: Fetcher;
  sending: boolean;
  onSend(body: string, extra?: { template?: OutboundTemplateSelection; attachmentIds?: string[] }): Promise<unknown>;
  onNote(body: string): Promise<unknown>;
}

function Composer(props: ComposerProps) {
  const [mode, setMode] = useState<'reply' | 'note'>('reply');
  const [message, setMessage] = useState('');
  const [cannedOpen, setCannedOpen] = useState(false);
  const [cannedSearch, setCannedSearch] = useState('');
  const [readyIds, setReadyIds] = useState<string[]>([]);
  const [attachmentsReady, setAttachmentsReady] = useState(true);
  const [attachmentClaim, setAttachmentClaim] = useState(0);
  const [attachmentReset, setAttachmentReset] = useState(0);
  const [templateOpen, setTemplateOpen] = useState(false);
  const [templateRequired, setTemplateRequired] = useState(false);
  const [template, setTemplate] = useState<OutboundTemplateSelection | null>(null);
  const [error, setError] = useState('');

  const cannedQuery = useQuery({
    queryKey: ['canned', cannedSearch],
    queryFn: () => props.admin.cannedResponses(cannedSearch || undefined),
    enabled: cannedOpen,
  });
  const responses: CannedResponse[] = cannedQuery.data ?? [];

  const detailsQuery = useQuery({ queryKey: ['conversation', props.conversationId], queryFn: () => props.api.getConversation(props.conversationId) });
  const details = detailsQuery.data;

  const templatesQuery = useQuery({
    queryKey: ['channel-templates', details?.channelId],
    queryFn: () => props.admin.channelTemplates(details!.channelId),
    enabled: templateOpen && Boolean(details?.channelId),
  });
  const templates: WhatsAppTemplateInfo[] | null = templatesQuery.data ?? null;

  const dropAttachments = () => {
    setReadyIds([]);
    setAttachmentReset((value) => value + 1);
  };

  const send = async () => {
    const body = message.trim();
    if (props.sending) return;
    if (mode === 'note') {
      if (!body) return;
      try { await props.onNote(body); setMessage(''); setError(''); } catch (err) { setError(errorMessage(err)); }
      return;
    }
    if (templateRequired && !template) {
      setTemplateOpen(true);
      setError('The 24-hour customer service window is closed. Choose an approved template to send.');
      return;
    }
    if (!body && !template && readyIds.length === 0) return;
    try {
      // Outside the window a template is the deliverable; WhatsApp content comes from the approved
      // template snapshot. Record which template was used so the timeline row and conversation
      // preview stay meaningful, and never combine a template with staged attachments.
      const outboundBody = template ? `${template.name} (${template.language})` : body;
      await props.onSend(outboundBody, { template: template ?? undefined, attachmentIds: readyIds.length ? readyIds : undefined });
      if (readyIds.length > 0) setAttachmentClaim((value) => value + 1);
      setMessage('');
      setTemplate(null);
      setTemplateOpen(false);
      setTemplateRequired(false);
      setError('');
    } catch (err) {
      const code = errorCode(err);
      if (code === 'messaging_window_closed') {
        dropAttachments();
        setTemplateRequired(true);
        setTemplateOpen(true);
        setError('The 24-hour customer service window is closed. Choose an approved template to send.');
      } else {
        setError(errorMessage(err));
      }
    }
  };

  const openPicker = () => { setTemplateOpen(true); setError(''); };

  return <section className={`composer ${mode === 'note' ? 'is-note' : ''}`} aria-label="Message composer">
    <div className="composer-modes"><button aria-pressed={mode === 'reply'} className={mode === 'reply' ? 'active' : ''} onClick={() => setMode('reply')}>Reply</button><button aria-pressed={mode === 'note'} className={mode === 'note' ? 'active' : ''} onClick={() => setMode('note')}>Internal note</button></div>
    <textarea aria-label="Message" value={message} onChange={(event) => setMessage(event.target.value)} placeholder={mode === 'note' ? 'Write a private note' : 'Write a reply'} />
    {mode === 'reply' && !templateOpen && <button type="button" aria-label="Use an approved template" onClick={openPicker}>{template ? 'Change template' : 'Use a template'}</button>}
    {mode === 'reply' && templateOpen && <TemplatePicker
      templates={templates}
      loading={templatesQuery.isPending}
      error={templatesQuery.isError ? 'Approved templates could not be loaded.' : ''}
      onCancel={() => { setTemplateOpen(false); setError(''); }}
      onConfirm={(selection) => { if (selection) dropAttachments(); setTemplate(selection); setTemplateOpen(false); setError(''); }}
    />}
    <div className="composer-actions">
      <button aria-label="Canned responses" aria-expanded={cannedOpen} onClick={() => setCannedOpen((open) => !open)}>Canned responses</button>
      <button aria-label="Add emoji" onClick={() => setMessage((current) => `${current}${current ? ' ' : ''}🙂`)}>🙂</button>
      {mode === 'reply' && <AttachmentComposer
        attachments={props.attachments}
        put={props.attachmentPut}
        disabled={Boolean(template) || templateRequired}
        resetKey={`${props.conversationId}:${attachmentReset}`}
        claimSignal={attachmentClaim}
        onSelectionChange={(ids, ready) => { setReadyIds(ids); setAttachmentsReady(ready); }}
      />}
      <button className="send-button" onClick={() => void send()} disabled={props.sending || !attachmentsReady || (mode === 'reply' && templateRequired && !template)}>{mode === 'note' ? 'Add note' : 'Send reply'}</button>
    </div>
    {template && <p role="status">Sending as approved template {template.name} ({template.language}).</p>}
    {error && <p role="alert">{error}</p>}
    {cannedOpen && <div className="canned-menu" onKeyDown={event => { if (event.key === 'Escape') { setCannedOpen(false); event.currentTarget.closest('.composer')?.querySelector<HTMLButtonElement>('[aria-label="Canned responses"]')?.focus(); } }}>
      {cannedQuery.isPending && <p role="status">Loading saved responses…</p>}
      {cannedQuery.isError && <p role="alert">Saved responses could not be loaded. <button onClick={() => cannedQuery.refetch()}>Try again</button></p>}
      {!cannedQuery.isPending && !cannedQuery.isError && responses.length === 0 && <p>No saved responses match your search.</p>}
      <button onClick={() => setCannedOpen(false)}>Close saved responses</button>
      <input aria-label="Search canned responses" value={cannedSearch} onChange={(event) => setCannedSearch(event.target.value)} autoFocus />
      {responses.map((response) => <button key={response.id} onClick={() => { setMessage((current) => current ? `${current} ${response.content}` : response.content); setCannedOpen(false); }}>{response.title}</button>)}
    </div>}
  </section>;
}

function errorMessage(error: unknown): string {
  if (error instanceof ApiError) return error.message || 'The message could not be sent. Try again.';
  return 'The message could not be sent. Try again.';
}

function errorCode(error: unknown): string | undefined {
  return error instanceof ApiError ? error.code : undefined;
}

function CustomerPanel({ conversationId, api, notes, onNotesChange }: { conversationId: string; api: InboxApi; notes: string | null; onNotesChange(value: string): void }) {
  const queryClient = useQueryClient();
  const detailsQuery = useQuery({ queryKey: ['conversation', conversationId], queryFn: () => api.getConversation(conversationId) });
  const notesMutation = useMutation({
    mutationFn: (value: string | null) => api.updateCustomerNotes(conversationId, value),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['conversation', conversationId] }),
  });
  const details = detailsQuery.data;
  return <aside className="customer-panel" aria-label="Customer details">
    <h2>Customer</h2>
    {detailsQuery.isPending && <p role="status">Loading…</p>}
    {detailsQuery.isError && <p role="alert">Customer details could not be loaded. <button onClick={() => detailsQuery.refetch()}>Try again</button></p>}
    {details && <>
      <strong aria-label={details.contactName}>{details.contactName}</strong>
      <p>{details.platform} conversation</p>
      <p>{details.phone}{details.email ? ` · ${details.email}` : ''}</p>
      <label>Customer notes<textarea aria-label="Customer notes" value={notes ?? details.customerNotes ?? ''} onChange={(event) => onNotesChange(event.target.value)} /></label>
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
