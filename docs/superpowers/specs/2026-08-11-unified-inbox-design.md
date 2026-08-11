# Unified Multi-Channel Inbox Platform — Technical Design Spec

**Version:** 1.0
**Date:** August 11, 2026
**Source:** Product Requirements Document (PRD) v1.1
**Product type:** Multi-tenant SaaS — shared messaging inbox for SMBs
**Status:** Approved for implementation planning

---

## 1. System Context & Architecture

### 1.1 Overview

The Unified Multi-Channel Inbox Platform is a multi-tenant web application that aggregates customer conversations from Facebook Messenger, Instagram, WhatsApp, and TikTok into a single shared inbox per business. All authorized staff in a business see the same conversations; there is no conversation assignment.

### 1.2 High-Level Diagram

```text
                    ┌──────────────────────────────────────────────────┐
                    │                   Clients                        │
                    │                                                  │
                    │   React SPA (Vite + Tailwind + TanStack Query)   │
                    │   SignalR Client                                 │
                    └───────────────┬──────────────────┬───────────────┘
                                    │ HTTPS / JWT      │ WebSocket
                    ┌───────────────▼──────────────────▼───────────────┐
                    │                 Load Balancer                    │
                    └───────────────┬──────────────────────────────────┘
                                    │
                ┌───────────────────┼───────────────────┐
                ▼                   ▼                   ▼
        ┌───────────────┐   ┌───────────────┐   ┌───────────────┐
        │   API #1      │   │   API #2      │   │   API #3      │
        │ ASP.NET Core  │   │ ASP.NET Core  │   │ ASP.NET Core  │
        │ + SignalR Hub │   │ + SignalR Hub │   │ + SignalR Hub │
        └──────┬────────┘   └──────┬────────┘   └──────┬────────┘
               │                   │                   │
               └───────────────────┼───────────────────┘
                                   ▼
                    ┌──────────────────────────┐
                    │         RabbitMQ         │
                    │  webhook-events queue    │
                    │  outbound-messages queue │
                    └────────────┬─────────────┘
                                 │
                 ┌───────────────┼───────────────┐
                 ▼               ▼               ▼
        ┌───────────────┐ ┌───────────────┐ ┌───────────────┐
        │   Worker #1   │ │   Worker #2   │ │   Worker #3   │
        │ Message       │ │ Message       │ │ Message       │
        │ Processors    │ │ Processors    │ │ Processors    │
        └──────┬────────┘ └──────┬────────┘ └──────┬────────┘
               │                 │                 │
               └─────────────────┼─────────────────┘
                                 ▼
                    ┌──────────────────────────┐
                    │      PostgreSQL          │   ◄── Redis (cache, rate limiting,
                    │   (primary store)        │        signalr backplane, queue DLQ)
                    │                          │   ◄── Object Storage (media)
                    └──────────────────────────┘

                    External platforms:
                    Meta (Facebook/Instagram)  ◄── webhook / Graph API
                    WhatsApp Business          ◄── webhook / Cloud API
                    TikTok                     ◄── webhook / API
```

### 1.3 Architecture Principles

- **Stateless API:** API instances are horizontally scalable; no in-memory state is required for correctness.
- **Queue-based processing:** Webhook ingestion and outbound sending are decoupled from the HTTP request/response via RabbitMQ.
- **Provider agnosticism:** The core domain never depends on provider-specific payload shapes. All provider traffic flows through channel adapters.
- **Tenant isolation at the backend:** every query path scopes to the authenticated tenant; frontend filtering is never a security boundary.
- **Clean architecture:** Domain → Application → Infrastructure → API dependency direction.

### 1.4 Solution Structure

```text
src/
│
├── Domain/                          # Entities, Enums, ValueObjects, Events, Interfaces
├── Application/                     # Use cases: Conversations, Messages, Contacts,
│                                    #   Channels, Users, CannedResponses, Notifications
├── Infrastructure/                  # Persistence (EF Core), Messaging (RabbitMQ),
│                                    #   Channels (Meta/Instagram/WhatsApp/TikTok),
│                                    #   Identity, Storage, Notifications, RealTime
└── Api/                             # Controllers, Hubs, Middleware, Webhooks
```

### 1.5 Technology Stack

