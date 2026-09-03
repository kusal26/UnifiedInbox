import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { App } from './app/App';
import { AuthProvider } from './auth/AuthProvider';
import { ToastProvider } from './components/ToastProvider';
import './styles.css';

const queryClient = new QueryClient({ defaultOptions: { queries: { staleTime: 15_000, retry: 1 } } });
createRoot(document.getElementById('root')!).render(
  <StrictMode><QueryClientProvider client={queryClient}><ToastProvider><AuthProvider><App /></AuthProvider></ToastProvider></QueryClientProvider></StrictMode>,
);
