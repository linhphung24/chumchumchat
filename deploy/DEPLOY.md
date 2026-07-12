# Hướng dẫn deploy ChumChat lên VPS Linux (Ubuntu)

Mô hình: **Kestrel (app .NET) chạy như service systemd ở cổng nội bộ 5000 → nginx làm reverse proxy nhận 80/443 → Let's Encrypt cấp SSL miễn phí**.

Yêu cầu: VPS Ubuntu 22.04 hoặc 24.04, quyền root/sudo, và một domain.

---

## Bước 0 — Domain (bắt buộc, vì webhook cần HTTPS)

Các nền tảng (Zalo, Facebook, Shopee, TikTok) **chỉ chấp nhận webhook URL HTTPS**, và SSL miễn phí cần domain.

- Mua domain (~200–300k/năm) tại Tenten, Mắt Bão, PA Việt Nam, hoặc Namecheap/Cloudflare.
- Vào trang quản lý DNS, tạo bản ghi **A**: tên `chat` (hoặc `@`) → trỏ về **IP của VPS**.
- Chờ 5–30 phút, kiểm tra bằng lệnh `ping chat.tenmien.vn` thấy đúng IP là được.

Từ đây trở xuống giả sử domain là `chat.tenmien.vn` — thay bằng domain thật của bạn.

## Bước 1 — Publish app trên máy Windows của bạn

Mở terminal tại thư mục dự án `D:\source_code\chumchumchat`:

```powershell
dotnet publish src/ChumChat.Web -c Release -r linux-x64 --self-contained -o publish
```

`--self-contained` đóng gói sẵn cả .NET runtime → **không cần cài .NET trên VPS**.

## Bước 2 — Upload lên VPS

Dùng WinSCP (giao diện kéo thả) hoặc lệnh scp:

```powershell
scp -r publish/* root@IP_VPS:/var/www/chumchat/
```

(Nếu chưa có thư mục, SSH vào VPS chạy trước: `mkdir -p /var/www/chumchat`)

## Bước 3 — Cài đặt trên VPS (SSH vào VPS, chạy lần lượt)

```bash
# Tạo user riêng cho app (an toàn hơn chạy bằng root)
sudo useradd -r -s /usr/sbin/nologin chumchat
sudo chown -R chumchat:chumchat /var/www/chumchat
sudo chmod +x /var/www/chumchat/ChumChat.Web

# Đăng ký service systemd (file mẫu có sẵn trong repo: deploy/chumchat.service)
sudo cp /var/www/chumchat/deploy/chumchat.service /etc/systemd/system/ 2>/dev/null \
  || sudo nano /etc/systemd/system/chumchat.service   # dán nội dung file deploy/chumchat.service
sudo systemctl daemon-reload
sudo systemctl enable --now chumchat

# Kiểm tra app đã chạy
sudo systemctl status chumchat --no-pager
curl http://localhost:5000   # phải trả về HTML
```

## Bước 4 — Kết nối các kênh (làm sau khi có HTTPS ở bước 5)

Không cần sửa file gì trên server: mở **`https://chat.tenmien.vn/settings`**, dán khóa app của từng kênh, bấm **Lưu** rồi **Kết nối** — trang cấp quyền của Zalo/Facebook/Shopee/TikTok sẽ mở ra, đồng ý là token tự lưu vào database và tự gia hạn.

Nhớ khai báo trên trang developer của từng nền tảng đúng các URL mà trang `/settings` hiển thị (callback URL + webhook URL).

## Bước 5 — nginx + SSL

```bash
sudo apt update && sudo apt install -y nginx certbot python3-certbot-nginx

# Cấu hình site (file mẫu: deploy/chumchat.nginx.conf — nhớ thay domain)
sudo nano /etc/nginx/sites-available/chumchat    # dán nội dung, sửa chat.example.com
sudo ln -s /etc/nginx/sites-available/chumchat /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx

# Cấp SSL — certbot tự sửa config nginx và tự gia hạn mỗi 90 ngày
sudo certbot --nginx -d chat.tenmien.vn
```

## Bước 6 — Tường lửa

```bash
sudo ufw allow OpenSSH
sudo ufw allow 'Nginx Full'
sudo ufw enable
```

## Bước 7 — Khai báo webhook với các nền tảng

Giờ app đã có địa chỉ công khai `https://chat.tenmien.vn`. Vào trang developer của từng nền tảng, khai báo webhook URL:

