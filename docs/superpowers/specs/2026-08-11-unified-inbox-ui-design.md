# Unified Inbox UI Implementation Design

## Goal

Replace the minimal single-file frontend with a responsive, component-based React workspace that faithfully adapts the visual hierarchy and interactions in `preview.html`. The UI must be useful against the current stub backend without presenting local demonstration data as persisted server data.

## Scope

- A shared application shell with workspace navigation, global search, notifications, and responsive breakpoints.
- An API-backed inbox: login, conversation list, search, status and unread filtering, activity timeline, message sending, internal notes, status updates, and read updates.
- Complete workspace views for Overview, Channels, Team, Canned Responses, Audit Log, and Settings.
- Accessible controls, empty/loading/error states, toasts, keyboard focus, and local persistence for user-interface preferences.

## Architecture

The frontend is decomposed by feature rather than placing all behavior in `main.tsx`:

```text
src/
  app/          application shell, routes, UI state
  api/          authenticated request client and API contracts
  auth/         login and session context
  components/   reusable primitives: icons, buttons, avatars, panels, toasts
  inbox/        list, filters, timeline, composer, profile panel
  workspace/    overview, channels, team, canned, audit, settings views
  data/         labelled local demo data for unavailable management APIs
```

The existing API remains authoritative for login and inbox resources. The authenticated API client attaches the in-memory token and normalizes HTTP failures. Management pages use clearly bounded local demo data until matching server endpoints become usable; mutation-like interactions update that local state and show a toast.

## Visual System

The interface follows the reference's dense, calm operations-console character: a charcoal left rail, warm off-white canvas, bright blue action color, restrained borders, compact metadata, and colored platform/status badges. Typography uses a distinctive display face for major headings plus a practical humanist body face, with a robust local fallback. Visual density remains intentional: panels are padded enough to scan, while inbox rows preserve fast triage.

The desktop inbox has three panes (list, conversation, customer context). At medium widths the customer context becomes a drawer, and at narrow widths the selected conversation replaces the list with a back control. Management views collapse tables into horizontally scrollable, labelled data panels rather than losing information.

## Interaction Model

- Login transitions into the inbox and stores the access token only in React state for the session.
- Global and inbox search refine the visible list; filters are combinable only where meaningful and announce their active state.
- Selecting a conversation fetches activity, marks activity read through the current cursor, and shows messages and staff-only notes with different semantic styling.
- The composer toggles between reply and internal-note modes. Canned text inserts into the editable draft. Sending uses a new idempotency key, optimistically retains the draft until success, and surfaces failure in a toast.
- Status is set with an explicit menu, not a hidden cycle. Emoji and attachment controls provide working client-side affordances; attachment submission is disabled with a concise explanation until the API endpoint accepts content uploads.
- Notifications, preferences, and management-page actions have tangible in-UI feedback.

## Error Handling and Accessibility

Every server-loaded region supports loading, empty, and failure states with retry. Buttons have text or accessible labels, dialogs and menus close with Escape, focus is visible, color is never the sole carrier of status, and timeline notes announce their privacy. The responsive conversation transition maintains a practical keyboard path back to the list.

## Testing and Verification

Vitest/Testing Library tests cover login rendering, navigation, filtering, timeline note distinction, canned insertion, composer idempotency header generation, and fallback UI state. The production build and frontend test suite must pass after implementation. Browser verification should exercise login, inbox selection, status update, note creation, and a management view at desktop and mobile widths.
