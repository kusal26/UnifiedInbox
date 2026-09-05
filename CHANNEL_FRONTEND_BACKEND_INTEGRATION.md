Social Channel Integration Guide: Frontend–Backend

> **Scope note (WhatsApp production):** this document is future-channel
> reference only. It describes the shared adapter vision for Messenger,
> Instagram, WhatsApp, and TikTok. The current production release covers
> **WhatsApp only**; Messenger/Instagram/TikTok sections are non-normative
> until their adapters land. WhatsApp behavior is normatively defined by the
> API, `docs/COMPLETION_PLAN.md`, and `docs/runbooks/production-checklist.md`.

Product: Unified Multi-Channel Inbox SaaS
Channels: Facebook Messenger, Instagram Direct, WhatsApp Business (Meta), TikTok Direct Messages (TikTok Business Messaging API)
Frontend target: React, TypeScript, Vite, TanStack Query, SignalR client
Backend target: .NET 10, ASP.NET Core, PostgreSQL, Redis, RabbitMQ, SignalR
Verified against provider-owned references: September 3, 2026 (re-checked same day — added explicit WhatsApp Embedded Signup v4 pin, v2 deprecates October 15, 2026; added TikTok Business Messaging API as a fourth channel)

This guide covers two provider families with different ownership, approval, and auth models:

• Meta (Messenger, Instagram, WhatsApp) — one Meta Business app, Graph API family, App Review/Advanced Access. Covered in §4–§7 and §9–§11.
• TikTok (Direct Messages) — a separate TikTok Developer app, TikTok's own OAuth 2.0 and Business Messaging API, with its own gated approval process. Covered in §5.4, §6.4, and §20.

Everything else in this guide — the adapter interface, persistence model, credential encryption, webhook processing pipeline, and rollout process — is shared across every provider, which is the reason to keep adding channels behind the same contracts rather than growing parallel, provider-specific pipelines.

1. Purpose

This guide describes how to replace the simulations in preview.html with production integrations. It covers:

• Meta and TikTok application and approval requirements.
• Multi-tenant channel authorization.
• Facebook, Instagram, WhatsApp, and TikTok onboarding differences.
• Webhook verification, routing, persistence, and processing.
• The API contract between the React frontend and ASP.NET Core backend.
• Outbound messaging and real-time delivery updates.
• Credential storage, tenant isolation, error handling, and rollout.

The central security rule is:

The frontend starts authorization and displays state. The backend owns credentials, calls provider APIs, verifies webhooks, identifies the tenant, and sends messages.

The browser must never receive the Meta App Secret, the TikTok App Secret, system-user tokens, Page tokens, Instagram tokens, TikTok access/refresh tokens, or stored WhatsApp credentials.

2. Confirmed channel capabilities

All four channels can send and receive business messages through official provider APIs — three through Meta's Graph API family, one through TikTok's Business Messaging API.


Channel
Account connected by the tenant
Receive
Send
Provider onboarding

Facebook Messenger
Published Facebook Page
Page webhooks
Messenger Send API
Facebook Login for Business

Instagram Direct
Instagram Business or Creator account
Instagram webhooks
Instagram Send API
Business Login for Instagram

WhatsApp Business
WABA and registered business phone number
WhatsApp messages webhooks
WhatsApp Cloud API
WhatsApp Embedded Signup

TikTok Direct Messages
TikTok Business Account
TikTok webhook events
TikTok Business Messaging API
TikTok OAuth 2.0 (TikTok Developer app)



Limitations:

• Facebook connects Page Messenger, not a personal Facebook inbox.
• Instagram connects Professional accounts, not personal accounts.
• Instagram recipients normally must initiate the conversation before the business can reply.
• Messenger recipients must initiate or otherwise qualify under Meta's messaging-window rules.
• WhatsApp free-form replies are allowed during the customer-service window. Outside it, use an approved template.
• TikTok connects Business Accounts only, not personal accounts, and conversations are user-initiated: a TikTok user must message the business first.
• TikTok Business Messaging API access is gated behind a separate application/approval step in the TikTok Developer Portal, on top of normal app creation — budget lead time for this the same way you would for WhatsApp's Tech Provider verification.
• TikTok Business Messaging API availability is region-restricted by the business account's registration region. Confirm the current excluded-region list against the TikTok Developer Portal before onboarding a tenant, since providers have changed this list over time and sources disagree on the exact current set.
• Provider capabilities are not identical. Keep provider differences inside backend channel adapters.

3. SaaS ownership model

Create two Meta apps owned by the SaaS company, and a separate TikTok Developer app owned by the SaaS company:

• One development/staging Meta app, one production Meta app.
• One development/staging TikTok Developer app, one production TikTok Developer app.

TikTok's app is a distinct credential set (its own App ID/App Secret, its own OAuth, its own webhook signing key) — it is not part of the Meta app and does not share App Review or Advanced Access with it. Treat it as a peer provider integration, not an extension of the Meta one.

Do not require each SaaS tenant to create a developer app for either provider. Each tenant authorizes the SaaS-owned production app (Meta or TikTok, as applicable) to access selected business assets.