| Layer | Technology |
|---|---|
| Frontend | React, TypeScript, Vite, Tailwind CSS, TanStack Query, SignalR Client |
| Backend | .NET 10, ASP.NET Core, EF Core, SignalR |
| Database | PostgreSQL |
| Cache / rate limiting / backplane | Redis |
| Messaging | RabbitMQ (MVP); Kafka optional later |
| Infrastructure | Docker, Docker Compose, Nginx, Object Storage |

---

## 2. Domain Model

### 2.1 Entities

All tenant-scoped entities carry a `TenantId` and are only accessible within their tenant.

#### Tenant

```text
Id          Guid
Name        string
Slug        string (unique, url-safe)
Status      TenantStatus (Active | Suspended | Deleted)
CreatedAt   DateTime
UpdatedAt   DateTime
```

#### User

```text
Id          Guid
TenantId    Guid
Name        string
Email       string (unique per tenant)
Role        Role (Owner | Admin | Agent)
Status      UserStatus (Active | Invited | Disabled)
CreatedAt   DateTime
UpdatedAt   DateTime
```

Identity credentials (password hash, external login) live in the Identity subsystem, keyed by `UserId`.

#### Channel

```text
Id                        Guid
TenantId                  Guid
Platform                  Platform (FacebookMessenger | Instagram | WhatsApp | TikTok)
DisplayName               string
ExternalAccountId         string
Status                    ChannelStatus (Connected | Disconnected | Error | ReauthorizationRequired)
CredentialReference       string   # reference into encrypted credential store, never the token itself
ConnectedAt               DateTime?
LastWebhookAt             DateTime?
LastSuccessfulSyncAt      DateTime?
CreatedAt                 DateTime
UpdatedAt                 DateTime
```

#### Contact

```text
Id          Guid
TenantId    Guid
Name        string
Phone       string?
Email       string?
AvatarUrl   string?
Notes       string?        # lightweight custom notes shown in customer profile
CreatedAt   DateTime
UpdatedAt   DateTime
```

#### ContactPlatformIdentity

One contact may have multiple platform identities. The system does **not** auto-merge identities across platforms.

```text
Id                    Guid
TenantId              Guid
ContactId             Guid
Platform              Platform
ExternalPlatformUserId string
ExternalAccountId     string          # the business's channel account the customer talks to
ChannelId             Guid?           # linked channel, when determinable
CreatedAt             DateTime
UpdatedAt             DateTime

UNIQUE (TenantId, Platform, ChannelId, ExternalPlatformUserId)
```

#### Conversation

```text
Id                     Guid
TenantId               Guid
ChannelId              Guid
ContactId              Guid
ExternalConversationId string
Status                 ConversationStatus (Open | Pending | Closed)
LastMessageAt          DateTime
UnreadCount            int
CreatedAt              DateTime
UpdatedAt              DateTime

UNIQUE (ChannelId, ExternalConversationId)
```

There is intentionally **no `AssignedUserId`**. There is no conversation owner.

#### Message

```text
Id                  Guid
TenantId            Guid
ConversationId      Guid
ExternalMessageId   string
SenderType          SenderType (Customer | Staff | System)
SenderUserId        Guid?          # staff id when SenderType == Staff, else null
MessageType         MessageType (Text | Image | Video | Audio | Document | ...)
Direction           Direction (Inbound | Outbound)
Content             string
ProviderTimestamp   DateTime?       # provider-provided timestamp
WebhookReceivedAt   DateTime?
CreatedAt           DateTime
DeliveryStatus      DeliveryStatus?  # outbound only, where provider supports

UNIQUE (ConversationId, ExternalMessageId)
```

Message deduplication key: `ChannelId + ExternalMessageId` per the PRD. Because every conversation belongs to exactly one channel, the equivalent `(ConversationId, ExternalMessageId)` unique index enforces the same invariant. The Application layer checks this idempotency key before persisting a normalized message.

#### Attachment

```text
Id             Guid
TenantId       Guid
MessageId      Guid
StorageKey     string          # object storage reference
FileName       string
MimeType       string
SizeBytes      long
Url            string?         # signed/secure URL for retrieval
CreatedAt      DateTime
```

#### InternalNote

