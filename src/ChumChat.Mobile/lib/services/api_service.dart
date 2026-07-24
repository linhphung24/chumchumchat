import 'dart:convert';
import 'package:http/http.dart' as http;
import '../models/models.dart';

class ApiService {
  static String baseUrl = "http://localhost:5000"; // Có thể thay đổi theo Server IP / VPS domain

  static Map<String, String> get headers => {
        "Content-Type": "application/json",
        "Accept": "application/json",
      };

  static Future<Map<String, dynamic>> login(String username, String password) async {
    final url = Uri.parse("$baseUrl/api/v1/mobile/login");
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

    final url = Uri.parse("$baseUrl/api/v1/mobile/conversations").replace(queryParameters: queryParams);
    final response = await http.get(url, headers: headers);

    if (response.statusCode == 200) {
      final List data = jsonDecode(response.body);
      return data.map((json) => ConversationModel.fromJson(json)).toList();
    }
    return [];
  }

  static Future<Map<String, dynamic>> getMessages(int conversationId) async {
    final url = Uri.parse("$baseUrl/api/v1/mobile/conversations/$conversationId/messages");
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
    return {"messages": <MessageModel>[], "orders": <OrderModel>[]};
  }

  static Future<bool> sendReply(int conversationId, String text, {String? imageUrl}) async {
    final url = Uri.parse("$baseUrl/api/v1/mobile/conversations/$conversationId/reply");
    final response = await http.post(
      url,
      headers: headers,
      body: jsonEncode({"text": text, "imageUrl": imageUrl}),
    );
    return response.statusCode == 200;
  }

  static Future<bool> toggleAi(bool enabled) async {
    final url = Uri.parse("$baseUrl/api/v1/mobile/ai/toggle");
    final response = await http.post(
      url,
      headers: headers,
      body: jsonEncode({"enabled": enabled}),
    );
    return response.statusCode == 200;
  }

  static Future<bool> getAiStatus() async {
    final url = Uri.parse("$baseUrl/api/v1/mobile/ai/status");
    final response = await http.get(url, headers: headers);
    if (response.statusCode == 200) {
      final data = jsonDecode(response.body);
      return data['enabled'] ?? false;
    }
    return false;
  }

  static Future<List<ProductModel>> getProducts() async {
    final url = Uri.parse("$baseUrl/api/v1/mobile/products");
    final response = await http.get(url, headers: headers);
    if (response.statusCode == 200) {
      final List data = jsonDecode(response.body);
      return data.map((j) => ProductModel.fromJson(j)).toList();
    }
    return [];
  }
}
