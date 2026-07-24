import 'package:flutter/material.dart';
import '../models/models.dart';
import '../services/api_service.dart';
import 'chat_detail_screen.dart';

class InboxScreen extends StatefulWidget {
  final int staffId;
  final String staffName;
  final bool isAdmin;

  const InboxScreen({
    Key? key,
    required this.staffId,
    required this.staffName,
    required this.isAdmin,
  }) : super(key: key);

  @override
  State<InboxScreen> createState() => _InboxScreenState();
}

class _InboxScreenState extends State<InboxScreen> {
  List<ConversationModel> _conversations = [];
  bool _isLoading = true;
  bool _isSyncing = false;
  bool _aiEnabled = true;
  int? _selectedChannel;
  bool _mineOnly = false;

  final _searchController = TextEditingController();
  String? _activeSearch;

  @override
  void initState() {
    super.initState();
    _loadData();
    _checkAiStatus();
  }

  Future<void> _checkAiStatus() async {
    final status = await ApiService.getAiStatus();
    if (mounted) setState(() => _aiEnabled = status);
  }

  Future<void> _loadData() async {
    setState(() => _isLoading = true);
    try {
      final list = await ApiService.getConversations(
        channel: _selectedChannel,
        mineOnly: _mineOnly,
        staffId: widget.staffId,
        search: _activeSearch,
      );
      if (mounted) setState(() => _conversations = list);
    } catch (e) {
      // ignore
    } finally {
      if (mounted) setState(() => _isLoading = false);
    }
  }