text
SaaS Meta app                          SaaS TikTok Developer app
├── Tenant A                           ├── Tenant A
│   ├── Facebook Page A                │   └── TikTok Business Account A
│   ├── Instagram Professional Account A
│   └── WhatsApp Phone Number A        ├── Tenant B
└── Tenant B                           │   └── TikTok Business Account B
    ├── Facebook Page B
    ├── Instagram Professional Account B
    └── WhatsApp Phone Number B


Use one webhook endpoint per environment per provider (one Meta webhook URL, one TikTok webhook URL). Route every event to a tenant by its external business asset ID, regardless of provider.

4. Provider application configuration

4.1 Meta application configuration

In the Meta Developer Dashboard, configure a Business app with the applicable Messenger, Instagram, WhatsApp, Facebook Login for Business, and Webhooks products/use cases.

Production configuration must include:

• Verified SaaS company Business Portfolio.
• App ID and App Secret.
• App domains.
• HTTPS OAuth redirect URIs.
• HTTPS webhook callback URL.
• Webhook verify token.
• Privacy policy URL.
• Terms URL.
• User-data deletion URL/callback.
• Live app mode.
• App Review and Advanced Access for permissions used on customer-owned assets.
• Tech Provider/access verification when required for public WhatsApp onboarding.
• A WhatsApp Embedded Signup configuration.

Recommended URLs:

text
Frontend:              https://app.example.com
API:                   https://api.example.com
Meta OAuth callback:   https://api.example.com/api/v1/integrations/meta/oauth/callback
Meta webhook:          https://api.example.com/api/v1/webhooks/meta
Post-connect frontend: https://app.example.com/settings/channels/connect/result


Pin a supported Graph API version in backend configuration. Do not use an unversioned endpoint or hard-code a version in React.

4.2 TikTok application configuration

In the TikTok Developer Portal (developers.tiktok.com), create a TikTok for Business app and apply for Business Messaging API access. This access grant is separate from, and in addition to, normal app creation — treat it as its own approval gate in the rollout timeline, similar to WhatsApp's Tech Provider step.

Production configuration must include:

• A TikTok Developer account and registered app.
• App ID (Client Key) and App Secret (Client Secret).
• Approved access to the Business Messaging API (requires a dedicated application and review; not self-serve).
• The "TikTok Accounts" permission scope enabled once approved.
• HTTPS OAuth redirect URI.
• HTTPS webhook callback URL, configured to receive TikTok webhook events.
• Privacy policy and Terms URLs.

Recommended URLs:

text
Frontend:                https://app.example.com
API:                      https://api.example.com
TikTok OAuth callback:    https://api.example.com/api/v1/integrations/tiktok/oauth/callback
TikTok webhook:           https://api.example.com/api/v1/webhooks/tiktok
Post-connect frontend:    https://app.example.com/settings/channels/connect/result


TikTok's OAuth and messaging endpoints are hosted at open.tiktokapis.com, distinct from Meta's graph.facebook.com/graph.instagram.com. Do not assume a shared host, token format, or versioning scheme with the Meta integration — TikTok uses Bearer access tokens from its own OAuth 2.0 flow, not Graph API-style tokens.

5. Permissions and subscriptions

Request only permissions the product actually uses.

5.1 Facebook Messenger

Recommended permissions for listing Pages, reading conversations, subscribing the Page, and messaging:

text
pages_show_list
pages_messaging
pages_manage_metadata
pages_read_engagement


Store:

• Facebook Page ID.
• Page name.
• Encrypted Page access token.
• Granted scopes and granular target IDs.
• Token/data-access expiry when returned.

Subscribe the selected Page:

http
POST https://graph.facebook.com/{VERSION}/{PAGE_ID}/subscribed_apps
Authorization: Bearer {PAGE_ACCESS_TOKEN}
Content-Type: application/json


json
{
  "subscribed_fields": [
    "messages",
    "messaging_postbacks",
    "message_deliveries",
    "message_reads"
  ]
}


Send a reply:

http
POST https://graph.facebook.com/{VERSION}/{PAGE_ID}/messages
Authorization: Bearer {PAGE_ACCESS_TOKEN}
Content-Type: application/json


json
{
  "recipient": { "id": "PAGE_SCOPED_CUSTOMER_ID" },
  "messaging_type": "RESPONSE",
  "message": { "text": "Hello from ABC Retail" }
}


The customer ID is Page-scoped and comes from Messenger webhooks or conversation data. Do not treat it as a global Facebook user ID.

5.2 Instagram Direct

Use Instagram API with Instagram Login and Business Login for Instagram. This route supports Business and Creator accounts and does not require a linked Facebook Page.

Permissions:

text
instagram_business_basic
instagram_business_manage_messages


Store:

• Instagram Professional Account ID.
• Username and display name.
• Encrypted Instagram user access token.
• Granted scopes.
• Token/data-access expiry.

Subscribe the selected Instagram account:

http
POST https://graph.instagram.com/{VERSION}/{IG_ACCOUNT_ID}/subscribed_apps
Authorization: Bearer {INSTAGRAM_ACCESS_TOKEN}
Content-Type: application/json


json
{
  "subscribed_fields": [
    "messages",
    "messaging_postbacks",
    "messaging_seen",
    "message_reactions"
  ]
}


Send a reply:

http
POST https://graph.instagram.com/{VERSION}/{IG_ACCOUNT_ID}/messages
Authorization: Bearer {INSTAGRAM_ACCESS_TOKEN}
Content-Type: application/json


