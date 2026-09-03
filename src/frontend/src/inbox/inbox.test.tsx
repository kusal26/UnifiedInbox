import '@testing-library/jest-dom/vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { InboxApi } from '../api/inbox';
import type { AdminApi } from '../api/admin';
import type { AttachmentsApi } from '../api/attachments';
import { AuthProvider } from '../auth/AuthProvider';
import { InboxPage } from './InboxPage';
import { ConversationTimeline } from './ConversationTimeline';

afterEach(cleanup);

const jamie = {
  id: 'c-jamie', contactName: 'Jamie Customer', platform: 'WhatsApp', preview: 'Can you help?', status: 'Open' as const,
  unread: true, updatedAt: '2026-08-11T09:00:00Z',
};

const jamieDetails = {
  id: 'c-jamie', status: 'Open' as const, channelId: 'ch-1', platform: 'WhatsApp', contactId: 'p-1',
  contactName: 'Jamie Customer', phone: '+15550001', email: null, customerNotes: null,
  lastReadSequence: 0, updatedAt: '2026-08-11T09:00:00Z',
};

function apiStub(overrides: Partial<InboxApi> = {}): InboxApi {
  return {
    login: vi.fn(),
    listConversations: vi.fn().mockResolvedValue({ items: [jamie], nextCursor: null }),
    getConversation: vi.fn().mockResolvedValue(jamieDetails),
    getActivity: vi.fn().mockResolvedValue({ items: [], nextCursor: null }),
    addNote: vi.fn(), setStatus: vi.fn(), markRead: vi.fn().mockResolvedValue(jamie), updateCustomerNotes: vi.fn(),
    sendMessage: vi.fn(),
    ...overrides,
  } as InboxApi;
}

function adminStub(overrides: Partial<AdminApi> = {}): AdminApi {
  return { cannedResponses: vi.fn().mockResolvedValue([]), ...overrides } as unknown as AdminApi;
}

function attachmentsStub(overrides: Partial<AttachmentsApi> = {}): AttachmentsApi {
  return { upload: vi.fn(), ...overrides } as unknown as AttachmentsApi;
}

function renderPage(api: InboxApi, admin?: AdminApi, attachments?: AttachmentsApi) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(<QueryClientProvider client={client}><AuthProvider login={vi.fn().mockRejectedValue(new Error('no session'))}><InboxPage api={api} admin={admin} attachments={attachments} /></AuthProvider></QueryClientProvider>);
}

describe('InboxPage', () => {
  it('loads conversations and filters Jamie in Open conversations', async () => {
    renderPage(apiStub());

    expect(await screen.findByText('Jamie Customer')).toBeVisible();
    await userEvent.click(screen.getByRole('button', { name: 'Open' }));
    await userEvent.type(screen.getByLabelText('Search conversations'), 'Jamie');

    expect(screen.getByText('Jamie Customer')).toBeVisible();
  });

  it('loads the next cursor page on demand', async () => {
    const listConversations = vi.fn()
      .mockResolvedValueOnce({ items: [jamie], nextCursor: 'cursor-1' })
      .mockResolvedValueOnce({ items: [], nextCursor: null });
    renderPage(apiStub({ listConversations }));

    await screen.findByText('Jamie Customer');
    await userEvent.click(screen.getByRole('button', { name: 'Load more' }));

    await waitFor(() => expect(listConversations).toHaveBeenCalledTimes(2));
    expect(listConversations.mock.calls[1][0]).toMatchObject({ cursor: 'cursor-1' });
  });

  it('marks a nonempty successfully loaded timeline read through its latest sequence', async () => {
    const markRead = vi.fn().mockResolvedValue(jamie);
    const api = apiStub({
      markRead,
      getActivity: vi.fn().mockResolvedValue({
        items: [{ id: 'a2', conversationId: jamie.id, kind: 'Message', body: 'Hello', createdAt: '2026-08-11T09:01:00Z', sequence: 9 }],
        nextCursor: null,
      }),
    });
    renderPage(api);

    await screen.findByText('Hello');
    await waitFor(() => expect(markRead).toHaveBeenCalledWith(jamie.id, 9));
  });

  it('does not mark a failed or empty timeline as read', async () => {
    const markRead = vi.fn();
    const api = apiStub({ markRead, getActivity: vi.fn().mockResolvedValue({ items: [], nextCursor: null }) });
    renderPage(api);

    await screen.findByText('No activity yet');
    expect(markRead).not.toHaveBeenCalled();
  });
});

describe('ConversationTimeline', () => {
  it('labels internal notes as private to staff', () => {
    render(<ConversationTimeline state="ready" items={[
      { id: 'note-1', conversationId: 'c-jamie', kind: 'InternalNote', body: 'Escalate this', createdAt: '2026-08-11T09:00:00Z', sequence: 1 },
    ]} />);

    expect(screen.getByText('Private to staff')).toBeVisible();
  });
});

