import { BrowserRouter, Navigate, Outlet, Route, Routes } from 'react-router-dom';
import { LoginPage } from '../auth/LoginPage';
import { useAuth } from '../auth/AuthProvider';
import { AppShell } from './AppShell';
import { appRoutes } from './routes';

function RoutePlaceholder({ label }: { label: string }) {
  return <section className="route-placeholder"><p className="eyebrow">Workspace</p><h1>{label}</h1></section>;
}

export function App() {
  return <BrowserRouter>
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route element={<ProtectedApp />}>
        {appRoutes.map((route) => <Route key={route.path} path={route.path} element={<RoutePlaceholder label={route.label} />} />)}
      </Route>
    </Routes>
  </BrowserRouter>;
}

function ProtectedApp() {
  const { token, logout } = useAuth();
  if (!token) return <Navigate to="/login" replace />;
  return <AppShell onLogout={logout}><Outlet /></AppShell>;
}
