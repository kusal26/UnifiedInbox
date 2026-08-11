# Unified Inbox UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver a complete, responsive React workspace that matches `preview.html`, while using the current stub API for inbox data and clearly-labelled local state for management views.

**Architecture:** Replace the monolithic frontend entry point with feature modules. An `AuthProvider` owns the session token; an API client owns authenticated HTTP and error normalization; the inbox owns server-backed state; and workspace pages use local demo data only when the stub API lacks the required endpoint. Shared components own visual primitives, toast feedback, and accessible interaction patterns.

**Tech Stack:** React 19, TypeScript, Vite, React Router 7, Vitest, Testing Library, existing ASP.NET Core stub API.

---

## Target file structure

```text
src/frontend/src/
  app/App.tsx                         application routes and shell composition
  app/AppShell.tsx                    rail, top bar, mobile workspace controls
  app/routes.ts                       route names, labels, and page metadata
  api/client.ts                       typed API request/error boundary
  api/inbox.ts                        inbox API request functions and contracts
  auth/AuthProvider.tsx               in-memory token and login/logout actions
  auth/LoginPage.tsx                  accessible workspace login
  components/Icon.tsx                 shared icon glyphs and accessible names
  components/ToastProvider.tsx        transient feedback and retry-safe state
  components/ui.tsx                   Avatar, Button, EmptyState, Panel, StatusBadge
  data/workspaceDemo.ts               explicitly local management data and actions
  inbox/ConversationList.tsx          filtering/searchable conversation list
  inbox/ConversationTimeline.tsx      message/note rendering and timeline states
  inbox/CustomerPanel.tsx             desktop customer context / tablet drawer
  inbox/InboxPage.tsx                 query orchestration and responsive selection
  inbox/MessageComposer.tsx           reply/note mode and idempotent send path
  inbox/inbox.test.tsx                inbox interaction coverage
  workspace/WorkspacePages.tsx        overview, management, audit, settings pages
  workspace/workspace.test.tsx        navigation and demo-state coverage
  styles.css                           design tokens, shared UI, responsive layout
  main.tsx                             React root only
```

## Task 1: Establish testable app foundations

**Files:**
- Create: `src/frontend/src/api/client.ts`
- Create: `src/frontend/src/api/inbox.ts`
- Create: `src/frontend/src/auth/AuthProvider.tsx`
- Create: `src/frontend/src/components/ToastProvider.tsx`
- Create: `src/frontend/src/components/ui.tsx`
- Create: `src/frontend/src/app/routes.ts`
- Test: `src/frontend/src/api/inbox.test.ts`
- Modify: `src/frontend/src/api/activity.ts`

- [ ] **Step 1: Write failing API-client tests**

```tsx
import { describe, expect, it, vi } from 'vitest';
import { createInboxApi } from './inbox';

it('adds the bearer token and idempotency key when sending', async () => {
  const fetcher = vi.fn().mockResolvedValue(new Response(JSON.stringify({ id: 'm1' }), { status: 200 }));
  await createInboxApi(() => 'token-1', fetcher).sendMessage('c1', 'Hello', 'key-1');
  expect(fetcher).toHaveBeenCalledWith('/api/v1/conversations/c1/messages', expect.objectContaining({
    headers: expect.objectContaining({ Authorization: 'Bearer token-1', 'Idempotency-Key': 'key-1' })
  }));
});
```

- [ ] **Step 2: Run the focused test to verify failure**

Run: `bun --cwd src/frontend test --run src/api/inbox.test.ts`

Expected: FAIL because `createInboxApi` does not exist.

- [ ] **Step 3: Implement the typed API boundary**

Create an `ApiError` with `status` and `message`; make `request<T>` JSON-encode bodies and decode non-2xx responses. Export `createInboxApi(getToken, fetcher = fetch)` with `login`, `listConversations`, `getActivity`, `addNote`, `setStatus`, `markRead`, and `sendMessage`. Preserve `normalizeActivity` and make `getActivity` return `{ items, nextCursor }`.

