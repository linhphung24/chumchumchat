import 'dart:convert';
import 'package:flutter/material.dart';
import 'package:http/http.dart' as http;
import '../models/models.dart';
import '../services/api_service.dart';
import 'chat_detail_screen.dart';

class OrdersSummaryScreen extends StatefulWidget {
  const OrdersSummaryScreen({Key? key}) : super(key: key);

  @override
  State<OrdersSummaryScreen> createState() => _OrdersSummaryScreenState();
}

class _OrdersSummaryScreenState extends State<OrdersSummaryScreen> {
  bool _isLoading = true;
  List<dynamic> _orders = [];
  Set<int> _selectedOrderIds = {};

  final TextEditingController _searchController = TextEditingController();
  final TextEditingController _productController = TextEditingController();
  String _selectedArea = "";
  String _groupFilter = "all"; // all, grouped, single
  String _statusFilter = "active"; // active, completed, cancelled, all

  @override
  void initState() {
    super.initState();
    _fetchOrders();
  }

  Future<void> _fetchOrders() async {
    setState(() => _isLoading = true);
    try {
      final queryParams = <String, String>{};
      if (_searchController.text.trim().isNotEmpty) {
        queryParams['search'] = _searchController.text.trim();
      }
      if (_productController.text.trim().isNotEmpty) {
        queryParams['product'] = _productController.text.trim();
      }
      if (_selectedArea.isNotEmpty) {
        queryParams['area'] = _selectedArea;
      }
      if (_groupFilter == 'grouped') {
        queryParams['grouped'] = 'true';
      } else if (_groupFilter == 'single') {
        queryParams['grouped'] = 'false';
      }
      if (_statusFilter.isNotEmpty) {
        queryParams['status'] = _statusFilter;
      }

      final uri = Uri.parse("${ApiService.formattedBaseUrl}/api/v1/mobile/orders/summary").replace(queryParameters: queryParams);
      final response = await http.get(uri, headers: ApiService.headers);

      if (response.statusCode == 200) {
        final List<dynamic> data = jsonDecode(response.body);
        setState(() {
          _orders = data;
          _isLoading = false;
        });
      } else {
        setState(() => _isLoading = false);
      }
    } catch (e) {
      if (mounted) setState(() => _isLoading = false);
    }
  }

  void _resetFilters() {
    _searchController.clear();
    _productController.clear();
    setState(() {
      _selectedArea = "";
      _groupFilter = "all";
      _statusFilter = "active";
      _selectedOrderIds.clear();
    });
    _fetchOrders();
  }

