# LineBot_Jam — LINE 任務提醒 Bot

使用者可以直接用口語化的方式跟 LINE Bot 說要新增什麼待辦事項(例如「明天下午6點提醒我倒垃圾」),由 Groq API(function calling,OpenAI 相容格式)判斷意圖、解析事項與到期時間,資訊不足時會主動反問,確認後才寫入資料庫;到期前主動分階段推播提醒。非新增任務相關的訊息則由 AI 自由對話回覆(帶入使用者目前的待辦事項作為 context)。

## 技術棧

- 後端框架:C# + ASP.NET Core Web API(.NET 8),含 `BackgroundService` 做定時排程
- 資料庫:PostgreSQL(原本用 SQL Server Express,因應部署到 Render 改為 PostgreSQL)
- ORM:Entity Framework Core + Npgsql,**Database First**(`dotnet ef dbcontext scaffold`)
- AI:[Groq API](https://console.groq.com/)(`/openai/v1/chat/completions`,OpenAI 相容格式,免費額度以「每分鐘請求數」計算,比按日計算的額度對個人使用更寬裕),用於新增任務判斷與一般對話回覆。原本用 Gemini API,因免費配額用盡且新舊 key 共用同一專案配額而換過來;換過來後**沒有內建搜尋工具**,問天氣/匯率這類需要即時資訊的問題目前答不出來,是已知的功能縮減
- LINE 串接:直接以 `HttpClient` 呼叫 LINE Messaging API,webhook 簽章以 HMAC-SHA256 驗證(未使用官方 SDK)

## 訊息格式

**主要方式:口語化,不需要固定格式**,例如:

```
明天下午6點提醒我倒垃圾
提醒我開會          → Bot 會反問「請問是什麼時候?」
```

AI 判斷使用者意圖後,行為分三種:

1. **資訊完整** → 回覆確認問句(「要幫你新增:『倒垃圾』,到期時間 8/20 18:00,確定嗎?」),使用者回「確定」才會真的寫入 `TASKS`,回「取消」則不寫入。純問答式聊天完全不會留下任何暫存狀態。
2. **資訊不足**(例如沒講時間)→ 反問,並記住對話上下文(10 分鐘內有效),下一句話會自動跟前面的內容合併判斷。
3. **不是新增意圖**(單純聊天、詢問待辦清單等)→ 直接文字回覆,不留記憶。

還是支援舊的**固定格式快速路徑**,做為 AI API 打不通時的備援:

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
直接處理    呼叫 AI(Groq)
(零延遲)   (帶 create_tasks / ask_clarification / confirm_task / cancel_task 四個工具)
   │             │
   │      ┌──────┼──────┬──────────┐
   │      ▼      ▼      ▼          ▼
   │  create_tasks ask_clarification confirm_task/cancel_task  純文字
   │      │           │              │                    (聊天,不留記憶)
   │      ▼           ▼              ▼
   │  存為暫存提議   存對話上下文    真正寫入/清空 TASKS
   │  回覆確認問句   回覆追問
   └──────┴───────────────────────────┘
                    │
                    ▼
          回覆使用者(LINE Reply API)

  ※ AI API 失敗時:先試固定格式(新增 事項 M/d HH:mm)搶救,
    還是不行則保留未過期提議，回覆稍後重試及固定格式提示
```

> 「確定/取消」精確字眼命中時直接本地處理,不等 AI;其他說法(「可以」「先不要好了」等)一樣能正確送出 `confirm_task`/`cancel_task`,只是要多等一次 API 呼叫。

**到期提醒推播流程**

```
排程器(BackgroundService,每分鐘執行)
        │
        ▼
查詢到期任務(選擇當下適用階段,比對 REMINDER_LOGS 避免重複通知)
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
├─ "PendingTasksJson"  TEXT           NULL   -- 待確認的多筆提議(JSON 陣列)
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
- [x] ~~Gemini 加 `google_search` grounding 工具~~ —— 已實作但後來整個棄用(見下一項)
- [x] AI 供應商從 Gemini 換成 [Groq](https://console.groq.com/)(OpenAI 相容格式):Gemini 免費配額用盡,且同專案新申請的 key 共用同一配額池、換 key 沒用;Groq 免費層以「每分鐘請求數」計算,對個人使用更寬裕。**代價是搜尋 grounding 功能一併拿掉**(Groq 沒有內建搜尋工具),問天氣/匯率之類需要即時資訊的問題暫時答不出來

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
dotnet user-secrets set "Groq:ApiKey" "<console.groq.com → API Keys → Create API Key>"
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
   - `Groq__ApiKey`
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
| `Groq:ApiKey` | Groq API 金鑰,只放 User Secrets/Render 環境變數,不進版控 |
| `Groq:Model` | 使用的 Groq 模型(預設 `openai/gpt-oss-120b`,支援 function calling)。注意 `llama-3.3-70b-versatile` 雖然還列在文件上,但實測免費層沒有存取權限(會回 `model_not_found`),換模型前建議先用 `/openai/v1/models` 確認帳號實際可用清單 |
| `Reminder:IntervalMinutes` | 提醒排程掃描間隔,預設 1 分鐘 |

## Render 休眠與恢復（持久化 Webhook）

免費 Web Service 的冷啟動發生在程式接到請求之前，程式無法消除這段延遲；LINE 後台第一次 Verify 仍可能逾時。先開啟 `/health` 等服務醒來，再按 Verify。`/health` 只代表 HTTP 程序存活，不代表資料庫或外部 API 正常。

**部署後請到 LINE Developers Console → Messaging API → Webhook settings 開啟 Webhook redelivery。** LINE 沒有保證每個失敗事件都一定會重送；沒有抵達應用程式、且沒有重送的事件仍無法恢復。

新流程：驗證簽章 → 寫入 PostgreSQL `WEBHOOK_JOBS` → 回 HTTP 200 → 背景依序處理 AI/任務 → 發送已保存的回覆。

- 只有資料庫保存成功才回 200；資料庫或佇列尚未就緒時回 503，交由 LINE 重送。LINE 的空事件 Verify 不查資料庫。
- 以 `webhookEventId` 去重（舊事件以 `message.id` 備援）。任務變更與待回覆內容在同一個交易提交；重送及重新啟動不會重新執行已處理的事件。
- 背景工作以 PostgreSQL advisory lock 協調重疊部署。佇列適合目前個人 Bot 的低流量，依入列順序處理；AI 最長等待 20 秒，LINE API 最長 10 秒。
- Reply 明確回傳 `Invalid reply token` 時，改成 Push 到原本的使用者／群組／聊天室。**Push 會使用 LINE 訊息額度**，仍可能因額度、封鎖或權限失敗。一般 4xx 不重試、不改用 Push。
- Push 暫時失敗使用同一個持久化 `X-Line-Retry-Key` 重試，採有限次數的退避，且不超過第一次 Push 後 23 小時。Reply 逾時或程序在 Reply 中斷時，無法判斷是否已送達，會停止自動補送並留下 `LastError`，避免重複訊息；已新增的任務仍保留。
- 超過 10 分鐘的舊事件不再執行指令，回覆請使用者重新傳送，避免把舊的「明天」解讀成恢復當天的明天。AI 暫時失敗會保留尚未過期的提議，回覆可重試的訊息。
- 已結束的佇列紀錄保留 7 天，包含輸入及回覆，之後定期清除；去重保證也只涵蓋紀錄保留期間。請限制資料庫存取權限。未完成的工作保留等待恢復。

`WEBHOOK_JOBS` 是新增的獨立資料表，啟動時會自動執行內嵌的 `Linebot_jam/Linebot_jam/Data/WebhookQueue.sql`，不修改既有任務表。資料庫帳號需要建表／建索引權限；若正式環境限制 DDL，請先由管理者執行該 SQL。初始化失敗會每 5 秒重試並寫入日誌。全新資料庫仍要先執行 `CreateTable.sql` 建立業務資料表。

提醒排程改成服務運作時每分鐘掃描，醒來後只補送當下適用的一個階段，例如已到期只送「已到期」，不補發「還有 3 天／1 天／3 小時」。**服務休眠期間背景排程不會執行**；若需要準時提醒，需使用不休眠的服務或獨立常駐排程。這次不自動變更 Render 付費方案。

排查順序：先看 Render 是否出現 `Durable webhook queue ready`，再查 LINE Webhook 錯誤統計及 `WEBHOOK_JOBS.LastError`。處理嘗試上限為 3 次；回覆發送失敗不會再次新增任務。提醒推播仍沿用原本發送後記錄模式，多實例或發送後資料庫失敗的重複推播風險尚未全面改成 outbox。

參考：[Render 免費服務限制](https://render.com/docs/free)、[LINE Webhook 重送](https://developers.line.biz/en/docs/messaging-api/receiving-messages/)、[LINE API 安全重試](https://developers.line.biz/en/docs/messaging-api/retrying-api-request/)。

### 回歸測試

```bash
dotnet test Linebot_jam/Linebot_jam.Tests/Linebot_jam.Tests.csproj -c Release
```

本機未設定 `LINEBOT_TEST_POSTGRES` 時，PostgreSQL 整合測試會標記略過。請只將此變數設為可測試的資料庫連線字串；整合測試會建立及刪除自身的隨機 schema。GitHub Actions 會啟動獨立 PostgreSQL 16，執行去重、交易回滾、重新啟動、過期事件與回覆中斷測試，不使用正式資料庫或 LINE/Groq 金鑰。