json
{
  "recipient": { "id": "INSTAGRAM_SCOPED_CUSTOMER_ID" },
  "message": { "text": "Hello from ABC Retail" }
}


The recipient ID is an Instagram-scoped ID received from the messaging webhook. Do not substitute a username.

5.3 WhatsApp Business

Use WhatsApp Cloud API and Embedded Signup for SaaS customer onboarding.

Permissions used by the integration:

text
whatsapp_business_management
whatsapp_business_messaging
business_management


Meta's Embedded Signup release guide explicitly calls for Advanced Access to business_management and whatsapp_business_management. Request the access level required by the dashboard for every permission used by the released flow.

Pin Embedded Signup to v4. Embedded Signup has its own version cadence, separate from the Graph API version. v2 is deprecated on October 15, 2026; do not build against it. Set version: 'v4' in the extras object passed to FB.login() on the frontend (see §6.3 and §18). Re-check https://developers.facebook.com/documentation/business-messaging/whatsapp/embedded-signup/versions before implementation and before every Embedded Signup upgrade, since this cadence moves independently of the Graph API version pinned elsewhere in this guide.

Store:

• Customer Meta Business ID.
• WABA ID.
• Phone Number ID.
• Display phone number and verified display name.
• Encrypted credential reference/token.
• Granted scopes.
• Number registration and quality state.

After Embedded Signup, the backend must validate the returned code and identifiers, obtain the WABA/phone details from Meta, perform any required number-registration step, and subscribe the application to the WABA.

http
POST https://graph.facebook.com/{VERSION}/{WABA_ID}/subscribed_apps
Authorization: Bearer {AUTHORIZED_TOKEN}


The WABA subscription sends webhook events for phone numbers under that WABA to the configured callback.

Send a free-form reply during the support window:

http
POST https://graph.facebook.com/{VERSION}/{PHONE_NUMBER_ID}/messages
Authorization: Bearer {WHATSAPP_MESSAGING_TOKEN}
Content-Type: application/json


json
{
  "messaging_product": "whatsapp",
  "recipient_type": "individual",
  "to": "CUSTOMER_WA_ID",
  "type": "text",
  "text": { "body": "Hello from ABC Retail" }
}


Outside the support window, send an approved template rather than a free-form message. The backend adapter, not React, determines whether a template is required.

5.4 TikTok Direct Messages

TikTok Direct Messages go through the TikTok Business Messaging API, part of TikTok's "API for Business" suite and distinct from TikTok's Marketing/Ads API. It is a gated product: your app must be individually approved for Business Messaging API access before any of the calls below will work, even in staging.

Eligibility and preconditions:

• Only TikTok Business Accounts can be connected — personal accounts are not supported.
• The tenant's TikTok Business Account must be set to accept direct messages from everyone, or messages arriving while that setting is off will need manual acceptance inside the TikTok app before your integration can see them. Surface this requirement in the connection wizard.
• Availability is region-restricted by where the TikTok Business Account is registered. Treat the excluded-region list as something to re-check at connection time (see §5, Limitations) rather than hard-coding it, since it is not consistently documented across sources and can change.

Authentication uses TikTok's own OAuth 2.0, not Meta's:

• Authorization endpoint under tiktok.com, token endpoints under open.tiktokapis.com.
• Access tokens are short-lived (on the order of ~24 hours) and refresh tokens are longer-lived (on the order of ~30 days) — plan proactive refresh in the backend rather than relying on user re-authorization, and alert the tenant well before a refresh token would lapse from inactivity.
• Tokens are passed as Authorization: Bearer {ACCESS_TOKEN}.

Store:

• TikTok Business Account ID.
• Display name / handle.
• Encrypted access token and refresh token.
• Granted scopes.
• Access token expiry and refresh token expiry.

