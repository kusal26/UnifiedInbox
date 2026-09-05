# UI/UX implementation handover — 2026-09-05

## User goal and current stop point

The user requested: "patch all. it should look like production app not ai slop" after a UI audit and live browser review. Preserve API contracts, authentication/authorization, existing workflows, and backend behavior. Continue the implementation and verification; do not restart the audit.

The user then requested handover because their usage limit was approaching. Work is **not fully complete**. Code is uncommitted. Core fixes, tests, and an initial browser pass are complete; canonical-repo reconciliation and populated-inbox verification remain.

## CRITICAL: two different checkouts

The session environment originally identified the Windows checkout:

`C:\Users\LENOVO\Downloads\unifiedInbox`

All changes described here were made THERE, against HEAD:

`bfe438dbf3859b331d0f7b5cebcd21f70903c73f`

Late in the session, the user supplied AGENTS.md instructions identifying the canonical repository as:

`/home/kusal/UnifiedInbox` in Ubuntu-24.04 WSL

Windows access: `\\wsl.localhost\Ubuntu-24.04\home\kusal\UnifiedInbox`

Read-only inspection found canonical HEAD:

`083ff9f7123919ec79cbd69e0ec3a9c7b4c55889`

The WSL checkout's status output was clean at that inspection. It has newer auth work (`AuthProvider.ready`) absent from the Windows baseline. **Do not copy the entire Windows frontend over WSL or reset either checkout.** Compare the changes and port only the UI patch, preserving canonical changes, notably ready-gated session restoration and numeric role writes.

All modified and new files remain available in the Windows checkout. This handover and the audit checklist also live there.

## Deployment caveat

Docker is WSL-native (`/usr/bin/docker`). The frontend service was rebuilt using:

```bash
cd /mnt/c/Users/LENOVO/Downloads/unifiedInbox
docker compose up -d --build --no-deps frontend
```

This successfully recreated ONLY `unifiedinbox-frontend-1`, using the Windows UI patch. It did not recreate the API, worker, databases, or volumes. However, this means the currently served frontend was built from the older Windows baseline, not canonical WSL HEAD. Rebuild the frontend from the canonical checkout after porting the patch so newer auth/session fixes are preserved in the served bundle.

The first Docker build failed because Windows node_modules were copied into Linux (`env: 'node\r': No such file or directory`). A new `src/frontend/Dockerfile.dockerignore` excludes node_modules, dist, .git, .worktrees, and browser logs. The second build succeeded.

Compose warned about missing JWT/credential variables in the Windows checkout. No dependent services were recreated; do not restart the whole stack from that checkout. Use canonical environment configuration.

Current verified UI bundle from successful build:

- CSS `/assets/index-WBTFDV1Z.css`
- JS `/assets/index-Ba5ODBsy.js`

The browser cached old SPA HTML after deployment. Navigating to `/canned?ui-review=3` loaded the new bundle. Use a cache-busting query or hard refresh when verifying. Consider correct HTML cache headers if appropriate, but none were changed in this session.

## Files changed in Windows checkout

Modified:

- `src/frontend/src/styles.css`: replaced fragmented styling with consistent base controls, neutral surfaces, blue primary actions, dark sidebar, page headers, auth forms, scrolling shell, responsive tables, inbox columns, dialog and toast styles.
- `src/frontend/src/app/AppShell.tsx`: labeled mobile navigation via native dialog; preserved role-filtered routes; workspace identity is static; global search submits to inbox `?q=`; removed incorrect conversation badge sourced from notifications; notification badge stays with notifications; skip link and content region.
- `src/frontend/src/workspace/WorkspacePages.tsx`: mutation feedback/pending states, confirmation dialogs, saved-response draft preservation, visible field labels, responsive table wrappers, health-dependent indicator, export response checks, human-readable notification/audit labels, shared page descriptions, retry/empty states.
- `src/frontend/src/inbox/InboxPage.tsx`: separate mobile thread presentation state and back control; selected row focus restoration; details dialog; visible contact name; timeline scroll wrapper; pressed states; keyboard status menu; canned picker states and Escape; customer-load retry; inbox empty state; URL search input.
- `src/frontend/src/inbox/ConversationTimeline.tsx`: available timestamp and delivery status displayed. Does not invent missing sender/direction/media fields.
- `src/frontend/src/auth/AuthPages.tsx`: forgot-password error/pending feedback, error focus, registration field guidance.
- `src/frontend/src/auth/AcceptInvitationPage.tsx`: focused error summary.
- `src/frontend/src/channels/ChannelRepairPage.tsx`: pending state for repair start.
- `src/frontend/src/components/ToastProvider.tsx`: styled toast container.
- `src/frontend/src/components/ui.tsx`: button variants, empty-state class, reusable focused FormError.
- `src/frontend/src/inbox/inbox.test.tsx`: mobile list/thread regression; updated ambiguous name assertion now that name is correctly visible in multiple places.
- `src/frontend/src/workspace/WorkspacePages.test.tsx`: invitation failure and canned draft recovery regressions; human-readable notification expectation.

New:

- `src/frontend/src/components/Dialog.tsx`: native `<dialog>` wrapper with title, close action, Escape callback, focus return.
- `src/frontend/src/components/useAction.tsx`: shared pending lock/error/success feedback for existing async actions.
- `src/frontend/Dockerfile.dockerignore`.
- `docs/REACT_UI_UX_IMPROVEMENT_CHECKLIST.md`: detailed audit, prototype-to-React applicability mapping, R01–R11 checklist. Its completion section still describes the pre-implementation state; update it after reconciliation/verification.
- This handover.

No backend source, API contracts, or database files were intentionally changed. No dependency was added. No commit was made.

