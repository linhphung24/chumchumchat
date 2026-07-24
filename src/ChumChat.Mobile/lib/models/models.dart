class ConversationModel {
  final int id;
  final int channel;
  final String channelName;
  final String externalId;
  final String customerName;
  final String customerPhone;
  final String customerAddress;
  final String? avatarUrl;
  final String lastMessagePreview;
  final DateTime lastMessageAt;
  final int unreadCount;
  final String? tag;
  final int? assignedStaffId;

  ConversationModel({
    required this.id,
    required this.channel,
    required this.channelName,
    required this.externalId,
    required this.customerName,
    required this.customerPhone,
    required this.customerAddress,
    this.avatarUrl,
    required this.lastMessagePreview,
    required this.lastMessageAt,
    required this.unreadCount,
    this.tag,
    this.assignedStaffId,
  });

  factory ConversationModel.fromJson(Map<String, dynamic> json) {
    return ConversationModel(
      id: json['id'] ?? 0,
      channel: json['channel'] ?? 0,
      channelName: getChannelLabel(json['channel'] ?? 0),
      externalId: json['externalId'] ?? '',
      customerName: json['customerName'] ?? 'Khách hàng',
      customerPhone: json['customerPhone'] ?? '',
      customerAddress: json['customerAddress'] ?? '',
      avatarUrl: json['avatarUrl'],
      lastMessagePreview: json['lastMessagePreview'] ?? '',
      lastMessageAt: DateTime.tryParse(json['lastMessageAt'] ?? '') ?? DateTime.now(),
      unreadCount: json['unreadCount'] ?? 0,
      tag: json['tag'],
      assignedStaffId: json['assignedStaffId'],
    );
  }

  static String getChannelLabel(int channel) {
    switch (channel) {
      case 0:
        return 'Zalo OA';
      case 1:
        return 'Messenger';
      case 2:
        return 'Shopee';
      case 3:
        return 'TikTok Shop';
      case 4:
        return 'Instagram';
      case 5:
        return 'Zalo Cá Nhân';
      case 6:
        return 'Messenger Cá Nhân';
      case 7:
        return 'Threads';
      case 8:
        return 'Google Location';
      default:
        return 'Kênh';
    }
  }
}

class MessageModel {
  final int id;
  final int conversationId;
  final int direction; // 0 = Inbound (Khách), 1 = Outbound (Shop)
  final String text;
  final String? attachmentUrl;
  final DateTime sentAt;
  final int status;

  MessageModel({
    required this.id,
    required this.conversationId,
    required this.direction,
    required this.text,
    this.attachmentUrl,
    required this.sentAt,
    required this.status,
  });

  bool get isOutbound => direction == 1;

  factory MessageModel.fromJson(Map<String, dynamic> json) {
    return MessageModel(
      id: json['id'] ?? 0,
      conversationId: json['conversationId'] ?? 0,
      direction: json['direction'] ?? 0,
      text: json['text'] ?? '',
      attachmentUrl: json['attachmentUrl'],
      sentAt: DateTime.tryParse(json['sentAt'] ?? '') ?? DateTime.now(),
      status: json['status'] ?? 0,
    );
  }
}

class OrderModel {
  final int id;
  final int conversationId;
  final String title;
  final int amount;
  final String note;
  final String? trelloCardUrl;
  final DateTime createdAt;

  OrderModel({
    required this.id,
    required this.conversationId,
    required this.title,
    required this.amount,
    required this.note,
    this.trelloCardUrl,
    required this.createdAt,
  });

  factory OrderModel.fromJson(Map<String, dynamic> json) {
    return OrderModel(
      id: json['id'] ?? 0,
      conversationId: json['conversationId'] ?? 0,
      title: json['title'] ?? '',
      amount: json['amount'] ?? 0,
      note: json['note'] ?? '',
      trelloCardUrl: json['trelloCardUrl'],
      createdAt: DateTime.tryParse(json['createdAt'] ?? '') ?? DateTime.now(),
    );
  }
}

class ProductModel {
  final int id;
  final String name;
  final int price;
  final String sku;
  final int stockQuantity;

  ProductModel({
    required this.id,
    required this.name,
    required this.price,
    required this.sku,
    required this.stockQuantity,
  });

  factory ProductModel.fromJson(Map<String, dynamic> json) {
    return ProductModel(
      id: json['id'] ?? 0,
      name: json['name'] ?? '',
      price: json['price'] ?? 0,
      sku: json['sku'] ?? '',
      stockQuantity: json['stockQuantity'] ?? 999,
    );
  }
}
