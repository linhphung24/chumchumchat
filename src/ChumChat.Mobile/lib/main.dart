import 'package:flutter/material.dart';
import 'screens/login_screen.dart';
import 'screens/main_tab_screen.dart';

void main() {
  runApp(const ChumChatApp());
}

class ChumChatApp extends StatefulWidget {
  const ChumChatApp({Key? key}) : super(key: key);

  @override
  State<ChumChatApp> createState() => _ChumChatAppState();
}

class _ChumChatAppState extends State<ChumChatApp> {
  bool _isLoggedIn = false;
  int _staffId = 0;
  String _staffName = "";
  bool _isAdmin = false;

  void _handleLoginSuccess(int staffId, String name, bool isAdmin) {
    setState(() {
      _isLoggedIn = true;
      _staffId = staffId;
      _staffName = name;
      _isAdmin = isAdmin;
    });
  }

  void _handleLogout() {
    setState(() {
      _isLoggedIn = false;
      _staffId = 0;
      _staffName = "";
      _isAdmin = false;
    });
  }

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'ChumChat Mobile',
      debugShowCheckedModeBanner: false,
      theme: ThemeData(
        colorScheme: ColorScheme.fromSeed(seedColor: const Color(0xFF2563EB)),
        useMaterial3: true,
        scaffoldBackgroundColor: const Color(0xFFF8FAFC),
      ),
      home: _isLoggedIn
          ? MainTabScreen(
              staffId: _staffId,
              staffName: _staffName,
              isAdmin: _isAdmin,
              onLogout: _handleLogout,
            )
          : LoginScreen(onLoginSuccess: _handleLoginSuccess),
    );
  }
}
