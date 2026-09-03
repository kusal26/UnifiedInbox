import { request, type Fetcher } from './client';
import { normalizeRole, type UserRole } from './auth';

export interface TeamMember { id: string; displayName: string; email: string; role: UserRole; isActive: boolean }
export interface Invitation { id: string; email: string; role: UserRole; expiresAt: string; createdAt: string }
export interface ManagedChannel {
  id: string; displayName: string; platform: string; externalAccountId: string;
  isHealthy: boolean; isEnabled: boolean; status: string;
  lastWebhookAt?: string | null; lastOutboundAt?: string | null;
}
export interface CannedResponse { id: string; title: string; shortcut: string; content: string }
export interface NotificationItem { id: string; type: string; text: string; isRead: boolean; createdAt: string }
export interface NotificationPreference { id: string; userId: string; kind: string; enabled: boolean }
export interface AuditEntry { id: string; actorId?: string | null; action: string; resource: string; metadata: string; createdAt: string }
export interface OverviewMetrics {
  days: number; since: string; conversationsOpened: number; openConversations: number;
  messagesInbound: number; messagesOutbound: number; notesCreated: number;
}
export interface WorkspaceInfo { id: string; name: string; slug: string; retentionDays: number }
export interface ChannelHealthEntry { id: string; channelId: string; isHealthy: boolean; reason: string; createdAt: string }

type ApiMember = Omit<TeamMember, 'role'> & { role: number | UserRole };
type ApiInvitation = Omit<Invitation, 'role'> & { role: number | UserRole };

export function createAdminApi(getToken: () => string | null, fetcher: Fetcher = fetch) {
  const headers = (): HeadersInit => {
    const token = getToken();
    return token ? { Authorization: `Bearer ${token}` } : {};
  };
  const endpoint = (path: string) => `/api/v1${path}`;
  return {
    users: () => request<ApiMember[]>(fetcher, endpoint('/users'), { headers: headers() }).then((items) => items.map((m) => ({ ...m, role: normalizeRole(m.role) }))),
    setRole: (id: string, role: UserRole) => request<ApiMember>(fetcher, endpoint(`/users/${id}/role`), { method: 'PUT', headers: headers(), body: { role } }).then((m) => ({ ...m, role: normalizeRole(m.role) })),
    setActive: (id: string, isActive: boolean) => request<ApiMember>(fetcher, endpoint(`/users/${id}/active`), { method: 'PUT', headers: headers(), body: { isActive } }).then((m) => ({ ...m, role: normalizeRole(m.role) })),
    invitations: () => request<ApiInvitation[]>(fetcher, endpoint('/invitations'), { headers: headers() }).then((items) => items.map((i) => ({ ...i, role: normalizeRole(i.role) }))),
    invite: (email: string, role: UserRole) => request<ApiInvitation>(fetcher, endpoint('/invitations'), { method: 'POST', headers: headers(), body: { email, role } }).then((i) => ({ ...i, role: normalizeRole(i.role) })),
    acceptInvitation: (token: string, displayName: string, password: string) => request<{ accepted: boolean }>(fetcher, endpoint('/invitations/accept'), { method: 'POST', body: { token, displayName, password } }),
    revokeInvitation: (id: string) => request<void>(fetcher, endpoint(`/invitations/${id}`), { method: 'DELETE', headers: headers() }),
    channels: () => request<ManagedChannel[]>(fetcher, endpoint('/channels'), { headers: headers() }),
    beginConnect: (displayName: string) => request<{ attemptId: string; state: string; expiresAt: string }>(fetcher, endpoint('/channels/connect/attempt'), { method: 'POST', headers: headers(), body: { displayName } }),
    completeConnect: (data: { state: string; code: string; phoneNumberId: string; businessId: string; displayName: string }) => request<ManagedChannel>(fetcher, endpoint('/channels/connect/complete'), { method: 'POST', headers: headers(), body: data }),
    beginReauthorize: (id: string) => request<{ attemptId: string; state: string; expiresAt: string }>(fetcher, endpoint(`/channels/${id}/reauthorize`), { method: 'POST', headers: headers() }),
    testChannel: (id: string) => request<{ healthy: boolean; detail: string }>(fetcher, endpoint(`/channels/${id}/test`), { method: 'POST', headers: headers() }),
    channelHealth: (id: string) => request<ChannelHealthEntry[]>(fetcher, endpoint(`/channels/${id}/health`), { headers: headers() }),
    setChannelEnabled: (id: string, enabled: boolean) => request<ManagedChannel>(fetcher, endpoint(`/channels/${id}/enabled`), { method: 'PUT', headers: headers(), body: { enabled } }),
    disconnectChannel: (id: string) => request<void>(fetcher, endpoint(`/channels/${id}/disconnect`), { method: 'POST', headers: headers() }),
    cannedResponses: (search?: string) => request<CannedResponse[]>(fetcher, endpoint(`/canned-responses${search ? `?q=${encodeURIComponent(search)}` : ''}`), { headers: headers() }),
    addCanned: (data: Omit<CannedResponse, 'id'>) => request<CannedResponse>(fetcher, endpoint('/canned-responses'), { method: 'POST', headers: headers(), body: data }),
    updateCanned: (id: string, data: Omit<CannedResponse, 'id'>) => request<CannedResponse>(fetcher, endpoint(`/canned-responses/${id}`), { method: 'PUT', headers: headers(), body: data }),
    deleteCanned: (id: string) => request<void>(fetcher, endpoint(`/canned-responses/${id}`), { method: 'DELETE', headers: headers() }),
    notifications: (unreadOnly = false) => request<NotificationItem[]>(fetcher, endpoint(`/notifications${unreadOnly ? '?unreadOnly=true' : ''}`), { headers: headers() }),
    markNotificationRead: (id: string) => request<void>(fetcher, endpoint(`/notifications/${id}/read`), { method: 'POST', headers: headers() }),
    markAllNotificationsRead: () => request<void>(fetcher, endpoint('/notifications/read-all'), { method: 'POST', headers: headers() }),
    preferences: () => request<NotificationPreference[]>(fetcher, endpoint('/notification-preferences'), { headers: headers() }),
    setPreference: (kind: string, enabled: boolean) => request<NotificationPreference[]>(fetcher, endpoint('/notification-preferences'), { method: 'PUT', headers: headers(), body: { kind, enabled } }),
    audit: (search?: string) => request<AuditEntry[]>(fetcher, endpoint(`/audit-logs${search ? `?q=${encodeURIComponent(search)}` : ''}`), { headers: headers() }),
    auditExportUrl: (search?: string) => `/api/v1/audit-logs/export${search ? `?q=${encodeURIComponent(search)}` : ''}`,
    metrics: (days = 30) => request<OverviewMetrics>(fetcher, endpoint(`/metrics/overview?days=${days}`), { headers: headers() }),
    workspace: () => request<WorkspaceInfo>(fetcher, endpoint('/workspace'), { headers: headers() }),
    updateWorkspace: (data: { name: string; retentionDays: number }) => request<WorkspaceInfo>(fetcher, endpoint('/workspace'), { method: 'PUT', headers: headers(), body: data }),
  };
}

export type AdminApi = ReturnType<typeof createAdminApi>;
