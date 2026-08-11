# Product Requirements Document (PRD)
# Unified Multi-Channel Inbox Platform

**Version:** 1.1  
**Date:** August 11, 2026  
**Product Type:** Multi-Tenant SaaS  
**Target Users:** Small-to-Medium Businesses

---

# 1. Executive Summary

The **Unified Multi-Channel Inbox Platform** is a lightweight, multi-tenant messaging platform designed for small and medium-sized businesses.

The platform aggregates customer conversations from multiple social and messaging channels into a single shared inbox.

Instead of business staff switching between Facebook Messenger, Instagram, WhatsApp, and TikTok, all conversations are accessible from one application.

The platform focuses specifically on:

- Customer messaging
- Shared team inbox
- Real-time communication
- Conversation history
- Internal staff collaboration
- Basic customer information
- Multi-channel integration

The platform intentionally avoids complex CRM, e-commerce, and marketing functionality.

### Core concept

A business has one shared inbox.

Every authorized user belonging to that business can access the business's conversations.

There is **no conversation assignment or individual conversation ownership** in the initial product.

```text
Business
│
├── User A
├── User B
├── User C
│
└── Shared Inbox
     ├── Conversation 1
     ├── Conversation 2
     ├── Conversation 3
     └── Conversation 4
```

All authorized users can view and manage the same conversations.

---

# 2. Product Vision

> **One shared inbox for every customer conversation.**

The product should allow a small business to connect its messaging channels and allow all authorized staff members to manage customer communications from one simple interface.

The product should feel like a shared workplace inbox rather than a complex CRM.

---

# 3. Problem Statement

Small businesses commonly communicate with customers through several platforms.

For example:

```text
Facebook Messenger
Instagram
WhatsApp
TikTok
```

This creates several problems:

- Staff must switch between applications.
- Conversations are scattered across platforms.
- Customer history is difficult to track.
- Multiple employees may respond to the same customer without seeing each other's activity.
- Messages can easily be missed.
- Frequently repeated questions require repetitive typing.
- Business owners lack a centralized view of customer communication.

The platform solves this by providing one shared inbox.

---

# 4. Core Product Principle

The primary product model is:

> **Shared Inbox, Shared Visibility.**

Every authorized user belonging to a business can access the business's conversations.

There is no concept of:

- Conversation ownership
- Assigned agent
- Assigned conversation
- Agent-specific inbox
- "My conversations"

Instead, the system records **which staff member performed an action** for accountability.

For example:

```text
Customer:
"Do you have this product?"

Kusal:
"Yes, it is available."

Sita:
"Customer also asked about delivery."

Customer sees:
The customer-facing messages.

Staff sees:
Customer messages
+
Messages sent by Kusal
+
Internal note from Sita
```

---

# 5. Goals

## 5.1 Primary Goals

The platform must:

- Centralize customer conversations.
- Support multiple messaging channels.
- Provide a shared inbox.
- Allow all authorized business users to access conversations.
- Deliver new messages in near real time.
- Allow staff to respond from one interface.
- Maintain complete conversation history.
- Support internal staff notes.
- Support conversation statuses.
- Support canned responses.
- Prevent duplicate message processing.
- Securely isolate tenant data.
- Provide reliable webhook processing.
- Provide channel connection monitoring.

---

# 6. Non-Goals

The initial version will not attempt to become a complete CRM, help desk, or e-commerce system.

The following are outside the initial scope:

- Product catalog
- Shopping cart
- Order management
- Inventory management
- Payment processing
- Marketing campaigns
- Email marketing
- Advanced marketing automation
- Sales pipeline
- Lead scoring
- Customer loyalty programs
- Complex CRM workflows
- Voice calls
- Video calls
- Conversation assignment
- Agent ownership
- Agent-specific inboxes
- Complex ticketing/SLA system

---

# 7. Target Users

## 7.1 Business Owner

The business owner manages the workspace.

The owner can:

- Connect messaging channels.
- Manage users.
- Manage roles.
- View all conversations.
- Send messages.
- Add internal notes.
- Change conversation status.
- Manage canned responses.
- View channel health.
- Manage workspace settings.

---

## 7.2 Administrator

An administrator manages the business workspace on behalf of the owner.

Administrators can:

- Manage users.
- View all conversations.
- Send customer replies.
- Add internal notes.
- Change conversation status.
- Manage canned responses.
- Manage supported workspace settings.
- View channel health.

---

## 7.3 Agent / Staff

Agents are normal business employees who work with the shared inbox.

All authorized agents belonging to the business have access to the business's conversations.

Agents can:

- View all conversations belonging to their business.
- Search conversations.
- Filter conversations.
- Read complete conversation history.
- Send customer replies.
- Add private internal notes.
- Change conversation status.
- Use canned responses.
- View customer information.
- See messages sent by other staff members.
- Receive real-time updates.

There is no conversation assignment.

---

# 8. Access Model

The access model is tenant-based.

```text
Tenant / Business
│
├── Owner
├── Admin
├── Agent
│
└── Shared Inbox
     │
     ├── Conversation A
     ├── Conversation B
     ├── Conversation C
     └── Conversation D
```

