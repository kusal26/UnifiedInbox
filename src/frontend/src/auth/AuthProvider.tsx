import { createContext, useContext, useMemo, useState, type PropsWithChildren } from 'react';
import { createInboxApi, type LoginCredentials } from '../api/inbox';

export interface AuthContextValue {
  token: string | null;
  login(credentials: LoginCredentials): Promise<void>;
  logout(): void;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: PropsWithChildren) {
  const [token, setToken] = useState<string | null>(null);
  const value = useMemo<AuthContextValue>(() => ({
    token,
    async login(credentials) {
      const response = await createInboxApi(() => null).login(credentials);
      setToken(response.accessToken);
    },
    logout: () => setToken(null),
  }), [token]);
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext);
  if (!context) throw new Error('useAuth must be used within an AuthProvider');
  return context;
}