```text
Id             Guid
TenantId       Guid
ConversationId Guid
Content        string
CreatedByUserId Guid
CreatedAt      DateTime
```

#### CannedResponse

```text
Id             Guid
TenantId       Guid
Title          string
Shortcut       string           # e.g. "/location"
Content        string
CreatedBy      Guid
CreatedAt      DateTime
UpdatedAt      DateTime
```

#### WebhookEvent

```text
Id                Guid
TenantId          Guid
ChannelId         Guid
ExternalEventId   string
EventType         string
PayloadReference  string          # storage reference or raw payload snapshot
Status            WebhookEventStatus (Received | Processing | Processed | Failed | Ignored)
RetryCount        int
ReceivedAt        DateTime
ProcessedAt       DateTime?
ErrorMessage      string?
```

#### Notification

```text
Id             Guid
TenantId       Guid
UserId         Guid
Type           NotificationType (NewMessage | NewUnreadConversation | NewInternalNote |
                                MessageDeliveryFailed | ChannelDisconnected | ChannelReauthorizationRequired)
Title          string
Body           string
Data           jsonb?            # structured context (e.g. conversationId)
ReadAt         DateTime?
CreatedAt      DateTime
```

#### AuditLog

```text
Id             Guid
TenantId       Guid
ActorUserId    Guid?
Action         string
ResourceType   string
ResourceId     string?
Timestamp      DateTime
Metadata       jsonb?
```

### 2.2 Enums

- `Role`: `Owner`, `Admin`, `Agent`
- `Platform`: `FacebookMessenger`, `Instagram`, `WhatsApp`, `TikTok`
- `TenantStatus`: `Active`, `Suspended`, `Deleted`
- `UserStatus`: `Active`, `Invited`, `Disabled`
- `ChannelStatus`: `Connected`, `Disconnected`, `Error`, `ReauthorizationRequired`
- `ConversationStatus`: `Open`, `Pending`, `Closed`
- `SenderType`: `Customer`, `Staff`, `System`
- `MessageType`: `Text`, `Image`, `Video`, `Audio`, `Document`, `Sticker`, `Link`
- `Direction`: `Inbound`, `Outbound`
- `DeliveryStatus`: `Pending`, `Sent`, `Delivered`, `Read`, `Failed`
- `WebhookEventStatus`: `Received`, `Processing`, `Processed`, `Failed`, `Ignored`

### 2.3 Value Objects

- `CredentialReference` — encrypted reference to a stored secret; never serializes the token itself.
- `ExternalIdentity` — `(Platform, ExternalAccountId, ExternalPlatformUserId)` used by adapters for contact/conversation lookup.

### 2.4 Domain Invariants

1. No `ConversationAssignment` entity exists anywhere in the system.
2. `ChannelId + ExternalConversationId` is unique per channel — prevents duplicate conversations.
3. `ChannelId + ExternalMessageId` (via `ConversationId` + `ExternalMessageId` unique index) is unique — prevents duplicate messages.
4. Internal notes are never sent to external platforms.
5. Staff messages must record `SenderUserId`.
6. Delivery statuses are only set to values the provider actually supports.

### 2.5 Domain Events

Domain events raise on notable state changes and are published to the real-time layer:

```text
MessageReceived
MessageSent
MessageStatusChanged
ConversationCreated
ConversationUpdated
ConversationStatusChanged
InternalNoteCreated
NotificationCreated
```

---

## 3. Database Design

### 3.1 Tables

```text
tenants
users
channels
contacts
contact_platform_identities
conversations
messages
attachments
internal_notes
canned_responses
webhook_events
notifications
audit_logs
```

There is no `conversation_assignments` table in the MVP.

### 3.2 Important Indexes

```text
users                (TenantId), (Email)
channels             (TenantId), (TenantId, Platform)
contacts             (TenantId)
contact_platform_identities (TenantId), (ContactId), (Platform, ExternalPlatformUserId)
conversations        (TenantId), (ChannelId, ExternalConversationId) UNIQUE,
                     (ContactId), (Status), (LastMessageAt DESC)
messages             (TenantId), (ConversationId, CreatedAt), (ConversationId, ExternalMessageId) UNIQUE,
                     (ExternalMessageId)
internal_notes       (TenantId), (ConversationId, CreatedAt)
webhook_events       (TenantId), (ChannelId), (Status), (ReceivedAt)
notifications        (TenantId, UserId, ReadAt)
audit_logs           (TenantId, Timestamp)
```