All authorized users in the tenant can access all conversations belonging to that tenant.

### Example

If a business has:

```text
Kusal
Ram
Sita
```

all three users can see:

```text
Customer A
Customer B
Customer C
Customer D
```

If Kusal sends a reply, Ram and Sita will see that reply in real time.

---

# 9. Multi-Tenancy

The platform must support multiple businesses using the same application.

Each business is represented by a `Tenant`.

Example:

```text
Tenant A
├── Users
├── Channels
├── Contacts
├── Conversations
├── Messages
└── Canned Responses

Tenant B
├── Users
├── Channels
├── Contacts
├── Conversations
├── Messages
└── Canned Responses
```

Tenant A must never be able to access Tenant B's data.

Tenant isolation must be enforced by the backend.

Frontend filtering alone must never be considered a security mechanism.

---

# 10. Supported Channels

The initial product will support:

- Facebook Messenger
- Instagram Direct Messages
- WhatsApp Business
- TikTok Direct Messages

Channel capabilities depend on the APIs and policies provided by each platform.

The system must not assume that every channel supports identical functionality.

---

# 11. Facebook Messenger

The system should support, where available through the official API:

- Receiving messages.
- Sending messages.
- Conversation identification.
- Customer identification.
- Message timestamps.
- Supported media.
- Delivery status.

---

# 12. Instagram Direct Messages

The system should support, where available:

- Receiving DMs.
- Sending replies.
- Customer identification.
- Conversation mapping.
- Supported media.
- Delivery/read status where available.

---

# 13. WhatsApp Business

The system should support:

- Receiving messages.
- Sending messages.
- Conversation mapping.
- Customer identification.
- Supported media.
- Delivery status.
- Read status where available.

WhatsApp messaging rules, templates, conversation windows, and API restrictions must be respected.

---

# 14. TikTok Direct Messages

The system should support TikTok messaging capabilities available through the applicable official API.

The platform must account for TikTok-specific limitations.

The channel adapter must determine which features are supported rather than assuming feature parity with WhatsApp or Meta.

---

# 15. Channel Adapter Architecture

The core application must remain independent of provider-specific payload structures.

Conceptually:

```text
IMessagingChannel
│
├── FacebookMessengerAdapter
├── InstagramAdapter
├── WhatsAppAdapter
└── TikTokAdapter
```

Each adapter is responsible for translating between the external platform and the unified messaging model.

Responsibilities may include:

- Webhook validation.
- Payload parsing.
- Message normalization.
- Sending messages.
- Sending media.
- Customer lookup.
- Conversation lookup.
- Delivery status processing.

---

# 16. Message Normalization

Different platforms provide different payload structures.

The platform must convert them into a common internal format.

Example:

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

Common fields:

- Tenant ID
- Channel ID
- External message ID
- External conversation ID
- Sender ID
- Sender name
- Platform
- Direction
- Message type
- Content
- Timestamp
- Attachments
- Delivery status

---

# 17. Core Domain Model

The initial domain should contain:

```text
Tenant
User
Role
Channel
Contact
Conversation
Message
Attachment
InternalNote
CannedResponse
WebhookEvent
Notification
AuditLog
```

There is intentionally **no ConversationAssignment entity**.

---

# 18. Tenant

Represents a business/workspace.

### Fields

```text
Id
Name
Slug
Status
CreatedAt
UpdatedAt
```

Possible statuses:

```text
Active
Suspended
Deleted
```

---

# 19. User

Represents a member of a business.

### Fields

```text
Id
TenantId
Name
Email
Role
Status
CreatedAt
UpdatedAt
```

Roles:

```text
Owner
Admin
Agent
```

---

# 20. Channel

Represents a connected messaging account.

### Fields

```text
Id
TenantId
Platform
DisplayName
ExternalAccountId
Status
CredentialReference
ConnectedAt
LastWebhookAt
LastSuccessfulSyncAt
CreatedAt
UpdatedAt
```

Possible platforms:

```text
FacebookMessenger
Instagram
WhatsApp
TikTok
```

Possible statuses:

```text
Connected
Disconnected
Error
ReauthorizationRequired
```

---

# 21. Contact

Represents a customer.

### Fields

```text
Id
TenantId
Name
Phone
Email
AvatarUrl
CreatedAt
UpdatedAt
```

Platform-specific identities should be stored separately where necessary.

For example:

```text
Contact
│
├── WhatsApp Identity
│     └── ExternalPlatformUserId
│
├── Instagram Identity
│     └── ExternalPlatformUserId
│
└── Facebook Identity
      └── ExternalPlatformUserId
```

The system should not assume that the same customer can automatically be identified across different platforms.

---

# 22. Conversation

Represents a customer conversation.

### Fields

```text
Id
TenantId
ChannelId
ContactId
ExternalConversationId
Status
LastMessageAt
UnreadCount
CreatedAt
UpdatedAt
```

There is intentionally **no `AssignedUserId`**.

There is no conversation owner.

