import { useEffect, useRef, useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { useAuth } from './AuthProvider';

export function LoginPage() {
  const { login } = useAuth();
  const navigate = useNavigate();
  const errorRef = useRef<HTMLDivElement>(null);
  const [tenantSlug, setTenantSlug] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [isPending, setIsPending] = useState(false);

  useEffect(() => {
    if (error) errorRef.current?.focus();
  }, [error]);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError('');
    setIsPending(true);
    try {
      await login({ tenantSlug, email, password });
      navigate('/', { replace: true });
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to open inbox. Please try again.');
    } finally {
      setIsPending(false);
    }
  }

  return <main className="login-page">
    <form className="login-form" onSubmit={handleSubmit} aria-labelledby="login-heading">
      <p className="eyebrow">Unified Inbox</p>
      <h1 id="login-heading">Open your workspace</h1>
      <p>Sign in to manage your team’s conversations.</p>
      {error && <div ref={errorRef} role="alert" tabIndex={-1}>{error}</div>}
      <label>
        Workspace slug
        <input name="tenantSlug" value={tenantSlug} onChange={(event) => setTenantSlug(event.target.value)} autoComplete="organization" required />
      </label>
      <label>
        Email
        <input name="email" type="email" value={email} onChange={(event) => setEmail(event.target.value)} autoComplete="email" required />
      </label>
      <label>
        Password
        <input name="password" type="password" value={password} onChange={(event) => setPassword(event.target.value)} autoComplete="current-password" required />
      </label>
      <button type="submit" disabled={isPending}>{isPending ? 'Opening inbox…' : 'Open inbox'}</button>
    </form>
  </main>;
}
