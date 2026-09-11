-- Additive upgrade, also run automatically by WebhookBackgroundService.
CREATE TABLE IF NOT EXISTS "WEBHOOK_JOBS" (
    "EventId" TEXT PRIMARY KEY,
    "Sequence" BIGINT GENERATED ALWAYS AS IDENTITY,
    "Payload" TEXT NOT NULL,
    "ReceivedAt" TIMESTAMPTZ NOT NULL,
    "Processed" BOOLEAN NOT NULL DEFAULT FALSE,
    "ProcessingAttempts" INTEGER NOT NULL DEFAULT 0,
    "ReplyMessages" TEXT NULL,
    "Destination" TEXT NULL,
    "ReplyAttempted" BOOLEAN NOT NULL DEFAULT FALSE,
    "UsePush" BOOLEAN NOT NULL DEFAULT FALSE,
    "RetryKey" UUID NOT NULL,
    "PushStartedAt" TIMESTAMPTZ NULL,
    "DeliveryAttempts" INTEGER NOT NULL DEFAULT 0,
    "NextAttemptAt" TIMESTAMPTZ NOT NULL,
    "Finished" BOOLEAN NOT NULL DEFAULT FALSE,
    "LastError" TEXT NULL
);
CREATE INDEX IF NOT EXISTS "IX_WebhookJobs_Pending"
    ON "WEBHOOK_JOBS" ("Sequence") WHERE NOT "Processed";
CREATE INDEX IF NOT EXISTS "IX_WebhookJobs_Delivery"
    ON "WEBHOOK_JOBS" ("NextAttemptAt") WHERE "Processed" AND NOT "Finished";