### 3.3 Tenant Isolation Strategy

- Every tenant-scoped table carries `TenantId`.
- A global EF Core query filter applies `TenantId == currentTenantId` on every tenant-scoped entity.
- Authorization middleware resolves the tenant from the JWT claim; all controllers operate only on that tenant.
- A second enforcement layer (authorization handlers / resource checks) rejects cross-tenant access at the use-case boundary. Database constraints alone are not the isolation mechanism — the API layer enforces it.

### 3.4 Messaging / Outbox

The MVP relies on RabbitMQ queues. For outbound message reliability, outbound sends are represented as `Message` rows with `DeliveryStatus = Pending` before enqueueing. If at-least-once outbound semantics require stronger guarantees, a transactional outbox table (`outbox_events`) will be added; it is out of MVP scope.

---

## 4. Channel Adapter Architecture

### 4.1 Interface

```text
IMessagingChannel
│
├── FacebookMessengerAdapter
├── InstagramAdapter
├── WhatsAppAdapter
└── TikTokAdapter
```

```text
interface IMessagingChannel
{
    Platform Platform { get; }
    ChannelCapabilities Capabilities { get; }
    Task<WebhookValidationResult> ValidateWebhookAsync(HttpRequest request);
    Task<NormalizedWebhookPayload> ParseWebhookAsync(HttpRequest request);
    Task<OutboundSendResult> SendAsync(SendMessageCommand command, ChannelCredential credential);
    Task<OutboundSendResult> SendMediaAsync(SendMediaCommand command, ChannelCredential credential);
    Task<ChannelHealth> CheckHealthAsync(Channel channel);
    Task<ReauthorizationResult> ReauthorizeAsync(...);
}
```

### 4.2 Capability Model

Adapters **declare** what they support; the system must never assume feature parity.

```text
ChannelCapabilities
{
    SupportsInboundText
    SupportsOutboundText
    SupportsMedia
    SupportsDeliveryStatus        # Sent/Delivered/Read as provider reports
    SupportsReadStatus
    SupportsWebhookSignatures
    SupportsConversationReopen    # whether inbound can reopen closed conversations
}
```

UI and API behavior derive from `Capabilities`. Unsupported delivery statuses are never fabricated or displayed.

### 4.3 Webhook Responsibilities

1. Validate request (signature, timestamp, source, webhook configuration).
2. Persist the webhook event (see §5 inbound pipeline).
3. Parse and normalize payload to the canonical message model.
4. Perform idempotency check.

### 4.4 Canonical Message Model

```json
{
  "tenantId": "tenant-id",
  "channelId": "channel-id",
  "externalMessageId": "platform-message-id",
  "externalConversationId": "platform-conversation-id",
  "senderId": "customer-id",
  "senderName": "John Doe",
  "platform": "WHATSAPP",
  "direction": "INBOUND",
  "messageType": "TEXT",
  "content": "Hello",
  "timestamp": "2026-08-11T10:00:00Z"
}
```

Common fields: tenant ID, channel ID, external message ID, external conversation ID, sender ID, sender name, platform, direction, message type, content, timestamp, attachments, delivery status.

### 4.5 Per-Platform Notes

- **Facebook Messenger / Instagram (Meta):** Graph API webhooks, page-scoped identity. Signature verification via app secret. Sending via Graph API page tokens.
- **WhatsApp Business:** Cloud API webhooks, signature verification (X-Hub-Signature-256). Respect 24-hour conversation window and template requirements for outbound outside the window. Message-level delivery/read status is available.
- **TikTok:** Adapter determines which features are supported; capability flags reflect real API constraints. Delivery/read status may be limited.

---

## 5. Inbound Message Pipeline

### 5.1 Flow

