import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useAuth } from '../auth/AuthProvider';
import { createAuthApi, type CurrentUser } from './auth';
import { createAdminApi, type AdminApi } from './admin';
import { createAttachmentsApi, type AttachmentsApi } from './attachments';
import { createInboxApi, type InboxApi } from './inbox';

export function useClients(): { inbox: InboxApi; auth: ReturnType<typeof createAuthApi>; admin: AdminApi; attachments: AttachmentsApi } {
  const { token } = useAuth();
  return useMemo(() => ({
    inbox: createInboxApi(() => token),
    auth: createAuthApi(() => token),
    admin: createAdminApi(() => token),
    attachments: createAttachmentsApi(() => token),
  }), [token]);
}

export function useMe(): { user?: CurrentUser; isPending: boolean; isError: boolean } {
  const { token } = useAuth();
  const query = useQuery({
    queryKey: ['me'],
    queryFn: () => createAuthApi(() => token).me(),
    enabled: Boolean(token),
    staleTime: 60_000,
  });
  return { user: query.data, isPending: query.isPending, isError: query.isError };
}

export function canAdmin(user?: CurrentUser): boolean {
  return user?.role === 'Owner' || user?.role === 'Admin';
}

export function isOwner(user?: CurrentUser): boolean {
  return user?.role === 'Owner';
}
