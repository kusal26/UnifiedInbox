import type { SVGProps } from 'react';

export type IconName = 'audit' | 'bell' | 'canned' | 'channels' | 'chevron-down' | 'inbox' | 'log-out' | 'overview' | 'search' | 'settings' | 'team';

const paths: Record<IconName, string> = {
  audit: 'M7 3h10v18H7zM10 7h4M10 11h4M10 15h3',
  bell: 'M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9M10 21h4',
  canned: 'M6 3h12v18H6zM9 8h6M9 12h6M9 16h3',
  channels: 'M5 12h14M12 5v14M5 5h3v3H5zM16 5h3v3h-3zM5 16h3v3H5zM16 16h3v3h-3z',
  'chevron-down': 'm7 10 5 5 5-5',
  inbox: 'M4 5h16v14H4zM4 15h4l2 3h4l2-3h4',
  'log-out': 'M10 17l5-5-5-5M15 12H3M21 4v16',
  overview: 'M4 4h6v6H4zM14 4h6v6h-6zM4 14h6v6H4zM14 14h6v6h-6z',
  search: 'm21 21-4.35-4.35M19 11a8 8 0 1 1-16 0 8 8 0 0 1 16 0Z',
  settings: 'M12 15.5a3.5 3.5 0 1 0 0-7 3.5 3.5 0 0 0 0 7ZM19.4 15a1.7 1.7 0 0 0 .34 1.88l.06.06-2.32 2.32-.06-.06a1.7 1.7 0 0 0-1.88-.34 1.7 1.7 0 0 0-1.04 1.56v.08h-3.28v-.08a1.7 1.7 0 0 0-1.04-1.56 1.7 1.7 0 0 0-1.88.34l-.06.06-2.32-2.32.06-.06A1.7 1.7 0 0 0 6.32 15 1.7 1.7 0 0 0 4.76 14h-.08v-3.28h.08A1.7 1.7 0 0 0 6.32 9.68 1.7 1.7 0 0 0 6 7.8l-.06-.06 2.32-2.32.06.06a1.7 1.7 0 0 0 1.88.34 1.7 1.7 0 0 0 1.04-1.56v-.08h3.28v.08a1.7 1.7 0 0 0 1.04 1.56 1.7 1.7 0 0 0 1.88-.34l.06-.06 2.32 2.32-.06.06a1.7 1.7 0 0 0-.34 1.88 1.7 1.7 0 0 0 1.56 1.04h.08V14h-.08A1.7 1.7 0 0 0 19.4 15Z',
  team: 'M8 11a3 3 0 1 0 0-6 3 3 0 0 0 0 6ZM16 12a2.5 2.5 0 1 0 0-5M3 20a5 5 0 0 1 10 0M14 15.5a4.5 4.5 0 0 1 7 3.7',
};

interface IconProps extends Omit<SVGProps<SVGSVGElement>, 'aria-label'> {
  name: IconName;
  /** An accessible name for icons which convey information independently. */
  label?: string;
}

export function Icon({ name, label, ...props }: IconProps) {
  return <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden={label ? undefined : true} aria-label={label} role={label ? 'img' : undefined} focusable="false" {...props}>
    <path d={paths[name]} />
  </svg>;
}