---

# 23. Conversation Status

Initial statuses:

```text
Open
Pending
Closed
```

### Open

Conversation requires active communication.

### Pending

Staff is waiting for something before continuing.

Example:

> Waiting for customer confirmation.

### Closed

Conversation is considered completed.

A new customer message may automatically reopen a closed conversation.

Recommended behavior:

```text
Closed
   +
New Customer Message
   ↓
Open
```

---

# 24. Message

Represents a communication event.

### Fields

```text
Id
TenantId
ConversationId
ExternalMessageId
SenderType
SenderUserId
MessageType
Direction
Content
Timestamp
DeliveryStatus
CreatedAt
```

### SenderType

```text
Customer
Staff
System
```

### SenderUserId

For staff messages:

```text
SenderType = Staff
SenderUserId = ID of employee
```

For customer messages:

```text
SenderType = Customer
SenderUserId = NULL
```

This allows the system to show which employee sent a reply without assigning the conversation to that employee.

---

# 25. Internal Notes

Internal notes are private staff communication attached to a conversation.

Example:

```text
Internal Note:
Customer is asking for bulk pricing.
Check with the manager.
```

Internal notes:

- Are visible only to business users.
- Are never sent to customers.
- Must be visually distinguished from customer messages.
- Should appear chronologically.
- Should record which staff member created them.

Example:

```text
Sita
Internal Note
Customer requested bulk pricing.
10:32 AM
```

---

# 26. Canned Responses

Canned responses are predefined replies for frequently asked questions.

Example:

```text
Title:
Store Location

Content:
Our store is located at...
```

Fields:

```text
Id
TenantId
Title
Content
CreatedBy
CreatedAt
UpdatedAt
```

Agents can search/select a canned response and insert it into the message composer.

---

# 27. Unified Inbox

The inbox is the primary interface.

It should provide:

- Conversation list.
- Search.
- Platform indicators.
- Unread counts.
- Last message.
- Timestamp.
- Conversation status.
- Customer name.

Example:

```text
┌───────────────────────────────────────────┐
│ Shared Inbox                              │
├───────────────────────────────────────────┤
│ Search                                    │
│                                           │
│ 🟢 WhatsApp                              │
│ John Doe                                  │
│ "Is the store open today?"               │
│ 2 min ago                                 │
│                                           │
│ 🟣 Instagram                              │
│ Sarah                                     │
│ "Can I order this?"                      │
│ 5 min ago                                 │
│                                           │
│ 🔵 Messenger                              │
│ David                                     │
│ "What is the price?"                     │
│ 10 min ago                                │
└───────────────────────────────────────────┘
```

---

# 28. Shared Inbox Behavior

All authorized users see the same inbox.

Example:

```text
Business ABC

User A ─┐
User B ─┼──> Shared Conversations
User C ─┘
```

If User A sends a message:

```text
User A
   ↓
Customer
   ↓
Conversation updated
   ↓
User B sees update
User C sees update
```

The system should provide real-time synchronization.

---

# 29. Conversation View

The conversation view should display:

- Customer information.
- Platform.
- Complete message history.
- Internal notes.
- Staff identity for staff messages.
- Message timestamps.
- Delivery status.
- Attachments.
- Conversation status.

Example:

```text
John Doe
WhatsApp

────────────────────────

John:
Do you have this in stock?

Kusal:
Yes, it is available.

Sita:
Internal Note:
Customer may also ask about delivery.

John:
How much is delivery?

────────────────────────

[ Type a message... ] [Send]
```

---

# 30. Outbound Message Composer

The composer should support:

- Plain text.
- Emojis.
- Attachments/media where supported.
- Canned responses.

The user should be able to:

```text
Type message
    ↓
Attach media
    ↓
Select canned response
    ↓
Send
```

---

# 31. Real-Time Communication

The platform must provide real-time updates.

Recommended technology:

**SignalR/WebSockets**

Events may include:

```text
message.received
message.sent
message.status_changed
conversation.created
conversation.updated
conversation.status_changed
internal_note.created
notification.created
```

All authorized users connected to the business should receive appropriate events.

---

# 32. Real-Time Message Flow

```text
Customer
   ↓
WhatsApp / Instagram / Facebook / TikTok
   ↓
Webhook
   ↓
Message Queue
   ↓
Message Processor
   ↓
Database
   ↓
SignalR
   ↓
All authorized business users
```

The new message should appear without page refresh.

---

# 33. Webhook Processing

Webhook endpoints must be designed for high concurrency.

Webhook processing should:

1. Validate the request.
2. Verify provider signature where applicable.
3. Identify the channel.
4. Persist the webhook event.
5. Perform idempotency checks.
6. Queue the event.
7. Return an appropriate response.

Heavy processing should happen asynchronously.

---

# 34. Message Reliability

The system must prevent duplicate messages.

External providers may deliver the same event more than once.

Example:

```text
Webhook #1 → Message ABC → Process
Webhook #2 → Message ABC → Duplicate
Webhook #3 → Message ABC → Duplicate
```

Only one internal message should be created.