describe('conversation actions', () => {
  it('updates a conversation through the explicit status menu with rollback data', async () => {
    const setStatus = vi.fn().mockResolvedValue({ ...jamie, status: 'Pending' });
    renderPage(apiStub({ setStatus }));

    await screen.findByText('Jamie Customer');
    await userEvent.click(screen.getByRole('button', { name: 'Status: Open' }));
    await userEvent.click(screen.getByRole('menuitem', { name: 'Pending' }));

    expect(setStatus).toHaveBeenCalledWith(jamie.id, 'Pending');
  });

  it('sends each reply with a distinct idempotency key', async () => {
    const sendMessage = vi.fn()
      .mockResolvedValueOnce({ id: 'message-1', conversationId: jamie.id, kind: 'Message', body: 'Thanks', createdAt: '', sequence: 10 })
      .mockResolvedValueOnce({ id: 'message-2', conversationId: jamie.id, kind: 'Message', body: 'Again', createdAt: '', sequence: 11 });
    renderPage(apiStub({ sendMessage }));

    await screen.findByText('Jamie Customer');
    const message = screen.getByLabelText('Message');
    await userEvent.type(message, 'Thanks');
    await userEvent.click(screen.getByRole('button', { name: 'Send reply' }));
    await userEvent.type(message, 'Again');
    await userEvent.click(screen.getByRole('button', { name: 'Send reply' }));

    await waitFor(() => expect(sendMessage).toHaveBeenCalledTimes(2));
    expect(sendMessage.mock.calls[0][2]).not.toBe(sendMessage.mock.calls[1][2]);
  });

  it('sends an internal note and labels it as private', async () => {
    const addNote = vi.fn().mockResolvedValue({ id: 'note-2', conversationId: jamie.id, kind: 'InternalNote', body: 'Follow up', createdAt: '', sequence: 10 });
    renderPage(apiStub({ addNote }));

    await screen.findByText('Jamie Customer');
    await userEvent.click(screen.getByRole('button', { name: 'Internal note' }));
    await userEvent.type(screen.getByLabelText('Message'), 'Follow up');
    await userEvent.click(screen.getByRole('button', { name: 'Add note' }));

    await waitFor(() => expect(addNote).toHaveBeenCalledWith(jamie.id, 'Follow up'));
    expect(await screen.findByText('Private to staff')).toBeVisible();
  });

  it('inserts live canned responses and emoji, and uploads attachments', async () => {
    const upload = vi.fn().mockResolvedValue('att-1');
    const sendMessage = vi.fn().mockResolvedValue({ id: 'message-9', conversationId: jamie.id, kind: 'Message', body: 'Thanks', createdAt: '', sequence: 10 });
    const admin = adminStub({ cannedResponses: vi.fn().mockResolvedValue([{ id: 'cr-1', title: 'Thanks for reaching out', shortcut: '/thanks', content: 'Thanks for reaching out' }]) });
    renderPage(apiStub({ sendMessage }), admin, attachmentsStub({ upload }));

    await screen.findByText('Jamie Customer');
    await userEvent.click(screen.getByRole('button', { name: 'Canned responses' }));
    await userEvent.type(screen.getByLabelText('Search canned responses'), 'thanks');
    await userEvent.click(await screen.findByRole('button', { name: 'Thanks for reaching out' }));
    await userEvent.click(screen.getByRole('button', { name: 'Add emoji' }));

    expect(screen.getByLabelText('Message')).toHaveValue('Thanks for reaching out 🙂');

    const file = new File(['bytes'], 'photo.jpg', { type: 'image/jpeg' });
    await userEvent.upload(screen.getByLabelText('Attach files'), file);
    await waitFor(() => expect(upload).toHaveBeenCalled());
    expect(await screen.findByText('Attached: photo.jpg')).toBeVisible();

    await userEvent.click(screen.getByRole('button', { name: 'Send reply' }));
    await waitFor(() => expect(sendMessage).toHaveBeenCalledWith(jamie.id, 'Thanks for reaching out 🙂', expect.any(String), expect.objectContaining({ attachmentIds: ['att-1'] })));
  });

  it('rolls back optimistic replies when sending fails', async () => {
    const sendMessage = vi.fn().mockRejectedValue(new Error('offline'));
    renderPage(apiStub({ sendMessage }));

    await screen.findByText('Jamie Customer');
    await userEvent.type(screen.getByLabelText('Message'), 'Hello?');
    await userEvent.click(screen.getByRole('button', { name: 'Send reply' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('The message could not be sent');
  });
});
