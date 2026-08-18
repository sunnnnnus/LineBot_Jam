CREATE TABLE "USERS" (
    "Id"                INT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "LineUserId"        VARCHAR(50)   NOT NULL UNIQUE,
    "DisplayName"       VARCHAR(100)  NULL,
    "CreatedAt"         TIMESTAMP     NOT NULL DEFAULT NOW(),
    -- 以下為 AI 對話式新增任務的暫存狀態(等待使用者確認/補充資訊用,不代表正式資料)
    "PendingContent"    VARCHAR(200)  NULL,
    "PendingDueAt"      TIMESTAMP     NULL,
    "PendingRawInput"   VARCHAR(1000) NULL,
    "PendingUpdatedAt"  TIMESTAMP     NULL
);

CREATE TABLE "TASKS" (
    "Id"               INT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "UserId"           INT NOT NULL,
    "Content"          VARCHAR(200) NOT NULL,
    "DueAt"            TIMESTAMP NOT NULL,
    "PriorityScore"    INT NULL,
    "ComplexityScore"  INT NULL,   -- 複雜度分數,TODO: 判斷方式未決定
    "Status"           VARCHAR(20) NOT NULL DEFAULT 'pending',
    "CreatedAt"        TIMESTAMP NOT NULL DEFAULT NOW(),
    "UpdatedAt"        TIMESTAMP NOT NULL DEFAULT NOW(),
    CONSTRAINT "FK_Tasks_Users" FOREIGN KEY ("UserId") REFERENCES "USERS"("Id")
);

CREATE TABLE "REMINDER_LOGS" (
    "Id"            INT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "TaskId"        INT NOT NULL,
    "ReminderType"  VARCHAR(20) NOT NULL,   -- 3d_before / 1d_before / 3h_before / due
    "SentAt"        TIMESTAMP NOT NULL DEFAULT NOW(),
    "Channel"       VARCHAR(20) NULL,
    CONSTRAINT "FK_ReminderLogs_Tasks" FOREIGN KEY ("TaskId") REFERENCES "TASKS"("Id"),
    CONSTRAINT "UQ_ReminderLogs_Task_Type" UNIQUE ("TaskId", "ReminderType")
);
