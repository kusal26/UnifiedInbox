import type { SVGProps } from 'react';

export type IconName = 'bell' | 'chevron-down' | 'inbox' | 'log-out' | 'search';

const paths: Record<IconName, string> = {
  bell: 'M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9M10 21h4',
  'chevron-down': 'm7 10 5 5 5-5',
  inbox: 'M4 5h16v14H4zM4 15h4l2 3h4l2-3h4',
  'log-out': 'M10 17l5-5-5-5M15 12H3M21 4v16',
  search: 'm21 21-4.35-4.35M19 11a8 8 0 1 1-16 0 8 8 0 0 1 16 0Z',
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
