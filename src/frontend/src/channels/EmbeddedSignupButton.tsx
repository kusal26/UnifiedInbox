import { useEffect, useRef, useState } from 'react';
import type { ConnectionAttempt } from '../api/admin';

export interface EmbeddedSignupSession {
  code: string;
  phoneNumberId: string;
  businessId: string;
}

interface MetaSdk {
  init(options: Record<string, unknown>): void;
  login(callback: (response: { authResponse?: { code?: string }; status?: string }) => void, options: Record<string, unknown>): void;
}

declare global {
  interface Window {
    FB?: MetaSdk;
    fbAsyncInit?: () => void;
  }
}

export const META_SDK_URL = 'https://connect.facebook.net/en_US/sdk.js';

export const EMBEDDED_SIGNUP_ORIGINS = ['https://www.facebook.com', 'https://business.facebook.com'];

function asRecord(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === 'object' ? (value as Record<string, unknown>) : null;
}

function firstString(root: Record<string, unknown>, keys: string[]): string | null {
  for (const key of keys) {
    const value = root[key];
    if (typeof value === 'string' && value.trim()) return value;
  }
  return null;
}

export function extractSignupSession(event: MessageEvent): EmbeddedSignupSession | null {
  if (!EMBEDDED_SIGNUP_ORIGINS.includes(event.origin)) return null;
  const outer = asRecord(event.data);
  if (!outer) return null;
  const root = asRecord(outer.data) ?? outer;
  const code = firstString(root, ['code', 'authorizationCode']);
  const phoneNumberId = firstString(root, ['phone_number_id', 'phoneNumberId']);
  const businessId = firstString(root, ['business_id', 'waba_id', 'businessId']);
  if (!code || !phoneNumberId || !businessId) return null;
  return { code, phoneNumberId, businessId };
}

export function loadMetaSdk(url: string = META_SDK_URL, timeoutMs = 15_000): Promise<MetaSdk> {
  return new Promise((resolve, reject) => {
    if (window.FB) return resolve(window.FB);
    const existing = document.querySelector<HTMLScriptElement>(`script[src="${url}"]`);
    const timer = window.setTimeout(() => reject(new Error('The Meta SDK timed out while loading.')), timeoutMs);
    const cleanup = () => window.clearTimeout(timer);
    const onReady = () => {
      cleanup();
      if (window.FB) { resolve(window.FB); return; }
      // A dead script element from a previous failed attempt would never fire load again; drop it
      // so a later retry re-inserts a fresh node.
      document.querySelectorAll<HTMLScriptElement>(`script[src="${url}"]`).forEach((node) => node.remove());
      reject(new Error('The Meta SDK did not initialise.'));
    };
    const onError = () => {
      cleanup();
      document.querySelectorAll<HTMLScriptElement>(`script[src="${url}"]`).forEach((node) => node.remove());
      reject(new Error('Could not load the Meta SDK.'));
    };
    if (existing) {
      existing.addEventListener('load', onReady);
      existing.addEventListener('error', onError);
      return;
    }
    const script = document.createElement('script');
    script.src = url;
    script.async = true;
    script.addEventListener('load', onReady);
    script.addEventListener('error', onError);
    window.fbAsyncInit = onReady;
    document.head.appendChild(script);
  });
}

interface EmbeddedSignupButtonProps {
  attempt: ConnectionAttempt;
  label?: string;
  onSession(session: EmbeddedSignupSession): void | Promise<void>;
  onError?(error: string): void;
}

export function EmbeddedSignupButton({ attempt, label = 'Continue in the Meta popup', onSession, onError }: EmbeddedSignupButtonProps) {
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState('');
  const launchedRef = useRef(false);
  const deliveredRef = useRef(false);
  const sessionRef = useRef(onSession);
  sessionRef.current = onSession;
  const errorRef = useRef(onError);
  errorRef.current = onError;

  useEffect(() => {
    const handler = (event: MessageEvent) => {
      const session = extractSignupSession(event);
      if (!session || deliveredRef.current) return;
      deliveredRef.current = true;
      launchedRef.current = false;
      setBusy(false);
      setNotice('');
      void sessionRef.current(session);
    };
    window.addEventListener('message', handler);
    return () => window.removeEventListener('message', handler);
  }, []);

  const launch = async () => {
    if (busy || launchedRef.current) return;
    launchedRef.current = true;
    setBusy(true);
    setNotice('Opening Meta Embedded Signup…');
    try {
      const FB = await loadMetaSdk();
      FB.init({ appId: attempt.metaAppId, version: attempt.graphVersion, cookie: false, xfbml: false });
      FB.login((response) => {
        // WhatsApp Embedded Signup delivers the authorization code and session
        // identifiers through a postMessage from Meta's popup. The login callback
        // only reports the dialog outcome, so we surface cancel/denial here and
        // wait for the validated session payload to complete the connection.
        if (!response?.authResponse) {
          launchedRef.current = false;
          setBusy(false);
          setNotice('The Meta signup popup was closed before the connection completed. Try again.');
        }
      }, {
        config_id: attempt.configurationId,
        response_type: 'code',
        override_default_response_type: true,
        extras: { session_version: attempt.embeddedSignupVersion },
      });
      setNotice('Complete the signup in the Meta popup. This attempt expires at ' + new Date(attempt.expiresAt).toLocaleTimeString() + '.');
    } catch (error) {
      launchedRef.current = false;
      setBusy(false);
      const message = error instanceof Error ? error.message : 'Meta signup could not be started.';
      setNotice(message);
      errorRef.current?.(message);
    }
  };

  return <span className="embedded-signup">
    <button type="button" onClick={() => void launch()} disabled={busy}>{busy ? 'Opening Meta…' : label}</button>
    {notice && <p role="status">{notice}</p>}
  </span>;
}
