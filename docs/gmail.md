# 個人 Gmail → TronClass → LINE

此功能預設停用。啟用後，服務醒著時每 5 分鐘同步一次 Gmail；不改變 Render 睡眠、GitHub 喚醒排程或既有提醒時間規則。

## 使用方式

- 只分析寄件者為 `elearn@mail.fju.edu.tw` 且主旨包含「作業」或「公告」的信件。讀取 Gmail 全文，再次比對精確寄件者；不標記已讀、不刪信、不讀附件。
- Groq 負責判斷待辦、重要公告或混合內容，非重要內容可略過；沒有使用固定關鍵字判定停課或作業。
- 待辦傳送獨立 LINE 卡片：新增提醒／修改時間／略過。點「修改時間」選擇台灣時間，會重新顯示提議，仍需確認。
- 重要公告直接通知；本文有明確事件日期且已過期的公告略過，不建立任務。
- 缺少截止日或時分時不能直接新增；過期作業只提示已過期。確認時也會重新檢查期限。
- 新增後走原本 3 天、1 天、3 小時、到期的階段選擇，不回補已錯過的提前提醒。
- 同信可有多個待辦及公告，各待辦分開確認，不會覆蓋原本聊天中的待确认提議。

## 一次性 Gmail 唯讀授權

1. 在 [Google Cloud Console](https://console.cloud.google.com/) 建立／選擇專案，啟用 Gmail API。設定 OAuth 同意畫面，測試階段加入自己的 Gmail 為測試使用者。
2. 建立 **Web application** OAuth client，加入以下 authorized redirect URI（注意沒有結尾斜線）：
   `https://developers.google.com/oauthplayground`
3. 開啟 [Google OAuth Playground](https://developers.google.com/oauthplayground/)，右上設定選 **Use your own OAuth credentials**，填入自己的 Client ID 與 Client secret；Endpoints 選 Google、Access type 選 Offline。不要使用 Playground 預設 client，否則 refresh token 會在 24 小時後撤銷。
4. Step 1 只輸入 `https://www.googleapis.com/auth/gmail.readonly`，Authorize APIs，登入自己的 Gmail 並授權。Step 2 選 Exchange authorization code for tokens，取得 **Refresh token**，不是短效 Access token。
5. 不要把憑證放入 Git、對話或截圖。把以下三個值直接存入 Render Environment：`Gmail__ClientId`、`Gmail__ClientSecret`、`Gmail__RefreshToken`。

Google 外部應用若維持 Testing 狀態，含 Gmail 權限的 refresh token 通常在 7 天後到期；長期使用需要依 Google 同意畫面規範調整發佈狀態／授權設定。換密碼、撤銷授權等也可能使 token 失效，屆時需重新授權。Gmail readonly 是信箱範圍的唯讀授權；指定寄件者的篩選由本程式執行，Google 並沒有只授權單一寄件者的這個 scope。

## 綁定自己的 LINE 與啟用

先在與機器人的一對一聊天室傳送「我的識別碼」，取得實際 LINE userId。不可使用資料庫「第一筆」或自行猜 `USERS.Id = 1`；並排訊息／其他使用者可能先建檔。

| Render 環境變數 | 值 |
|---|---|
| `Gmail__Enabled` | 設好其他值後才設 `true`；未設定預設 `false` |
| `Gmail__AccountEmail` | 授權的 Gmail 完整信箱地址，與 Gmail profile 核對 |
| `Gmail__OwnerLineUserId` | 上述指令回傳的 LINE userId |
| `Gmail__ClientId` | 自己的 OAuth Client ID |
| `Gmail__ClientSecret` | 自己的 OAuth Client secret |
| `Gmail__RefreshToken` | 唯讀授權的 Refresh token |
| `Gmail__PollMinutes` | 預設 `5`，範圍 1～60 |
| `Gmail__StartAtUtc` | 通常留空；如需首次匯入舊信，可指定例如 `2026-09-21T00:00:00Z` |

沿用既有 `Groq__ApiKey` 與 `Groq__Model`，郵件分類使用 JSON mode 並驗證輸出格式。首次成功同步會記錄開始時間，預設只收之後的新信；Gmail 查詢有分頁與重疊視窗，不依賴已讀／未讀狀態。`StartAtUtc` 只影響第一次建立同步狀態，之後更改環境變數不會重設游標。

資料表由現有啟動程序自動增建（不改舊表）：`MAIL_SYNC_STATE`、`MAIL_RECEIPTS`、`MAIL_PROPOSALS`。同一信箱不能在已有狀態下靜默改綁另一位 LINE 使用者。

## 可靠性與邊界

Gmail 訊息 ID 作永久去重鍵；信件摘要、提議與 LINE outbox 在同一交易保存。AI 失敗保留同步位置，下次重試，不把失敗視為已處理。LINE push 沿用既有持久化重試鍵。不同 Gmail ID 視為不同信件，目前不推測是否為同一作業的新版本。

LINE 按鈕帶提議 ID 與版本。服務端核對實際發話者與提議擁有者、拒絕群組處理，確認和新增任務使用既有 webhook 原子交易；重複點擊不重複新增。修改後舊版本按鈕會回傳最新提議。純文字「確定」仍只處理原本聊天的提議，不會猜要確認哪封郵件。

郵件本文只在分析時傳給 Groq，不寫入新郵件資料表；資料庫保留去重識別、日期和待辦摘要。郵件內文不允許指定收件者或呼叫新增任務工具。AI 仍可能誤判，待辦一定需要使用者確認；重要公告為直接推送，不是每日定時摘要。

Render 休眠期間不會讀信；恢復後從持久化位置補掃。本版沒有將 Gmail 同步接到外部排程。尚未設定 Google 授權前，不能宣稱已成功讀到真實信件。正式啟用後應用一封新的 TronClass 信件驗證卡片與通知；資料庫自動化測試使用假 Gmail/Groq，不碰真實信箱。

## 參考

- [Gmail 搜尋與時間邊界](https://developers.google.com/workspace/gmail/api/guides/filtering)
- [Google OAuth Playground](https://developers.google.com/oauthplayground/)
- [Refresh token 有效期限](https://developers.google.com/identity/protocols/oauth2#expiration)
- [Groq JSON output](https://console.groq.com/docs/structured-outputs)