```ts
export type ConversationStatus = 'Open' | 'Pending' | 'Closed';
export type Activity = { id: string; kind: 'Message' | 'InternalNote'; body: string; sequence: number; createdAt: string; senderUserId?: string; status?: string };
export const createInboxApi = (getToken: () => string | null, fetcher: typeof fetch = fetch) => ({
  sendMessage: (id: string, body: string, key: string) => request(`/api/v1/conversations/${id}/messages`, {
    method: 'POST', token: getToken(), headers: { 'Idempotency-Key': key }, body: { body }, fetcher
  })
});
```

- [ ] **Step 4: Add session and feedback providers**

`AuthProvider` must expose `{ token, login, logout }`, retain the token only in React state, and call the API login endpoint. `ToastProvider` must expose `showToast(message, kind)` and render a polite `aria-live` region. Add small reusable `Button`, `Panel`, `Avatar`, `StatusBadge`, `EmptyState`, `LoadingState`, and `ErrorState` components with semantic props.

- [ ] **Step 5: Run API and existing utility tests**

Run: `bun --cwd src/frontend test --run src/api/inbox.test.ts src/api/activity.test.ts`

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/frontend/src/api src/frontend/src/auth src/frontend/src/components src/frontend/src/app/routes.ts
git commit -m "feat(ui): add frontend API and session foundations"
```

## Task 2: Build the application shell and navigation

**Files:**
- Create: `src/frontend/src/app/App.tsx`
- Create: `src/frontend/src/app/AppShell.tsx`
- Create: `src/frontend/src/components/Icon.tsx`
- Test: `src/frontend/src/app/App.test.tsx`
- Modify: `src/frontend/src/main.tsx`
- Modify: `src/frontend/src/styles.css`

- [ ] **Step 1: Write failing shell navigation test**

```tsx
it('navigates from Shared Inbox to Channels with an accessible current-page marker', async () => {
  render(<App />);
  await userEvent.click(screen.getByRole('link', { name: 'Channels' }));
  expect(screen.getByRole('heading', { name: 'Channels' })).toBeVisible();
  expect(screen.getByRole('link', { name: 'Channels' })).toHaveAttribute('aria-current', 'page');
});
```

- [ ] **Step 2: Run the shell test to verify failure**

Run: `bun --cwd src/frontend test --run src/app/App.test.tsx`

Expected: FAIL because the app shell and routes do not exist.

- [ ] **Step 3: Implement routes and shell**

Use `BrowserRouter` routes for `/`, `/overview`, `/channels`, `/team`, `/canned`, `/audit`, and `/settings`. Define nav data once in `routes.ts`:

```ts
export const workspaceRoutes = [
  { path: '/', label: 'Shared Inbox', icon: 'inbox' },
  { path: '/overview', label: 'Overview', icon: 'chart' },
  { path: '/channels', label: 'Channels', icon: 'channels' },
  { path: '/team', label: 'Team', icon: 'team' },
  { path: '/canned', label: 'Canned Responses', icon: 'sparkle' },
  { path: '/audit', label: 'Audit Log', icon: 'history' },
  { path: '/settings', label: 'Settings', icon: 'settings' }
] as const;
```

The shell renders a labelled `<nav>`, workspace identity, active unread badge, global search control, notification button, and logout control. `Icon` uses a fixed internal SVG/glyph map and requires an `aria-label` when it is a standalone button.

- [ ] **Step 4: Replace the bootstrap entry point**

`main.tsx` must only render providers and `<App />`:

```tsx
createRoot(document.getElementById('root')!).render(
  <StrictMode><ToastProvider><AuthProvider><App /></AuthProvider></ToastProvider></StrictMode>
);
```

- [ ] **Step 5: Add responsive shell styling**

Define CSS variables for the reference palette and use 240px rail / 68px topbar desktop dimensions. At `max-width: 860px`, condense rail labels; at `max-width: 680px`, make the rail a bottom navigation with text labels and preserve page headings. Ensure every visible focusable element has a `:focus-visible` outline.

- [ ] **Step 6: Run shell tests and production build**

Run: `bun --cwd src/frontend test --run src/app/App.test.tsx; bun --cwd src/frontend run build`

Expected: PASS and Vite exits 0.

- [ ] **Step 7: Commit**

```powershell
git add src/frontend/src/app src/frontend/src/components/Icon.tsx src/frontend/src/main.tsx src/frontend/src/styles.css
git commit -m "feat(ui): add responsive workspace shell"
```

## Task 3: Implement login and protected app entry

**Files:**
- Create: `src/frontend/src/auth/LoginPage.tsx`
- Test: `src/frontend/src/auth/LoginPage.test.tsx`
- Modify: `src/frontend/src/app/App.tsx`
- Modify: `src/frontend/src/auth/AuthProvider.tsx`

- [ ] **Step 1: Write failing login form test**

```tsx
it('submits the workspace, email, and password then enters the inbox', async () => {
  render(<LoginPage />);
  await userEvent.type(screen.getByLabelText('Workspace slug'), 'acme');
  await userEvent.type(screen.getByLabelText('Email'), 'agent@acme.test');
  await userEvent.type(screen.getByLabelText('Password'), 'demo');
  await userEvent.click(screen.getByRole('button', { name: 'Open inbox' }));
  await waitFor(() => expect(login).toHaveBeenCalledWith({ tenantSlug: 'acme', email: 'agent@acme.test', password: 'demo' }));
});
```

- [ ] **Step 2: Run the login test to verify failure**

Run: `bun --cwd src/frontend test --run src/auth/LoginPage.test.tsx`

Expected: FAIL because the component is absent.

- [ ] **Step 3: Implement login and auth gate**

Render a labelled form with workspace slug, email, password, submit pending state, and server error. On success set only the provider token and navigate to `/`; on failure retain entered fields and place focus on the error summary. `App` must route unauthenticated users to this page and prevent shell routes from rendering until a token exists.

- [ ] **Step 4: Run focused auth tests**

Run: `bun --cwd src/frontend test --run src/auth/LoginPage.test.tsx src/app/App.test.tsx`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/frontend/src/auth src/frontend/src/app/App.tsx
git commit -m "feat(ui): add workspace login flow"
```