```text
Customer
   ↓
WhatsApp / Instagram / Facebook / TikTok
   ↓
Webhook endpoint (/webhooks/{provider})
   ↓
Validate request (signature, timestamp, source)
   ↓
Persist WebhookEvent (Received)
   ↓
Idempotency check (ExternalEventId) — duplicate → mark Ignored, return 200
   ↓
Queue event (RabbitMQ: webhook-events)
   ↓
Return 200 to provider quickly
   ↓
Worker: Message Processor
   ↓
Normalize payload via channel adapter
   ↓
Resolve channel → tenant
   ↓
Find or create conversation (ChannelId + ExternalConversationId)
   ↓
Reopen if Closed (default: Closed + new customer message → Open)
   ↓
Idempotency check (ChannelId + ExternalMessageId) — duplicate → skip persist
   ↓
Persist message (ProviderTimestamp, WebhookReceivedAt, CreatedAt)
   ↓
Update conversation: LastMessageAt, UnreadCount, Status
   ↓
Process media attachments (object storage, secure URLs)
   ↓
Mark WebhookEvent Processed
   ↓
Publish domain events
   ↓
SignalR broadcast to all authorized users in tenant
   ↓
Create in-app/browser notifications as configured
```

### 5.2 Ordering

- Store provider timestamp, webhook receipt timestamp, and internal creation timestamp.
- Conversation display ordering uses provider timestamps where reliable; missing/inconsistent timestamps fall back to webhook receipt timestamp.
- Out-of-order arrivals are tolerated: messages are inserted with their provider timestamps and sorted for display; conversation `LastMessageAt` is updated to the max provider timestamp.

### 5.3 Closed Conversation Reopening

Default behavior: a new inbound customer message on a closed conversation sets status to `Open`. This is the default unless business settings specify otherwise (no overrides in MVP — reopening is unconditional).

---

## 6. Outbound Message Pipeline

### 6.1 Flow

```text
Staff sends reply
   ↓
POST /api/v1/conversations/{id}/messages (Direction=Outbound, SenderType=Staff)
   ↓
Create Message row, DeliveryStatus = Pending
   ↓
Enqueue outbound job (RabbitMQ: outbound-messages)
   ↓
Return 201 (message accepted)
   ↓
Worker: Outbound Sender
   ↓
Load channel + credentials (decrypted)
   ↓
Channel adapter SendAsync
   ↓
Success: update DeliveryStatus (Sent/Delivered) as provider reports
   ↓
Failure (temporary): retry with backoff
   ↓
Failure (permanent): DeliveryStatus = Failed, notify admins, record error
   ↓
Broadcast message.sent / message.status_changed via SignalR
```

### 6.2 Duplicate Outbound Prevention

- Outbound `Message` rows have a client-generated idempotency key; the API returns the existing message if the same key is re-submitted.
- A message is sent at most once; retries are tracked on the message and cap at the retry policy limit.

### 6.3 Delivery Status

Where supported by the provider:

```text
Pending → Sent → Delivered → Read
        ↘ Failed
```

Status transitions arrive via webhook events and update the `Message` row, then broadcast `message.status_changed`. The system never fabricates unsupported statuses.

---

## 7. Real-Time Architecture

### 7.1 SignalR Hub

```text
/hubs/inbox
```

- Clients connect with a JWT; the hub authenticates and reads the tenant claim.
- Users are grouped by `tenantId`: `Group: tenant-{tenantId}`.
- Events are only published to the authenticated user's own tenant group. Tenant B users receive none of Tenant A's events.

### 7.2 Event Catalog

```text
message.received
message.sent
message.status_changed
conversation.created
conversation.updated
conversation.status_changed
internal_note.created
notification.created
channel.health_changed
```

### 7.3 Delivery

- Domain events from message processing are published to a SignalR dispatcher that fans out to the tenant group.
- Redis backplane allows multiple API instances to share hub connections in scaled deployments.
- The React client uses the SignalR client to update conversation list, message thread, unread counts, and status live without page refresh.

---

## 8. API Surface

### 8.1 REST Endpoints

