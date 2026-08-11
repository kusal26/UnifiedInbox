import type { PropsWithChildren } from 'react';
import { NavLink } from 'react-router-dom';
import { Icon } from '../components/Icon';
import { appRoutes } from './routes';

export function AppShell({ children }: PropsWithChildren) {
  return <div className="workspace-shell">
    <aside className="workspace-rail" aria-label="Workspace navigation">
      <NavLink className="workspace-mark" to="/" aria-label="Unified Inbox home">
        <Icon name="inbox" />
        <span>Unified<br />Inbox</span>
      </NavLink>
      <nav aria-label="Primary navigation">
        {appRoutes.map((route) => <NavLink className="workspace-nav-link" key={route.path} to={route.path} end={route.path === '/'}>
          <Icon name={route.icon} />
          <span>{route.label}</span>
          {route.path === '/' && <b aria-label="12 unread conversations">12</b>}
        </NavLink>)}
      </nav>
    </aside>
    <section className="workspace-main">
      <header className="workspace-topbar">
        <button className="workspace-switcher" type="button" aria-label="Switch workspace">
          <span className="workspace-avatar">A</span>
          <span>Acme workspace</span>
          <Icon name="chevron-down" />
        </button>
        <div className="workspace-actions">
          <label className="global-search">
            <Icon name="search" />
            <span className="sr-only">Search workspace</span>
            <input type="search" placeholder="Search" aria-label="Search workspace" />
          </label>
          <button className="icon-button" type="button" aria-label="Notifications"><Icon name="bell" /></button>
          <button className="logout-button" type="button"><Icon name="log-out" /> <span>Log out</span></button>
        </div>
      </header>
      <main className="workspace-content">{children}</main>
    </section>
  </div>;
}
