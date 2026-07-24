# Hướng Dẫn Biên Dịch & Chạy Ứng Dụng Mobile ChumChat (Flutter cho Android & iOS)

Thư mục mã nguồn ứng dụng Mobile: `src/ChumChat.Mobile`

---

## 🚀 1. Yêu Cầu Cài Đặt Ban Đầu (Prerequisites)

1. **Cài đặt Flutter SDK:**
   - Tải Flutter tại: [https://docs.flutter.dev/get-started/install](https://docs.flutter.dev/get-started/install)
   - Thêm đường dẫn `flutter/bin` vào biến môi trường `PATH`.

2. **Cài đặt Android Studio (để tạo file Android .APK / .AAB):**
   - Tải Android Studio & cài đặt Android SDK Command-line Tools.

3. **Cài đặt Xcode (để build ứng dụng iOS .IPA / iPhone - chỉ cần trên máy macOS):**
   - Tải Xcode từ Mac App Store.

---

## 📦 2. Các Bước Lệnh Biên Dịch & Chạy App

Mở cửa sổ terminal / Command Prompt tại thư mục `src/ChumChat.Mobile`:

```bash
cd d:\source_code\chumchumchat\src\ChumChat.Mobile
```

### A. Tải thư viện phụ thuộc:
```bash
flutter pub get
```

### B. Chạy ứng dụng trên Thiết bị thử nghiệm / Simulator:
```bash
flutter run
```

### C. Xuất bản ứng dụng Android (file .APK cho điện thoại Android):
```bash
flutter build apk --release
```
-> File `.apk` hoàn chỉnh sẽ nằm tại: `src/ChumChat.Mobile/build/app/outputs/flutter-apk/app-release.apk`. Bạn có thể gửi file này qua Zalo / Email để cài trực tiếp vào bất kỳ điện thoại Android nào!

### D. Xuất bản ứng dụng iOS (Cho iPhone / iPad):
```bash
flutter build ipa --release
```
-> Mở thư mục `ios` bằng Xcode để đóng gói và đưa lên Apple App Store hoặc TestFlight!

---

## 📱 3. Hướng Dẫn Sử Dụng Ứng Dụng Mobile

1. **Địa chỉ Server VPS:**
   - Khi mở App lần đầu, nhập tên đăng nhập nhân viên / admin và địa chỉ VPS của bạn (Ví dụ: `http://103.x.x.x:5000` hoặc domain của bạn).
2. **Quản lý Hộp thư:**
   - Đầy đủ bộ lọc kênh, tìm kiếm tin nhắn/tên khách theo từ khóa (ấn nút Tìm hoặc Enter).
   - Nút bật/tắt **🤖 AI AutoPilot** ngay trên thanh tiêu đề ứng dụng.
   - Gửi/nhận tin nhắn 2 chiều thời gian thực (Cả chat 1-1 và tin nhắn Nhóm Zalo).
