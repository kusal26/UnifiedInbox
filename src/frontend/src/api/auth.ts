import { request, type Fetcher } from './client';

export const userRoles = ['Owner', 'Admin', 'Agent'] as const;
export type UserRole = typeof userRoles[number];

export interface CurrentUser {
  id: string;
  tenantId: string;
  email: string;
  displayName: string;
  role: UserRole;
  workspaceName: string;
}

export interface Registration {
  workspaceName: string;
  workspaceSlug: string;
  displayName: string;
  email: string;
  password: string;
}

export interface SessionInfo { id: string; createdAt: string; expiresAt: string; isCurrent: boolean }

type ApiUser = Omit<CurrentUser, 'role' | 'workspaceName'> & { role: number | UserRole; workspaceName?: string };

export function normalizeRole(role: number | UserRole): UserRole {
  return typeof role === 'number' ? userRoles[role] ?? 'Agent' : role;
}

export function createAuthApi(getToken: () => string | null, fetcher: Fetcher = fetch) {
  const authorized = (headers: HeadersInit = {}): HeadersInit => {
    const token = getToken();
    return token ? { ...headers, Authorization: `Bearer ${token}` } : headers;
  };
  const endpoint = (path: string) => `/api/v1${path}`;
  return {
    register: (data: Registration) => request<{ message: string }>(fetcher, endpoint('/auth/register'), { method: 'POST', body: data }),
    verifyEmail: (token: string) => request<{ verified: boolean }>(fetcher, endpoint('/auth/verify-email'), { method: 'POST', body: { token } }),
    resendVerification: (email: string) => request<{ message: string }>(fetcher, endpoint('/auth/resend-verification'), { method: 'POST', body: { email } }),
    forgotPassword: (email: string) => request<{ message: string }>(fetcher, endpoint('/auth/forgot-password'), { method: 'POST', body: { email } }),
    resetPassword: (resetToken: string, newPassword: string) => request<{ reset: boolean }>(fetcher, endpoint('/auth/reset-password'), { method: 'POST', body: { token: resetToken, newPassword } }),
    me: () => request<ApiUser>(fetcher, endpoint('/auth/me'), { headers: authorized() }).then((user) => ({ ...user, role: normalizeRole(user.role) }) as CurrentUser),
    sessions: () => request<SessionInfo[]>(fetcher, endpoint('/auth/sessions'), { headers: authorized() }),
    revokeSession: (id: string) => request<void>(fetcher, endpoint(`/auth/sessions/${id}`), { method: 'DELETE', headers: authorized() }),
    revokeAllSessions: () => request<void>(fetcher, endpoint('/auth/sessions'), { method: 'DELETE', headers: authorized() }),
  };
}

export type AuthApi = ReturnType<typeof createAuthApi>;