A uniqueness constraint should be implemented around an appropriate provider-specific identity, such as:

```text
ChannelId + ExternalMessageId
```

---

# 35. Webhook Event Storage

Webhook events should be persisted.

### Fields

```text
Id
TenantId
ChannelId
ExternalEventId
EventType
PayloadReference
Status
RetryCount
ReceivedAt
ProcessedAt
ErrorMessage
```

Status:

```text
Received
Processing
Processed
Failed
Ignored
```

This allows events to be:

- Investigated.
- Retried.
- Reprocessed.
- Audited.

---

# 36. Retry Strategy

Temporary failures should be retried.

Example:

```text
Attempt 1
   ↓
Failure
   ↓
Backoff
   ↓
Attempt 2
   ↓
Failure
   ↓
Backoff
   ↓
Attempt 3
   ↓
Dead Letter Queue
```

The system should use:

- Exponential backoff.
- Maximum retry count.
- Dead-letter handling.
- Structured error logging.

Permanent failures should not retry indefinitely.

---

# 37. Outbound Message Reliability

Outbound messages should also be processed reliably.

```text
Staff sends reply
       ↓
Create Message
Status = Pending
       ↓
Queue
       ↓
Channel Adapter
       ↓
External Platform
       ↓
Success / Failure
       ↓
Update Message
       ↓
Notify all authorized users
```

The system should prevent accidental duplicate outbound messages.

---

# 38. Message Delivery Status

Where supported by the external provider:

```text
Pending
Sent
Delivered
Read
Failed
```

The system must never fabricate unsupported statuses.

For example, if TikTok only provides limited status information, the UI should reflect only what is actually available.

---

# 39. Channel Connection Lifecycle

Administrators must be able to:

- Connect a channel.
- Disconnect a channel.
- Reconnect a channel.
- Reauthorize a channel.
- View connection status.
- View channel errors.
- View webhook health.

Example:

```text
WhatsApp
Status: Connected
Last webhook: 30 seconds ago
```

Or:

```text
Instagram
Status: Reauthorization Required

[Reconnect]
```

---

# 40. Channel Health

The system should monitor:

```text
Connection status
Last webhook received
Last successful outbound message
Last synchronization
Authentication status
Provider errors
```

Administrators should be notified when a channel becomes unhealthy.

---

# 41. Notifications

Notifications may include:

- New message.
- New unread conversation.
- New internal note.
- Message delivery failure.
- Channel disconnected.
- Channel requires reauthorization.

The initial version can support:

- In-app notifications.
- Browser notifications.

Mobile push notifications can be added later.

---

# 42. Unread Management

Each conversation should maintain an unread count.

Example:

```text
Conversation
UnreadCount = 3
```

When a user opens the conversation, the application may mark it as read for the business.

Because the inbox is shared, read state should be treated carefully.

Recommended MVP behavior:

> Reading a conversation marks it as read for the shared business inbox rather than for an individual agent.

This avoids creating unnecessary per-agent read-state complexity.

---

# 43. Search

Users should be able to search by:

- Customer name.
- Phone number.
- Platform username/handle.
- Message content.
- Conversation ID.

---

# 44. Conversation Filters

Initial filters:

```text
All
Open
Pending
Closed
Unread
```

Platform filters:

```text
Facebook
Instagram
WhatsApp
TikTok
```

There is no:

```text
Assigned to me
Assigned to someone
My conversations
```

because conversations are not assigned.

---

# 45. Customer Profile

The customer sidebar should display:

```text
Name
Phone
Email
Avatar
Platform
Platform ID
Custom Notes
Conversation History
```

The customer profile should remain lightweight.

It is not intended to become a full CRM.

---

# 46. Staff Activity

Although conversations are not assigned to individual users, staff actions should be identifiable.

For example:

```text
Kusal sent:
"Yes, we have it available."

Sita added internal note:
"Check delivery price."
```

Staff actions that should record the acting user include:

- Sending messages.
- Adding internal notes.
- Changing conversation status.
- Editing canned responses.
- Managing channels.
- Managing users.

---

# 47. Role-Based Access Control

## Owner

Full workspace access.

Can:

- Manage workspace.
- Manage users.
- Manage channels.
- Manage roles.
- View all conversations.
- Send messages.
- Add notes.
- Change statuses.
- Manage canned responses.
- View audit logs.

## Admin

Can:

- Manage agents.
- Manage conversations.
- Manage channels where permitted.
- Send messages.
- Add notes.
- Change statuses.
- Manage canned responses.

## Agent

Can:

- View all business conversations.
- Search conversations.
- Send customer replies.
- Add internal notes.
- Change conversation status.
- Use canned responses.
- View customer information.

All roles remain restricted to their own tenant.

---

# 48. Security

The platform must implement:

- HTTPS/TLS.
- Secure authentication.
- Authorization.
- Tenant isolation.
- Input validation.
- Rate limiting.
- Secure secret management.
- Audit logging.
- Secure file handling.

---

# 49. Credential Security

External platform credentials and access tokens must not be stored as plain text where avoidable.

