import { useRef, useState } from 'react';

/** Shared feedback for existing async operations; does not change their requests. */
export function useAction() {
  const lock = useRef(false);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  async function run(action: () => Promise<unknown>, success = '') {
    if (lock.current) return;
    lock.current = true; setPending(true); setError(''); setNotice('');
    try { await action(); setNotice(success); }
    catch (error) { setError(error instanceof Error ? error.message : 'This action could not be completed. Please try again.'); }
    finally { lock.current = false; setPending(false); }
  }
  return { pending, error, notice, run, feedback: <>{error && <p role="alert">{error}</p>}{notice && <p role="status" className="success-notice">{notice}</p>}</> };
}
