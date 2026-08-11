# Webhook replay

Prerequisites: operator access, the original immutable payload, and the target channel ID.

Diagnosis: inspect the webhook receipt status and signature validation result. Never edit production tables directly.

Replay: resubmit the original request through the authenticated webhook replay command/API. The external message ID is the idempotency key; replaying it must not create a second message.

Success evidence: one internal message, one processing audit entry, and a completed outbox event.

Stop if the signature cannot be validated or the provider payload is unsupported; escalate to the channel owner.
