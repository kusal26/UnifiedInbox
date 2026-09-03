import { request, type Fetcher } from './client';

const conversationStatuses = ['Open', 'Pending', 'Closed'] as const;
const messageStatuses = ['Pending', 'Sending', 'Sent', 'Delivered', 'Read', 'Failed', 'Unknown'] as const;
const activityKinds = ['Message', 'InternalNote'] as const;

export type ConversationStatus = typeof conversationStatuses[number];
export type MessageStatus = typeof messageStatuses[number];
export type ActivityKind = typeof activityKinds[number];

export interface LoginCredentials {
  tenantSlug: string;
  email: string;
  password: string;
}

export interface LoginResponse { accessToken: string }

export interface Conversation {
  id: string;
  contactName: string;
  platform: string;
  preview: string;
  status: ConversationStatus;
  unread: boolean;
  updatedAt: string;
}

export interface ActivityItem {
  id: string;
  conversationId: string;
  kind: ActivityKind;
  body: string;
  createdAt: string;
  sequence: number;
  authorId?: string | null;
  status?: MessageStatus | null;
}

export interface ActivityPage { items: ActivityItem[]; nextCursor: string | null }

export interface InboxApi {
  login(credentials: LoginCredentials): Promise<LoginResponse>;
  listConversations(filters?: { search?: string; status?: ConversationStatus }): Promise<Conversation[]>;
  getActivity(conversationId: string, options?: { before?: string; limit?: number }): Promise<ActivityPage>;
  addNote(conversationId: string, body: string): Promise<ActivityItem>;
  setStatus(conversationId: string, status: ConversationStatus): Promise<Conversation>;
  markRead(conversationId: string, throughSequence: number): Promise<Conversation>;
  sendMessage(conversationId: string, body: string, idempotencyKey: string): Promise<ActivityItem>;
}

type ApiConversation = Omit<Conversation, 'status'> & { status: number | ConversationStatus };
type ApiActivityItem = Omit<ActivityItem, 'kind' | 'status' | 'authorId'> & {
  kind: number | ActivityKind;
  status?: number | MessageStatus | null;
  authorId?: string | null;
  senderUserId?: string | null;
};
type ApiActivityPage = { items: ApiActivityItem[]; nextCursor: string | null };

function fromEnum<T extends readonly string[]>(value: number | T[number], values: T): T[number] {
  return typeof value === 'number' ? values[value] ?? values[0] : value;
}

function toConversationStatus(status: ConversationStatus): number {
  return conversationStatuses.indexOf(status);
}

function normalizeConversation(conversation: ApiConversation): Conversation {
  return { ...conversation, status: fromEnum(conversation.status, conversationStatuses) };
}

function normalizeActivityItem(item: ApiActivityItem): ActivityItem {
  return {
    ...item,
    kind: fromEnum(item.kind, activityKinds),
    status: item.status == null ? item.status : fromEnum(item.status, messageStatuses),
    authorId: item.senderUserId ?? item.authorId ?? null,
  };
}

export function createInboxApi(getToken: () => string | null, fetcher: Fetcher = fetch): InboxApi {
  const authorized = (headers: HeadersInit = {}): HeadersInit => {
    const token = getToken();
    return token ? { ...headers, Authorization: `Bearer ${token}` } : headers;
  };
  const endpoint = (path: string) => `/api/v1${path}`;

  return {
    login: (credentials) => request(fetcher, endpoint('/auth/login'), { method: 'POST', credentials: 'include', body: credentials }),
    listConversations: (filters = {}) => {
      const params = new URLSearchParams();
      if (filters.search) params.set('search', filters.search);
      if (filters.status) params.set('status', filters.status);
      const query = params.size ? `?${params}` : '';
      return request<ApiConversation[]>(fetcher, endpoint(`/conversations${query}`), { headers: authorized() })
        .then((items) => items.map(normalizeConversation));
    },
    getActivity: (conversationId, options = {}) => {
      const params = new URLSearchParams();
      if (options.before) params.set('before', options.before);
      if (options.limit) params.set('limit', String(options.limit));
      const query = params.size ? `?${params}` : '';
      return request<ApiActivityPage>(fetcher, endpoint(`/conversations/${conversationId}/activity${query}`), { headers: authorized() })
        .then(({ items, nextCursor }) => ({ items: items.map(normalizeActivityItem), nextCursor }));
    },
    addNote: (conversationId, body) => request<ApiActivityItem>(fetcher, endpoint(`/conversations/${conversationId}/notes`), { method: 'POST', headers: authorized(), body: { body } }).then(normalizeActivityItem),
    setStatus: (conversationId, status) => request<ApiConversation>(fetcher, endpoint(`/conversations/${conversationId}/status`), { method: 'PATCH', headers: authorized(), body: { status: toConversationStatus(status) } }).then(normalizeConversation),
    markRead: (conversationId, throughSequence) => request<ApiConversation>(fetcher, endpoint(`/conversations/${conversationId}/read`), { method: 'PUT', headers: authorized(), body: { throughSequence } }).then(normalizeConversation),
    sendMessage: (conversationId, body, idempotencyKey) => request<ApiActivityItem>(fetcher, endpoint(`/conversations/${conversationId}/messages`), { method: 'POST', headers: authorized({ 'Idempotency-Key': idempotencyKey }), body: { body } }).then(normalizeActivityItem),
  };
}
