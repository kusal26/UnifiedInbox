import { useState, type FormEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { createAdminApi } from '../api/admin';
import { ApiError } from '../api/client';

export function AcceptInvitationPage() {
  const navigate = useNavigate();
  const [token, setToken] = useState(new URLSearchParams(window.location.search).get('token') ?? '');
  const [displayName, setDisplayName] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [isPending, setIsPending] = useState(false);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError('');
    setIsPending(true);
    try {
      await createAdminApi(() => null).acceptInvitation(token.trim(), displayName.trim(), password);
      navigate('/login', { replace: true });
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : 'This invitation is invalid or expired.');
    } finally {
      setIsPending(false);
    }
  }

  return <main className="login-page">
    <form className="login-form" onSubmit={handleSubmit} aria-labelledby="invite-heading">
      <p className="eyebrow">Unified Inbox</p>
      <h1 id="invite-heading">Accept your invitation</h1>
      <p>Your workspace invited you by email. Choose your name and password to join.</p>
      {error && <div role="alert" tabIndex={-1}>{error}</div>}
      <label>Invitation token<textarea name="token" aria-label="Invitation token" value={token} onChange={(event) => setToken(event.target.value)} required /></label>
      <label>Your name<input name="displayName" value={displayName} onChange={(event) => setDisplayName(event.target.value)} required autoComplete="name" /></label>
      <label>Password<input name="password" type="password" value={password} onChange={(event) => setPassword(event.target.value)} required minLength={12} autoComplete="new-password" /></label>
      <button type="submit" disabled={isPending}>{isPending ? 'Joining…' : 'Join workspace'}</button>
      <p><Link to="/login">Back to sign in</Link></p>
    </form>
  </main>;
}
