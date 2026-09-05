import { useEffect, useRef, type ButtonHTMLAttributes, type HTMLAttributes, type PropsWithChildren } from 'react';

export function Button({ children, variant = 'secondary', className = '', ...props }: PropsWithChildren<ButtonHTMLAttributes<HTMLButtonElement> & { variant?: 'primary' | 'secondary' | 'danger' }>) {
  return <button type="button" className={`${variant} ${className}`} {...props}>{children}</button>;
}

export function Panel({ children, ...props }: PropsWithChildren<HTMLAttributes<HTMLElement>>) {
  return <section {...props}>{children}</section>;
}

export function Avatar({ name, src }: { name: string; src?: string }) {
  return src ? <img src={src} alt={name} /> : <span role="img" aria-label={name}>{name.slice(0, 1).toUpperCase()}</span>;
}

export function StatusBadge({ status }: { status: string }) {
  return <span aria-label={`Status: ${status}`}>{status}</span>;
}

export function EmptyState({ title, children }: PropsWithChildren<{ title: string }>) {
  return <section className="empty-state" aria-label={title}><h2>{title}</h2>{children}</section>;
}

export function LoadingState({ label = 'Loading' }: { label?: string }) {
  return <p role="status" aria-live="polite">{label}</p>;
}

export function ErrorState({ message, onRetry }: { message: string; onRetry?: () => void }) {
  return <section role="alert"><p>{message}</p>{onRetry && <Button onClick={onRetry}>Try again</Button>}</section>;
}

export function FormError({ message }: { message: string }) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => { ref.current?.focus(); }, [message]);
  return <div ref={ref} tabIndex={-1} role="alert">{message}</div>;
}