Use:

- Encryption.
- Secret management.
- Restricted access.
- Token rotation where supported.

Tokens must never appear in:

- Application logs.
- Error messages.
- Frontend responses.
- Audit logs.

---

# 50. Data Encryption

Data must be protected both in transit and at rest.

### In transit

Use:

```text
HTTPS / TLS
```

### At rest

Use database/storage encryption.

Highly sensitive credentials may also require application-level encryption.

---

# 51. Privacy

The platform will process customer conversations and potentially sensitive information.

The platform must provide:

- Tenant isolation.
- Role-based access.
- Secure storage.
- Audit trails.
- Retention policies.
- Secure deletion.
- Controlled access to customer data.

The platform must comply with applicable privacy requirements and third-party messaging platform policies.

---

# 52. Audit Logging

Important actions should be logged.

Examples:

```text
User created
User removed
Role changed
Channel connected
Channel disconnected
Conversation status changed
Internal note created
Message sent
Canned response created
Canned response updated
```

Audit records:

```text
ActorUserId
TenantId
Action
ResourceType
ResourceId
Timestamp
Metadata
```

Sensitive credentials must never be recorded.

---

# 53. Rate Limiting

Rate limits should apply to:

- Authentication.
- API requests.
- Webhooks.
- Message sending.
- File uploads.

External provider limits must also be respected.

---

# 54. Scalability

The architecture should support horizontal scaling.

```text
             Load Balancer
                  │
        ┌─────────┼─────────┐
        ▼         ▼         ▼
      API #1    API #2    API #3
        │         │         │
        └─────────┼─────────┘
                  ▼
                Queue
                  │
        ┌─────────┼─────────┐
        ▼         ▼         ▼
     Worker #1 Worker #2 Worker #3
```

Webhook ingestion should not depend on a single server.

---

# 55. High Availability

Requirements:

- Stateless API services.
- Horizontally scalable workers.
- Database backups.
- Health checks.
- Queue-based processing.
- Retry mechanisms.
- Monitoring.
- Alerting.

---

# 56. Performance Requirements

Under normal operating conditions:

### Incoming message

The message should appear in the shared inbox within seconds of successful webhook receipt.

### API

Normal API operations should generally complete within a few hundred milliseconds, excluding external provider latency.

### Real-time

Events should be propagated to connected users with minimal delay.

---

# 57. Media Handling

The platform should support media where the provider allows it.

Possible types:

```text
Image
Video
Audio
Document
```

Large files should preferably use object storage rather than passing through application servers unnecessarily.

Example:

```text
Application
    ↓
Object Storage
    ↓
Secure URL
    ↓
Client
```

---

# 58. API Architecture

REST APIs should be used for normal application operations.

Example:

```text
/api/v1/auth
/api/v1/tenants
/api/v1/users
/api/v1/channels
/api/v1/contacts
/api/v1/conversations
/api/v1/messages
/api/v1/canned-responses
/api/v1/notifications
```

Webhook endpoints:

```text
/webhooks/meta
/webhooks/whatsapp
/webhooks/tiktok
```

Exact routes may vary depending on provider requirements.

---

# 59. Real-Time API

A SignalR hub may be provided:

```text
/hubs/inbox
```

Users should only receive events belonging to their authenticated tenant.

Example:

```text
Tenant A
 ├── User A
 └── User B

Both receive:
message.received
conversation.updated
message.sent
```

Tenant B users must receive none of Tenant A's events.

---

# 60. Database

Recommended database:

**PostgreSQL**

Primary tables:

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

There is no:

```text
conversation_assignments
```

table in the MVP.

Important indexes:

```text
TenantId
ConversationId
ChannelId
ContactId
ExternalMessageId
ExternalConversationId
LastMessageAt
Status
```

---

# 61. Message Ordering

External messages may arrive out of order.

The system should store:

- Provider timestamp.
- Webhook receipt timestamp.
- Internal creation timestamp.

Conversation display ordering should use provider timestamps where reliable.

The system should handle missing or inconsistent timestamps safely.

---

# 62. Duplicate Conversation Prevention

The same external conversation must not create multiple internal conversations.

A suitable identity should be maintained:

```text
ChannelId
+
ExternalConversationId
```

Provider-specific conversation semantics must be considered.

---

# 63. Conversation Reopening

When a new customer message arrives:

```text
Closed Conversation
       +
New Customer Message
       ↓
Open
```

This should be the default behavior unless business settings specify otherwise.

---

# 64. Shared Inbox Concurrency

Because all staff members can work on the same conversations, the system must handle simultaneous actions.

Example:

```text
Customer asks:
"Do you deliver?"

Kusal sees message
Sita sees message
Ram sees message
```

If Kusal replies, the reply should immediately appear for Sita and Ram.

The UI should update automatically to prevent stale conversation views.

---

# 65. Concurrent Reply Awareness

A future enhancement may show:

```text
Kusal is typing...
```

or:

```text
Kusal is replying...
```

This can help prevent two staff members from replying simultaneously.

For MVP, real-time message synchronization is sufficient.