| Kênh | URL webhook |
|------|-------------|
| Zalo OA | `https://chat.tenmien.vn/webhooks/zalo` |
| Messenger | `https://chat.tenmien.vn/webhooks/messenger` (verify token phải trùng với `Channels:Messenger:VerifyToken`) |
| Shopee | `https://chat.tenmien.vn/webhooks/shopee` |
| TikTok Shop | `https://chat.tenmien.vn/webhooks/tiktok` |

Mở `https://chat.tenmien.vn` trên trình duyệt → thấy inbox là xong. Nhắn thử một tin vào OA/Fanpage/shop để kiểm tra tin đổ về.

---

## (Tùy chọn) Sidecar Zalo cá nhân

⚠ Kênh Zalo cá nhân dùng thư viện **không chính thức** (zca-js) — Zalo có thể khóa tài khoản. Nên dùng tài khoản phụ.

```bash
# Trên VPS: cài Node.js 20+
curl -fsSL https://deb.nodesource.com/setup_20.x | sudo bash - && sudo apt install -y nodejs

# Copy thư mục sidecars/zalo-personal từ máy Windows lên VPS
# (chạy trên máy Windows):  scp -r sidecars/zalo-personal root@IP_VPS:/var/www/chumchat-zalo

cd /var/www/chumchat-zalo && npm install
chown -R chumchat:chumchat /var/www/chumchat-zalo

# Đăng ký service (sửa API_KEY trong file trước — trùng với ô API Key trên trang Cấu hình)
cp deploy/chumchat-zalo-personal.service /etc/systemd/system/
systemctl daemon-reload && systemctl enable --now chumchat-zalo-personal

journalctl -u chumchat-zalo-personal -f
```

Sau đó vào `https://chat.tenmien.vn/settings` → thẻ **Zalo cá nhân** → điền địa chỉ sidecar + API Key → **Lưu** → **Kiểm tra & hiện mã QR**. **Mã QR hiện thẳng trong trình duyệt** — mở Zalo trên điện thoại quét là xong (không cần tải file qr.png về nữa).

## Đăng nhập lần đầu

App có đăng nhập. Lần chạy đầu tự tạo tài khoản admin **`admin` / `admin`** — mở `https://chat.tenmien.vn`, đăng nhập rồi **đổi mật khẩu ngay** (nút 🔑 góc trên). Trong ⚙ Cấu hình (chỉ admin), tạo tài khoản cho nhân viên. Khóa mã hóa phiên đăng nhập lưu tại `dataprotection-keys/` cạnh app nên không bị đăng xuất khi deploy lại — **backup thư mục này cùng `chumchat.db`**.

## Cập nhật phiên bản mới sau này

Trên máy Windows:

```powershell
dotnet publish src/ChumChat.Web -c Release -r linux-x64 --self-contained -o publish
scp -r publish/* root@IP_VPS:/var/www/chumchat/
```

Trên VPS:

```bash
# QUAN TRỌNG: scp bằng root làm file thuộc về root → app (chạy bằng user chumchat)
# sẽ không ghi được vào wwwroot/uploads. Luôn chown lại sau khi upload:
sudo chown -R chumchat:chumchat /var/www/chumchat
sudo systemctl restart chumchat
```

(File database `chumchat.db` và `appsettings.Production.json` nằm trên VPS, không bị ghi đè khi upload — nhưng vẫn nên backup `chumchat.db` định kỳ: `cp /var/www/chumchat/chumchat.db ~/backup-$(date +%F).db`)

## Xem log khi có sự cố

```bash
sudo journalctl -u chumchat -f          # log app realtime
sudo tail -f /var/log/nginx/error.log   # log nginx
```

## Lỗi thường gặp

| Triệu chứng | Nguyên nhân / cách xử lý |
|---|---|
| Trang mở được nhưng bấm gì cũng đơ | nginx thiếu header WebSocket (`Upgrade`/`Connection`) — dùng đúng file mẫu `deploy/chumchat.nginx.conf` |
| Webhook nền tảng báo fail khi verify | DNS chưa trỏ xong, hoặc SSL chưa cấp — kiểm tra `https://chat.tenmien.vn` mở được trên trình duyệt trước |
| Tin nhắn thật không đổ về | Xem `journalctl -u chumchat -f` rồi nhắn thử — nếu payload lệch schema, log sẽ in đầy đủ body để sửa parser |
| 502 Bad Gateway | App chưa chạy — `sudo systemctl status chumchat` xem lỗi |
