import 'dart:convert';
import 'package:http/http.dart' as http;
import '../models/models.dart';

class ApiService {
  static String baseUrl = "https://chat.chumchumbakery.com";

  static String get formattedBaseUrl {
    String url = baseUrl.trim();
    if (url.endsWith('/')) url = url.substring(0, url.length - 1);
    if (!url.startsWith('http://') && !url.startsWith('https://')) {
      url = 'http://$url';
    }
    return url;
  }

  static Map<String, String> get headers => {
        "Content-Type": "application/json",
        "Accept": "application/json",
      };

  static Future<Map<String, dynamic>> login(String username, String password) async {
    final url = Uri.parse("$formattedBaseUrl/api/v1/mobile/login");
    final response = await http.post(
      url,
      headers: headers,
      body: jsonEncode({"username": username, "password": password}),
    );
    return jsonDecode(response.body);
  }

  static Future<List<ConversationModel>> getConversations({
    int? channel,
    bool mineOnly = false,
    int? staffId,
    String? search,
  }) async {
    final queryParams = <String, String>{};
    if (channel != null) queryParams["channel"] = channel.toString();
    if (mineOnly) queryParams["mineOnly"] = "true";
    if (staffId != null) queryParams["staffId"] = staffId.toString();
    if (search != null && search.isNotEmpty) queryParams["search"] = search;

    final url = Uri.parse("$formattedBaseUrl/api/v1/mobile/conversations").replace(queryParameters: queryParams);
    final response = await http.get(url, headers: headers);

    if (response.statusCode == 200) {
      final List data = jsonDecode(response.body);
      return data.map((json) => ConversationModel.fromJson(json)).toList();
    }
    return [];
  }

  static Future<Map<String, dynamic>> getMessages(int conversationId) async {
    final url = Uri.parse("$formattedBaseUrl/api/v1/mobile/conversations/$conversationId/messages");
    try {
      final response = await http.get(url, headers: headers);
      if (response.statusCode == 200) {
        final Map<String, dynamic> data = jsonDecode(response.body);
        final List msgs = data['messages'] ?? [];
        final List ords = data['orders'] ?? [];

        return {
          "messages": msgs.map((j) => MessageModel.fromJson(j)).toList(),
          "orders": ords.map((j) => OrderModel.fromJson(j)).toList(),
        };
      }
    } catch (_) {}
    return {"messages": <MessageModel>[], "orders": <OrderModel>[]};
  }

  static Future<Map<String, dynamic>> sendReply(int conversationId, String text, {String? imageUrl}) async {
    final url = Uri.parse("$formattedBaseUrl/api/v1/mobile/conversations/$conversationId/reply");
    try {
      final response = await http.post(
        url,
        headers: headers,
        body: jsonEncode({"text": text, "imageUrl": imageUrl}),
      );
      if (response.statusCode == 200) {
        return {"success": true};
      }
      try {
        final data = jsonDecode(response.body);
        return {"success": false, "message": data['error'] ?? data['message'] ?? "Lỗi mã ${response.statusCode}"};
      } catch (_) {
        return {"success": false, "message": "Lỗi server (${response.statusCode}): Không thể gửi tin qua kênh kết nối này"};
      }
    } catch (e) {
      return {"success": false, "message": "Lỗi kết nối mạng: $e"};
    }
  }

  static Future<bool> toggleAi(bool enabled) async {
    final url = Uri.parse("$formattedBaseUrl/api/v1/mobile/ai/toggle");
    final response = await http.post(
      url,
      headers: headers,
      body: jsonEncode({"enabled": enabled}),
    );
    return response.statusCode == 200;
  }

  static Future<bool> getAiStatus() async {
    final url = Uri.parse("$formattedBaseUrl/api/v1/mobile/ai/status");
    final response = await http.get(url, headers: headers);
    if (response.statusCode == 200) {
      final data = jsonDecode(response.body);
      return data['enabled'] ?? false;
    }
    return false;
  }

  static Future<List<ProductModel>> getProducts() async {
    final url = Uri.parse("$formattedBaseUrl/api/v1/mobile/products");
    final response = await http.get(url, headers: headers);
    if (response.statusCode == 200) {
      final List data = jsonDecode(response.body);
      return data.map((j) => ProductModel.fromJson(j)).toList();
    }
    return [];
  }

  static Future<Map<String, dynamic>> groupOrders(List<int> orderIds, {String? batchCode}) async {
    final url = Uri.parse("$formattedBaseUrl/api/v1/mobile/orders/group");
    final response = await http.post(url, headers: headers, body: jsonEncode({"orderIds": orderIds, "batchCode": batchCode}));
    if (response.statusCode == 200) {
      return jsonDecode(response.body);
    }
    throw Exception("Không thể ghép đơn (Mã ${response.statusCode})");
  }

  static Future<bool> ungroupOrders(List<int> orderIds) async {
    final url = Uri.parse("$formattedBaseUrl/api/v1/mobile/orders/ungroup");
    final response = await http.post(url, headers: headers, body: jsonEncode({"orderIds": orderIds}));
    return response.statusCode == 200;
  }

  static Future<Map<String, dynamic>> bookLalamoveGroupOrders(List<int> orderIds, {String? batchCode}) async {
    final url = Uri.parse("$formattedBaseUrl/api/v1/mobile/orders/lalamove-book");
    final response = await http.post(url, headers: headers, body: jsonEncode({"orderIds": orderIds, "batchCode": batchCode}));
    if (response.statusCode == 200) {
      return jsonDecode(response.body);
    }
    final data = jsonDecode(response.body);
    throw Exception(data['error'] ?? "Lỗi khi đặt Lalamove (Mã ${response.statusCode})");
  }

  static Future<bool> updateOrderStatus(int orderId, String status) async {
    final url = Uri.parse("$formattedBaseUrl/api/v1/mobile/orders/$orderId/status");
    final response = await http.post(url, headers: headers, body: jsonEncode({"status": status}));
    return response.statusCode == 200;
  }
}
