# Render Free：每半小時提醒

GitHub Actions 的 `Reminder scheduler` 每小時第 7、37 分鐘執行，間隔 30 分鐘，避開整點。它以 HTTPS 呼叫 `POST /api/reminder/process`，每次最長等 120 秒、最多嘗試 3 次（間隔 10、20 秒），讓 Render 有時間完成冷啟動。GitHub 排程可能延遲；提醒不是精準到分鐘，也不是硬性保證 30 分鐘內送達。

## 設定與啟用

1. 使用專用的隨機密鑰，不要使用 LINE/Groq/DB 金鑰。
2. Render → linebot-jam → Environment：設定 `Reminder__SchedulerToken` 為該密鑰。
3. GitHub → Settings → Secrets and variables → Actions：新增 secret `REMINDER_SCHEDULER_TOKEN`，值必須與 Render 相同；新增 variable `REMINDER_BASE_URL` 為 `https://linebot-jam.onrender.com`。
4. 部署本版，先在 Actions 手動執行 `Reminder scheduler`。成功訊息 `Reminder scan accepted into the durable queue` 表示觸發已持久化，不表示所有 LINE 推播已送達。
5. 手動測試成功後，把 GitHub variable `REMINDER_SCHEDULER_ENABLED` 設為 `true`，Render `Reminder__UseExternalScheduler` 設為 `true`，停用內建提醒 timer。Blueprint 管理者也應把 `render.yaml` 的相同開關改為 `true`，避免下一次同步蓋回去。

切換完成前預設保留內建 timer；它只在程式醒著時有效。要回退可將 Render 開關改回 `false`，但無法藉此喚醒睡眠服務。兩種觸發方式同時運作時仍共用資料庫去重。

若這是 public repo，GitHub 長期沒有 repository activity 時可能停用 scheduled workflows，請定期確認 Actions 狀態。排程執行失敗會在 GitHub Actions 留下失敗紀錄；通知依 GitHub 帳號設定。若需要準時／高可靠提醒，仍應使用常駐服務或專用排程平台。

## 處理與去重

- 端點使用 Bearer token，未設定 token 時回 503；驗證失敗回 401，不建立任何工作。不要把密鑰放 URL、README 或 git。
- 每輪呼叫使用相同 `X-Scheduler-Run-Id` 重試，確保跨半小時邊界仍去重。未提供時使用 UTC 半小時時間格。
- 端點只將掃描請求持久化到 `WEBHOOK_JOBS`，成功回 202。服務恢復後背景 worker 執行當下的提醒掃描，不依賴排程 HTTP 連線存活。
- 掃描把同批提醒的訊息、接收者及 retry key 保存為 outbox，並在同一個交易寫入 `REMINDER_DISPATCHES` 的 `(TaskId, ReminderType)` 唯一預約。重複掃描不會再排入同一階段。
- 只有 LINE 接受發送（或回覆已接受相同 retry key）後才寫入 `REMINDER_LOGS`，並在同一交易結束 outbox。發送後程序中斷時，使用原本的訊息及 retry key 重試。
- Push 重試不超過首次發送後 23 小時；永久失敗／超出重試次數會保留 `LastError` 及預約，避免每次排程重新洗版。需要人工確認失敗原因後再決定是否補發，不保證一定送達。
- 成功提醒 outbox 在 7 天後清除，已送出紀錄仍保留；失敗提醒 outbox 及預約保留供排查。

資料表由啟動程序自動增量建立，請確認 Render logs 有 `Durable webhook queue ready`。無 DDL 權限時由管理者先執行 `Linebot_jam/Linebot_jam/Data/WebhookQueue.sql`。

## 測試

```text
dotnet test Linebot_jam/Linebot_jam.Tests/Linebot_jam.Tests.csproj -c Release
python -m unittest discover -s scripts -p "test_*.py"
```

PostgreSQL 整合測試在 CI 執行，包含外部觸發重新啟動、合併提醒只排一次、失敗不標成已送。

參考：[Render Free](https://render.com/docs/free)、[GitHub 排程限制](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#schedule)、[LINE 重試機制](https://developers.line.biz/en/docs/messaging-api/retrying-api-request/)。
