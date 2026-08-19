# LineBot_Jam — LINE 任務提醒 Bot

使用者可以直接用口語化的方式跟 LINE Bot 說要新增什麼待辦事項(例如「明天下午6點提醒我倒垃圾」),由 Gemini API(function calling)判斷意圖、解析事項與到期時間,資訊不足時會主動反問,確認後才寫入資料庫;到期前主動分階段推播提醒。非新增任務相關的訊息則由 Gemini 自由對話回覆(帶入使用者目前的待辦事項作為 context)。

## 技術棧

- 後端框架:C# + ASP.NET Core Web API(.NET 8),含 `BackgroundService` 做定時排程
- 資料庫:PostgreSQL(原本用 SQL Server Express,因應部署到 Render 改為 PostgreSQL)
- ORM:Entity Framework Core + Npgsql,**Database First**(`dotnet ef dbcontext scaffold`)
- AI:Gemini API(`generateContent`,function calling + `google_search` grounding 同時提供,Gemini 3 系列模型支援兩者混用),用於新增任務判斷與一般對話回覆
- LINE 串接:直接以 `HttpClient` 呼叫 LINE Messaging API,webhook 簽章以 HMAC-SHA256 驗證(未使用官方 SDK)

## 訊息格式

**主要方式:口語化,不需要固定格式**,例如:

```
明天下午6點提醒我倒垃圾
提醒我開會          → Bot 會反問「請問是什麼時候?」
```

Gemini 判斷使用者意圖後,行為分三種:

1. **資訊完整** → 回覆確認問句(「要幫你新增:『倒垃圾』,到期時間 8/20 18:00,確定嗎?」),使用者回「確定」才會真的寫入 `TASKS`,回「取消」則不寫入。純問答式聊天完全不會留下任何暫存狀態。
2. **資訊不足**(例如沒講時間)→ 反問,並記住對話上下文(10 分鐘內有效),下一句話會自動跟前面的內容合併判斷。
3. **不是新增意圖**(單純聊天、詢問待辦清單等)→ 直接文字回覆,不留記憶。

還是支援舊的**固定格式快速路徑**,做為 Gemini API 打不通時的備援:

```
新增 {事項} {M/d} {HH:mm}
```

範例:`新增 倒垃圾 8/20 18:00`(符合此格式時會直接寫入,不會經過確認步驟)。

## 系統流程

**訊息處理流程**

```
使用者傳送 LINE 訊息
        │
        ▼
 有等待確認的提議,且回覆是「確定/取消」精確字眼?
        │
   ┌────┴────┐
   ▼是        ▼否(或沒有等待確認的提議)
直接處理    呼叫 Gemini
(零延遲)   (帶 create_task / ask_clarification / confirm_task / cancel_task 四個工具)
   │             │
   │      ┌──────┼──────┬──────────┐
   │      ▼      ▼      ▼          ▼
   │  create_task ask_clarification confirm_task/cancel_task  純文字
   │      │           │              │                    (聊天,不留記憶)
   │      ▼           ▼              ▼
   │  存為暫存提議   存對話上下文    真正寫入/清空 TASKS
   │  回覆確認問句   回覆追問
   └──────┴───────────────────────────┘
                    │
                    ▼
          回覆使用者(LINE Reply API)

  ※ Gemini API 失敗時:先試固定格式(新增 事項 M/d HH:mm)搶救,
    還是不行則回覆「前往 ChatGPT」的連結按鈕
```

> 「確定/取消」精確字眼命中時直接本地處理,不等 Gemini;其他說法(「可以」「先不要好了」等)一樣能正確送出 `confirm_task`/`cancel_task`,只是要多等一次 API 呼叫。

**到期提醒推播流程**

```
排程器(BackgroundService,每小時執行)
        │
        ▼
查詢到期任務(依優先順序排序,比對 REMINDER_LOGS 避免重複通知)
        │
        ▼
   推播 LINE 訊息(Push API)
```

提醒分 4 個階段獨立觸發,各階段只會推播一次:

| 階段 | 觸發時機 |
|---|---|
| `3d_before` | 到期前 3 天 |
| `1d_before` | 到期前 1 天 |
| `3h_before` | 到期前 3 小時 |
| `due` | 到期當下 |

## 資料庫結構

