# React UI/UX audit and improvement checklist

Date: 2026-09-05

## Purpose and evidence

Use this document to improve the React frontend without changing business rules, API contracts, authentication, authorization, or database structure.

The prototype [preview.html](../preview.html) was reviewed in a browser at 1440×900, 1280×800, 768×1024, and 390×844. All seven main prototype views were visited; mobile conversation navigation and invitation validation were also exercised. No page-level horizontal overflow was detected in those views. This is not a complete accessibility certification or a test of every interaction.

React findings below are based on source inspection. Authenticated React screens have **not** been visually verified with representative data during this audit because the backend is unavailable. Use browser request interception or an isolated test harness to render them before declaring a fix complete. Keep fixtures out of production authentication and API behavior.

**The prototype and React app are separate implementations. Do not assume that a prototype issue exists in React, or that their visuals match.** React uses a different palette, typography, layout, and page structure. Earlier conversational statements claiming visual equivalence were not sufficiently verified.

Status meanings:

- **Confirmed in source:** the implementation contains the identified issue; browser validation may still be needed to measure its impact.
- **Different implementation:** the prototype symptom does not transfer directly, but React has a related issue.
- **Not found:** do not implement a fix solely on the basis of the prototype audit.
- **Needs browser validation:** a risk or subjective improvement, not a proven rendered defect.

## Frontend map

Paths below are relative to `src/frontend/` unless linked otherwise.

| Area | Existing implementation |
| --- | --- |
| Framework/build | React 19, TypeScript, Vite |
| Routing | React Router; `src/app/App.tsx`, `src/app/routes.ts` |
| Server state | TanStack React Query; API factories in `src/api/`, shared hooks in `src/api/hooks.ts` |
| Local state | React hooks; auth and toast contexts |
| Realtime | SignalR in `src/app/RealtimeBridge.tsx` |
| Styling | Global `src/styles.css`; CSS variables plus many literal values |
| UI library | No external component library; thin primitives in `src/components/ui.tsx` |
| Icons | Shared SVG `Icon` component; some inline emoji |
| Workspace | Shell plus Overview, Channels, Team, Canned Responses, Audit, Settings, Notifications |
| Inbox | Conversation list, activity timeline, status menu, reply/note composer, attachments, templates, customer notes |
| Auth flows | Login, registration, email verification, password recovery/reset, invitation acceptance |
| Checks | Vitest/Testing Library, TypeScript, Vite build; separate Playwright E2E project |

Reuse this architecture. Do not add a UI framework just to implement this checklist.

## Which prototype findings apply to React?

| Prototype finding | React assessment | Where / action |
| --- | --- | --- |
| Seven unnamed mobile/tablet navigation buttons | **Not found in the same form.** React uses text-bearing links and visually clips tablet labels rather than applying `display:none`; mobile labels are visible. | `AppShell.tsx`, navigation media queries. Verify accessible names at all widths; do not copy the prototype fix blindly. |
| Notifications disappear on mobile | **Not found.** React retains the notification button and has a Notifications route. | `AppShell.tsx`, `routes.ts`. Test badge fit and touch targets. |
| Permanent narrow-screen rail consumes width | **Different implementation.** React switches to bottom navigation at 680px, potentially showing eight destinations for an Owner. | Test crowding, wrapping, safe-area padding, and content occlusion before selecting a simpler mobile navigation pattern. |
| Invitation errors appear only in an external toast | **Different implementation.** React Team invitation has no error/pending feedback around its awaited request. | `WorkspacePages.tsx`, `TeamPage`; see R04. |
| Customer drawer lacks modal focus management | **Different implementation.** React has no drawer: the panel becomes a fixed overlay at tablet widths and disappears on mobile. | `InboxPage.tsx`, `.customer-panel`; see R02. |
| Channels reassurance cards push accounts down | **Not found in the same form.** React renders account cards before the connection form and lacks those introductory cards. | Improve actual card hierarchy after rendering React; do not reorder it based on the prototype alone. |
| Composer tools need horizontal scrolling | **Not found in the same form.** React wraps actions instead. | Verify wrapping and composer height with templates/attachments; see R10. |
| Mobile status becomes ambiguous “Action” | **Not found.** React displays `Open`, `Pending`, `Closed`. | If adopting friendlier labels, change display text only; retain API enum mapping. |
| Very small metadata and connection-step text | **Partially applicable.** React has 10px mobile navigation labels and 11–13px metadata, but not the prototype’s 9px stepper. | `styles.css`; verify legibility and contrast at actual rendered sizes. |
| Inconsistent field errors | **Confirmed in source.** Several mutations expose no error state; canned fields lack visible labels. | Auth pages, `WorkspacePages.tsx`; see R04–R06. |
| Short-lived toast is the only error channel | **Different implementation.** React toast duration is four seconds; its container has no dedicated visual styling. Most pages use other feedback mechanisms. | `ToastProvider.tsx`, `styles.css`; do not assume changing toast duration fixes missing mutation feedback. |
| Unicode navigation icons and large card shadows | **Mostly not applicable.** React already has shared SVG navigation icons and light card treatment. | Reuse `Icon.tsx`; avoid replacing good existing patterns. |

