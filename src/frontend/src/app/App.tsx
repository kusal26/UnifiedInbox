import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import { LoginPage } from '../auth/LoginPage';
import { useAuth } from '../auth/AuthProvider';
import { AppShell } from './AppShell';
import { createInboxApi } from '../api/inbox';
import { InboxPage } from '../inbox/InboxPage';
import { AuditPage, CannedPage, ChannelsPage, OverviewPage, SettingsPage, TeamPage } from '../workspace/WorkspacePages';

export function App() {
  return <BrowserRouter>
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="/*" element={<ProtectedApp />} />
    </Routes>
  </BrowserRouter>;
}

function ProtectedApp() {
  const { token, logout } = useAuth();
  if (!token) return <Navigate to="/login" replace />;
  const api = createInboxApi(() => token);
  return <AppShell onLogout={logout}><Routes>
    <Route path="/" element={<InboxPage api={api} />} />
    <Route path="/overview" element={<OverviewPage />} /><Route path="/channels" element={<ChannelsPage />} />
    <Route path="/team" element={<TeamPage />} /><Route path="/canned" element={<CannedPage />} />
    <Route path="/audit" element={<AuditPage />} /><Route path="/settings" element={<SettingsPage />} />
  </Routes></AppShell>;
}
