import { request, type Fetcher } from './client';

export type ConversationStatus = 'Open' | 'Pending' | 'Closed' | string;

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
  kind: string;
  body: string;
  createdAt: string;
  sequence: number;
  authorId?: string | null;
  status?: string | null;
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

export function createInboxApi(getToken: () => string | null, fetcher: Fetcher = fetch): InboxApi {
  const authorized = (headers: HeadersInit = {}): HeadersInit => {
    const token = getToken();
    return token ? { ...headers, Authorization: `Bearer ${token}` } : headers;
  };
  const endpoint = (path: string) => `/api/v1${path}`;

  return {
    login: (credentials) => request(fetcher, endpoint('/auth/login'), { method: 'POST', body: credentials }),
    listConversations: (filters = {}) => {
      const params = new URLSearchParams();
      if (filters.search) params.set('search', filters.search);
      if (filters.status) params.set('status', filters.status);
      const query = params.size ? `?${params}` : '';
      return request(fetcher, endpoint(`/conversations${query}`), { headers: authorized() });
    },
    getActivity: (conversationId, options = {}) => {
      const params = new URLSearchParams();
      if (options.before) params.set('before', options.before);
      if (options.limit) params.set('limit', String(options.limit));
      const query = params.size ? `?${params}` : '';
      return request(fetcher, endpoint(`/conversations/${conversationId}/activity${query}`), { headers: authorized() });
    },
    addNote: (conversationId, body) => request(fetcher, endpoint(`/conversations/${conversationId}/notes`), { method: 'POST', headers: authorized(), body: { body } }),
    setStatus: (conversationId, status) => request(fetcher, endpoint(`/conversations/${conversationId}/status`), { method: 'PATCH', headers: authorized(), body: { status } }),
    markRead: (conversationId, throughSequence) => request(fetcher, endpoint(`/conversations/${conversationId}/read`), { method: 'PUT', headers: authorized(), body: { throughSequence } }),
    sendMessage: (conversationId, body, idempotencyKey) => request(fetcher, endpoint(`/conversations/${conversationId}/messages`), { method: 'POST', headers: authorized({ 'Idempotency-Key': idempotencyKey }), body: { body } }),
  };
}