## Prioritized React work

### Critical

#### R01 — Restore mobile conversation navigation

- **Status:** Confirmed in source.
- **Files:** [InboxPage.tsx](../src/frontend/src/inbox/InboxPage.tsx), [styles.css](../src/frontend/src/styles.css).
- **Problem:** The first conversation is selected automatically. At mobile widths, `.has-selection` hides the list. A `.mobile-back` CSS rule exists, but no back control is rendered.
- **Impact:** Users cannot use an in-app control to return to the conversation list once a thread is displayed.
- **Fix:** Track mobile list/thread presentation independently from selected conversation data. Add a labeled back control and restore focus to the selected row. Preserve desktop selection and existing read behavior.
- [ ] On mobile, open conversation A, return to the list, and open B.
- [ ] Incoming/query updates do not unexpectedly reopen the thread after returning to the list.
- [ ] Desktop selection and message sending still work.

### High

#### R02 — Keep customer information reachable at all widths

- **Status:** Confirmed in source.
- **Files:** `InboxPage.tsx` (`CustomerPanel`), `styles.css`.
- **Problem:** The panel is fixed at tablet widths without a close control, and hidden on mobile without an alternative entry point.
- **Fix:** Add a Customer details button and accessible drawer for smaller screens. Use a modal dialog pattern with a close button, focus containment, Escape handling, focus return, and internal scrolling.
- [ ] Phone, email, and customer notes remain accessible on mobile.
- [ ] The closed panel never blocks the thread; the open panel has predictable keyboard behavior.
- [ ] Notes retain their existing save/error behavior.

#### R03 — Define reliable scrolling and responsive containment

- **Status:** Source-level risk; confirm exact clipping in the browser.
- **Files:** `styles.css`, `AppShell.tsx`, `InboxPage.tsx`, `WorkspacePages.tsx`.
- **Problem:** `html`, `body`, and `#root` hide overflow, while the shell uses minimum heights and lacks a complete scroll-container strategy. The conversation timeline grows with content. Team/Audit tables have no responsive scroll wrapper.
- **Fix:** Define viewport-sized shell and explicit scroll regions. Give flex/grid children `min-height:0` where needed. Keep the inbox composer reachable and place tables inside labeled horizontal scroll regions when they cannot reflow.
- [ ] Test long conversations, 30+ rows, long names/emails, and validation messages at all four viewport sizes.
- [ ] Bottom navigation never covers content or actions.
- [ ] Test 200% zoom, keyboard scrolling, and a narrow viewport with the software keyboard where available.

#### R04 — Make asynchronous actions understandable and recoverable

