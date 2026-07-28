import 'package:flutter/material.dart';
import 'inbox_screen.dart';
import 'orders_summary_screen.dart';
import 'settings_screen.dart';

class MainTabScreen extends StatefulWidget {
  final int staffId;
  final String staffName;
  final bool isAdmin;
  final VoidCallback onLogout;

  const MainTabScreen({
    Key? key,
    required this.staffId,
    required this.staffName,
    required this.isAdmin,
    required this.onLogout,
  }) : super(key: key);

  @override
  State<MainTabScreen> createState() => _MainTabScreenState();
}

class _MainTabScreenState extends State<MainTabScreen> {
  int _currentIndex = 0;

  @override
  Widget build(BuildContext context) {
    final screens = [
      InboxScreen(staffId: widget.staffId, staffName: widget.staffName, isAdmin: widget.isAdmin),
      const OrdersSummaryScreen(),
      SettingsScreen(staffName: widget.staffName, isAdmin: widget.isAdmin, onLogout: widget.onLogout),
    ];

    return Scaffold(
      body: screens[_currentIndex],
      bottomNavigationBar: BottomNavigationBar(
        currentIndex: _currentIndex,
        selectedItemColor: const Color(0xFF2563EB),
        onTap: (idx) => setState(() => _currentIndex = idx),
        items: const [
          BottomNavigationBarItem(icon: Icon(Icons.chat_bubble_outline), activeIcon: Icon(Icons.chat_bubble), label: "Hộp thư"),
          BottomNavigationBarItem(icon: Icon(Icons.inventory_2_outlined), activeIcon: Icon(Icons.inventory_2), label: "Tổng hợp Đơn"),
          BottomNavigationBarItem(icon: Icon(Icons.settings_outlined), activeIcon: Icon(Icons.settings), label: "Cài đặt"),
        ],
      ),
    );
  }
}