## Verification already completed

On the Windows working tree after the final source edits:

- `bunx tsc --noEmit`: passed.
- `bun run test --run`: **54 tests passed across 12 files**.
- Linux frontend Docker production build: passed (Vite benign SignalR annotation warnings only).
- Earlier `git diff --check`: no whitespace errors (Windows line-ending warnings only).

The 54 tests include existing idempotency, optimistic rollback, internal note, attachment readiness/rejection, template-window, auth, role visibility, and channel-flow coverage.

Browser tool available: `mcp__playwright__browser_run_code_unsafe` and screenshot tools. Discover metadata via `ALL_TOOLS` if needed. Browser currently has tabs for localhost:8080. Do not assume these tools share the shell filesystem.

Live backend-connected browser verification after rebuilt bundle:

- Visited all eight routes at **1440×900, 1280×800, 768×1024, 390×844, 320×700**.
- No document-level horizontal overflow across those 40 combinations.
- Verified mobile navigation opens a native dialog and navigates to every destination with visible labels.
- Verified Audit Log can scroll to its final row. At 390×844, table wrapper clientWidth 342, scrollWidth 620; final row bottom about 784px after scrolling, within viewport. Previously final rows were inaccessible and IDs clipped.
- Inspected rebuilt Canned Responses desktop screenshot: visible Title/Shortcut/Message labels, orderly form, primary Create action, separated Edit/Delete controls.
- Inspected rebuilt mobile Audit screenshot: working vertical and horizontal scroll, readable action labels, no crowded bottom nav.

Before rebuilding, login/register/reset/invite were browser-confirmed unstyled; new auth styling still needs a final browser pass.

## Data/access context

- Live UI + API: `http://localhost:8080`, API prefix `/api/v1` on same origin.
- Mailpit: `http://localhost:8025`.
- Verified workspace: Demo Tour, slug `demo-tour`, owner `owner@demo-tour.test`.
- The user provided its password in the conversation. Do not persist passwords in repository documentation. Reuse the current authenticated browser session or obtain credentials from the conversation/user.
- Demo Tour currently contains one saved response and a pending invitation, **zero conversations and zero connected channels**.
- Do not create real WhatsApp connections or send messages merely to test visual layout. Use browser-only intercepted API fixtures or isolated tests for populated inbox/channel states.
- The documented old `acme` seed login failed and should not be retried.

## Next agent: recommended sequence

1. Read the user's AGENTS.md and canonical checkout guidance. Inspect diffs between the two frontend trees before writing.
2. Port only the UI changes to canonical WSL, preserving newer auth ready/session restoration, numeric role serialization, and other existing changes. Keep the Windows working tree as a recoverable source until port verification is complete.
3. Review implementation gaps below and fix those within the user's existing UI scope.
4. Run WSL-native frontend tests, typecheck, and build. Follow canonical testing/plan conventions. Do not delete volumes or restart backend services for frontend validation.
5. Rebuild only frontend from canonical WSL with its actual environment. Verify the served asset hash changes.
6. Browser-check public auth pages, all workspace pages, and populated inbox/customer drawer using browser-only request interception. Keep mock fixtures out of production bundles and do not bypass production authentication.
7. Update audit checklist and write final changes/design-system/remaining-work report. Commit according to canonical repository guidance only after targeted gates pass, preserving unrelated work.

## Open issues / review carefully

- Canonical patch port is the highest priority. Current served Windows-baseline UI may omit newer canonical behavior even though auth source was not directly edited.
- Populated inbox/browser verification remains outstanding: thread switching/back, customer dialog Escape/focus, long timeline/composer reachability, templates/attachments at 320px and tablet widths.
- Native Dialog focus return under React StrictMode and its close/backdrop semantics should be checked in browser. Unit tests do not exercise native showModal.
- Global search `?q=` synchronization uses window.location in InboxPage, relying on router rerender; verify repeated submissions while already in inbox. Use a router-aware approach compatible with standalone InboxPage unit tests if needed.
- Two CustomerPanel instances render when its dialog opens (desktop panel plus dialog). Check whether unsaved notes should be shared; do not introduce draft loss.
- UI error summaries improved, but not all forms have field-specific RFC7807 mapping/aria-describedby; avoid claiming full WCAG conformance.
- Team revoke button does not yet bind disabled state although shared useAction lock suppresses overlapping requests.
- Channel connect completion still uses existing local try/catch; pending feedback is stronger on start/test/toggle than completion.
- ChannelRepairPage still needs checking of loading-user vs denied-role display (canonical may differ).
- Status menu keyboard handling implemented; canned picker Escape implemented. Verify focus order and short-screen dialogs.
- Auth field-helper additions preserve existing constraints. Full auth integration gate was not rerun; no backend tests were run for this UI-only patch.
- Main list empty states improved. Do not claim all backend failure paths verified live; mutation failure tests used mocks.
- No outbound/inbound message alignment was fabricated because current ActivityItem DTO does not explicitly provide direction. Discuss or verify contract semantics before adding direction styling.
- Design uses system sans-serif, restrained neutral canvas, white/cream surfaces, dark blue navigation, blue primary actions, minimal card shadows. It is deliberately cleaner and denser than the old React UI; not a pixel-perfect copy of preview.html.

## Handy commands

Use WSL-native tooling per AGENTS.md:

```bash
cd /home/kusal/UnifiedInbox
export PATH=/home/kusal/.dotnet/tools:/home/kusal/.bun/bin:/usr/local/bin:/usr/bin:/bin
cd src/frontend
bun run test --run
bunx tsc --noEmit
bun run build
```

Avoid `docker compose down -v`: it destroys the user’s current test data and is unnecessary for this task.