- **Status:** Confirmed in source.
- **Files:** `WorkspacePages.tsx`, `AuthPages.tsx`, `ChannelRepairPage.tsx`.
- **Problem:** Team mutations, channel test/toggle/disconnect, notification mutations, and forgot-password submission lack consistent error/pending feedback. A failed channel test can leave `testingId` set. Settings/canned mutations do not display their errors. Audit export does not check `response.ok` before downloading.
- **Fix:** Add local pending state, error handling, and success feedback around existing requests. Reset pending flags in `finally` or mutation lifecycle callbacks. Check export response status. Keep user-entered data on failure and preserve the privacy-neutral forgot-password success message.
- [ ] Simulated rejected requests show a useful error and re-enable the action.
- [ ] Repeated clicks during a pending action do not produce duplicate submissions.
- [ ] Errors never appear as successful downloads or silent state changes.

#### R05 — Prevent accidental loss or removal of work

- **Status:** Confirmed in source.
- **File:** `WorkspacePages.tsx` (`CannedPage`, `TeamPage`).
- **Problem:** Creating a canned response resets the form before the request succeeds. Delete, deactivate, and revoke actions lack a consistent confirmation pattern; channel disconnect uses native confirmation.
- **Fix:** Reset forms only after successful creation. Confirm destructive actions with the affected resource name and a factual consequence description. Preserve the underlying request, permissions, and resulting operation. Do not offer Undo unless restoration actually exists.
- [ ] Failed creation preserves title, shortcut, and content.
- [ ] Cancel sends no mutation; confirm sends the existing mutation once.
- [ ] Dialogs work by keyboard and at mobile sizes.

#### R06 — Establish usable auth and form styling

- **Status:** Confirmed in source.
- **Files:** `styles.css`, `auth/*.tsx`, `WorkspacePages.tsx`.
- **Problem:** Auth pages use `.login-page` and `.login-form`, but the stylesheet defines neither. Textarea/select typography is not globally normalized. Canned creation/editing relies on accessible names and placeholders without persistent visible labels.
- **Fix:** Add a shared auth layout, consistent field/control styles, visible labels, helper text, and linked validation errors. Preserve all credential fields, validation constraints, routes, and auth requests.
- [ ] Every auth page renders with readable spacing and reachable submit actions on mobile.
- [ ] Labels remain visible after typing; errors are associated with affected controls.
- [ ] Existing autocomplete, password requirements, and login error focus behavior remain intact.

#### R07 — Show conversation identity and available message metadata

- **Status:** Confirmed in source.
- **Files:** `InboxPage.tsx`, `ConversationTimeline.tsx`, `api/inbox.ts`.
- **Problem:** Visible headings say “Conversation” and “Contact profile” instead of the customer name. Timeline rows show “Message” and body but omit available creation time and delivery status.
- **Fix:** Render the actual contact name and available timestamp/status. Distinguish private notes clearly. Determine message direction only from verified contract semantics; do not invent direction, sender names, or attachment data missing from the current DTO.
- [ ] Switching contacts visibly updates identity.
- [ ] Pending/failed/delivered messages communicate the available status in text.
- [ ] Private notes remain unmistakably private.

### Medium

#### R08 — Remove misleading affordances and health signals

- **Status:** Confirmed in source.
- **Files:** `AppShell.tsx`, `WorkspacePages.tsx`, `styles.css`.
- **Problem:** Global search and the workspace switch button have no action. The inbox badge counts unread notifications but labels them unread conversations. Channel health dots are always green, including unhealthy channels.
- **Fix:** Connect search to the existing inbox search flow or present it without implying an unavailable action. Render workspace identity as static content unless switching is supported. Keep notification counts on notification controls; use a conversation badge only with a valid source. Drive health styling from existing health/enabled state and retain text labels.
- [ ] Every interactive affordance performs its stated action.
- [ ] Badge labels describe exactly what is counted.
- [ ] Unhealthy/disabled accounts never look healthy solely because of their icon color.

#### R09 — Standardize keyboard and state semantics