```sql
"USERS"
├─ "Id"                INT GENERATED ALWAYS AS IDENTITY PK
├─ "LineUserId"        VARCHAR(50)   UNIQUE      -- LINE 使用者識別碼
├─ "DisplayName"       VARCHAR(100)  NULL
├─ "CreatedAt"         TIMESTAMP     DEFAULT NOW()
├─ "PendingContent"    VARCHAR(200)   NULL   -- AI 對話式新增的暫存狀態(等待確認/補充用)
├─ "PendingDueAt"      TIMESTAMP      NULL
├─ "PendingRawInput"   VARCHAR(1000)  NULL
└─ "PendingUpdatedAt"  TIMESTAMP      NULL   -- 超過 10 分鐘視為過期

"TASKS"
├─ "Id"               INT GENERATED ALWAYS AS IDENTITY PK
├─ "UserId"           INT FK → "USERS"."Id"
├─ "Content"          VARCHAR(200)           -- 事項內容
├─ "DueAt"            TIMESTAMP              -- 到期時間
├─ "PriorityScore"    INT NULL               -- 依 DueAt 計算(與建立時間無關,永遠可正確排序)
├─ "ComplexityScore"  INT NULL               -- TODO:複雜度演算法未定
├─ "Status"           VARCHAR(20)  DEFAULT 'pending'   -- pending / done / archived
├─ "CreatedAt" / "UpdatedAt"  TIMESTAMP  DEFAULT NOW()
└─ ...

"REMINDER_LOGS"
├─ "Id"            INT GENERATED ALWAYS AS IDENTITY PK
├─ "TaskId"        INT FK → "TASKS"."Id"
├─ "ReminderType"  VARCHAR(20)   -- 3d_before / 1d_before / 3h_before / due
├─ "SentAt"        TIMESTAMP DEFAULT NOW()
├─ "Channel"       VARCHAR(20) NULL   -- 目前僅 LINE
└─ UNIQUE ("TaskId", "ReminderType")  -- 同一任務同一階段只會提醒一次
```

建表腳本見 [`CreateTable.sql`](CreateTable.sql)。所有識別字都用雙引號保留原本的大小寫命名(PostgreSQL 預設會把未加引號的識別字折成小寫)。

## 開發階段

- [x] ① DB 設計與建表
- [x] ② Webhook 基礎串接(接收 LINE POST、HMAC-SHA256 驗證簽章、Reply API 回話)
- [x] ③ 訊息解析邏輯(判斷新增格式,解析事項與到期時間寫入 TASKS)
- [x] ④ 優先順序計算(依 DueAt 算分數寫入 PriorityScore)
- [x] ⑤ 排程與提醒推播(BackgroundService 定時掃描,四階段提醒,比對 REMINDER_LOGS 避免重複通知)
- [x] ⑥ Gemini 分支整合(查詢 TASKS 作為 context 丟給 Gemini;並用 function calling 升級成口語化新增任務,支援反問補充資訊、確認後才寫入)
- [x] 資料庫由 SQL Server 遷移到 PostgreSQL(為了部署到 Render——Render 不提供代管 SQL Server)
- [x] ⑦ Render 部署準備(Dockerfile、render.yaml Blueprint、程式碼配合 PORT/連線字串環境變數)
- [x] Render 實際上線(修過 ASP.NET Core 在容器裡因 inotify 資源不足導致 `CreateBuilder()` 崩潰的問題,見下方部署章節)
- [x] Gemini 加 `google_search` grounding 工具,與既有 4 個 function calling 工具同時提供(Gemini 3 系列支援混用,不需要分兩次呼叫)——程式碼已完成,今日 API 免費額度用完,待下次額度重置後端到端測試

### 待決定事項

- `ComplexityScore` 判斷邏輯尚未實作(AI 判斷 / 關鍵字權重表 / 使用者手動輸入 1–5 分,三者擇一)
- 優先順序最終公式:`DueAt` 與 `ComplexityScore` 兩個變數要如何結合(加權分數 or 分層判斷),目前 `PriorityScore` 僅依 `DueAt` 計算

## 本機開發設置

### 需求

