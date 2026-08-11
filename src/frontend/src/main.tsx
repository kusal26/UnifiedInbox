import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './app/App';
import { AuthProvider } from './auth/AuthProvider';
import { ToastProvider } from './components/ToastProvider';
import './styles.css';

createRoot(document.getElementById('root')!).render(
  <StrictMode><ToastProvider><AuthProvider><App /></AuthProvider></ToastProvider></StrictMode>,
);