```text
POST   /api/v1/auth/login
POST   /api/v1/auth/refresh
POST   /api/v1/auth/register              # creates account + tenant + owner user
GET    /api/v1/auth/me

GET    /api/v1/tenants/{tenantId}
PUT    /api/v1/tenants/{tenantId}

GET    /api/v1/users
POST   /api/v1/users                       # invite staff
PATCH  /api/v1/users/{id}                  # role, status
DELETE /api/v1/users/{id}

GET    /api/v1/channels
POST   /api/v1/channels                    # start connect flow
GET    /api/v1/channels/{id}
POST   /api/v1/channels/{id}/disconnect
POST   /api/v1/channels/{id}/reconnect
POST   /api/v1/channels/{id}/reauthorize
GET    /api/v1/channels/{id}/health

GET    /api/v1/contacts/{id}
PATCH  /api/v1/contacts/{id}               # name, notes

GET    /api/v1/conversations               # filters + search + pagination
GET    /api/v1/conversations/{id}
PATCH  /api/v1/conversations/{id}          # status change
POST   /api/v1/conversations/{id}/read     # mark read for the shared business inbox
GET    /api/v1/conversations/{id}/messages

POST   /api/v1/conversations/{id}/messages # staff reply
POST   /api/v1/conversations/{id}/notes    # internal note

POST   /api/v1/attachments                 # media upload → object storage → secure URL

GET    /api/v1/canned-responses
POST   /api/v1/canned-responses
PUT    /api/v1/canned-responses/{id}
DELETE /api/v1/canned-responses/{id}

GET    /api/v1/notifications
POST   /api/v1/notifications/{id}/read

GET    /api/v1/audit-logs
```

**Unread / read state.** Each conversation maintains `UnreadCount`. Opening a conversation marks it read for the shared business inbox, not for an individual agent (PRD §42). This avoids per-agent read-state complexity in the MVP.

Webhook endpoints:

```text
/webhooks/meta        # Facebook Messenger + Instagram
/webhooks/whatsapp
/webhooks/tiktok
```

Exact routes may vary per provider requirements.

### 8.2 Authentication & Authorization

- JWT-based auth (`access` token short-lived, `refresh` token for rotation).
- `tenantId` claim binds every request to a tenant.
- Role-based authorization matrix:

| Capability | Owner | Admin | Agent |
|---|---|---|---|
| View all tenant conversations | ✅ | ✅ | ✅ |
| Search/filter conversations | ✅ | ✅ | ✅ |
| Send customer replies | ✅ | ✅ | ✅ |
| Add internal notes | ✅ | ✅ | ✅ |
| Change conversation status | ✅ | ✅ | ✅ |
| Use canned responses | ✅ | ✅ | ✅ |
| Manage canned responses | ✅ | ✅ | ❌ |
| Manage users | ✅ | ✅ | ❌ |
| Manage channels | ✅ | where permitted | ❌ |
| Manage workspace settings | ✅ | supported subset | ❌ |
| View channel health | ✅ | ✅ | ❌ |
| View audit logs | ✅ | ❌ | ❌ |

All roles are restricted to their own tenant.

### 8.3 Pagination & Filtering

- Conversation list supports status filter (`All/Open/Pending/Closed/Unread`) and platform filter (`Facebook/Instagram/WhatsApp/TikTok`).
- No assignment filters exist (`Assigned to me`, `My conversations` are intentionally absent).
- Cursor or offset pagination on conversation and message lists.

---

## 9. Security

### 9.1 Tenant Isolation

- Backend-enforced on every request via JWT `tenantId` claim + global query filters + use-case authorization checks.
- Frontend filtering is never considered a security mechanism.
- Cross-tenant access attempts are denied and logged.

### 9.2 Authentication & Authorization

- HTTPS/TLS everywhere.
- Secure credential hashing (password via ASP.NET Core Identity / PBKDF2+).
- Role-based access control enforced via authorization policies and handlers.

### 9.3 Credential Security

- Provider tokens/credentials encrypted at rest (application-level encryption) and stored via secret management; only a `CredentialReference` is stored on `Channel`.
- Tokens never appear in logs, error messages, frontend responses, or audit logs.

### 9.4 Webhook Security

- Verify provider signature and timestamp where supported.
- Validate event source and webhook configuration.
- Reject invalid requests; implement replay protection where appropriate.

### 9.5 API Security

Every API request enforces: authentication, authorization, tenant access, input validation, rate limiting.

### 9.6 Other Controls