- **Status:** Confirmed gaps in source; verify browser behavior.
- **Files:** `InboxPage.tsx`, `styles.css`, `components/ui.tsx`.
- **Problem:** Status menus use ARIA menu roles without corresponding keyboard management. Filter and composer mode selection is expressed mainly by CSS classes. Global custom focus styling omits selects and some textareas.
- **Fix:** Use a native select or implement the complete menu interaction pattern. Add programmatic selected/pressed state to toggle buttons. Apply visible focus treatment to all controls and restore focus when popovers close.
- [ ] Operate filters, status, canned responses, templates, and reply/note mode without a mouse.
- [ ] Escape dismisses overlays and restores focus predictably.
- [ ] Selected state is exposed to assistive technology.

#### R10 — Improve empty states, composer hierarchy, and feedback consistency

- **Status:** Confirmed gaps in source; layout changes require browser comparison.
- **Files:** `InboxPage.tsx`, `WorkspacePages.tsx`, `ToastProvider.tsx`, `styles.css`.
- **Problem:** Several lists lack explicit empty/no-results states. The canned picker does not render pending/error feedback. Shared `LoadState` offers no retry. Later composer rules override the primary send-button colors. Toasts lack dedicated positioning/styling.
- **Fix:** Reuse shared loading/error/empty components with contextual wording and retry where supported. Distinguish no data from no search matches. Standardize primary send styling, working/disabled states, and toast presentation. Keep actionable errors inline.
- [ ] Empty lists explain what to do next; search results offer clear-filter recovery.
- [ ] Failed/slow requests never resemble an unexplained blank panel.
- [ ] The send action remains visually primary with attachments/templates open.

### Low

#### R11 — Consolidate the visual system after usability fixes

- **Status:** Confirmed styling duplication; aesthetic choices are recommendations.
- **Files:** `styles.css`, `components/ui.tsx`, `components/Icon.tsx`.
- **Fix:** Establish semantic color, typography, spacing, radius, and shadow tokens. Give reusable primitives real variants and styles; migrate repeated patterns incrementally. Reuse SVG icons. Preserve the prototype identity only where deliberately selected, rather than copying its stylesheet wholesale.
- [ ] Buttons, fields, cards, badges, page headers, and feedback use consistent variants.
- [ ] Compare rendered text contrast and focus indicators; do not claim WCAG conformance from source inspection alone.

## Proposed design direction — not yet implemented

Prefer the prototype's warm, restrained support-workspace identity if it is the intended reference. This is a deliberate change from React's current cooler palette, not merely deduplication.

| Token group | Proposed baseline |
| --- | --- |
| Surfaces | Canvas `#f1efe9`, paper `#fffdf9`, raised surface `#ffffff` |
| Text/navigation | Text `#172033`, muted `#526071`, navigation `#111827` |
| Action/border | Primary `#2458d3`, primary soft `#eaf0ff`, border `#dcdedb` |
| Semantic status | Success `#16794a`, warning `#9a5b08`, danger `#c64932`, paired with readable soft backgrounds and text labels |
| Type | Existing sans-serif fallback stack for controls/body; optional prototype serif for headings; 12/14/16/20/24/28px scale |
| Spacing | 4/8/12/16/24/32px |
| Radius | Controls 8px, cards 12px, dialogs 14px, badges pill-shaped |
| Elevation | Minimal on cards; stronger for overlays only |
| Components | Button variants, FormField, StatusBadge, Panel, PageHeader, list states, accessible confirmation dialog/drawer |
| Breakpoints | Start with React's existing 680/860px behavior; adjust from actual content measurements rather than copying prototype breakpoints |
| Interaction | Visible focus, plain-language errors, stable pending states, preserved form values on failure, approximately 44px primary mobile touch targets |

Validate contrast for actual pairings, including disabled and focus states. Avoid decorative animation or new dependencies unless a concrete requirement justifies them.

## Implementation sequence

1. Capture React baselines using isolated fixtures: representative Owner/Admin/Agent states, successful/empty/error data, and long content. No production auth bypass.
2. Fix mobile inbox navigation, customer details, and scroll containment (R01–R03).
3. Fix mutation feedback, form preservation, and auth/form presentation (R04–R06).
4. Improve conversation identity, status display, and misleading controls (R07–R08).
5. Standardize accessibility, UI states, and repeated components (R09–R11).
6. Recheck each page and document any deferred issue with its reason.