## Task 4: Build API-backed conversation triage and timeline

**Files:**
- Create: `src/frontend/src/inbox/InboxPage.tsx`
- Create: `src/frontend/src/inbox/ConversationList.tsx`
- Create: `src/frontend/src/inbox/ConversationTimeline.tsx`
- Create: `src/frontend/src/inbox/CustomerPanel.tsx`
- Test: `src/frontend/src/inbox/inbox.test.tsx`
- Modify: `src/frontend/src/styles.css`

- [ ] **Step 1: Write failing inbox behavior tests**

```tsx
it('filters API conversations by status and query', async () => {
  render(<InboxPage />);
  await screen.findByText('Jamie Customer');
  await userEvent.click(screen.getByRole('button', { name: 'Open' }));
  await userEvent.type(screen.getByLabelText('Search conversations'), 'Jamie');
  expect(screen.getByText('Jamie Customer')).toBeVisible();
});

it('visually distinguishes private notes in the activity timeline', () => {
  render(<ConversationTimeline items={[{ id: 'n1', kind: 'InternalNote', body: 'private', sequence: 2, createdAt: '2026-08-11T10:00:00Z' }]} />);
  expect(screen.getByText('Private to staff')).toBeVisible();
});
```

- [ ] **Step 2: Run inbox tests to verify failure**

Run: `bun --cwd src/frontend test --run src/inbox/inbox.test.tsx`

Expected: FAIL because the inbox components do not exist.

- [ ] **Step 3: Implement list and activity data flow**

`InboxPage` loads conversations after authentication, selects the first row when none is selected, loads selected activity, and supports retry states. `ConversationList` filters its already-loaded rows by global query, local query, status (`All`, `Open`, `Pending`, `Closed`), and `Unread`, with `aria-pressed` filter buttons. `ConversationTimeline` sorts ascending by `sequence`, groups a date heading, renders customer/staff messages and notes semantically, and exposes load/error/empty states.

- [ ] **Step 4: Implement customer context and responsive selection**

Show platform, contact name, status, unread state, and shared-visibility explanation in `CustomerPanel`. At desktop it is the third pane; at tablet it opens as a labelled drawer; at mobile `InboxPage` shows either list or thread and provides `Back to conversations`.

- [ ] **Step 5: Mark read safely after timeline load**

When items load, pass the latest `sequence` to `markRead`. Do not mark as read when the timeline request fails or the item list is empty.

```ts
const latestSequence = Math.max(0, ...page.items.map(item => item.sequence));
if (latestSequence > 0) await api.markRead(conversationId, latestSequence);
```

