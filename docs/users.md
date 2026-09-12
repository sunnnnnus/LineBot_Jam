# 使用者身分與資料歸屬

LINE 訊息的 `source.userId` 是外部使用者識別，寫入 `USERS.LineUserId`（NOT NULL、UNIQUE）。
`USERS.Id` 是既有整數主鍵；`TASKS.UserId` 是參照它的外鍵。保留此設計可繼續使用舊資料與待確認狀態，不需更換主鍵或搬移任務。

流程：已驗證 LINE 簽章的 webhook → `LineUserContext.ResolveAsync(evt.Source)` → 查找或建立使用者 → 用 `User.Id` 存取個人資料。

- `LineUserContext` 以 scoped 生命週期綁定單一事件的使用者，禁止切換身分。
- 缺少、空白或超過欄位長度的 userId 不建立帳號，也不呼叫 AI；groupId/roomId 不可替代個人身分。
- 首次建立採 PostgreSQL `ON CONFLICT DO NOTHING`，保留唯一性與既有個人暫存狀態。
- 查詢與修改待辦從 `LineUserContext.Tasks` 開始，再加入任務編號等條件；新增透過 `AddTask` 指定目前使用者。
- 未來偏好設定等資料表，以 `UserId` 外鍵參照 `USERS.Id`。不要採用訊息文字、AI 參數或前端自行傳入的 UserId 當身分驗證。
- 系統排程需要跨使用者掃描，仍由 ReminderProcessor 按任務擁有者分組、推送至其 LineUserId。

群組內的訊息仍識別發話者本人；目前回覆會送回原對話，因此群組成員可以看見該回覆。這不是群組共用任務功能。

LINE userId 在同一 provider 下可對應同一使用者；不同 provider 的 ID 不應視為相同身分。
參考：[LINE 官方說明](https://developers.line.biz/en/docs/messaging-api/getting-user-ids/)。
