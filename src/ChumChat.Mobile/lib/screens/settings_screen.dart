import 'package:flutter/material.dart';
import '../services/api_service.dart';

class SettingsScreen extends StatelessWidget {
  final String staffName;
  final bool isAdmin;
  final VoidCallback onLogout;

  const SettingsScreen({
    Key? key,
    required this.staffName,
    required this.isAdmin,
    required this.onLogout,
  }) : super(key: key);

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: const Text("⚙️ Cài Đặt & Cấu Hình", style: TextStyle(fontWeight: FontWeight.bold, fontSize: 18)),
      ),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          // Profile Card
          Card(
            shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
            child: ListTile(
              leading: const CircleAvatar(child: Text("👤")),
              title: Text(staffName, style: const TextStyle(fontWeight: FontWeight.bold)),
              subtitle: Text(isAdmin ? "Quản trị viên (Admin)" : "Nhân viên trực chat"),
            ),
          ),
          const SizedBox(height: 16),

          // Server Config Info
          Card(
            shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
            child: ListTile(
              leading: const Icon(Icons.dns, color: Colors.blue),
              title: const Text("Địa chỉ VPS / Server"),
              subtitle: Text(ApiService.baseUrl, style: const TextStyle(fontSize: 12)),
            ),
          ),
          const SizedBox(height: 24),

          // Logout Button
          ElevatedButton.icon(
            onPressed: onLogout,
            style: ElevatedButton.styleFrom(
              backgroundColor: Colors.red.shade50,
              foregroundColor: Colors.red,
              elevation: 0,
              padding: const EdgeInsets.symmetric(vertical: 14),
              shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(10)),
            ),
            icon: const Icon(Icons.logout),
            label: const Text("ĐĂNG XUẤT TÀI KHOẢN", style: TextStyle(fontWeight: FontWeight.bold)),
          ),
        ],
      ),
    );
  }
}
