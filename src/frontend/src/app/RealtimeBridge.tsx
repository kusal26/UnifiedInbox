import { useEffect } from 'react';
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { useQueryClient } from '@tanstack/react-query';

const eventTypes = ['conversation.created', 'conversation.updated', 'message.created', 'message.statusChanged', 'note.created', 'channel.updated', 'notification.created'];

export function RealtimeBridge({ token }: { token: string }) {
  const queryClient = useQueryClient();
  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/inbox', { accessTokenFactory: () => token })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();

    const handle = (type: string) => {
      // Targeted cache updates per event type. Message payloads carry only the
      // entity id, so open timelines refetch while lists update precisely.
      switch (type) {
        case 'message.created':
        case 'message.statusChanged':
        case 'note.created':
          void queryClient.invalidateQueries({ queryKey: ['activity'] });
          void queryClient.invalidateQueries({ queryKey: ['conversations'] });
          break;
        case 'conversation.created':
        case 'conversation.updated':
          void queryClient.invalidateQueries({ queryKey: ['conversations'] });
          break;
        case 'channel.updated':
          void queryClient.invalidateQueries({ queryKey: ['channels'] });
          break;
        case 'notification.created':
          void queryClient.invalidateQueries({ queryKey: ['notifications'] });
          void queryClient.invalidateQueries({ queryKey: ['unread-count'] });
          break;
        default:
          void queryClient.invalidateQueries();
          break;
      }
      window.dispatchEvent(new Event('inbox:refresh'));
    };

    for (const event of eventTypes) connection.on(event, () => handle(event));
    connection.onreconnected(() => {
      // Targeted refresh on reconnect: resync everything that may have changed while away.
      void queryClient.invalidateQueries({ queryKey: ['conversations'] });
      void queryClient.invalidateQueries({ queryKey: ['activity'] });
      void queryClient.invalidateQueries({ queryKey: ['notifications'] });
      void queryClient.invalidateQueries({ queryKey: ['channels'] });
      window.dispatchEvent(new Event('inbox:refresh'));
    });
    void connection.start().catch(() => undefined);
    return () => { void connection.stop(); };
  }, [queryClient, token]);
  return null;
}
