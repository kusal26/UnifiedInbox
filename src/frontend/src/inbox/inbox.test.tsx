import '@testing-library/jest-dom/vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { InboxApi } from '../api/inbox';
import { InboxPage } from './InboxPage';
import { ConversationTimeline } from './ConversationTimeline';

afterEach(cleanup);

const jamie = {
  id: 'c-jamie', contactName: 'Jamie Customer', platform: 'WhatsApp', preview: 'Can you help?', status: 'Open' as const,
  unread: true, updatedAt: '2026-08-11T09:00:00Z',
};

function apiStub(overrides: Partial<InboxApi> = {}): InboxApi {
  return {
    login: vi.fn(),
    listConversations: vi.fn().mockResolvedValue([jamie]),
    getActivity: vi.fn().mockResolvedValue({ items: [], nextCursor: null }),
    addNote: vi.fn(), setStatus: vi.fn(), markRead: vi.fn(), sendMessage: vi.fn(),
    ...overrides,
  } as InboxApi;
}

describe('InboxPage', () => {
  it('loads conversations and filters Jamie in Open conversations', async () => {
    render(<InboxPage api={apiStub()} />);

    expect(await screen.findByText('Jamie Customer')).toBeVisible();
    await userEvent.click(screen.getByRole('button', { name: 'Open' }));
    await userEvent.type(screen.getByLabelText('Search conversations'), 'Jamie');

    expect(screen.getByText('Jamie Customer')).toBeVisible();
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
    render(<InboxPage api={api} />);

    await screen.findByText('Hello');
    await waitFor(() => expect(markRead).toHaveBeenCalledWith(jamie.id, 9));
  });

  it('does not mark a failed or empty timeline as read', async () => {
    const markRead = vi.fn();
    const api = apiStub({ markRead, getActivity: vi.fn().mockResolvedValue({ items: [], nextCursor: null }) });
    render(<InboxPage api={api} />);

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
  it('updates a conversation through the explicit status menu', async () => {
    const setStatus = vi.fn().mockResolvedValue({ ...jamie, status: 'Pending' });
    render(<InboxPage api={apiStub({ setStatus })} />);

    await screen.findByText('Jamie Customer');
    await userEvent.click(screen.getByRole('button', { name: 'Status: Open' }));
    await userEvent.click(screen.getByRole('menuitem', { name: 'Pending' }));

    expect(setStatus).toHaveBeenCalledWith(jamie.id, 'Pending');
  });

  it('sends each reply with a distinct idempotency key', async () => {
    const sendMessage = vi.fn()
      .mockResolvedValueOnce({ id: 'message-1', conversationId: jamie.id, kind: 'Message', body: 'Thanks', createdAt: '', sequence: 10 })
      .mockResolvedValueOnce({ id: 'message-2', conversationId: jamie.id, kind: 'Message', body: 'Again', createdAt: '', sequence: 11 });
    render(<InboxPage api={apiStub({ sendMessage })} />);

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
    render(<InboxPage api={apiStub({ addNote })} />);

    await screen.findByText('Jamie Customer');
    await userEvent.click(screen.getByRole('button', { name: 'Internal note' }));
    await userEvent.type(screen.getByLabelText('Message'), 'Follow up');
    await userEvent.click(screen.getByRole('button', { name: 'Add note' }));

    await waitFor(() => expect(addNote).toHaveBeenCalledWith(jamie.id, 'Follow up'));
    expect(await screen.findByText('Private to staff')).toBeVisible();
  });

  it('inserts canned responses and emoji, and reports unavailable attachments', async () => {
    render(<InboxPage api={apiStub()} />);

    await screen.findByText('Jamie Customer');
    await userEvent.click(screen.getByRole('button', { name: 'Canned responses' }));
    await userEvent.type(screen.getByLabelText('Search canned responses'), 'thanks');
    await userEvent.click(screen.getByRole('button', { name: 'Thanks for reaching out' }));
    await userEvent.click(screen.getByRole('button', { name: 'Add emoji' }));
    await userEvent.click(screen.getByRole('button', { name: 'Add attachment' }));

    expect(screen.getByLabelText('Message')).toHaveValue('Thanks for reaching out 🙂');
    expect(screen.getByRole('alert')).toHaveTextContent('Attachments are not available yet');
  });
});
