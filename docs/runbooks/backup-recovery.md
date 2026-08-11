# Backup recovery

Restore an encrypted backup into an isolated environment, apply migrations, and run the verification script. Validate tenant isolation, webhook deduplication, and outbox recovery before traffic is restored.

Initial objective: RPO <= 15 minutes and RTO <= 4 hours. Record restore evidence and escalate if either objective is missed.