  Future<void> _changeOrderStatus(int orderId, String status) async {
    try {
      final ok = await ApiService.updateOrderStatus(orderId, status);
      if (ok && mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(
            content: Text(status == 'COMPLETED' ? '✔ Đã chuyển đơn sang Hoàn thành' : status == 'CANCELED' ? '❌ Đã chuyển đơn sang Hủy' : '🔄 Đã khôi phục trạng thái'),
            backgroundColor: status == 'COMPLETED' ? Colors.green : status == 'CANCELED' ? Colors.red : Colors.blue,
          ),
        );
        _fetchOrders();
      }
    } catch (e) {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text("✕ Lỗi đổi trạng thái: $e"), backgroundColor: Colors.red),
        );
      }
    }
  }

  Future<void> _groupSelectedOrders() async {
    if (_selectedOrderIds.isEmpty) return;

    final batchController = TextEditingController(
      text: "CH-${DateTime.now().year}${DateTime.now().month.toString().padLeft(2, '0')}${DateTime.now().day.toString().padLeft(2, '0')}-${DateTime.now().hour.toString().padLeft(2, '0')}${DateTime.now().minute.toString().padLeft(2, '0')}",
    );

    final confirmed = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: Text("🔗 Ghép chuyến ${_selectedOrderIds.length} đơn hàng"),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const Text("Nhập mã đợt / chuyến giao hàng ghép:"),
            const SizedBox(height: 8),
            TextField(
              controller: batchController,
              decoration: const InputDecoration(
                border: OutlineInputBorder(),
                hintText: "Mã chuyến (VD: CH-THANHXUAN-CHIEU)",
              ),
            ),
          ],
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(ctx, false), child: const Text("Hủy")),
          ElevatedButton(
            style: ElevatedButton.styleFrom(backgroundColor: Colors.purple.shade700, foregroundColor: Colors.white),
            onPressed: () => Navigator.pop(ctx, true),
            child: const Text("Xác nhận ghép"),
          ),
        ],
      ),
    );

    if (confirmed == true) {
      final url = Uri.parse("${ApiService.formattedBaseUrl}/api/v1/mobile/orders/group");
      final resp = await http.post(
        url,
        headers: ApiService.headers,
        body: jsonEncode({
          "orderIds": _selectedOrderIds.toList(),
          "batchCode": batchController.text.trim(),
        }),
      );

      if (resp.statusCode == 200) {
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(content: Text("✓ Đã tạo chuyến đơn ghép thành công!")),
        );
        setState(() => _selectedOrderIds.clear());
        _fetchOrders();
      }
    }
  }

  Future<void> _ungroupSelectedOrders() async {
    if (_selectedOrderIds.isEmpty) return;

    final url = Uri.parse("${ApiService.formattedBaseUrl}/api/v1/mobile/orders/ungroup");
    final resp = await http.post(
      url,
      headers: ApiService.headers,
      body: jsonEncode({
        "orderIds": _selectedOrderIds.toList(),
      }),
    );

    if (resp.statusCode == 200) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(content: Text("✓ Đã hủy đơn ghép thành công!")),
      );
      setState(() => _selectedOrderIds.clear());
      _fetchOrders();
    }
  }

  String _extractArea(String address) {
    if (address.isEmpty) return "";
    final addr = address.toLowerCase();

    // 12 Quận Nội Thành
    if (addr.contains("thanh xuân") || addr.contains("khương đình") || addr.contains("vũ tông phan") || addr.contains("nguyễn trãi") || addr.contains("thượng đình") || addr.contains("khương trung") || addr.contains("nhân chính")) return "Thanh Xuân";
    if (addr.contains("cầu giấy") || addr.contains("xuân thủy") || addr.contains("trung hòa") || addr.contains("dịch vọng") || addr.contains("trần thái tông") || addr.contains("nguyên hồng")) return "Cầu Giấy";
    if (addr.contains("đống đa") || addr.contains("chùa bộc") || addr.contains("thái hà") || addr.contains("tây sơn") || addr.contains("xã đàn") || addr.contains("cát linh") || addr.contains("láng")) return "Đống Đa";
    if (addr.contains("hai bà trưng") || addr.contains("bạch mai") || addr.contains("minh khai") || addr.contains("trần khát chân") || addr.contains("trương định") || addr.contains("đại la")) return "Hai Bà Trưng";
    if (addr.contains("hoàn kiếm") || addr.contains("tràng tiền") || addr.contains("hàng bài") || addr.contains("hàng đào") || addr.contains("lý thái tổ")) return "Hoàn Kiếm";
    if (addr.contains("ba đình") || addr.contains("kim mã") || addr.contains("đội cấn") || addr.contains("liễu giai") || addr.contains("giảng võ")) return "Ba Đình";
    if (addr.contains("tây hồ") || addr.contains("thuỵ khuê") || addr.contains("thụy khuê") || addr.contains("xuân la") || addr.contains("yên phụ") || addr.contains("nhật tân")) return "Tây Hồ";
    if (addr.contains("hoàng mai") || addr.contains("định công") || addr.contains("linh đàm") || addr.contains("lĩnh nam") || addr.contains("tân mai") || addr.contains("giáp bát")) return "Hoàng Mai";
    if (addr.contains("hà đông") || addr.contains("quang trung") || addr.contains("văn phú") || addr.contains("mộ lao") || addr.contains("yết kiêu") || addr.contains("vạn phúc")) return "Hà Đông";
    if (addr.contains("nam từ liêm") || addr.contains("mỹ đình") || addr.contains("mễ trì") || addr.contains("trung văn") || addr.contains("cầu diễn") || addr.contains("tây mỗ") || addr.contains("đại mỗ")) return "Nam Từ Liêm";
    if (addr.contains("bắc từ liêm") || addr.contains("cổ nhuế") || addr.contains("xuân đỉnh") || addr.contains("phú diễn") || addr.contains("thụy phương")) return "Bắc Từ Liêm";
    if (addr.contains("long biên") || addr.contains("ngọc lâm") || addr.contains("bồ đề") || addr.contains("giang biên") || addr.contains("thạch bàn") || addr.contains("sài đồng")) return "Long Biên";

    // Huyện & Thị Xã Ngoại Thành
    if (addr.contains("thanh trì") || addr.contains("ngũ hiệp") || addr.contains("ngọc hồi") || addr.contains("văn điển") || addr.contains("tả thanh oai") || addr.contains("vĩnh quỳnh")) return "Thanh Trì";
    if (addr.contains("gia lâm") || addr.contains("trâu quỳ") || addr.contains("ninh hiệp") || addr.contains("bát tràng")) return "Gia Lâm";
    if (addr.contains("đông anh") || addr.contains("vĩnh ngọc") || addr.contains("kim chung") || addr.contains("tiên dương")) return "Đông Anh";
    if (addr.contains("hoài đức") || addr.contains("an khánh") || addr.contains("vân canh") || addr.contains("lại yên")) return "Hoài Đức";
    if (addr.contains("đan phượng") || addr.contains("thị trấn phùng")) return "Đan Phượng";
    if (addr.contains("sóc sơn")) return "Sóc Sơn";
    if (addr.contains("mê linh")) return "Mê Linh";
    if (addr.contains("chương mỹ") || addr.contains("chúc sơn") || addr.contains("xuân mai")) return "Chương Mỹ";
    if (addr.contains("thanh oai") || addr.contains("kim bài")) return "Thanh Oai";
    if (addr.contains("thường tín")) return "Thường Tín";
    if (addr.contains("phú xuyên")) return "Phú Xuyên";
    if (addr.contains("quốc oai")) return "Quốc Oai";
    if (addr.contains("thạch thất") || addr.contains("hòa lạc")) return "Thạch Thất";
    if (addr.contains("ba vì")) return "Ba Vì";
    if (addr.contains("phúc thọ")) return "Phúc Thọ";
    if (addr.contains("mỹ đức")) return "Mỹ Đức";
    if (addr.contains("ứng hòa") || addr.contains("vân đình")) return "Ứng Hòa";
    if (addr.contains("sơn tây")) return "Sơn Tây";

    return "";
  }

  @override
  Widget build(BuildContext context) {
    int totalCount = _orders.length;
    double totalRevenue = 0;
    int groupedCount = 0;
    for (var o in _orders) {
      totalRevenue += (o['amount'] ?? o['Amount'] ?? 0).toDouble();
      if (o['isGrouped'] == true || o['IsGrouped'] == true) {
        groupedCount++;
      }
    }

    return Scaffold(
      appBar: AppBar(
        title: const Text("📦 Tổng Hợp Đơn Hàng", style: TextStyle(fontWeight: FontWeight.bold, fontSize: 18)),
        actions: [
          IconButton(icon: const Icon(Icons.refresh), onPressed: _fetchOrders),
        ],
      ),
      body: Column(
        children: [
          // Metrics Cards Bar
          Container(
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
            color: Colors.blue.shade50,
            child: Row(
              children: [
                Expanded(
                  child: Column(
                    children: [
                      const Text("Tổng đơn", style: TextStyle(fontSize: 11, color: Colors.black54)),
                      Text("$totalCount", style: const TextStyle(fontWeight: FontWeight.bold, fontSize: 16, color: Colors.blue)),
                    ],
                  ),
                ),
                Expanded(
                  child: Column(
                    children: [
                      const Text("Doanh thu", style: TextStyle(fontSize: 11, color: Colors.black54)),
                      Text("${totalRevenue.toInt()}đ", style: const TextStyle(fontWeight: FontWeight.bold, fontSize: 14, color: Colors.green)),
                    ],
                  ),
                ),
                Expanded(
                  child: Column(
                    children: [
                      const Text("Đơn ghép", style: TextStyle(fontSize: 11, color: Colors.black54)),
                      Text("$groupedCount", style: TextStyle(fontWeight: FontWeight.bold, fontSize: 16, color: Colors.purple.shade700)),
                    ],
                  ),
                ),
              ],
            ),
          ),

          // Filters Expandable Bar
          ExpansionTile(
            title: Text(
              "🔍 Bộ lọc tìm kiếm & khu vực (${_selectedArea.isEmpty ? 'Tất cả' : _selectedArea})",
              style: const TextStyle(fontSize: 13, fontWeight: FontWeight.bold),
            ),
            children: [
              Padding(
                padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 6),
                child: Column(
                  children: [
                    TextField(
                      controller: _searchController,
                      onChanged: (_) => _fetchOrders(),
                      decoration: const InputDecoration(
                        hintText: "Tìm tên khách, SĐT, ghi chú...",
                        prefixIcon: Icon(Icons.search, size: 20),
                        isDense: true,
                        border: OutlineInputBorder(),
                      ),
                    ),
                    const SizedBox(height: 8),
                    Row(
                      children: [
                        Expanded(
                          child: TextField(
                            controller: _productController,
                            onChanged: (_) => _fetchOrders(),
                            decoration: const InputDecoration(
                              hintText: "Lọc loại bánh...",
                              isDense: true,
                              border: OutlineInputBorder(),
                            ),
                          ),
                        ),
                        const SizedBox(width: 8),
                        Expanded(
                          child: DropdownButtonFormField<String>(
                            value: _selectedArea.isEmpty ? null : _selectedArea,
                            hint: const Text("Khu vực", style: TextStyle(fontSize: 12)),
                            isDense: true,
                            decoration: const InputDecoration(border: OutlineInputBorder()),
                            items: const [
                              DropdownMenuItem(value: "", child: Text("Tất cả khu vực")),
                              // 12 Quận Nội Thành
                              DropdownMenuItem(value: "Thanh Xuân", child: Text("Q. Thanh Xuân")),
                              DropdownMenuItem(value: "Cầu Giấy", child: Text("Q. Cầu Giấy")),
                              DropdownMenuItem(value: "Đống Đa", child: Text("Q. Đống Đa")),
                              DropdownMenuItem(value: "Hai Bà Trưng", child: Text("Q. Hai Bà Trưng")),
                              DropdownMenuItem(value: "Hoàn Kiếm", child: Text("Q. Hoàn Kiếm")),
                              DropdownMenuItem(value: "Ba Đình", child: Text("Q. Ba Đình")),
                              DropdownMenuItem(value: "Tây Hồ", child: Text("Q. Tây Hồ")),
                              DropdownMenuItem(value: "Hoàng Mai", child: Text("Q. Hoàng Mai")),
                              DropdownMenuItem(value: "Hà Đông", child: Text("Q. Hà Đông")),
                              DropdownMenuItem(value: "Nam Từ Liêm", child: Text("Q. Nam Từ Liêm")),
                              DropdownMenuItem(value: "Bắc Từ Liêm", child: Text("Q. Bắc Từ Liêm")),
                              DropdownMenuItem(value: "Long Biên", child: Text("Q. Long Biên")),
                              // Huyện Ngoại Thành
                              DropdownMenuItem(value: "Thanh Trì", child: Text("H. Thanh Trì")),
                              DropdownMenuItem(value: "Gia Lâm", child: Text("H. Gia Lâm")),
                              DropdownMenuItem(value: "Đông Anh", child: Text("H. Đông Anh")),
                              DropdownMenuItem(value: "Hoài Đức", child: Text("H. Hoài Đức")),
                              DropdownMenuItem(value: "Đan Phượng", child: Text("H. Đan Phượng")),
                              DropdownMenuItem(value: "Sóc Sơn", child: Text("H. Sóc Sơn")),
                              DropdownMenuItem(value: "Mê Linh", child: Text("H. Mê Linh")),
                              DropdownMenuItem(value: "Chương Mỹ", child: Text("H. Chương Mỹ")),
                              DropdownMenuItem(value: "Thanh Oai", child: Text("H. Thanh Oai")),
                              DropdownMenuItem(value: "Thường Tín", child: Text("H. Thường Tín")),
                              DropdownMenuItem(value: "Phú Xuyên", child: Text("H. Phú Xuyên")),
                              DropdownMenuItem(value: "Quốc Oai", child: Text("H. Quốc Oai")),
                              DropdownMenuItem(value: "Thạch Thất", child: Text("H. Thạch Thất")),
                              DropdownMenuItem(value: "Ba Vì", child: Text("H. Ba Vì")),
                              DropdownMenuItem(value: "Phúc Thọ", child: Text("H. Phúc Thọ")),
                              DropdownMenuItem(value: "Mỹ Đức", child: Text("H. Mỹ Đức")),
                              DropdownMenuItem(value: "Ứng Hòa", child: Text("H. Ứng Hòa")),
                              DropdownMenuItem(value: "Sơn Tây", child: Text("TX. Sơn Tây")),
                            ],
                            onChanged: (val) {
                              setState(() => _selectedArea = val ?? "");
                              _fetchOrders();
                            },
                          ),
                        ),
                      ],
                    ),
                    const SizedBox(height: 8),
                    SingleChildScrollView(
                      scrollDirection: Axis.horizontal,
                      child: Row(
                        children: [
                          ChoiceChip(
                            label: const Text("⏳ Đang xử lý"),
                            selected: _statusFilter == 'active',
                            selectedColor: Colors.blue.shade100,
                            onSelected: (_) {
                              setState(() => _statusFilter = 'active');
                              _fetchOrders();
                            },
                          ),
                          const SizedBox(width: 6),
                          ChoiceChip(
                            label: const Text("✅ Hoàn thành"),
                            selected: _statusFilter == 'completed',
                            selectedColor: Colors.green.shade100,
                            onSelected: (_) {
                              setState(() => _statusFilter = 'completed');
                              _fetchOrders();
                            },
                          ),
                          const SizedBox(width: 6),
                          ChoiceChip(
                            label: const Text("❌ Đã hủy"),
                            selected: _statusFilter == 'cancelled',
                            selectedColor: Colors.red.shade100,
                            onSelected: (_) {
                              setState(() => _statusFilter = 'cancelled');
                              _fetchOrders();
                            },
                          ),
                          const SizedBox(width: 6),
                          ChoiceChip(
                            label: const Text("🌐 Tất cả"),
                            selected: _statusFilter == 'all',
                            onSelected: (_) {
                              setState(() => _statusFilter = 'all');
                              _fetchOrders();
                            },
                          ),
                        ],
                      ),
                    ),
                    const SizedBox(height: 6),
                    SingleChildScrollView(
                      scrollDirection: Axis.horizontal,
                      child: Row(
                        children: [
                          ChoiceChip(
                            label: const Text("Tất cả đợt"),
                            selected: _groupFilter == 'all',
                            onSelected: (_) {
                              setState(() => _groupFilter = 'all');
                              _fetchOrders();
                            },
                          ),
                          const SizedBox(width: 6),
                          ChoiceChip(
                            label: const Text("🔗 Chỉ đơn ghép"),
                            selected: _groupFilter == 'grouped',
                            onSelected: (_) {
                              setState(() => _groupFilter = 'grouped');
                              _fetchOrders();
                            },
                          ),
                          const SizedBox(width: 6),
                          ChoiceChip(
                            label: const Text("📦 Chỉ đơn lẻ"),
                            selected: _groupFilter == 'single',
                            onSelected: (_) {
                              setState(() => _groupFilter = 'single');
                              _fetchOrders();
                            },
                          ),
                          const SizedBox(width: 12),
                          TextButton(
                            onPressed: _resetFilters,
                            child: const Text("Xóa lọc", style: TextStyle(color: Colors.red)),
                          ),
                        ],
                      ),
                    ),
                  ],
                ),
              ),
            ],
          ),

          // Action Toolbar for Selected Orders
          if (_selectedOrderIds.isNotEmpty)
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
              color: Colors.purple.shade50,
              child: SingleChildScrollView(
                scrollDirection: Axis.horizontal,
                child: Row(
                  children: [
                    Text(
                      "Đã chọn ${_selectedOrderIds.length} đơn",
                      style: TextStyle(fontWeight: FontWeight.bold, color: Colors.purple.shade900),
                    ),
                    const SizedBox(width: 12),
                    ElevatedButton.icon(
                      style: ElevatedButton.styleFrom(backgroundColor: Colors.red.shade600, foregroundColor: Colors.white),
                      icon: const Icon(Icons.local_shipping, size: 16),
                      label: const Text("📦 Đặt Lalamove ghép"),
                      onPressed: _bookLalamoveGroupOrders,
                    ),
                    const SizedBox(width: 6),
                    ElevatedButton.icon(
                      style: ElevatedButton.styleFrom(backgroundColor: Colors.purple.shade700, foregroundColor: Colors.white),
                      icon: const Icon(Icons.link, size: 16),
                      label: const Text("Ghép chuyến"),
                      onPressed: _groupSelectedOrders,
                    ),
                    const SizedBox(width: 6),
                    OutlinedButton(
                      onPressed: _ungroupSelectedOrders,
                      child: const Text("Hủy ghép", style: TextStyle(color: Colors.red)),
                    ),
                  ],
                ),
              ),
            ),

          // Orders List
          Expanded(
            child: _isLoading
                ? const Center(child: CircularProgressIndicator())
                : _orders.isEmpty
                    ? const Center(child: Text("Không tìm thấy đơn hàng nào", style: TextStyle(color: Colors.grey)))
                    : ListView.builder(
                        itemCount: _orders.length,
                        itemBuilder: (ctx, idx) {
                          final o = _orders[idx];
                          final int orderId = o['id'] ?? o['Id'];
                          final bool isGrouped = o['isGrouped'] == true || o['IsGrouped'] == true;
                          final String batchCode = o['groupBatchCode'] ?? o['GroupBatchCode'] ?? '';
                          final String title = o['title'] ?? o['Title'] ?? 'Khách hàng';
                          final String phone = o['customerPhone'] ?? o['CustomerPhone'] ?? '';
                          final String address = o['customerAddress'] ?? o['CustomerAddress'] ?? '';
                          final double amount = (o['amount'] ?? o['Amount'] ?? 0).toDouble();
                          final String areaTag = _extractArea(address);
                          final isSelected = _selectedOrderIds.contains(orderId);
                          final conv = o['conversation'] ?? o['Conversation'];
                          final String customerTags = conv != null ? (conv['customerTags'] ?? conv['CustomerTags'] ?? '') : '';

                          final itemsList = (o['items'] ?? o['Items'] ?? []) as List<dynamic>;

                          return Card(
                            margin: const EdgeInsets.symmetric(horizontal: 10, vertical: 5),
                            shape: RoundedRectangleBorder(
                              side: BorderSide(
                                color: isSelected ? Colors.purple : (isGrouped ? Colors.purple.shade200 : Colors.grey.shade200),
                                width: isSelected ? 2 : 1,
                              ),
                              borderRadius: BorderRadius.circular(10),
                            ),
                            child: Padding(
                              padding: const EdgeInsets.all(10),
                              child: Column(
                                crossAxisAlignment: CrossAxisAlignment.start,
                                children: [
                                  Row(
                                    children: [
                                      Checkbox(
                                        value: isSelected,
                                        onChanged: (checked) {
                                          setState(() {
                                            if (checked == true) {
                                              _selectedOrderIds.add(orderId);
                                            } else {
                                              _selectedOrderIds.remove(orderId);
                                            }
                                          });
                                        },
                                      ),
                                      Expanded(
                                        child: Column(
                                          crossAxisAlignment: CrossAxisAlignment.start,
                                          children: [
                                            Text(
                                              "#$orderId - $title",
                                              style: const TextStyle(fontWeight: FontWeight.bold, fontSize: 14),
                                              overflow: TextOverflow.ellipsis,
                                              maxLines: 1,
                                            ),
                                            if (customerTags.isNotEmpty) ...[
                                              const SizedBox(height: 3),
                                              Wrap(
                                                spacing: 4,
                                                runSpacing: 4,
                                                children: customerTags.split(',').where((t) => t.trim().isNotEmpty).map((tag) {
                                                  return Container(
                                                    padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                                                    decoration: BoxDecoration(
                                                      color: Colors.purple.shade50,
                                                      border: Border.all(color: Colors.purple.shade200),
                                                      borderRadius: BorderRadius.circular(6),
                                                    ),
                                                    child: Text(
                                                      tag.trim(),
                                                      style: TextStyle(fontSize: 10, fontWeight: FontWeight.bold, color: Colors.purple.shade900),
                                                    ),
                                                  );
                                                }).toList(),
                                              ),
                                            ],
                                          ],
                                        ),
                                      ),
                                      const SizedBox(width: 4),
                                      if (isGrouped)
                                        Flexible(
                                          child: Container(
                                            padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
                                            decoration: BoxDecoration(
                                              color: Colors.purple.shade100,
                                              borderRadius: BorderRadius.circular(12),
                                            ),
                                            child: Text(
                                              "🔗 Chuyến: ${batchCode.isNotEmpty ? batchCode : 'Đơn ghép'}",
                                              style: TextStyle(fontSize: 11, fontWeight: FontWeight.bold, color: Colors.purple.shade900),
                                              overflow: TextOverflow.ellipsis,
                                              maxLines: 1,
                                            ),
                                          ),
                                        )
                                      else
                                        Container(
                                          padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
                                          decoration: BoxDecoration(
                                            color: Colors.grey.shade200,
                                            borderRadius: BorderRadius.circular(12),
                                          ),
                                          child: const Text("📦 Đơn lẻ", style: TextStyle(fontSize: 11, color: Colors.black54)),
                                        ),
                                    ],
                                  ),
                                  if (phone.isNotEmpty)
                                    Padding(
                                      padding: const EdgeInsets.only(left: 48, bottom: 2),
                                      child: Text("📞 $phone", style: const TextStyle(color: Colors.blue, fontWeight: FontWeight.bold, fontSize: 13)),
                                    ),
                                  if (address.isNotEmpty)
                                    Padding(
                                      padding: const EdgeInsets.only(left: 48, bottom: 4),
                                      child: Row(
                                        children: [
                                          if (areaTag.isNotEmpty)
                                            Container(
                                              margin: const EdgeInsets.only(right: 6),
                                              padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                                              decoration: BoxDecoration(color: Colors.lightBlue.shade50, borderRadius: BorderRadius.circular(4)),
                                              child: Text("📍 $areaTag", style: const TextStyle(fontSize: 11, color: Colors.lightBlue, fontWeight: FontWeight.bold)),
                                            ),
                                          Expanded(child: Text(address, style: const TextStyle(fontSize: 12, color: Colors.black87), overflow: TextOverflow.ellipsis)),
                                        ],
                                      ),
                                    ),
                                  if (itemsList.isNotEmpty)
                                    Padding(
                                      padding: const EdgeInsets.only(left: 48, top: 4, bottom: 4),
                                      child: Column(
                                        crossAxisAlignment: CrossAxisAlignment.start,
                                        children: itemsList.map((item) {
                                          final pName = item['productName'] ?? item['ProductName'] ?? 'Sản phẩm';
                                          final qty = item['quantity'] ?? item['Quantity'] ?? 1;
                                          return Text("• $pName x $qty", style: const TextStyle(fontSize: 12, fontWeight: FontWeight.w600));
                                        }).toList(),
                                      ),
                                    ),
                                  Padding(
                                    padding: const EdgeInsets.only(left: 48, top: 4),
                                    child: Wrap(
                                      alignment: WrapAlignment.spaceBetween,
                                      crossAxisAlignment: WrapCrossAlignment.center,
                                      spacing: 8,
                                      runSpacing: 6,
                                      children: [
                                        Text("Tổng tiền: ${amount.toInt()}đ", style: const TextStyle(fontWeight: FontWeight.bold, color: Colors.green, fontSize: 14)),
                                        Row(
                                          mainAxisSize: MainAxisSize.min,
                                          children: [
                                            if ((o['ahamoveStatus'] ?? o['AhamoveStatus'] ?? '') == 'COMPLETED' || (o['ahamoveStatus'] ?? o['AhamoveStatus'] ?? '') == 'COMPLETED_BY_DRIVER') ...[
                                              Container(
                                                padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 3),
                                                decoration: BoxDecoration(color: Colors.green.shade50, borderRadius: BorderRadius.circular(4), border: Border.all(color: Colors.green.shade200)),
                                                child: const Text("✅ Hoàn thành", style: TextStyle(fontSize: 10, fontWeight: FontWeight.bold, color: Colors.green)),
                                              ),
                                              const SizedBox(width: 4),
                                              IconButton(
                                                icon: const Icon(Icons.refresh, size: 16, color: Colors.blue),
                                                tooltip: "Khôi phục trạng thái",
                                                onPressed: () => _changeOrderStatus(orderId, ""),
                                                constraints: const BoxConstraints(),
                                                padding: const EdgeInsets.all(4),
                                              ),
                                            ] else if ((o['ahamoveStatus'] ?? o['AhamoveStatus'] ?? '') == 'CANCELED' || (o['ahamoveStatus'] ?? o['AhamoveStatus'] ?? '') == 'CANCELLED' || (o['ahamoveStatus'] ?? o['AhamoveStatus'] ?? '') == 'EXPIRED') ...[
                                              Container(
                                                padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 3),
                                                decoration: BoxDecoration(color: Colors.red.shade50, borderRadius: BorderRadius.circular(4), border: Border.all(color: Colors.red.shade200)),
                                                child: const Text("❌ Đã hủy", style: TextStyle(fontSize: 10, fontWeight: FontWeight.bold, color: Colors.red)),
                                              ),
                                              const SizedBox(width: 4),
                                              IconButton(
                                                icon: const Icon(Icons.refresh, size: 16, color: Colors.blue),
                                                tooltip: "Khôi phục trạng thái",
                                                onPressed: () => _changeOrderStatus(orderId, ""),
                                                constraints: const BoxConstraints(),
                                                padding: const EdgeInsets.all(4),
                                              ),
                                            ] else ...[
                                              PopupMenuButton<String>(
                                                icon: const Icon(Icons.more_vert, size: 18),
                                                padding: EdgeInsets.zero,
                                                constraints: const BoxConstraints(),
                                                itemBuilder: (ctx) => [
                                                  const PopupMenuItem(value: "COMPLETED", child: Row(children: [Icon(Icons.check_circle, color: Colors.green, size: 18), SizedBox(width: 8), Text("✔ Đánh dấu Hoàn thành")])),
                                                  const PopupMenuItem(value: "CANCELED", child: Row(children: [Icon(Icons.cancel, color: Colors.red, size: 18), SizedBox(width: 8), Text("❌ Hủy đơn này")])),
                                                ],
                                                onSelected: (val) => _changeOrderStatus(orderId, val),
                                              ),
                                            ],
                                            const SizedBox(width: 4),
                                            OutlinedButton.icon(
                                              style: OutlinedButton.styleFrom(
                                                foregroundColor: Colors.red.shade700,
                                                side: BorderSide(color: Colors.red.shade300),
                                                padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                                                minimumSize: Size.zero,
                                                tapTargetSize: MaterialTapTargetSize.shrinkWrap,
                                              ),
                                              icon: const Icon(Icons.local_shipping, size: 14),
                                              label: const Text("Lalamove", style: TextStyle(fontSize: 11, fontWeight: FontWeight.bold)),
                                              onPressed: () => _lalamoveSingleOrder(orderId),
                                            ),
                                            const SizedBox(width: 6),
                                            OutlinedButton.icon(
                                              style: OutlinedButton.styleFrom(
                                                foregroundColor: Colors.purple.shade700,
                                                side: BorderSide(color: Colors.purple.shade300),
                                                padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                                                minimumSize: Size.zero,
                                                tapTargetSize: MaterialTapTargetSize.shrinkWrap,
                                              ),
                                              icon: const Icon(Icons.link, size: 14),
                                              label: const Text("Ghép đơn", style: TextStyle(fontSize: 11, fontWeight: FontWeight.bold)),
                                              onPressed: () {
                                                setState(() {
                                                  _selectedOrderIds.clear();
                                                  _selectedOrderIds.add(orderId);
                                                });
                                                _groupSelectedOrders();
                                              },
                                            ),
                                            const SizedBox(width: 6),
                                            ElevatedButton.icon(
                                              style: ElevatedButton.styleFrom(
                                                backgroundColor: Colors.blue.shade600,
                                                foregroundColor: Colors.white,
                                                padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                                                minimumSize: Size.zero,
                                                tapTargetSize: MaterialTapTargetSize.shrinkWrap,
                                              ),
                                              icon: const Icon(Icons.chat_bubble_outline, size: 14),
                                              label: const Text("Chat khách", style: TextStyle(fontSize: 11, fontWeight: FontWeight.bold)),
                                              onPressed: () {
                                                final int convId = o['conversationId'] ?? o['ConversationId'] ?? 0;
                                                if (convId > 0) {
                                                  _openChatForConversation(convId, title);
                                                }
                                              },
                                            ),
                                          ],
                                        ),
                                      ],
                                    ),
                                  ),
                                ],
                              ),
                            ),
                          );
                        },
                      ),
          ),
        ],
      ),
    );
  }

  Future<void> _lalamoveSingleOrder(int orderId) async {
    setState(() {
      _selectedOrderIds.clear();
      _selectedOrderIds.add(orderId);
    });
    await _bookLalamoveGroupOrders();
  }

  Future<void> _bookLalamoveGroupOrders() async {
    if (_selectedOrderIds.isEmpty) return;

    showDialog(
      context: context,
      barrierDismissible: false,
      builder: (ctx) {
        return StatefulBuilder(
          builder: (context, setDialogState) {
            return AlertDialog(
              shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
              title: Row(
                children: [
                  const Icon(Icons.local_shipping, color: Colors.red),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      "Đặt Lalamove ghép (${_selectedOrderIds.length} điểm)",
                      style: const TextStyle(fontSize: 16, fontWeight: FontWeight.bold),
                    ),
                  ),
                ],
              ),
              content: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    "Hệ thống sẽ tạo chuyến xe Lalamove giao qua ${_selectedOrderIds.length} điểm dừng đã chọn.",
                    style: const TextStyle(fontSize: 13, color: Colors.black87),
                  ),
                  const SizedBox(height: 12),
                  Container(
                    padding: const EdgeInsets.all(10),
                    decoration: BoxDecoration(color: Colors.red.shade50, borderRadius: BorderRadius.circular(8)),
                    child: const Row(
                      children: [
                        Icon(Icons.info_outline, color: Colors.red, size: 18),
                        SizedBox(width: 8),
                        Expanded(
                          child: Text(
                            "Tự động đồng bộ mã tracking & trạng thái chuyến về ứng dụng.",
                            style: TextStyle(fontSize: 11, color: Colors.red, fontWeight: FontWeight.w600),
                          ),
                        ),
                      ],
                    ),
                  ),
                ],
              ),
              actions: [
                TextButton(
                  onPressed: () => Navigator.pop(ctx),
                  child: const Text("Hủy bỏ"),
                ),
                ElevatedButton.icon(
                  style: ElevatedButton.styleFrom(backgroundColor: Colors.red.shade600, foregroundColor: Colors.white),
                  icon: const Icon(Icons.flash_on, size: 16),
                  label: const Text("⚡ Tạo chuyến Lalamove"),
                  onPressed: () async {
                    Navigator.pop(ctx);
                    setState(() => _isLoading = true);
                    try {
                      final res = await ApiService.bookLalamoveGroupOrders(_selectedOrderIds.toList());
                      if (mounted) {
                        ScaffoldMessenger.of(context).showSnackBar(
                          SnackBar(
                            content: Text(res['message'] ?? '✔ Đã đặt Lalamove ghép thành công!'),
                            backgroundColor: Colors.green,
                          ),
                        );
                        _selectedOrderIds.clear();
                        _fetchOrders();
                      }
                    } catch (e) {
                      if (mounted) {
                        setState(() => _isLoading = false);
                        ScaffoldMessenger.of(context).showSnackBar(
                          SnackBar(
                            content: Text("✕ Lỗi đặt Lalamove: ${e.toString().replaceAll('Exception: ', '')}"),
                            backgroundColor: Colors.red,
                          ),
                        );
                      }
                    }
                  },
                ),
              ],
            );
          },
        );
      },
    );
  }

  Future<void> _openChatForConversation(int convId, String customerName) async {
    try {
      final response = await http.get(
        Uri.parse("${ApiService.formattedBaseUrl}/api/v1/mobile/conversations/$convId"),
        headers: ApiService.headers,
      );
      if (response.statusCode == 200) {
        final conv = ConversationModel.fromJson(jsonDecode(response.body));
        if (mounted) {
          Navigator.push(
            context,
            MaterialPageRoute(
              builder: (_) => ChatDetailScreen(conversation: conv, staffName: "Nhân viên"),
            ),
          );
        }
        return;
      }
    } catch (_) {}

    final conv = ConversationModel(
      id: convId,
      channel: 0,
      channelName: "Zalo / Messenger",
      externalId: "",
      customerName: customerName,
      customerPhone: "",
      customerAddress: "",
      avatarUrl: null,
      unreadCount: 0,
      lastMessagePreview: "",
      lastMessageAt: DateTime.now(),
    );

    if (mounted) {
      Navigator.push(
        context,
        MaterialPageRoute(
          builder: (_) => ChatDetailScreen(conversation: conv, staffName: "Nhân viên"),
        ),
      );
    }
  }
}
