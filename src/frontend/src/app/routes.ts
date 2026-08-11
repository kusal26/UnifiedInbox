import type { IconName } from '../components/Icon';

export interface AppRoute { path: string; label: string; icon: IconName }

export const appRoutes: AppRoute[] = [
  { path: '/', label: 'Shared Inbox', icon: 'inbox' },
  { path: '/overview', label: 'Overview', icon: 'overview' },
  { path: '/channels', label: 'Channels', icon: 'channels' },
  { path: '/team', label: 'Team', icon: 'team' },
  { path: '/canned', label: 'Canned Responses', icon: 'canned' },
  { path: '/audit', label: 'Audit Log', icon: 'audit' },
  { path: '/settings', label: 'Settings', icon: 'settings' },
];
