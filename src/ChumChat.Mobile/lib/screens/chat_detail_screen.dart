import 'package:flutter/material.dart';
import '../models/models.dart';
import '../services/api_service.dart';

class ChatDetailScreen extends StatefulWidget {
  final ConversationModel conversation;
  final String staffName;

  const ChatDetailScreen({
    Key? key,
    required this.conversation,
    required this.staffName,
  }) : super(key: key);

  @override
  State<ChatDetailScreen> createState() => _ChatDetailScreenState();
}

class _ChatDetailScreenState extends State<ChatDetailScreen> {
  List<MessageModel> _messages = [];
  List<OrderModel> _orders = [];
  bool _isLoading = true;
  bool _isSending = false;
  final _messageController = TextEditingController();
  final ScrollController _scrollController = ScrollController();

  @override
  void initState() {
    super.initState();
    _loadMessages();
  }

  Future<void> _loadMessages() async {
    try {
      final res = await ApiService.getMessages(widget.conversation.id);
      if (mounted) {
        setState(() {
          _messages = res['messages'] ?? [];
          _orders = res['orders'] ?? [];
          _isLoading = false;
        });
        _scrollToBottom();
      }
    } catch (e) {
      if (mounted) setState(() => _isLoading = false);
    }
  }

  void _scrollToBottom() {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (_scrollController.hasClients) {
        _scrollController.animateTo(
          _scrollController.position.maxScrollExtent,
          duration: const Duration(milliseconds: 200),
          curve: Curves.easeOut,
        );
      }
    });
  }

  Future<void> _sendMessage() async {
    final text = _messageController.text.trim();
    if (text.isEmpty) return;

    _messageController.clear();
    setState(() => _isSending = true);

    final success = await ApiService.sendReply(widget.conversation.id, text);
    if (success) {
      await _loadMessages();
    } else {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(content: Text("✕ Gửi tin nhắn thất bại")),
        );
      }
    }
    if (mounted) setState(() => _isSending = false);
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(widget.conversation.customerName, style: const TextStyle(fontSize: 16, fontWeight: FontWeight.bold)),
            Text(widget.conversation.channelName, style: const TextStyle(fontSize: 11, color: Colors.black54)),
          ],
        ),
        actions: [
          IconButton(
            icon: const Icon(Icons.refresh),
            onPressed: _loadMessages,
          ),
        ],
      ),
      body: Column(
        children: [
          // Order History Header Banner (if any)
          if (_orders.isNotEmpty)
            Container(
              padding: const EdgeInsets.all(8),
              color: Colors.amber.shade50,
              child: Row(
                children: [
                  const Icon(Icons.receipt_long, color: Colors.amber, size: 18),
                  const SizedBox(width: 6),
                  Text("Lịch sử: ${_orders.length} đơn hàng", style: const TextStyle(fontSize: 12, fontWeight: FontWeight.bold)),
                  const Spacer(),
                  Text("Đơn mới: ${_orders.first.amount}đ", style: const TextStyle(fontSize: 12, color: Colors.amber, fontWeight: FontWeight.bold)),
                ],
              ),
            ),

          // Message Bubbles
          Expanded(
            child: _isLoading
                ? const Center(child: CircularProgressIndicator())
                : ListView.builder(
                    controller: _scrollController,
                    padding: const EdgeInsets.all(12),
                    itemCount: _messages.length,
                    itemBuilder: (ctx, idx) {
                      final msg = _messages[idx];
                      final isMe = msg.isOutbound;
                      return Align(
                        alignment: isMe ? Alignment.centerRight : Alignment.centerLeft,
                        child: Container(
                          margin: const EdgeInsets.symmetric(vertical: 4),
                          padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
                          constraints: BoxConstraints(maxWidth: MediaQuery.of(context).size.width * 0.75),
                          decoration: BoxDecoration(
                            color: isMe ? const Color(0xFF2563EB) : Colors.grey.shade200,
                            borderRadius: BorderRadius.only(
                              topLeft: const Radius.circular(14),
                              topRight: const Radius.circular(14),
                              bottomLeft: isMe ? const Radius.circular(14) : const Radius.circular(2),
                              bottomRight: isMe ? const Radius.circular(2) : const Radius.circular(14),
                            ),
                          ),
                          child: Column(
                            crossAxisAlignment: isMe ? CrossAxisAlignment.end : CrossAxisAlignment.start,
                            children: [
                              if (msg.attachmentUrl != null && msg.attachmentUrl!.isNotEmpty)
                                ClipRRect(
                                  borderRadius: BorderRadius.circular(8),
                                  child: Image.network(msg.attachmentUrl!, width: 180, height: 180, fit: BoxFit.cover),
                                ),
                              if (msg.text.isNotEmpty)
                                Text(
                                  msg.text,
                                  style: TextStyle(
                                    color: isMe ? Colors.white : Colors.black87,
                                    fontSize: 14,
                                  ),
                                ),
                              const SizedBox(height: 4),
                              Text(
                                "${msg.sentAt.hour}:${msg.sentAt.minute.toString().padLeft(2, '0')}",
                                style: TextStyle(
                                  fontSize: 10,
                                  color: isMe ? Colors.white70 : Colors.black45,
                                ),
                              ),
                            ],
                          ),
                        ),
                      );
                    },
                  ),
          ),

          // Input Bar
          Container(
            padding: const EdgeInsets.all(8),
            color: Colors.white,
            child: Row(
              children: [
                Expanded(
                  child: TextField(
                    controller: _messageController,
                    onSubmitted: (_) => _sendMessage(),
                    decoration: InputDecoration(
                      hintText: "Nhập tin nhắn trả lời...",
                      isDense: true,
                      contentPadding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
                      border: OutlineInputBorder(borderRadius: BorderRadius.circular(20)),
                    ),
                  ),
                ),
                const SizedBox(width: 8),
                IconButton(
                  icon: _isSending ? const SizedBox(width: 20, height: 20, child: CircularProgressIndicator(strokeWidth: 2)) : const Icon(Icons.send, color: Color(0xFF2563EB)),
                  onPressed: _isSending ? null : _sendMessage,
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}