  Future<void> _toggleAi() async {
    final next = !_aiEnabled;
    final success = await ApiService.toggleAi(next);
    if (success && mounted) {
      setState(() => _aiEnabled = next);
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text("🤖 AI AutoPilot: ${next ? 'Đã BẬT' : 'Đã TẮT'}")),
      );
    }
  }

  void _onSearch() {
    final text = _searchController.text.trim();
    setState(() {
      _activeSearch = text.isEmpty ? null : text;
    });
    _loadData();
  }

  void _clearSearch() {
    _searchController.clear();
    setState(() {
      _activeSearch = null;
    });
    _loadData();
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: const Text("💬 Hộp Thư", style: TextStyle(fontWeight: FontWeight.bold, fontSize: 18)),
        actions: [
          IconButton(
            icon: Icon(_aiEnabled ? Icons.smart_toy : Icons.smart_toy_outlined,
                color: _aiEnabled ? Colors.green : Colors.red),
            tooltip: "Toggle AI AutoPilot",
            onPressed: _toggleAi,
          ),
          IconButton(
            icon: const Icon(Icons.refresh),
            onPressed: _loadData,
          ),
        ],
      ),
      body: Column(
        children: [
          // Filter Chips & Scope
          Container(
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
            color: Colors.white,
            child: Column(
              children: [
                Row(
                  children: [
                    ChoiceChip(
                      label: const Text("Tất cả"),
                      selected: !_mineOnly,
                      onSelected: (val) {
                        setState(() => _mineOnly = false);
                        _loadData();
                      },
                    ),
                    const SizedBox(width: 8),
                    ChoiceChip(
                      label: const Text("Của tôi"),
                      selected: _mineOnly,
                      onSelected: (val) {
                        setState(() => _mineOnly = true);
                        _loadData();
                      },
                    ),
                    const Spacer(),
                    Text("👤 ${widget.staffName}", style: const TextStyle(fontSize: 12, fontWeight: FontWeight.bold)),
                  ],
                ),
                const SizedBox(height: 8),

                // Search Box with Enter / Action button
                TextField(
                  controller: _searchController,
                  onSubmitted: (_) => _onSearch(),
                  decoration: InputDecoration(
                    hintText: "🔍 Tìm tên khách hoặc nội dung tin...",
                    isDense: true,
                    contentPadding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
                    border: OutlineInputBorder(borderRadius: BorderRadius.circular(10)),
                    suffixIcon: Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        if (_searchController.text.isNotEmpty)
                          IconButton(
                            icon: const Icon(Icons.clear, size: 18),
                            onPressed: _clearSearch,
                          ),
                        IconButton(
                          icon: const Icon(Icons.search, color: Colors.blue),
                          onPressed: _onSearch,
                        ),
                      ],
                    ),
                  ),
                ),
              ],
            ),
          ),

          if (_activeSearch != null)
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
              color: Colors.blue.shade50,
              child: Row(
                children: [
                  Text("Kết quả cho: \"$_activeSearch\"", style: const TextStyle(fontSize: 12, fontWeight: FontWeight.bold, color: Colors.blue)),
                  const Spacer(),
                  GestureDetector(
                    onTap: _clearSearch,
                    child: const Text("Xóa tìm", style: TextStyle(fontSize: 12, color: Colors.blue, decoration: TextDecoration.underline)),
                  ),
                ],
              ),
            ),

          // Conversation List
          Expanded(
            child: _isLoading
                ? const Center(child: CircularProgressIndicator())
                : _conversations.isEmpty
                    ? const Center(child: Text("Chưa có hội thoại nào"))
                    : ListView.separated(
                        itemCount: _conversations.length,
                        separatorBuilder: (_, __) => const Divider(height: 1),
                        itemBuilder: (ctx, idx) {
                          final conv = _conversations[idx];
                          return ListTile(
                            leading: CircleAvatar(
                              backgroundColor: Colors.blue.shade100,
                              child: Text(
                                conv.customerName.isNotEmpty ? conv.customerName[0].toUpperCase() : 'C',
                                style: const TextStyle(fontWeight: FontWeight.bold, color: Colors.blue),
                              ),
                            ),
                            title: Row(
                              children: [
                                Expanded(
                                  child: Text(
                                    conv.customerName,
                                    style: const TextStyle(fontWeight: FontWeight.bold, fontSize: 14),
                                    overflow: TextOverflow.ellipsis,
                                  ),
                                ),
                                Text(
                                  "${conv.lastMessageAt.hour}:${conv.lastMessageAt.minute.toString().padLeft(2, '0')}",
                                  style: const TextStyle(fontSize: 11, color: Colors.grey),
                                ),
                              ],
                            ),
                            subtitle: Row(
                              children: [
                                Container(
                                  padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 2),
                                  margin: const EdgeInsets.only(right: 6),
                                  decoration: BoxDecoration(
                                    color: Colors.blue.shade50,
                                    borderRadius: BorderRadius.circular(4),
                                  ),
                                  child: Text(
                                    conv.channelName,
                                    style: TextStyle(fontSize: 10, color: Colors.blue.shade800, fontWeight: FontWeight.bold),
                                  ),
                                ),
                                Expanded(
                                  child: Text(
                                    conv.lastMessagePreview,
                                    maxLines: 1,
                                    overflow: TextOverflow.ellipsis,
                                    style: const TextStyle(fontSize: 12, color: Colors.black87),
                                  ),
                                ),
                              ],
                            ),
                            trailing: conv.unreadCount > 0
                                ? Container(
                                    padding: const EdgeInsets.all(6),
                                    decoration: const BoxDecoration(color: Colors.red, shape: BoxShape.circle),
                                    child: Text(
                                      "${conv.unreadCount}",
                                      style: const TextStyle(color: Colors.white, fontSize: 10, fontWeight: FontWeight.bold),
                                    ),
                                  )
                                : null,
                            onTap: () async {
                              await Navigator.push(
                                context,
                                MaterialPageRoute(
                                  builder: (_) => ChatDetailScreen(conversation: conv, staffName: widget.staffName),
                                ),
                              );
                              _loadData();
                            },
                          );
                        },
                      ),
          ),
        ],
      ),
    );
  }
}