- .NET 8 SDK
- PostgreSQL(本機測試用,或直接接遠端的 Postgres 實例)
- [`dotnet-ef`](https://learn.microsoft.com/ef/core/cli/dotnet) 全域工具:`dotnet tool install --global dotnet-ef`

### 建立資料庫

```bash
psql -U postgres -h localhost -p 5432 -c "CREATE DATABASE \"LinebotJam\";"
psql -U postgres -h localhost -p 5432 -d LinebotJam -f CreateTable.sql
```

### 設定密鑰與連線字串(User Secrets,不會進版控)

```bash
cd Linebot_jam/Linebot_jam
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=LinebotJam;Username=postgres;Password=<你的密碼>"
dotnet user-secrets set "Line:ChannelSecret" "<LINE Developers Console → Basic settings>"
dotnet user-secrets set "Line:ChannelAccessToken" "<LINE Developers Console → Messaging API → Issue>"
dotnet user-secrets set "Gemini:ApiKey" "<Google AI Studio → Create API key>"
```

### 執行

```bash
dotnet run
```

### 本機測試 Webhook

本機開發沒有公開網址,LINE 平台無法直接打到 `localhost`,需要用 [ngrok](https://ngrok.com/) 之類的工具把本機服務曝露出去:

```bash
ngrok http 5240
```

把 ngrok 給的網址(例如 `https://xxxx.ngrok-free.app`)+ `/api/line/webhook` 路徑,貼到 LINE Developers Console 的 Webhook URL 設定裡,並開啟「Use webhook」。

> 免費版 ngrok 每次啟動網址都會換新的,記得每次重啟後回 LINE 後台更新一次。

## 部署到 Render

專案根目錄的 [`render.yaml`](render.yaml) 是 Render 的 Blueprint 設定檔,定義了一個 Web Service(用 [`Dockerfile`](Linebot_jam/Linebot_jam/Dockerfile) build)加一個免費的 PostgreSQL 資料庫。步驟:

1. 到 [Render Dashboard](https://dashboard.render.com/) → **New** → **Blueprint**,選這個 GitHub repo,Render 會自動讀 `render.yaml` 並列出要建立的兩個服務(`linebot-jam` Web Service + `linebot-jam-db` 資料庫)
2. 部署前,Render 會要求填幾個標記 `sync: false` 的環境變數(密鑰不寫在 `render.yaml` 裡):
   - `Line__ChannelSecret`
   - `Line__ChannelAccessToken`
   - `Gemini__ApiKey`
3. 資料庫連線資訊(host/port/user/password)已經在 `render.yaml` 裡設定成自動從 `linebot-jam-db` 帶入,不用手動填
4. **第一次部署後,資料庫是空的**,需要手動連上去跑一次 `CreateTable.sql` 建表:到 Render 的 `linebot-jam-db` 頁面複製「External Connection String」,在本機執行:
   ```bash
   psql "<External Connection String>" -f CreateTable.sql
   ```
5. 部署完成後,Render 會給一個固定網址(例如 `https://linebot-jam.onrender.com`),**注意 LINE 後台 Webhook URL 要填的是完整路徑 `https://<你的網址>/api/line/webhook`,不是網址本身**——只填網址本身會導致 Verify 時打到根目錄,回應對不上而逾時。這個網址固定不會變,不用再像 ngrok 一樣每次重貼。

> Render 免費方案的 Web Service 在沒有流量時會休眠,收到請求時才會喚醒(第一次回應可能延遲數秒到十幾秒);免費 PostgreSQL 有 90 天效期限制,到期前 Render 會提醒你要不要升級付費方案保留資料。
>
> **已知問題**:ASP.NET Core 預設會用 `FileSystemWatcher` 監控 `appsettings.json` 做設定熱重載,這在 Render 容器裡可能會因為 inotify 資源不足直接讓 `WebApplication.CreateBuilder()` 掛掉(服務整個連不上,不是單純冷啟動慢)。`Dockerfile` 裡已經設定 `DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false` 關掉這個監控,如果自己另外調整 Dockerfile 要注意別把這行拿掉。

## 設定項目一覽

| 設定 Key | 說明 |
|---|---|
| `ConnectionStrings:DefaultConnection` | PostgreSQL 連線字串(含帳密,只放 User Secrets,不進版控)。本機用這個;Render 上改用下面的 `DB_*` 環境變數自動組裝,不需要另外設定 |
| `DB_HOST` / `DB_PORT` / `DB_NAME` / `DB_USER` / `DB_PASSWORD` | Render 專用,`render.yaml` 已設定成自動從 `linebot-jam-db` 帶入,不用手動填 |
| `Line:ChannelSecret` | LINE Webhook 簽章驗證用 |
| `Line:ChannelAccessToken` | LINE Messaging API 呼叫用 |
| `Gemini:ApiKey` | Gemini API 金鑰 |
| `Gemini:Model` | 使用的 Gemini 模型(預設 `gemini-3.7-flash`;若遇到官方回報的暫時性過載 503,可先切換成 `gemini-3.6-flash` 等其他型號) |
| `Reminder:IntervalMinutes` | 提醒排程掃描間隔,預設 60 分鐘 |