- [ ] **Step 6: Run inbox tests and build**

Run: `bun --cwd src/frontend test --run src/inbox/inbox.test.tsx; bun --cwd src/frontend run build`

Expected: PASS and build exits 0.

- [ ] **Step 7: Commit**

```powershell
git add src/frontend/src/inbox src/frontend/src/styles.css
git commit -m "feat(ui): add responsive shared inbox"
```

## Task 5: Implement composer, notes, statuses, and feedback

**Files:**
- Create: `src/frontend/src/inbox/MessageComposer.tsx`
- Modify: `src/frontend/src/inbox/InboxPage.tsx`
- Modify: `src/frontend/src/inbox/ConversationTimeline.tsx`
- Test: `src/frontend/src/inbox/MessageComposer.test.tsx`

- [ ] **Step 1: Write failing composer tests**

```tsx
it('uses a different idempotency key for each outgoing send', async () => {
  const send = vi.fn().mockResolvedValue({ id: 'm1' });
  render(<MessageComposer onSend={send} onAddNote={vi.fn()} cannedResponses={[]} />);
  await userEvent.type(screen.getByLabelText('Message draft'), 'Hello');
  await userEvent.click(screen.getByRole('button', { name: 'Send message' }));
  await userEvent.type(screen.getByLabelText('Message draft'), 'Again');
  await userEvent.click(screen.getByRole('button', { name: 'Send message' }));
  expect(send.mock.calls[0][1]).not.toBe(send.mock.calls[1][1]);
});

it('inserts canned text into the editable message draft', async () => {
  render(<MessageComposer onSend={vi.fn()} onAddNote={vi.fn()} cannedResponses={[{ id: 'hours', title: 'Hours', content: 'Open 10–6' }]} />);
  await userEvent.click(screen.getByRole('button', { name: 'Canned responses' }));
  await userEvent.click(screen.getByRole('button', { name: 'Insert Hours' }));
  expect(screen.getByLabelText('Message draft')).toHaveValue('Open 10–6');
});
```

- [ ] **Step 2: Run composer tests to verify failure**

Run: `bun --cwd src/frontend test --run src/inbox/MessageComposer.test.tsx`

Expected: FAIL because the composer is absent.

- [ ] **Step 3: Implement composer modes and actions**

Use `crypto.randomUUID()` for outgoing idempotency keys. Support reply and internal-note modes with explicit mode text; reset mode only after a successful action. Canned response picker filters by title/content/shortcut. Emoji buttons append a selected emoji to the editable textarea. The attachment control opens an informative toast: `Attachments are not available in the current API stub.`

- [ ] **Step 4: Implement explicit status menu**

Render a button with `aria-haspopup="menu"` and `Open`, `Pending`, `Closed` actions. Close on Escape and after selection; call the API then update selected row state and show success/failure toast. Do not use a click-to-cycle status control.

- [ ] **Step 5: Run focused tests**

