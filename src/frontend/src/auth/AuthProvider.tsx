import { createContext, useContext, useEffect, useMemo, useState, type PropsWithChildren } from 'react';
import { request } from '../api/client';
import { createInboxApi, type LoginCredentials, type LoginResponse } from '../api/inbox';

export interface AuthContextValue {
  token: string | null;
  login(credentials: LoginCredentials): Promise<void>;
  logout(): void;
}

const AuthContext = createContext<AuthContextValue | null>(null);

interface AuthProviderProps extends PropsWithChildren {
  login?: (credentials: LoginCredentials) => Promise<LoginResponse>;
}

export function AuthProvider({ children, login: loginRequest }: AuthProviderProps) {
  const [token, setToken] = useState<string | null>(null);
  useEffect(() => {
    if (loginRequest) return;
    const controller = new AbortController();
    request<LoginResponse>(fetch, '/api/v1/auth/refresh', { method: 'POST', credentials: 'include', signal: controller.signal })
      .then(response => setToken(response.accessToken)).catch(() => undefined);
    return () => controller.abort();
  }, [loginRequest]);
  const value = useMemo<AuthContextValue>(() => ({
    token,
    async login(credentials) {
      const response = await (loginRequest ?? createInboxApi(() => null).login)(credentials);
      setToken(response.accessToken);
    },
    logout: () => { setToken(null); if (!loginRequest) void request(fetch, '/api/v1/auth/logout', { method: 'POST', credentials: 'include' }).catch(() => undefined); },
  }), [loginRequest, token]);
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext);
  if (!context) throw new Error('useAuth must be used within an AuthProvider');
  return context;
}
