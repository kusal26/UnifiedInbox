import { useState, type FormEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { createAuthApi } from '../api/auth';
import { ApiError } from '../api/client';

export function RegisterPage() {
  const navigate = useNavigate();
  const [error, setError] = useState('');
  const [isPending, setIsPending] = useState(false);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError('');
    setIsPending(true);
    try {
      const data = new FormData(event.currentTarget);
      await createAuthApi(() => null).register({
        workspaceName: String(data.get('workspaceName')),
        workspaceSlug: String(data.get('workspaceSlug')),
        displayName: String(data.get('displayName')),
        email: String(data.get('email')),
        password: String(data.get('password')),
      });
      navigate('/verify-email', { replace: true });
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : 'Registration failed. Please try again.');
    } finally {
      setIsPending(false);
    }
  }

  return <main className="login-page">
    <form className="login-form" onSubmit={handleSubmit} aria-labelledby="register-heading">
      <p className="eyebrow">Unified Inbox</p>
      <h1 id="register-heading">Create your workspace</h1>
      <p>We will email you a verification link before you can sign in.</p>
      {error && <div role="alert" tabIndex={-1}>{error}</div>}
      <label>Workspace name<input name="workspaceName" required autoComplete="organization" /></label>
      <label>Workspace slug<input name="workspaceSlug" required pattern="[a-z0-9-]{3,64}" title="3-64 lowercase letters, numbers, or hyphens" /></label>
      <label>Your name<input name="displayName" required autoComplete="name" /></label>
      <label>Email<input name="email" type="email" required autoComplete="email" /></label>
      <label>Password<input name="password" type="password" required minLength={12} autoComplete="new-password" title="At least 12 characters" /></label>
      <button type="submit" disabled={isPending}>{isPending ? 'Creating workspace…' : 'Create workspace'}</button>
      <p>Already have a workspace? <Link to="/login">Open your workspace</Link></p>
    </form>
  </main>;
}

export function VerifyEmailPage() {
  const [token, setToken] = useState(new URLSearchParams(window.location.search).get('token') ?? '');
  const [status, setStatus] = useState<'idle' | 'pending' | 'verified' | 'error'>('idle');

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setStatus('pending');
    try {
      await createAuthApi(() => null).verifyEmail(token.trim());
      setStatus('verified');
    } catch {
      setStatus('error');
    }
  }

  return <main className="login-page">
    <form className="login-form" onSubmit={handleSubmit} aria-labelledby="verify-heading">
      <p className="eyebrow">Unified Inbox</p>
      <h1 id="verify-heading">Verify your email</h1>
      {status === 'verified'
        ? <p role="status">Your email is verified. <Link to="/login">Open your workspace</Link></p>
        : <>
          <p>Paste the verification token from your email.</p>
          {status === 'error' && <div role="alert">That token is invalid or expired.</div>}
          <label>Verification token<textarea name="token" aria-label="Verification token" value={token} onChange={(event) => setToken(event.target.value)} required /></label>
          <button type="submit" disabled={status === 'pending'}>{status === 'pending' ? 'Verifying…' : 'Verify email'}</button>
        </>}
    </form>
  </main>;
}

export function ForgotPasswordPage() {
  const [email, setEmail] = useState('');
  const [sent, setSent] = useState(false);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    await createAuthApi(() => null).forgotPassword(email.trim());
    setSent(true);
  }

  return <main className="login-page">
    <form className="login-form" onSubmit={handleSubmit} aria-labelledby="forgot-heading">
      <p className="eyebrow">Unified Inbox</p>
      <h1 id="forgot-heading">Reset your password</h1>
      {sent
        ? <p role="status">If the account exists, a reset email is on its way.</p>
        : <>
          <label>Email<input name="email" type="email" value={email} onChange={(event) => setEmail(event.target.value)} required autoComplete="email" /></label>
          <button type="submit">Send reset email</button>
        </>}
      <p><Link to="/login">Back to sign in</Link></p>
    </form>
  </main>;
}

export function ResetPasswordPage() {
  const [token, setToken] = useState(new URLSearchParams(window.location.search).get('token') ?? '');
  const [password, setPassword] = useState('');
  const [status, setStatus] = useState<'idle' | 'pending' | 'done' | 'error'>('idle');

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setStatus('pending');
    try {
      await createAuthApi(() => null).resetPassword(token.trim(), password);
      setStatus('done');
    } catch {
      setStatus('error');
    }
  }

  return <main className="login-page">
    <form className="login-form" onSubmit={handleSubmit} aria-labelledby="reset-heading">
      <p className="eyebrow">Unified Inbox</p>
      <h1 id="reset-heading">Choose a new password</h1>
      {status === 'done'
        ? <p role="status">Your password was reset. <Link to="/login">Open your workspace</Link></p>
        : <>
          {status === 'error' && <div role="alert">That token is invalid or expired, or the password is too short.</div>}
          <label>Reset token<textarea name="token" aria-label="Reset token" value={token} onChange={(event) => setToken(event.target.value)} required /></label>
          <label>New password<input name="password" type="password" value={password} onChange={(event) => setPassword(event.target.value)} required minLength={12} autoComplete="new-password" /></label>
          <button type="submit" disabled={status === 'pending'}>{status === 'pending' ? 'Resetting…' : 'Reset password'}</button>
        </>}
    </form>
  </main>;
}