Run: `bun --cwd src/frontend test --run src/inbox/MessageComposer.test.tsx src/inbox/inbox.test.tsx`

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/frontend/src/inbox
git commit -m "feat(ui): add inbox composing and actions"
```

## Task 6: Implement complete workspace management views

**Files:**
- Create: `src/frontend/src/data/workspaceDemo.ts`
- Create: `src/frontend/src/workspace/WorkspacePages.tsx`
- Test: `src/frontend/src/workspace/workspace.test.tsx`
- Modify: `src/frontend/src/app/App.tsx`
- Modify: `src/frontend/src/styles.css`

- [ ] **Step 1: Write failing management-view tests**

```tsx
it('renders a visible local-data disclosure and updates a canned response draft locally', async () => {
  render(<CannedResponsesPage />);
  expect(screen.getByText('Demo workspace data')).toBeVisible();
  await userEvent.click(screen.getByRole('button', { name: 'New response' }));
  await userEvent.type(screen.getByLabelText('Response title'), 'Returns');
  await userEvent.click(screen.getByRole('button', { name: 'Save response' }));
  expect(screen.getByText('Returns')).toBeVisible();
});
```

- [ ] **Step 2: Run management tests to verify failure**

Run: `bun --cwd src/frontend test --run src/workspace/workspace.test.tsx`

Expected: FAIL because no workspace pages exist.

- [ ] **Step 3: Define isolated demo data and mutation helpers**

Export typed seed records for channel health, team members, canned responses, audit entries, and settings. The module must export pure helpers such as `addCannedResponse(state, input)` and `togglePreference(state, id)` so pages never mutate imported seed arrays. All pages rendering this data include the visible text `Demo workspace data`.

- [ ] **Step 4: Build each management page**

Implement:

- Overview: conversation/open/unread/sent metric cards, channel activity bars, and reliability facts.
- Channels: platform cards with health status, connected/reconnect affordance, and explanatory toast.
- Team: member table, role badge, invite form that locally adds a member.
- Canned Responses: searchable response table, new/edit dialog, and local persistence for the page session.
- Audit Log: timestamped table with action, actor, resource, and metadata.
- Settings: workspace fields, notification toggles, security/retention cards, and save-feedback toast.

Use a labelled panel heading and an empty state on filtered tables. Table wrappers must have `tabIndex={0}` and a descriptive `aria-label` when horizontally scrollable.

- [ ] **Step 5: Run management tests and full frontend suite**

Run: `bun --cwd src/frontend test --run`

Expected: PASS with zero failed tests.

- [ ] **Step 6: Commit**

```powershell
git add src/frontend/src/data src/frontend/src/workspace src/frontend/src/app/App.tsx src/frontend/src/styles.css
git commit -m "feat(ui): add workspace management views"
```

## Task 7: Complete responsive polish and verification

**Files:**
- Modify: `src/frontend/src/styles.css`
- Modify: `src/frontend/src/smoke.test.ts`
- Modify: `src/frontend/README.md` (create if absent)

- [ ] **Step 1: Add failing responsive/accessibility regression tests**

```tsx
it('exposes a mobile back action after a conversation is selected', async () => {
  window.innerWidth = 600;
  window.dispatchEvent(new Event('resize'));
  render(<InboxPage />);
  await userEvent.click(await screen.findByRole('button', { name: /Jamie Customer/ }));
  expect(screen.getByRole('button', { name: 'Back to conversations' })).toBeVisible();
});
```

- [ ] **Step 2: Run the regression test to verify failure**

Run: `bun --cwd src/frontend test --run src/inbox/inbox.test.tsx`

Expected: FAIL until the mobile selection behavior is implemented.

- [ ] **Step 3: Complete visual and interaction polish**

Implement design tokens for the charcoal rail, warm canvas, blue action color, channel/status colors, shadows, spacing, and typography. Include intentional loading skeletons, hover/active transitions, `prefers-reduced-motion` protection, visible keyboard focus, 44px touch targets at mobile size, and no information conveyed by color alone. Verify desktop three panes, tablet customer drawer, and mobile list/thread mode at 1280px, 800px, and 375px.

- [ ] **Step 4: Document local frontend use**

Create/update `src/frontend/README.md` with:

```markdown
## UI development

Run `bun run dev` from `src/frontend`. The Vite proxy expects the API at `http://127.0.0.1:5080`.

Use `acme`, `agent@acme.test`, and any non-empty password for the current stub login. Inbox data comes from the API; management views display labelled demo data until corresponding API endpoints are available.
```

- [ ] **Step 5: Run full frontend verification**

Run: `bun --cwd src/frontend test --run; bun --cwd src/frontend run build`

Expected: all tests pass and Vite production build exits 0.

- [ ] **Step 6: Commit**

```powershell
git add src/frontend
git commit -m "test(ui): verify responsive unified inbox experience"
```

## Final verification checklist

- [ ] Login accepts the tenant slug, email, and password and reaches the protected inbox.
- [ ] Conversation list uses the stub API and supports query/status/unread triage.
- [ ] Timeline renders messages and staff-only notes distinctly; read updates use the latest sequence.
- [ ] Reply, internal note, status, canned response, emoji, and disabled attachment actions give clear feedback.
- [ ] Overview, Channels, Team, Canned Responses, Audit Log, and Settings are navigable and label their local demo data.
- [ ] Desktop, tablet, and mobile layouts preserve keyboard navigation and meaningful content.
- [ ] `bun --cwd src/frontend test --run` and `bun --cwd src/frontend run build` both exit 0.
