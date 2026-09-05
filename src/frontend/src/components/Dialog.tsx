import { useEffect, useId, useRef, type PropsWithChildren } from 'react';

/** Native modal behavior supplies focus containment, Escape and inert background. */
export function Dialog({ title, onClose, children, className = '' }: PropsWithChildren<{ title: string; onClose(): void; className?: string }>) {
  const ref = useRef<HTMLDialogElement>(null);
  const titleId = useId();
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    const node = ref.current;
    // jsdom (unit tests) has no native dialog methods; the browser path stays native.
    if (node && typeof node.showModal === 'function' && !node.open) node.showModal();
    return () => { if (node && typeof node.close === 'function' && node.open) node.close(); previous?.focus?.(); };
  }, []);
  return <dialog ref={ref} className={`dialog ${className}`} aria-labelledby={titleId} onCancel={onClose} onClick={event => { if (event.target === ref.current) onClose(); }}>
    <div className="dialog-surface"><header className="dialog-header"><h2 id={titleId}>{title}</h2><button type="button" className="icon-button" aria-label={`Close ${title}`} onClick={onClose}>×</button></header>{children}</div>
  </dialog>;
}