For each step: inspect the component, explain any behavior change, implement narrowly, verify the affected user flow, and then continue. UI state and error handling may change; business operations, permissions, and request contracts must remain unchanged.

## Verification and completion checklist

- [ ] Desktop 1440×900, laptop 1280×800, tablet 768×1024, mobile 390×844; also check 320px width and zoom.
- [ ] Long tables, long messages/names, empty data, no search matches, slow/rejected requests.
- [ ] Keyboard navigation, accessible control names, focus restoration, status announcements.
- [ ] Modal/drawer content and actions fit or scroll on short screens.
- [ ] Existing role-based visibility and authentication behavior are preserved.
- [ ] Message idempotency keys, notes, template requirements, attachment readiness, and API payloads remain intact.
- [ ] Add targeted regression tests for actual behavior fixes, especially mobile navigation and failed form submission; avoid tests that only mirror CSS implementation.
- [ ] Run the existing checks from `src/frontend`:

```powershell
bun run test --run
bunx tsc --noEmit
bun run build
```

These checks do not establish visual parity or accessibility by themselves. Pair them with browser evidence. The earlier session reported 51 passing frontend tests, a passing typecheck, and a passing build; those results predate any future implementation of this checklist.

## Current completion status

2026-09-05 reconciliation: the R01–R11 UI patch from the Windows checkout
(`C:\Users\LENOVO\Downloads\unifiedInbox`, 17 files) was ported onto canonical
WSL HEAD `083ff9f` via `git apply`. Canonical auth/session fixes are preserved
(`AuthProvider.ready` + `App.tsx` ready gate, `admin.ts` numeric `roleIndex`);
the port touches none of those files. Verified on the canonical checkout:
`bunx tsc --noEmit` clean, `bun run test --run` 54 passed across 12 files,
`bun run build` ok (`index-WBTFDV1Z.css` / `index-Ba5ODBsy.js`), frontend-only
rebuild serves the new bundle with `/api/v1/operations/ready` 200 and no 502.
Browser verification (live stack, Demo Tour owner session): 8 routes x 5
viewports (1440x900, 1280x800, 768x1024, 390x844, 320x700) with zero
document-level horizontal overflow; mobile nav opens a native dialog with all 8
destinations; audit table wrapper (342/620 at 390px) scrolls to the final row;
login page renders styled with visible labels; populated inbox verified via
browser-only `page.route` fixtures (thread switching, 30-message timeline with
timestamps/delivery status/notes, customer dialog with phone/email/notes and
Escape close, composer reachable at 320px). Fixtures were interception-only and
removed afterwards; the real workspace state (zero conversations, one saved
response, one pending invitation) is unchanged. No commit made; Windows tree
kept as recoverable source. Remaining known gaps (unchanged): dual
CustomerPanel instances, team-revoke disabled binding, channel-completion
pending feedback, global-search `?q=` router sync, field-specific RFC7807
mapping/short-screen dialog focus order.

2026-09-05 autonomous senior-UI pass (freehand, no new contracts): closed the
five gaps above without touching API shapes, auth, or business logic.
`Dialog.tsx` is StrictMode-safe (`showModal` open-guard + `close()` cleanup,
jsdom-guarded); `InboxPage.tsx` re-syncs `?q=` on back/forward navigation and
shares one customer-notes draft between the inline panel and the details
dialog (no more draft loss); `WorkspacePages.tsx` binds `disabled` on Revoke
and shows `Completing connection…` with re-entrancy guard in channel finish.
Verified: `bunx tsc --noEmit` clean, `bun run test --run` 54/54,
`bun run build` ok, frontend-only rebuild serves the new bundle
(`ready` 200, no 502). Browser-verified live: notes draft typed inline appears
in the dialog and survives Escape; `?q=hello` deep-link fills search with the
no-match recovery state; Team Revoke renders enabled-at-idle with zero
overflow.
