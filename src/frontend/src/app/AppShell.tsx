import type { PropsWithChildren } from 'react';
import { NavLink, useNavigate } from 'react-router-dom';
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

  return <div className="workspace-shell">
    <aside className="workspace-rail" aria-label="Workspace navigation">
      <NavLink className="workspace-mark" to="/" aria-label="Unified Inbox home">
        <Icon name="inbox" />
        <span>Unified<br />Inbox</span>
      </NavLink>
      <nav aria-label="Primary navigation">
        {visibleRoutes.map((route) => <NavLink className="workspace-nav-link" key={route.path} to={route.path} end={route.path === '/'}>
          <Icon name={route.icon} />
          <span>{route.label}</span>
          {route.path === '/' && unreadCount > 0 && <b aria-label={`${unreadCount} unread conversations`}>{unreadCount}</b>}
        </NavLink>)}
      </nav>
    </aside>
    <section className="workspace-main">
      <header className="workspace-topbar">
        <button className="workspace-switcher" type="button" aria-label="Switch workspace">
          <span className="workspace-avatar">{(user?.workspaceName ?? '…').slice(0, 1)}</span>
          <span>{user?.workspaceName ?? 'Loading workspace…'}</span>
          <Icon name="chevron-down" />
        </button>
        <div className="workspace-actions">
          <label className="global-search">
            <Icon name="search" />
            <span className="sr-only">Search workspace</span>
            <input type="search" placeholder="Search" aria-label="Search workspace" />
          </label>
          <button className="icon-button" type="button" aria-label={unreadCount > 0 ? `Notifications, ${unreadCount} unread` : 'Notifications'} onClick={() => navigate('/notifications')}><Icon name="bell" />{unreadCount > 0 && <b>{unreadCount}</b>}</button>
          <button className="logout-button" type="button" onClick={onLogout}><Icon name="log-out" /> <span>Log out</span></button>
        </div>
      </header>
      <main className="workspace-content">{children}</main>
    </section>
  </div>;
}
