export interface AppRoute { path: string; label: string }

export const appRoutes: AppRoute[] = [
  { path: '/', label: 'Shared Inbox' },
  { path: '/overview', label: 'Overview' },
  { path: '/channels', label: 'Channels' },
  { path: '/team', label: 'Team' },
  { path: '/canned-responses', label: 'Canned Responses' },
  { path: '/audit-log', label: 'Audit Log' },
  { path: '/settings', label: 'Settings' },
];
