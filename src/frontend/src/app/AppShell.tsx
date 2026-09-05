import { useState, type PropsWithChildren } from 'react';
import { NavLink, useLocation, useNavigate } from 'react-router-dom';
import { Dialog } from '../components/Dialog';
import { useQuery } from '@tanstack/react-query';
import { Icon } from '../components/Icon';
import { appRoutes } from './routes';
import { useAuth } from '../auth/AuthProvider';
import { createAdminApi } from '../api/admin';
import { canAdmin, isOwner, useMe } from '../api/hooks';

interface AppShellProps extends PropsWithChildren {
  onLogout(): void;
}

export function AppShell({ children, onLogout }: AppShellProps) {
  const { token } = useAuth();
  const { user } = useMe();
  const navigate = useNavigate();
  const location = useLocation();
  const [navigationOpen, setNavigationOpen] = useState(false);
  const [search, setSearch] = useState('');
  const unread = useQuery({
    queryKey: ['unread-count'],
    queryFn: () => createAdminApi(() => token).notifications(true),
    enabled: Boolean(token),
    refetchInterval: 60_000,
  });
  const unreadCount = unread.data?.length ?? 0;
  const visibleRoutes = appRoutes.filter((route) => {
    if (route.path === '/audit') return isOwner(user);
    if (route.path === '/team' || route.path === '/channels' || route.path === '/settings') return canAdmin(user);
    return true;
  });

  const navigation = <nav aria-label="Primary navigation">
    {visibleRoutes.map(route => <NavLink className="workspace-nav-link" key={route.path} to={route.path} end={route.path === '/'} onClick={() => setNavigationOpen(false)}>
      <Icon name={route.icon} /><span>{route.label}</span>
      {route.path === '/notifications' && unreadCount > 0 && <b>{unreadCount}</b>}
    </NavLink>)}
  </nav>;
  return <div className="workspace-shell">
    <a className="skip-link" href="#main-content">Skip to content</a>
    <aside className="workspace-rail" aria-label="Workspace navigation">
      <NavLink className="workspace-mark" to="/" aria-label="Unified Inbox home">
        <Icon name="inbox" />
        <span>Unified Inbox</span>
      </NavLink>
      <p className="rail-caption">Workspace</p>
      {navigation}
      <div className="rail-user"><span className="workspace-avatar">{user?.displayName?.slice(0, 1)}</span><div><strong>{user?.displayName}</strong><small>{user?.role}</small></div></div>
    </aside>
    <section className="workspace-main">
      <header className="workspace-topbar">
        <button type="button" className="mobile-navigation" aria-label="Open navigation" onClick={() => setNavigationOpen(true)}>☰ <span>Menu</span></button>
        <div className="workspace-identity">
          <span className="workspace-avatar">{(user?.workspaceName ?? '…').slice(0, 1)}</span>
          <span>{user?.workspaceName ?? 'Loading workspace…'}</span>
        </div>
        <div className="workspace-actions">
          <form className="global-search" role="search" onSubmit={event => { event.preventDefault(); navigate(`/?q=${encodeURIComponent(search.trim())}`); }}>
            <Icon name="search" />
            <span className="sr-only">Search workspace</span>
            <input type="search" placeholder="Search conversations…" aria-label="Search workspace" value={search} onChange={event => setSearch(event.target.value)} />
          </form>
          <button className="icon-button" type="button" aria-label={unreadCount > 0 ? `Notifications, ${unreadCount} unread` : 'Notifications'} onClick={() => navigate('/notifications')}><Icon name="bell" />{unreadCount > 0 && <b>{unreadCount}</b>}</button>
          <button className="logout-button" type="button" onClick={onLogout}><Icon name="log-out" /> <span>Log out</span></button>
        </div>
      </header>
      <main id="main-content" tabIndex={-1} className={`workspace-content ${location.pathname === '/' ? 'is-inbox' : ''}`}>{children}</main>
    </section>
    {navigationOpen && <Dialog title="Navigation" className="navigation-dialog" onClose={() => setNavigationOpen(false)}>{navigation}</Dialog>}
  </div>;
}
