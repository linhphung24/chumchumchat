# ChumChat — Hộp thư khách hàng hợp nhất

Gom tin nhắn khách hàng từ **Zalo OA, Facebook Messenger, Shopee, TikTok Shop** về một màn hình duy nhất để trả lời, không phải mở 4 app.

## Kiến trúc

```
[Zalo OA]  [Messenger]  [Shopee]  [TikTok Shop]
    │           │           │           │
    ▼           ▼           ▼           ▼
 POST /webhooks/{kênh}  ← các nền tảng đẩy tin nhắn đến qua webhook
    │
    ▼
 Adapter từng kênh (xác minh chữ ký → chuẩn hóa về 1 schema chung)
    │
    ▼
 SQLite (hội thoại + tin nhắn)
    │
    ▼
 Inbox web (Blazor Server, realtime) — trả lời gửi ngược qua API của kênh
```

- **Backend + UI**: ASP.NET Core 10, Blazor Server (realtime sẵn có, không cần polling)
- **Database**: SQLite (file `chumchat.db`) — đổi sang SQL Server/PostgreSQL chỉ cần đổi connection string và package EF tương ứng
- **Mã nguồn chính**:
  - `src/ChumChat.Web/Channels/` — adapter 4 kênh (parse webhook, verify chữ ký, gửi tin)
  - `src/ChumChat.Web/Services/InboxService.cs` — lưu tin, chống trùng, gửi trả lời
  - `src/ChumChat.Web/Controllers/WebhooksController.cs` — endpoint nhận webhook
  - `src/ChumChat.Web/Components/Pages/Inbox.razor` — giao diện inbox

## Chạy thử ngay (chưa cần API key)

```bash
dotnet run --project src/ChumChat.Web
```

Mở http://localhost:5198 → **đăng nhập `admin` / `admin`** (tài khoản admin tạo tự động lần chạy đầu; đổi mật khẩu ngay ở nút 🔑). Vào trang **🧪 Giả lập** để bơm tin nhắn thử từ từng kênh và trả lời trong Inbox. Kênh chưa điền credentials sẽ hiện nhãn *"Kênh chưa kết nối"* và tin trả lời chỉ lưu trong app (đánh dấu *giả lập*).

**Tài khoản & phân công**: admin vào **⚙ Cấu hình** để tạo tài khoản nhân viên, thêm câu trả lời mẫu, và bật gợi ý AI. Mỗi hội thoại phân công được cho một nhân viên; nhân viên lọc **"Của tôi"** để xem việc của mình.

**Trợ lý AI**: tab riêng **✨ Trợ lý AI** (admin) — chọn nhà cung cấp (**Claude / ChatGPT / Gemini / DeepSeek**), điền API key, thông tin tiệm, **câu hỏi thường gặp**, và **ảnh tư liệu** (menu/bảng giá — Claude/ChatGPT/Gemini nhìn ảnh để trả lời; DeepSeek chỉ đọc chữ). Khung chat hiện nút **✨ Gợi ý AI** soạn câu trả lời theo toàn bộ kho kiến thức + ngữ cảnh hội thoại.

## Kết nối kênh thật — qua trang Cấu hình, không cần sửa code

Mở **`/settings`** trong app: với mỗi kênh, dán khóa app → bấm **Lưu** → bấm **Kết nối** để mở trang cấp quyền của nền tảng (đăng nhập, đồng ý là xong). Access token + refresh token tự lưu vào database và **tự gia hạn** bằng service chạy nền.

Việc duy nhất phải làm trên trang developer của từng nền tảng: tạo app, lấy khóa, và khai báo 2 URL (trang `/settings` hiển thị sẵn từng URL để copy):