---

# 66. Canned Response UX

Agents should be able to quickly search canned responses.

Example:

```text
Type:
/

↓
Store Location
Opening Hours
Payment Methods
Delivery Information

↓
Select:
Store Location

↓
Message inserted
```

The agent can edit the response before sending.

---

# 67. Onboarding

New business onboarding:

```text
Create Account
      ↓
Create Business Workspace
      ↓
Connect Messaging Channel
      ↓
Authorize Platform
      ↓
Verify Connection
      ↓
Invite Staff
      ↓
Open Shared Inbox
```

The business should be able to start receiving messages with minimal configuration.

---

# 68. Initial Dashboard

The dashboard should prioritize the inbox.

Example:

```text
┌──────────────────────────────────────────────────────┐
│ Unified Inbox                         Business Name  │
├───────────────┬──────────────────────┬───────────────┤
│ Conversations │ Active Conversation  │ Customer      │
│               │                      │               │
│ All           │ John Doe             │ John Doe      │
│ Open          │ WhatsApp             │ WhatsApp      │
│ Pending       │                      │ Phone         │
│ Closed        │ Hello, do you have   │ Notes         │
│ Unread        │ this product?        │               │
│               │                      │               │
│               │ Kusal                │               │
│               │ Yes, available.      │               │
│               │                      │               │
│               │ [Type message...]    │               │
└───────────────┴──────────────────────┴───────────────┘
```

---

# 69. Empty States

### No conversations

> No conversations yet. Connect a messaging channel to start receiving customer messages.

### No channels

> Connect your first messaging channel to start receiving messages.

### No unread messages

> You're all caught up.

---

# 70. Mobile Responsiveness

The application should support:

- Desktop.
- Tablet.
- Mobile browser.

The desktop experience should be optimized for shared inbox management.

A dedicated mobile application can be considered later.

---

# 71. Internationalization

The initial version may support English.

User-facing strings should be externalized so additional languages can be added later.

---

# 72. Accessibility

The UI should provide:

- Keyboard navigation.
- Appropriate contrast.
- Accessible labels.
- Accessible controls.
- Screen-reader-friendly structure.
- Status indicators that do not rely only on color.

---

# 73. Analytics

Initial analytics should remain simple.

Metrics:

```text
Total Conversations
Open Conversations
Pending Conversations
Closed Conversations
Unread Conversations
Messages Received
Messages Sent
```

Future analytics:

```text
Average Response Time
Average Resolution Time
Messages per Channel
Messages per Staff Member
Customer Satisfaction
```

---

# 74. Billing

Billing is not part of the core messaging functionality.

Future subscription models may consider:

```text
Number of users
Number of connected channels
Message volume
Storage usage
```

Billing should remain decoupled from the messaging pipeline.

---

# 75. Data Retention

The system should define retention policies for:

- Messages.
- Attachments.
- Webhook events.
- Audit logs.
- Deleted users.
- Deleted tenants.

Retention should consider legal requirements and third-party platform policies.

---

# 76. Backup and Recovery

The system must provide:

- Automated database backups.
- Backup encryption.
- Backup monitoring.
- Recovery procedures.
- Recovery testing.

Infrastructure planning should define:

```text
RPO
RTO
```

---

# 77. Security Threats

The platform should protect against:

- Cross-tenant data access.
- Broken authorization.
- Token leakage.
- Webhook spoofing.
- Replay attacks.
- Duplicate messages.
- API abuse.
- Malicious uploads.
- SQL injection.
- XSS.
- CSRF where applicable.
- Credential theft.

---

# 78. Webhook Security

Where supported, webhook signatures must be verified.

The system should validate:

```text
Signature
Timestamp
Event source
Webhook configuration
```

Replay protection should be implemented where appropriate.

Invalid webhook requests must be rejected.

---

# 79. API Security

Every API request must enforce:

```text
Authentication
Authorization
Tenant access
Input validation
Rate limiting
```

For example:

```text
User from Tenant A
       ↓
Requests Tenant B conversation
       ↓
Access denied
```

---

# 80. Observability

The system should provide:

- Structured application logs.
- Error tracking.
- Metrics.
- Distributed tracing.
- Queue monitoring.
- Webhook monitoring.
- External API monitoring.

Important metrics:

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

---

# 81. Dead-Letter Handling

Repeatedly failed events should move to a dead-letter queue.

```text
Webhook
   ↓
Processing
   ↓
Failure
   ↓
Retry
   ↓
Retry
   ↓
Retry
   ↓
Dead Letter Queue
```

Operators should be able to inspect and reprocess failed events where appropriate.

---

# 82. Error Handling

### Temporary errors

Examples:

- Provider timeout.
- Network failure.
- Rate limit.
- Temporary database failure.

These should normally be retried.

### Permanent errors

Examples:

- Invalid credentials.
- Revoked permission.
- Invalid recipient.
- Unsupported message type.

These should be reported and should not retry indefinitely.

---

# 83. MVP Scope

The MVP should focus on the following.

## Authentication

