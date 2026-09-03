# Production checklist

Complete every item before pointing real WhatsApp traffic at a deployment. Keep the evidence with the release notes.

## 1. Staging test number

Connect the staging WhatsApp test number through the Embedded Signup wizard (`/channels`). Confirm the channel row shows `connected`, exactly one `ProviderRoute` exists for its `phone_number_id`, and `channel.updated` was published. Send a message from the test handset and reply from the inbox; both directions must appear in the timeline.

## 2. Embedded Signup configuration

`WhatsApp:AppId`, `WhatsApp:AppSecret`, and `WhatsApp:GraphVersion` are set from the secret store (never in the image or repository). The Meta app has `whatsapp_business_messaging` and `whatsapp_business_management` approved. Proof: a fresh connect attempt completes without `scopes_missing`, and the code exchange never logs the secret.

## 3. Webhook delivery proof

In Meta's app dashboard, subscribe the staging WABA to `messages`. Send a test message and verify: the API answers 200, a `WebhookReceipt` reaches `Processed`, exactly one inbound message exists after replaying the same payload twice, and the realtime event arrives in an open browser session. See `webhook-replay.md`.

## 4. Backup and restore rehearsal

Take a Postgres base backup plus WAL archives (`backup-recovery.md`), restore into an empty staging database, run the one-shot `migrator`, and boot the API against it. The `/ready` probe must pass and a spot-checked conversation must render. Record the measured RTO/RPO.

## 5. Fake-provider rejection proof

With `ASPNETCORE_ENVIRONMENT=Production`, startup must refuse `WHATSAPP_USE_FAKE=true` and refuse missing `WhatsApp:AppSecret`, `WhatsApp:VerifyToken`, `Credentials:MasterKey` (32-byte base64), and `Jwt:SigningKey` (32+ chars). Proof: `RejectUnsafeProductionConfiguration` throws on each case in a staging boot test.

## 6. Documented rollback

Each release tags the API, worker, and frontend images together. Roll back by redeploying the previous tag triple and re-running `migrator` (migrations are additive; destructive changes ship separately with their own rehearsal). Database restores follow `backup-recovery.md`. Never roll the database back without replaying the outbox first: see `outbound-reconciliation.md`.