Send a reply (shape simplified; confirm the current request schema in TikTok's Business Messaging API reference before implementation, since the payload format is versioned independently of this guide):

http
POST https://open.tiktokapis.com/v2/business/message/send/
Authorization: Bearer {TIKTOK_ACCESS_TOKEN}
Content-Type: application/json


json
{
  "business_id": "TIKTOK_BUSINESS_ACCOUNT_ID",
  "recipient": { "user_id": "TIKTOK_SCOPED_CUSTOMER_ID" },
  "message": { "type": "text", "text": "Hello from ABC Retail" }
}


The recipient ID is TikTok-scoped and comes from an inbound webhook event, the same pattern as Messenger's PSID and Instagram's IGSID. Do not substitute a TikTok username.

TikTok conversations are user-initiated: a TikTok user must message the business before the business can reply. There is no documented free-form/template split analogous to WhatsApp's messaging window, but re-verify this against the current API reference before relying on it, since messaging-window-style restrictions are exactly the kind of provider rule that changes without much notice.

Webhook signature verification differs from Meta's X-Hub-Signature-256 pattern: TikTok signs webhook payloads and sends the signature in a TikTok-Signature header, with a timestamp embedded in the signed payload. Reject requests where the timestamp is outside a small tolerance window (a few seconds) to guard against replay, in addition to validating the signature itself — TikTok's own guidance calls out clock-skew tolerances tighter than Meta's webhook validation, so don't reuse the Meta validator's tolerance constants for the TikTok adapter.

6. End-to-end connection flows

6.1 Facebook Messenger

text
React                          ASP.NET Core                       Meta

  | POST messenger/start           |                              |
  |------------------------------->| create signed OAuth state    |
  | authorizationUrl               |                              |
  |<-------------------------------|                              |
  | redirect to Meta -------------------------------------------->|
  |                                  callback code + state <------|
  |                                  validate state               |
  |                                  exchange code                |
  |                                  load eligible Pages -------->|
  | redirect result page            |                              |
  |<---------------------------------|                              |
  | GET connection attempt/assets   |                              |
  |------------------------------->|                              |
  | choose Page                     |                              |
  | POST attempt/complete          | subscribe Page ------------>|
  |------------------------------->| encrypt Page token           |
  | active ChannelConnection       |                              |
  |<-------------------------------|                              |


Use a one-time OAuth state record. The state identifies the tenant and initiating user on the server; never trust a tenant ID returned directly by the browser.

6.2 Instagram

Use the same server-owned authorization-attempt pattern, but direct the user through Business Login for Instagram and exchange the returned code for an Instagram user token. Query the authorized professional account, show it to the tenant, then subscribe its subscribed_apps endpoint.

6.3 WhatsApp

WhatsApp Embedded Signup uses Meta's JavaScript flow in React, but credential completion remains server-side.

text
1. React requests a one-time WhatsApp connection attempt from the backend.
2. Backend returns the public Meta App ID, Embedded Signup configuration ID,
   and a signed attempt ID. It returns no App Secret or stored token.
3. React launches Meta Embedded Signup.
4. Meta returns a short-lived authorization code and session information such
   as WABA ID and Phone Number ID.
5. React sends the code, returned IDs, and attempt ID to the backend.
6. Backend validates the attempt, exchanges/validates the code, verifies that
   the returned WABA and phone belong to the authorization, registers the
   number when required, subscribes the WABA, encrypts credentials, and creates
   the ChannelConnection.
7. Backend returns the normalized connection DTO.


Treat the WABA ID and Phone Number ID from the browser as untrusted until verified with Meta.

6.4 TikTok

TikTok's connection flow follows the same server-owned OAuth state pattern as Messenger and Instagram (§6.1), not the Embedded-Signup-launched-from-React pattern used for WhatsApp — TikTok does not have a WhatsApp-style embedded JS widget for this flow.

text
React                          ASP.NET Core                       TikTok

  | POST tiktok/start              |                              |
  |------------------------------->| create signed OAuth state    |
  | authorizationUrl               |                              |
  |<-------------------------------|                              |
  | redirect to TikTok ------------------------------------------>|
  |                                  callback code + state <------|
  |                                  validate state               |
  |                                  exchange code for access +   |
  |                                  refresh token -------------->|
  |                                  fetch authorized TikTok      |
  |                                  Business Account(s) -------->|
  | redirect result page            |                              |
  |<---------------------------------|                              |
  | GET connection attempt/assets   |                              |
  |------------------------------->|                              |
  | choose Business Account         |                              |
  | POST attempt/complete          | encrypt access + refresh     |
  |------------------------------->| tokens                       |
  | active ChannelConnection       |                              |
  |<-------------------------------|                              |


Before completing the connection, verify that the authorized TikTok Business Account is registered in an eligible region and configured to accept direct messages from everyone; surface a clear, specific error rather than a generic failure if either check fails, since both are common onboarding blockers that are easy for a tenant to misdiagnose as a bug.

As with the Meta flows, never trust a tenant ID or Business Account ID supplied directly by the browser — the backend derives the tenant from the signed OAuth state and verifies the Business Account against TikTok before persisting it.

7. Frontend–backend REST contract

All routes require the SaaS user's normal application authentication except the Meta callback and webhook routes. The backend derives the tenant from the authenticated membership.

7.1 Channel list

http
GET /api/v1/channels


json
{
  "items": [
    {
      "id": "01JCHANNEL",
      "platform": "instagram",
      "displayName": "@abc.retail",
      "externalAssetLabel": "Instagram 2841 9048",
      "status": "reauthorizationRequired",
      "inboxEnabled": true,
      "healthAlertsEnabled": true,
      "lastWebhookAt": "2026-09-03T07:10:00Z",
      "lastInboundAt": "2026-09-03T07:07:00Z",
      "lastOutboundAt": "2026-09-03T07:01:00Z",
      "connectedBy": { "id": "01JUSER", "name": "Kusal Thapa" },
      "capabilities": ["text", "image", "deliveryStatus", "reactions"]
    }
  ]
}


Never return tokens, provider authorization codes, App Secrets, or credential references in this DTO.

7.2 Start Facebook, Instagram, or TikTok authorization

http
POST /api/v1/channel-connections/{platform}/authorization-attempts
Idempotency-Key: {uuid}


platform is messenger, instagram, or tiktok. All three follow the same server-owned-state, code-exchange pattern (§6.1, §6.2, §6.4); WhatsApp uses the separate Embedded Signup flow in §7.6 instead.

json
{
  "returnUrl": "/settings/channels"
}


json
{
  "attemptId": "01JATTEMPT",
  "authorizationUrl": "https://provider.example/authorization",
  "expiresAt": "2026-09-03T08:15:00Z"
}


The backend constructs the provider URL, requested scopes, redirect URI, and signed state. For tiktok, authorizationUrl points at TikTok's OAuth authorization endpoint rather than Meta's, and the redirect URI configured in the TikTok Developer Portal must match exactly.

7.3 OAuth callback

http
GET /api/v1/integrations/meta/oauth/callback?code=...&state=...
GET /api/v1/integrations/tiktok/oauth/callback?code=...&state=...


Use separate callback routes per provider (as configured in §4.1 and §4.2) even though the validation logic below is identical in shape. Do not reuse the Meta callback route for TikTok — the two providers' redirect URI allowlists are configured independently, and collapsing them into one route makes the state-validation code harder to reason about per provider.

The backend:

1. Validates state, expiry, one-time use, tenant membership, and platform.
2. Exchanges the code using server-held app credentials.
3. Loads eligible provider assets.
4. Stores temporary asset choices against the authorization attempt.
5. Marks the attempt authorized.
6. Redirects to:

text
https://app.example.com/settings/channels/connect/result?attemptId=01JATTEMPT


Do not place the provider token or authorization code in the frontend redirect.

7.4 List authorized assets

http
GET /api/v1/channel-connections/authorization-attempts/{attemptId}/assets


json
{
  "platform": "messenger",
  "items": [
    {
      "externalAssetId": "page-951420",
      "displayName": "ABC Retail",
      "description": "Published Page · Full control",
      "alreadyConnected": true
    },
    {
      "externalAssetId": "page-337124",
      "displayName": "ABC Retail Wholesale",
      "description": "Published Page · Messaging access",
      "alreadyConnected": false
    }
  ]
}


For platform: "tiktok", items lists the TikTok Business Account(s) the authorizing user can grant, with description surfacing region eligibility and the "accept DMs from everyone" setting state so the frontend can warn the tenant before they pick an ineligible or misconfigured account.

The backend may use a short-lived server-side cache for temporary tokens, but the final credential must be encrypted in persistent storage.

7.5 Complete Facebook, Instagram, or TikTok connection

http
POST /api/v1/channel-connections/authorization-attempts/{attemptId}/complete
Idempotency-Key: {uuid}


json
{
  "externalAssetId": "page-337124"
}


Return 201 Created with the normalized channel DTO. Return 409 Conflict if the provider asset is already active in another workspace. This global uniqueness rule prevents accidental cross-tenant attachment, and applies the same way whether externalAssetId is a Page ID, an Instagram Professional Account ID, or a TikTok Business Account ID.

7.6 Start and complete WhatsApp Embedded Signup

http
POST /api/v1/channel-connections/whatsapp/embedded-signup-attempts


json
{
  "attemptId": "01JWAATTEMPT",
  "metaAppId": "PUBLIC_META_APP_ID",
  "configurationId": "PUBLIC_EMBEDDED_SIGNUP_CONFIG_ID",
  "embeddedSignupVersion": "v4",
  "expiresAt": "2026-09-03T08:15:00Z"
}


Return embeddedSignupVersion from the backend rather than hard-coding it in React, so the pinned version can be rotated centrally when Meta ships the next Embedded Signup release. Pass it in the extras.version field of the FB.login() call (see §5.3).

Complete it:

http
POST /api/v1/channel-connections/whatsapp/embedded-signup-attempts/{attemptId}/complete
Idempotency-Key: {uuid}


json
{
  "authorizationCode": "SHORT_LIVED_CODE",
  "wabaId": "72104482",
  "phoneNumberId": "104829"
}


The code is sensitive and short-lived. Send it once over HTTPS, never log it, and remove it from memory after the backend exchange.

7.7 Connection operations

http
POST   /api/v1/channels/{channelId}/test
POST   /api/v1/channels/{channelId}/reauthorization-attempts
PATCH  /api/v1/channels/{channelId}/settings
DELETE /api/v1/channels/{channelId}


Settings request:

json
{
  "inboxEnabled": true,
  "healthAlertsEnabled": true
}


Disconnect must unsubscribe/revoke provider access where appropriate, clear encrypted credential material, preserve conversation history according to retention policy, and create an audit event.

7.8 Conversation and send-message contract

http
GET /api/v1/conversations?channelId={id}&status=open&cursor={cursor}
GET /api/v1/conversations/{conversationId}
GET /api/v1/conversations/{conversationId}/messages?cursor={cursor}


Send:

http
POST /api/v1/conversations/{conversationId}/messages
Idempotency-Key: {clientMessageId}
Content-Type: application/json


json
{
  "clientMessageId": "8fd44899-4213-44d7-827a-e771e214fc50",
  "type": "text",
  "text": "Yes, this item is available."
}


Recommended response:

http
202 Accepted


json
{
  "message": {
    "id": "01JMESSAGE",
    "clientMessageId": "8fd44899-4213-44d7-827a-e771e214fc50",
    "direction": "outbound",
    "status": "queued",
    "text": "Yes, this item is available.",
    "sentBy": { "id": "01JUSER", "name": "Kusal Thapa" },
    "createdAt": "2026-09-03T07:20:00Z"
  }
}


The backend resolves the channel from the conversation, enforces tenant access, checks the provider's messaging window and capabilities, persists the message, queues delivery, and returns. Provider delivery happens asynchronously.

8. Standard error contract

Use ASP.NET Core ProblemDetails with stable product error codes.

json
{
  "type": "https://api.example.com/problems/channel-authorization-expired",
  "title": "Channel authorization expired",
  "status": 409,
  "detail": "Reconnect the Instagram account before sending another reply.",
  "code": "channel_authorization_expired",
  "traceId": "00-..."
}


Frontend mappings:


Code
UI behavior

channel_authorization_expired
Mark channel as needs attention and offer Reauthorize

asset_already_connected
Return to asset selection and show which workspace owns it, without leaking private workspace details

messaging_window_closed
Offer an approved template where supported

unsupported_message_type
Keep draft and explain supported attachment types

provider_rate_limited
Keep queued state and show automatic retry

provider_recipient_unavailable
Mark message failed with a permanent-error explanation

connection_attempt_expired
Restart provider authorization



9. Webhook endpoint

Use one public endpoint:

text
GET  /api/v1/webhooks/meta
POST /api/v1/webhooks/meta


9.1 Verification handshake

For GET requests:

1. Confirm hub.mode is subscribe.
2. Compare hub.verify_token with the configured verify token using a safe comparison.
3. Return the exact hub.challenge as plain text with HTTP 200.
4. Return HTTP 403 for a mismatch.

9.2 Event signature

For POST requests:

1. Capture the raw request body before JSON deserialization.
2. Read X-Hub-Signature-256.
3. Compute HMAC-SHA256 of the raw body with the Meta App Secret.
4. Compare signatures in constant time.
5. Reject an invalid or missing signature.
6. Persist the valid webhook envelope.
7. Publish its ID to RabbitMQ.
8. Return HTTP 200 promptly.

Do not perform profile lookups, attachment downloads, or conversation updates before acknowledging Meta.

9.3 Tenant routing

Route using the provider asset in the payload:


Webhook object/channel
Routing identifier

Messenger Page
Page ID in the entry/recipient

Instagram
Instagram Professional Account ID in the entry/recipient

WhatsApp
metadata.phone_number_id; retain WABA ID on the channel connection



Lookup pattern:

text
(Provider, ExternalAssetId) -> ChannelConnection -> TenantId


Never accept a webhook-supplied tenant ID.

9.4 Idempotency

Persist every webhook before processing. Add uniqueness constraints such as:

text
UNIQUE (Provider, ExternalEventId)
UNIQUE (ChannelConnectionId, ExternalMessageId)


When Meta retries an event, acknowledge the duplicate without creating another message.

10. Real-time frontend updates

SignalR must join users to a tenant group derived from authenticated membership:

text
tenant:{tenantId}


The client never chooses an arbitrary tenant group.

Recommended events:

text
channel.connection.updated
conversation.created
conversation.updated
message.created
message.statusChanged
internalNote.created
notification.created


Example message event:

json
{
  "event": "message.statusChanged",
  "data": {
    "conversationId": "01JCONVERSATION",
    "messageId": "01JMESSAGE",
    "status": "delivered",
    "occurredAt": "2026-09-03T07:20:03Z"
  }
}


Use TanStack Query for server state. On SignalR events, update the matching cache entry or invalidate the smallest relevant query. Reconnect with exponential backoff and refetch changed conversations after reconnection.

11. Recommended frontend structure

text
src/features/channels/
├── api/
│   ├── channelApi.ts
│   ├── channelQueries.ts
│   └── channelTypes.ts
├── components/
│   ├── ChannelList.tsx
│   ├── ChannelConnectionWizard.tsx
│   ├── PlatformChooser.tsx
│   ├── AuthorizedAssetPicker.tsx
│   ├── WhatsAppEmbeddedSignup.tsx
│   └── ChannelManagerDialog.tsx
├── hooks/
│   ├── useChannelAuthorization.ts
│   └── useChannelEvents.ts
└── pages/
    └── ChannelsPage.tsx

src/features/conversations/
├── api/
├── components/
├── hooks/
└── pages/


The frontend has no Meta adapter. It consumes normalized product DTOs only.

12. Recommended backend structure

text
src/
├── Api/
│   ├── Controllers/ChannelConnectionsController.cs
│   ├── Controllers/MessagesController.cs
│   ├── Webhooks/MetaWebhookController.cs
│   ├── Webhooks/TikTokWebhookController.cs
│   └── Hubs/InboxHub.cs
├── Application/
│   ├── Channels/
│   ├── Conversations/
│   ├── Messages/
│   └── Webhooks/
├── Domain/
│   ├── Channels/
│   ├── Conversations/
│   └── Messages/
└── Infrastructure/
    ├── Channels/Meta/MetaOAuthClient.cs
    ├── Channels/Meta/MessengerChannelAdapter.cs
    ├── Channels/Meta/InstagramChannelAdapter.cs
    ├── Channels/Meta/WhatsAppChannelAdapter.cs
    ├── Channels/Meta/MetaWebhookSignatureValidator.cs
    ├── Channels/TikTok/TikTokOAuthClient.cs
    ├── Channels/TikTok/TikTokChannelAdapter.cs
    ├── Channels/TikTok/TikTokWebhookSignatureValidator.cs
    ├── Persistence/
    ├── Messaging/
    └── Security/CredentialProtector.cs


Suggested adapter boundary:

csharp
public interface IMessagingChannelAdapter
{
    ChannelPlatform Platform { get; }
    Task<SendMessageResult> SendAsync(
        ChannelConnection connection,
        OutboundMessage message,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<NormalizedChannelEvent>> ParseWebhookAsync(
        JsonDocument payload,
        CancellationToken cancellationToken);
}


TikTokChannelAdapter implements the same interface as the three Meta adapters — this is the payoff of the adapter boundary: adding a new provider is a new class behind an existing contract, not a change to MessagesController, the webhook pipeline, or the SignalR hub. Give TikTokWebhookSignatureValidator its own implementation rather than generalizing MetaWebhookSignatureValidator; the header name, hashing details, and clock-skew tolerance differ enough between X-Hub-Signature-256 and TikTok-Signature that a shared validator would need provider-specific branches anyway.

Keep OAuth/connection provisioning separate from message sending and webhook normalization.

13. Persistence model

Minimum channel credential model:

text
ChannelConnection
├── Id
├── TenantId
├── Platform
├── DisplayName
├── ExternalAssetId
├── ExternalParentAssetId       (for example WABA ID)
├── ExternalBusinessId
├── Status
├── InboxEnabled
├── HealthAlertsEnabled
├── CredentialCiphertext
├── CredentialKeyVersion
├── GrantedScopes
├── TokenExpiresAt
├── DataAccessExpiresAt
├── ConnectedByUserId
├── ConnectedAt
├── LastWebhookAt
└── DisconnectedAt


Required constraint:

text
UNIQUE (Platform, ExternalAssetId)


This enforces that the same Page, Instagram account, WhatsApp phone number, or TikTok Business Account cannot be actively attached to two tenants.

Store OAuth connection attempts separately with nonce/state hash, tenant, initiating user, platform, expiry, consumed timestamp, and a reference to temporary encrypted authorization material.

TokenExpiresAt needs proactive refresh handling for TikTok specifically: TikTok's access tokens are short-lived (on the order of hours, not the weeks/months typical of a Meta long-lived Page token) and backed by a separate, longer-lived refresh token. Schedule a background refresh job per TikTok connection well before TokenExpiresAt, and alert on a connection whose refresh token is approaching its own expiry from inactivity — by the time TokenExpiresAt alone would fire, a TikTok connection can already need full reauthorization.

14. Credential security

• Encrypt provider credentials using a managed KMS/key vault or envelope encryption.
• Keep the encryption key outside the database.
• Rotate keys and record credential key version.
• Never log access tokens, refresh tokens, authorization codes, App Secrets, webhook signatures, or full webhook payloads containing unnecessary personal data.
• Redact secrets in exception telemetry.
• Verify returned asset IDs against the provider (Meta or TikTok) before persisting them.
• Validate OAuth state and prevent replay.
• Use short-lived, single-use connection attempts.
• Require Owner or Admin role for connection changes.
• Enforce tenant membership on every application API call.
• Audit connect, reauthorize, settings changes, tests, and disconnect.
• Revoke/unsubscribe access on disconnect where supported.

15. Mapping from preview.html

Replace these prototype behaviors during React implementation:


Prototype behavior
Production replacement

connectedChannels array
GET /api/v1/channels via TanStack Query

openConnectChannelDialog()
React connection wizard

authorizePendingChannel()
Start backend OAuth attempt (Messenger, Instagram, TikTok) or WhatsApp Embedded Signup

showAssetSelection()
GET authorized assets from the backend

provisionSelectedChannel()
POST connection completion

connectChannel()
Backend returns persisted ChannelConnectionDto

testChannel()
POST channel test and await SignalR/status update

reauthorizeChannel()
Start a new provider authorization attempt for the same asset

sendMessage()
POST normalized message endpoint

simulateIncomingMessage()
Remove; incoming messages originate from verified webhooks



Keep the visual states already represented by the prototype:

• Choosing a platform.
• Reviewing access.
• Provider authorization in progress.
• Selecting a business asset.
• Provisioning webhook access.
• Connection success.
• Healthy, testing, disconnected, and reauthorization-required states.

16. Implementation order

1. Create Meta development app and configure the common webhook.
2. Implement credential protection and channel persistence.
3. Implement webhook handshake and signature validation.
4. Persist and queue webhook envelopes with idempotency.
5. Implement Facebook Messenger end-to-end first: connect, webhook, normalize, inbox, reply, status.
6. Extract stable adapter contracts from the working Messenger path.
7. Implement Instagram Login, account subscription, webhook parsing, and sending.
8. Complete SaaS/Tech Provider requirements and implement WhatsApp Embedded Signup.
9. Implement WABA subscription, number verification/registration as needed, WhatsApp normalization, sending, templates, and status updates.
10. Apply for TikTok Business Messaging API access early — this approval step has independent lead time and does not depend on the Meta work finishing first, so submit it in parallel with steps 1–9 rather than after them.
11. Once approved, implement the TikTok adapter against the now-proven IMessagingChannelAdapter contract: connect, webhook, normalize, inbox, reply, status — reusing the Messenger path's shape rather than redesigning it.
12. Add SignalR events and React query-cache synchronization for all four channels.
13. Add reauthorization, disconnect, health checks, and operator alerts, including TikTok's shorter access-token refresh cycle and region/DM-setting checks.
14. Complete Meta App Review recordings and TikTok Business Messaging API review materials using the real staging application for each.

Do not implement partial channels simultaneously. Complete and harden one end-to-end provider path at a time, then reuse the normalized contracts — this applies across the Meta/TikTok boundary as much as within Meta's three channels. The one exception worth planning for on a calendar (not in the code) is step 10: TikTok's own approval step is worth starting early precisely because it's a external dependency, not because you should start building the TikTok adapter early.

17. Acceptance tests

Tenant connection

• An Owner/Admin can start a connection; an Agent receives 403.
• OAuth state cannot be replayed or used by another authenticated user.
• A selected asset is verified by the backend before storage.
• A provider asset already connected to another tenant returns 409 without exposing that tenant's identity.
• No API response contains a provider token or secret.
• A TikTok connection attempt for a Business Account in an ineligible region is rejected with a specific, actionable error before any credential is stored.
• A TikTok connection attempt for a Business Account not configured to accept DMs from everyone surfaces that as a distinct, specific error.

Incoming messages

• Invalid webhook signatures are rejected, for both Meta's X-Hub-Signature-256 and TikTok's TikTok-Signature validators.
• A TikTok webhook with a stale timestamp outside the tolerance window is rejected even with a valid signature.
• Valid webhooks are persisted before queueing.
• Duplicate webhook delivery creates one internal message.
• The provider asset routes to the correct tenant, across all four channels.
• A new customer message reopens a closed conversation.
• Authorized tenant users receive the SignalR event.

Outbound messages

• A user cannot send through another tenant's conversation.
• Duplicate clientMessageId requests create one outbound message.
• Queued, sent, delivered, read, and failed states update from provider results/webhooks.
• Messaging-window violations return a product error instead of silently failing.
• An attempt to message a TikTok user who has not yet messaged the business first returns a product error rather than a silent failure, consistent with TikTok's user-initiated model.
• Provider credentials never reach the browser.

Disconnect and recovery

• Disconnect stops new provider synchronization and removes credential material.
• Existing conversation history remains subject to retention policy.
• Reauthorization restores webhook subscription and sending.
• Expired/revoked access changes channel status and alerts workspace administrators.
• A TikTok connection whose refresh token lapses from inactivity is flagged for reauthorization before, not after, the tenant notices messages have stopped syncing.

18. Environment configuration

Backend environment/key-vault values:

env
APP_PUBLIC_URL=https://app.example.com
API_PUBLIC_URL=https://api.example.com

META_APP_ID=...
META_APP_SECRET=...
META_GRAPH_API_VERSION=vXX.X
META_WEBHOOK_VERIFY_TOKEN=...
META_OAUTH_CALLBACK_URL=https://api.example.com/api/v1/integrations/meta/oauth/callback

META_FACEBOOK_LOGIN_CONFIG_ID=...
META_INSTAGRAM_LOGIN_CONFIG_ID=...
META_WHATSAPP_EMBEDDED_SIGNUP_CONFIG_ID=...
META_WHATSAPP_EMBEDDED_SIGNUP_VERSION=v4

TIKTOK_APP_ID=...
TIKTOK_APP_SECRET=...
TIKTOK_OAUTH_CALLBACK_URL=https://api.example.com/api/v1/integrations/tiktok/oauth/callback
TIKTOK_WEBHOOK_SIGNING_KEY=...

CREDENTIAL_ENCRYPTION_KEY_REFERENCE=...
POSTGRES_CONNECTION_STRING=...
REDIS_CONNECTION_STRING=...
RABBITMQ_CONNECTION_STRING=...


Frontend build-time values may include only public identifiers:

env
VITE_API_BASE_URL=https://api.example.com
VITE_META_APP_ID=PUBLIC_APP_ID_IF_REQUIRED_BY_EMBEDDED_SIGNUP


TikTok does not need a frontend build-time App ID equivalent to WhatsApp's Embedded Signup case, since the TikTok flow is a full-page redirect initiated from a backend-returned authorizationUrl (§7.2), not a JS SDK launched from React. Keep TIKTOK_APP_ID/TIKTOK_APP_SECRET backend-only.

Prefer returning WhatsApp's public Embedded Signup configuration from an authenticated backend start-attempt endpoint so environment-specific configuration remains centralized.

19. Official references

• Meta Messenger Platform API collection
• Meta Messenger Conversations API requirements
• Meta Instagram Send API
• Meta Instagram webhook subscription
• Meta WhatsApp Business Platform
• Meta WhatsApp Embedded Signup
• Meta WhatsApp Embedded Signup versions — v2 deprecates October 15, 2026; confirm current pin before implementation
• Meta WhatsApp Cloud API messages
• Meta WhatsApp WABA subscription
• Meta WhatsApp incoming message object
• TikTok for Developers — API for Business overview — confirm the Business Messaging API is listed as its own product distinct from the Marketing API
• TikTok Business Messaging API Education Hub — apply for access here before building against it
• TikTok for Developers — OAuth 2.0
• TikTok for Developers — Webhook signature verification

Meta and TikTok can each change dashboard names, permission/access review requirements, API versions, and product availability independently of one another. Re-check the linked references and each provider's production app dashboard before implementation, before every version upgrade, and — for TikTok specifically — before onboarding any tenant, given the region-eligibility and gated-access details that are harder to pin down from documentation alone than Meta's equivalents.