- Registration.
- Login.
- Tenant creation.
- User management.
- Roles.

## Shared Inbox

- Conversation list.
- Conversation view.
- Search.
- Filters.
- Unread state.
- Open/Pending/Closed.

## Messaging

- Receive messages.
- Send messages.
- Message history.
- Staff identity.
- Message status where supported.

## Collaboration

- Internal notes.
- Real-time updates.
- Shared visibility.

## Channels

At least one channel should be implemented end-to-end before implementing all channels.

Recommended progression:

```text
First Channel
     ↓
Stable Messaging Pipeline
     ↓
Second Channel
     ↓
Third Channel
     ↓
Fourth Channel
```

## Reliability

- Webhook persistence.
- Idempotency.
- Retry.
- Dead-letter handling.
- Outbound failure handling.

---

# 84. MVP Definition of Done

The MVP is complete when:

### Authentication

- Users can log in.
- Users belong to a tenant.
- Roles work correctly.

### Shared Inbox

- All authorized users can see all tenant conversations.
- No conversation assignment exists.
- Search works.
- Filters work.
- Unread state works.

### Messaging

- Customer messages appear in the inbox.
- Agents can respond.
- Responses reach the customer.
- Staff sender identity is recorded.
- Message status works where supported.

### Collaboration

- Internal notes work.
- All authorized users can see internal notes.
- Staff messages appear in real time.

### Reliability

- Webhook events are persisted.
- Duplicate webhook events do not create duplicate messages.
- Failed processing is retried.
- Dead-letter handling exists.
- Outbound failures are handled.

### Channels

- At least one channel works completely end-to-end.
- Channel connection status is visible.
- Channel disconnection is handled.

### Security

- Tenant isolation works.
- Role authorization works.
- Credentials are securely stored.
- HTTPS is used.
- Sensitive data is not logged.

---

# 85. Acceptance Criteria

## Incoming Message

**Given** a customer sends a message,

**When** the platform receives the webhook,

**Then:**

1. The webhook is validated.
2. The event is persisted.
3. The event is queued.
4. The message is normalized.
5. The tenant is identified.
6. The correct conversation is identified or created.
7. The message is persisted.
8. Unread state is updated.
9. All authorized connected users receive a real-time update.

---

## Duplicate Message

**Given** the same external message is received multiple times,

**Then** only one internal message is created.

---

## Staff Reply

**Given** Kusal sends a reply,

**Then:**

1. The message is created.
2. The message is sent through the correct channel.
3. The customer receives the message.
4. The message records Kusal as the sender.
5. Other authorized users see the message in real time.

---

## Internal Note

**Given** Sita creates an internal note,

**Then:**

- Sita sees the note.
- Other authorized business users see the note.
- The customer does not see the note.
- The note records Sita as its creator.

---

## Conversation Status

**Given** an authorized user changes a conversation from `Open` to `Closed`,

**Then:**

- The conversation becomes `Closed`.
- Other connected business users see the status change.
- The action is recorded in the audit trail.

---

## Closed Conversation Reopened

**Given** a conversation is closed,

**When** the customer sends a new message,

**Then:**

- The message is added to the conversation.
- The conversation becomes `Open`.
- All authorized users receive the update.

---

## Tenant Isolation

**Given** a user belongs to Tenant A,

**When** the user attempts to access Tenant B's conversation,

**Then** access must be denied.

---

# 86. Recommended Technology Stack

## Frontend

```text
React
TypeScript
Vite
Tailwind CSS
TanStack Query
SignalR Client
```

## Backend

```text
.NET 10
ASP.NET Core
Entity Framework Core
Clean Architecture
Domain-Driven Design
```

## Database

```text
PostgreSQL
```

## Cache

```text
Redis
```

## Messaging

For the MVP:

```text
RabbitMQ
```

Kafka can be introduced later if the platform requires high-volume event streaming, analytics, or event-driven integrations.

## Real-Time

```text
ASP.NET Core SignalR
```

## Infrastructure

```text
Docker
Docker Compose
Nginx
Object Storage
Managed PostgreSQL
Redis
```

---

# 87. Recommended Backend Structure

```text
src/
│
├── Domain/
│   ├── Entities/
│   │   ├── Tenant.cs
│   │   ├── User.cs
│   │   ├── Channel.cs
│   │   ├── Contact.cs
│   │   ├── Conversation.cs
│   │   ├── Message.cs
│   │   ├── InternalNote.cs
│   │   └── CannedResponse.cs
│   │
│   ├── Enums/
│   ├── ValueObjects/
│   ├── Events/
│   └── Interfaces/
│
├── Application/
│   ├── Conversations/
│   ├── Messages/
│   ├── Contacts/
│   ├── Channels/
│   ├── Users/
│   ├── CannedResponses/
│   └── Notifications/
│
├── Infrastructure/
│   ├── Persistence/
│   ├── Messaging/
│   ├── Channels/
│   │   ├── Meta/
│   │   ├── Instagram/
│   │   ├── WhatsApp/
│   │   └── TikTok/
│   ├── Identity/
│   ├── Storage/
│   └── Notifications/
│
└── Api/
    ├── Controllers/
    ├── Hubs/
    ├── Middleware/
    └── Webhooks/
```

