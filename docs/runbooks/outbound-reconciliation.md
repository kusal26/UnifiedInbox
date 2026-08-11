# Outbound reconciliation

Find messages in `Unknown` or `Sending` state and query the provider request ID before retrying. Do not blindly resend an ambiguous request.

Success evidence is a provider status transition and a matching internal audit/outbox event. Stop on conflicting provider results and escalate to messaging operations.
