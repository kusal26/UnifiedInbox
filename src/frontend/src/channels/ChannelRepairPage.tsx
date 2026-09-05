import { useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { canAdmin, useClients, useMe } from '../api/hooks';
import { WorkspacePage } from '../workspace/WorkspacePages';
import { EmbeddedSignupButton } from './EmbeddedSignupButton';

export function ChannelRepairPage() {
  const { admin } = useClients();
  const { user } = useMe();
  const { channelId } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [attempt, setAttempt] = useState<Awaited<ReturnType<typeof admin.beginReauthorize>> | null>(null);
  const [result, setResult] = useState('');
  const [error, setError] = useState('');
  const [pending, setPending] = useState(false);

  const channels = useQuery({ queryKey: ['channels'], queryFn: () => admin.channels(), enabled: Boolean(user) && canAdmin(user) });
  const channel = channels.data?.find((item) => item.id === channelId);

  const start = async () => {
    if (!channelId || pending) return;
    setPending(true);
    setResult('');
    setError('');
    try {
      setAttempt(await admin.beginReauthorize(channelId));
    } catch (err) {
      setError(err instanceof Error ? err.message : 'The repair attempt could not be started.');
    } finally { setPending(false); }
  };

  const finish = async (session: { code: string; phoneNumberId: string; businessId: string }) => {
    if (!attempt || !channel) return;
    setError('');
    try {
      await admin.completeConnect({ state: attempt.state, nonce: attempt.nonce, code: session.code, phoneNumberId: session.phoneNumberId, businessId: session.businessId, displayName: channel.displayName || channel.externalAccountId });
      setAttempt(null);
      setResult(`Access restored for ${channel.displayName || channel.externalAccountId}.`);
      await queryClient.invalidateQueries({ queryKey: ['channels'] });
      navigate('/channels');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'The connection could not be repaired. Try again.');
      setAttempt(null);
    }
  };

  if (!canAdmin(user)) return <WorkspacePage title="Repair channel access"><p role="alert">You need an administrator role to repair a channel.</p></WorkspacePage>;

  return <WorkspacePage title="Repair channel access">
    {channels.isPending && <p role="status">Loading…</p>}
    {channel
      ? <div className="workspace-card">
          <h2>{channel.displayName || channel.platform}</h2>
          {!attempt
            ? <form className="form-stack" onSubmit={(event) => { event.preventDefault(); void start(); }}>
                <p>Reconnect this WhatsApp number through Meta Embedded Signup. The previous access was revoked or rejected by the provider.</p>
                <div className="button-row"><button type="submit" disabled={pending}>{pending ? 'Starting…' : 'Start repair'}</button></div>
              </form>
            : <EmbeddedSignupButton attempt={attempt} onSession={(session) => void finish(session)} onError={setError} />}
          {result && <p role="status">{result}</p>}
          {error && <p role="alert">{error}</p>}
        </div>
      : !channels.isPending && <p role="alert">The channel could not be found.</p>}
  </WorkspacePage>;
}
