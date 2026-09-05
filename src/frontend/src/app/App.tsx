import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import { LoginPage } from '../auth/LoginPage';
import { ForgotPasswordPage, RegisterPage, ResetPasswordPage, VerifyEmailPage } from '../auth/AuthPages';
import { AcceptInvitationPage } from '../auth/AcceptInvitationPage';
import { useAuth } from '../auth/AuthProvider';
import { AppShell } from './AppShell';
import { InboxPage } from '../inbox/InboxPage';
import { AuditPage, CannedPage, ChannelsPage, NotificationsPage, OverviewPage, SettingsPage, TeamPage } from '../workspace/WorkspacePages';
import { ChannelRepairPage } from '../channels/ChannelRepairPage';
import { RealtimeBridge } from './RealtimeBridge';

export function App() {
  return <BrowserRouter><Routes>
    <Route path="/login" element={<LoginPage />} />
    <Route path="/register" element={<RegisterPage />} />
    <Route path="/verify-email" element={<VerifyEmailPage />} />
    <Route path="/forgot-password" element={<ForgotPasswordPage />} />
    <Route path="/reset-password" element={<ResetPasswordPage />} />
    <Route path="/invitations/accept" element={<AcceptInvitationPage />} />
    <Route path="/*" element={<ProtectedApp />} />
  </Routes></BrowserRouter>;
}

function ProtectedApp() {
  const { token, logout } = useAuth();
  if (!token) return <Navigate to="/login" replace />;
  return <AppShell onLogout={logout}><RealtimeBridge token={token} /><Routes>
    <Route path="/" element={<InboxPage />} />
    <Route path="/overview" element={<OverviewPage />} />
    <Route path="/channels" element={<ChannelsPage />} />
    <Route path="/channels/:channelId/repair" element={<ChannelRepairPage />} />
    <Route path="/team" element={<TeamPage />} />
    <Route path="/canned" element={<CannedPage />} />
    <Route path="/audit" element={<AuditPage />} />
    <Route path="/settings" element={<SettingsPage />} />
    <Route path="/notifications" element={<NotificationsPage />} />
  </Routes></AppShell>;
}