- Rate limiting: authentication, API requests, webhooks, message sending, file uploads (Redis-based sliding window).
- Input validation at the API boundary (FluentValidation or DataAnnotations).
- File upload validation: MIME allowlist, size limits, storage in object storage with signed URLs; malicious uploads blocked.
- SQL injection prevented via EF Core parameterization.
- XSS prevented via framework encoding + React escaping.
- CSRF protection where applicable.
- Audit logging of important actions.

### 9.7 Threat Coverage

Cross-tenant access, broken authorization, token leakage, webhook spoofing, replay attacks, duplicate messages, API abuse, malicious uploads, SQL injection, XSS, CSRF, credential theft.

---

## 10. Reliability & Scalability

### 10.1 Idempotency

- Webhook events: dedupe on `ExternalEventId` (per channel). Duplicate delivery → `Ignored`, no message created.
- Messages: unique constraint on `(ConversationId, ExternalMessageId)`.
- Conversations: unique constraint on `(ChannelId, ExternalConversationId)`.
- Outbound sends: idempotency key on the outbound request.

### 10.2 Retry & Dead Letter

```text
Webhook
   ↓
Processing
   ↓
Temporary failure → exponential backoff, max retry count
   ↓
Max retries exceeded → Dead Letter Queue
```

- Exponential backoff with jitter; permanent failures (invalid credentials, revoked permission, invalid recipient, unsupported message type) are not retried indefinitely — they are reported and marked `Failed`.
- DLQ messages can be inspected and reprocessed by operators.

### 10.3 Message Reliability

- Webhook events are persisted before heavy processing so nothing is lost.
- Heavy processing is asynchronous (worker), never in the HTTP request path.
- Temporary errors (provider timeout, network failure, rate limit, DB hiccup) are retried.

### 10.4 High Availability

- Stateless API services, horizontally scalable workers.
- RabbitMQ for durable queueing; Redis backplane for SignalR.
- Database backups, health checks, monitoring, alerting.

### 10.5 Performance Targets

| Scenario | Target |
|---|---|
| Incoming message → shared inbox | within seconds of webhook receipt |
| Normal API operations | few hundred ms, excluding provider latency |
| Real-time event propagation | minimal delay |

### 10.6 Channel Health Monitoring

Monitor: connection status, last webhook received, last successful outbound message, last sync, authentication status, provider errors.

Administrators are notified when a channel becomes unhealthy (e.g., `ReauthorizationRequired`, stale webhook, elevated error rate).

---

## 11. Frontend Architecture

### 11.1 Stack

React + TypeScript + Vite + Tailwind CSS, TanStack Query (server state), SignalR client (real-time), React Router.

### 11.2 Views (mapped to preview.html prototype)

```text
/                       → Shared Inbox (default)
/overview               → Overview dashboard
/channels               → Channels
/team                   → Team
/canned                 → Canned Responses
/audit                  → Audit Log
/settings               → Settings
/auth/login             → Login
/auth/register          → Registration + workspace creation + channel connect
```

- **Shared Inbox:** three-column layout — conversation list (search + status/platform filters + unread + last message + timestamp + status), active conversation thread (customer/staff/note bubbles, status control, composer with internal-note toggle, canned responses, attachments, emoji), customer profile sidebar (name, phone, email, avatar, platform, platform ID, notes, shared-visibility notice).
- **Overview:** KPI cards (total/open/unread conversations, messages sent), channel activity breakdown, reliability panel (webhook success, duplicate rate, outbound failure, queue depth, realtime latency, connected channels).
- **Channels:** connected channel cards with status, last webhook, manage/reconnect actions, connect-channel flow.
- **Team:** member table with role/status/last active, invite flow.
- **Canned:** response table with title, shortcut, content, edit/new.
- **Audit:** audit log table with time, actor, action, resource, metadata.
- **Settings:** workspace (name, slug), notifications toggles, security, data retention.

### 11.3 Real-Time Client

- Single SignalR connection per authenticated session.
- On events: invalidate/update TanStack Query caches for conversation list, thread, unread counts, notifications.
- Toast/in-app notifications for new messages, delivery failures, channel alerts.
- Browser notifications via Notification API when enabled.

### 11.4 Responsive Behavior

- Desktop: full three-column inbox.
- Tablet (~≤1000px): hide profile panel.
- Mobile (~≤700px): sidebar collapses to icons, inbox single-column (conversation list or thread), settings nav hidden.

