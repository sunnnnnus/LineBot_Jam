CREATE TABLE dbo.USERS (
    Id                 INT IDENTITY(1,1) PRIMARY KEY,
    LineUserId         NVARCHAR(50)  NOT NULL UNIQUE,
    DisplayName        NVARCHAR(100) NULL,
    CreatedAt          DATETIME2     NOT NULL DEFAULT GETDATE(),
    -- 以下為 AI 對話式新增任務的暫存狀態(等待使用者確認/補充資訊用,不代表正式資料)
    PendingContent     NVARCHAR(200) NULL,
    PendingDueAt       DATETIME2     NULL,
    PendingRawInput    NVARCHAR(1000) NULL,
    PendingUpdatedAt   DATETIME2     NULL
);

CREATE TABLE dbo.TASKS (
    Id               INT IDENTITY(1,1) PRIMARY KEY,
    UserId           INT NOT NULL,
    Content          NVARCHAR(200) NOT NULL,
    DueAt            DATETIME2 NOT NULL,
    PriorityScore    INT NULL,
    ComplexityScore  INT NULL,   -- ���d��,TODO: �����׺t��k�M�w��A��
    Status           NVARCHAR(20) NOT NULL DEFAULT 'pending',
    CreatedAt        DATETIME2 NOT NULL DEFAULT GETDATE(),
    UpdatedAt        DATETIME2 NOT NULL DEFAULT GETDATE(),
    CONSTRAINT FK_Tasks_Users FOREIGN KEY (UserId) REFERENCES dbo.USERS(Id)
);

CREATE TABLE dbo.REMINDER_LOGS (
    Id            INT IDENTITY(1,1) PRIMARY KEY,
    TaskId        INT NOT NULL,
    ReminderType  NVARCHAR(20) NOT NULL,   -- 3d_before / 1d_before / 3h_before / due
    SentAt        DATETIME2 NOT NULL DEFAULT GETDATE(),
    Channel       NVARCHAR(20) NULL,
    CONSTRAINT FK_ReminderLogs_Tasks FOREIGN KEY (TaskId) REFERENCES dbo.TASKS(Id),
    CONSTRAINT UQ_ReminderLogs_Task_Type UNIQUE (TaskId, ReminderType)
);