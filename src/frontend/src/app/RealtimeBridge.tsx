import { useEffect } from 'react';
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { useQueryClient } from '@tanstack/react-query';

const events = ['conversation.created', 'conversation.updated', 'message.created', 'message.statusChanged', 'note.created', 'channel.updated', 'notification.created'];

export function RealtimeBridge({ token }: { token: string }) {
  const queryClient = useQueryClient();
  useEffect(() => {
    const connection = new HubConnectionBuilder().withUrl('/hubs/inbox', { accessTokenFactory: () => token }).withAutomaticReconnect().configureLogging(LogLevel.Warning).build();
    for (const event of events) connection.on(event, () => { void queryClient.invalidateQueries(); window.dispatchEvent(new Event('inbox:refresh')); });
    void connection.start();
    return () => { void connection.stop(); };
  }, [queryClient, token]);
  return null;
}