### 11.5 Accessibility

Keyboard navigation, appropriate contrast, accessible labels/controls, screen-reader-friendly structure, status indicators that do not rely only on color. UI strings externalized (English first).

---

## 12. Observability

### 12.1 Logging

- Structured JSON application logs with tenant and correlation IDs.
- Sensitive data (tokens, credentials) never logged.

### 12.2 Metrics

```text
Webhook processing latency
Webhook failure rate
Message processing latency
Outbound failure rate
Queue depth
API latency
Active real-time connections
Database performance
Channel health
```

### 12.3 Tracing & Monitoring

- Distributed tracing across API → queue → worker.
- Queue monitoring, webhook monitoring, external API monitoring.
- Error tracking and alerting for channel health and failure rates.

---

## 13. Phased Delivery Map

Mapping spec sections to PRD development phases:

| Phase | PRD | Covers | Spec sections |
|---|---|---|---|
| 1 — Foundation | §88 | Solution, auth, tenants, users, roles, PostgreSQL, Redis, Docker, React shell | 1, 2, 3, 8, 9 |
| 2 — Shared Inbox | §88 | Contacts, conversations, messages, status, search, filters, unread, internal notes | 2, 3, 8, 11 |
| 3 — Real-Time | §88 | SignalR, message/status/conversation/note events, shared inbox sync | 7, 11 |
| 4 — First Channel | §88 | One channel end-to-end (connect, webhook, validate, persist, queue, normalize, store, inbox, reply, delivery status) | 4, 5, 6, 10 |
| 5 — Additional Channels | §88 | Instagram, WhatsApp, TikTok via the common adapter | 4, 5, 6 |
| 6 — Reliability | §88 | Idempotency, retry, DLQ, outbound retry, webhook replay, channel health, monitoring, alerting | 10, 12 |
| 7 — Productivity | §88 | Canned responses, attachments, browser notifications, better search, advanced filters, typing indicators | 8, 11 |
| 8 — SaaS | §88 | Subscription, billing, usage limits, workspace settings, analytics, audit logs, data retention | 8, 9 |

### MVP Definition of Done (from PRD §84)

- Users log in, belong to a tenant, roles work.
- All authorized users see all tenant conversations; no assignment; search, filters, unread work.
- Customer messages appear in inbox; agents respond; responses reach the customer; staff sender identity recorded; message status works where supported.
- Internal notes work; all authorized users see them; staff messages appear in real time.
- Webhook events persisted; duplicates do not create duplicate messages; failed processing retried; DLQ exists; outbound failures handled.
- At least one channel works end-to-end; connection status visible; disconnection handled.
- Tenant isolation, role authorization, secure credential storage, HTTPS, no sensitive logging.

---

## Appendix A: Key Design Decisions

1. **No conversation assignment** — matches the core product principle "Shared Inbox, Shared Visibility". Enforced in the domain model (no entity, no filter).
2. **Adapters declare capabilities** — prevents fabricated features and unsupported delivery statuses (esp. TikTok).
3. **Backend-enforced tenant isolation** — global EF query filters + use-case authorization; frontend filtering is display-only.
4. **Queue-first ingestion** — webhooks return 200 quickly after persisting and enqueueing; heavy work happens in workers.
5. **Dedupe keys** — webhook `ExternalEventId`, message `ChannelId+ExternalMessageId`, conversation `ChannelId+ExternalConversationId`.
6. **Reopen-on-inbound** — closed conversations reopen to `Open` on new customer messages by default.
7. **Read state is shared** — opening a conversation marks it read for the business inbox, not per-agent (avoids per-agent read-state complexity).
8. **Tech stack follows PRD** — .NET 10, React, PostgreSQL, Redis, RabbitMQ, SignalR.

## Appendix B: Open Items (deferred by design)

- Transactional outbox for stronger outbound at-least-once guarantees (add if outbound loss is observed in practice).
- Per-agent read state (explicitly out of MVP per PRD §42).
- Typing indicators / concurrent reply awareness (Phase 7 enhancement).
- Billing, subscription, and usage limits (Phase 8; decoupled from messaging pipeline).
- Mobile push notifications (later).
- Additional channels (Telegram, Viber, LINE, email, live chat — future roadmap).
