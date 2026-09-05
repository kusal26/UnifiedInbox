import { createContext, useCallback, useContext, useState, type PropsWithChildren } from 'react';

export type ToastKind = 'success' | 'error' | 'info';
export interface ToastContextValue { showToast(message: string, kind?: ToastKind): void }
interface Toast { id: number; message: string; kind: ToastKind }
const ToastContext = createContext<ToastContextValue | null>(null);

export function ToastProvider({ children }: PropsWithChildren) {
  const [toasts, setToasts] = useState<Toast[]>([]);
  const showToast = useCallback((message: string, kind: ToastKind = 'info') => {
    const id = Date.now() + Math.random();
    setToasts((current) => [...current, { id, message, kind }]);
    window.setTimeout(() => setToasts((current) => current.filter((toast) => toast.id !== id)), 4000);
  }, []);
  return <ToastContext.Provider value={{ showToast }}>
    {children}
    <div className="toast-stack" aria-live="polite" aria-atomic="true" role="status">
      {toasts.map((toast) => <div key={toast.id} data-kind={toast.kind}>{toast.message}</div>)}
    </div>
  </ToastContext.Provider>;
}

export function useToast(): ToastContextValue {
  const context = useContext(ToastContext);
  if (!context) throw new Error('useToast must be used within a ToastProvider');
  return context;
}