---

# 88. Development Phases

## Phase 1 — Foundation

- Solution setup.
- Authentication.
- Tenant management.
- User management.
- Role management.
- PostgreSQL.
- Redis.
- Docker.
- React application.

---

## Phase 2 — Shared Inbox

- Contacts.
- Conversations.
- Messages.
- Conversation status.
- Search.
- Filters.
- Unread tracking.
- Internal notes.

No assignment functionality should be implemented.

---

## Phase 3 — Real-Time

- SignalR.
- New message events.
- Message status events.
- Conversation events.
- Internal note events.
- Shared inbox synchronization.

---

## Phase 4 — First Channel

Implement one channel completely:

```text
Connect
 ↓
Webhook
 ↓
Validate
 ↓
Persist Event
 ↓
Queue
 ↓
Normalize
 ↓
Store
 ↓
Inbox
 ↓
Reply
 ↓
Delivery Status
```

---

## Phase 5 — Additional Channels

Implement:

```text
Instagram
WhatsApp
TikTok
```

using the common channel abstraction.

---

## Phase 6 — Reliability

Implement:

- Idempotency.
- Retry policies.
- Dead-letter queue.
- Outbound retry.
- Webhook replay.
- Channel health.
- Monitoring.
- Alerting.

---

## Phase 7 — Productivity

Implement:

- Canned responses.
- Attachments.
- Browser notifications.
- Better search.
- Advanced filters.
- Typing indicators.
- Concurrent reply awareness.

---

## Phase 8 — SaaS

Implement:

- Subscription.
- Billing.
- Usage limits.
- Workspace settings.
- Analytics.
- Audit logs.
- Data retention controls.

---

# 89. Future Roadmap

## AI

Potential future capabilities:

- AI suggested replies.
- Conversation summaries.
- Intent detection.
- FAQ suggestions.
- Sentiment analysis.

AI should initially be **assistive**, not automatically send customer messages without user approval.

---

## Productivity

Potential features:

- Automation.
- Routing.
- Business rules.
- Advanced notifications.
- Agent presence.
- Typing indicators.

---

## CRM

Potential future features:

- Customer tags.
- Custom fields.
- Customer segmentation.
- Customer lifecycle.
- Customer history across channels.

---

## Analytics

Potential future metrics:

- Average response time.
- Resolution time.
- Channel performance.
- Messages per employee.
- Customer satisfaction.
- Conversation volume.

---

## Additional Channels

Potential future integrations:

- Telegram.
- Viber.
- LINE.
- Email.
- Website live chat.

---

# 90. Product Principles

## 1. Shared Inbox First

The product is built around a shared team inbox.

## 2. Messaging First

The product is a messaging platform, not an e-commerce system.

## 3. Reliability First

Messages must not silently disappear or be duplicated.

## 4. Provider Agnostic

The core domain should not depend on provider-specific payloads.

## 5. Tenant Isolation

Every business's data must remain isolated.

## 6. Simple UX

A small business should be able to understand and use the product without training.

## 7. No Unnecessary Complexity

Features such as conversation assignment should only be introduced if they solve a real customer problem.

---

# 91. Final User Experience

The intended experience is:

```text
Business Owner
      │
      ▼
Create Workspace
      │
      ▼
Connect WhatsApp
      │
      ▼
Invite Staff
      │
      ▼
Customer sends message
      │
      ▼
Webhook received
      │
      ▼
Message processed
      │
      ▼
Shared Inbox
      │
      ├───────────────┐
      ▼               ▼
   Kusal             Sita
      │               │
      └───────┬───────┘
              ▼
       Same Conversation
              │
              ▼
         Staff Reply
              │
              ▼
          Customer
```

No employee needs to claim or own a conversation.

Everyone authorized in the business can work from the same inbox.

---

# 92. Success Metrics

## Reliability

- Webhook success rate.
- Duplicate message rate.
- Message processing failure rate.
- Outbound failure rate.

## Performance

- Webhook-to-inbox latency.
- Outbound processing latency.
- API latency.
- Real-time event latency.

## Usage

- Active businesses.
- Active users.
- Connected channels.
- Messages received.
- Messages sent.
- Conversations handled.

## Product Effectiveness

- Unread conversations.
- Average response time.
- Closed conversations.
- Reopened conversations.
- Messages successfully delivered.

---

# 93. Product Success Definition

The platform succeeds when a small business can connect its messaging channels and allow its entire staff to manage customer conversations from one shared interface.

The essential experience is:

> **Customer sends a message.**

> **The message appears in the shared inbox.**

> **Every authorized staff member can see it.**

> **Any staff member can respond.**

> **Everyone sees the response in real time.**

> **Internal notes remain private to staff.**

> **Messages are not duplicated or silently lost.**

> **Each business's data remains completely isolated.**

The product should remain intentionally simple: **one business, one shared inbox, multiple channels, multiple staff members, one conversation history.**