| Kênh | Tạo app tại | Khóa cần dán vào /settings |
|------|-------------|----------------------------|
| Zalo OA | [developers.zalo.me](https://developers.zalo.me) (liên kết với OA) | App ID, Secret Key |
| Messenger | [developers.facebook.com](https://developers.facebook.com) (thêm Facebook Login + Messenger) | App ID, App Secret, Verify Token |
| Shopee | [open.shopee.com](https://open.shopee.com) (bật push webchat) | Partner ID, Partner Key |
| TikTok Shop | [partner.tiktokshop.com](https://partner.tiktokshop.com) | App Key, App Secret |
| Zalo cá nhân ⚠ | Không có API chính thức — chạy qua sidecar [zca-js](https://github.com/RFS-ADRENO/zca-js) trong `sidecars/zalo-personal` (đăng nhập quét QR). **Rủi ro bị Zalo khóa tài khoản** — nên dùng tài khoản phụ | API Key tự đặt (trùng giữa sidecar và app) |

Webhook và OAuth callback đòi hỏi **URL HTTPS công khai** — deploy lên VPS trước (xem [deploy/DEPLOY.md](deploy/DEPLOY.md)) rồi mới bấm Kết nối. Khi dev local có thể dùng `ngrok http 5198`.

### Lưu ý khi nối kênh thật

1. **Cấu trúc payload có thể lệch**: mapping webhook trong các adapter viết theo tài liệu công khai của từng nền tảng; khi nhận tin thật lần đầu, xem log (payload lỗi được ghi đầy đủ) và chỉnh lại parser nếu cấu trúc thay đổi.
2. **Chữ ký webhook**: kênh nào chưa có secret thì bước xác minh chữ ký được bỏ qua (tiện dev). Đã kết nối thật là tự bật xác minh.
3. Hiện mới xử lý **tin nhắn văn bản**; ảnh/video/sticker sẽ bị bỏ qua (có log).
4. Token hết hạn được service nền kiểm tra 5 phút/lần và refresh trước hạn 30 phút; nếu refresh thất bại (đổi mật khẩu, thu hồi quyền...) sẽ ghi log lỗi — vào `/settings` bấm Kết nối lại.

## Hướng phát triển tiếp

- [x] Cấu hình + kết nối OAuth qua giao diện (`/settings`)
- [x] Tự động refresh access token (Zalo, Shopee, TikTok)
- [x] Gắn thẻ trạng thái hội thoại (Đang tư vấn / Chờ phản hồi / Đã chốt đơn / Hủy)
- [x] Nút tạo thẻ Trello từ hội thoại khi chốt đơn (cấu hình trong `/settings`)
- [x] Đồng bộ tin nhắn cũ (nút "⟳ Đồng bộ tin cũ" trong `/settings`, kênh đã kết nối)
- [x] Tin nhắn ảnh: nhận ảnh khách gửi (webhook cả 4 kênh) + gửi ảnh cho khách (nút 📎)
- [x] Popup xem trước/sửa nội dung thẻ Trello trước khi tạo
- [x] Cột lịch sử đặt hàng bên phải khung chat (mỗi thẻ Trello tạo thành công = một đơn)
- [x] Kênh Zalo cá nhân qua sidecar zca-js (unofficial, có cảnh báo rủi ro)
- [x] Lấy tên + avatar thật của khách (Zalo getprofile, Facebook Graph)
- [x] Tài khoản nhân viên (đăng nhập) + phân công hội thoại + lọc "Của tôi"
- [x] Câu trả lời mẫu (quick replies) — nút bấm nhanh trên khung chat
- [x] Gợi ý trả lời bằng AI (chọn Claude / ChatGPT / Gemini / DeepSeek) — tab ✨ Trợ lý AI: thông tin tiệm, câu hỏi thường gặp, ảnh tư liệu (vision)
- [x] Tự động gửi file theo kịch bản — khách hỏi trúng từ khóa (không cần dấu) hoặc AI hiểu ý → bot tự gửi PDF/ảnh + tin kèm, chống spam 30 phút
- [ ] Tài khoản nhân viên + phân công hội thoại
- [ ] Câu trả lời mẫu (quick replies)
- [ ] Gợi ý trả lời bằng AI
