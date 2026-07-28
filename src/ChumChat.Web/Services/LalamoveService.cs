using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChumChat.Web.Channels;

namespace ChumChat.Web.Services;

public class LalamoveService
{
    private readonly HttpClient _http;
    private readonly ILogger<LalamoveService> _logger;

    public LalamoveService(HttpClient http, ILogger<LalamoveService> logger)
    {
        _http = http;
        _logger = logger;
    }

    private string GetBaseUrl(bool isSandbox)
    {
        return isSandbox ? "https://rest.sandbox.lalamove.com" : "https://rest.lalamove.com";
    }

    private string ComputeSignature(string apiSecret, string timestamp, string method, string path, string body)
    {
        var raw = $"{timestamp}\r\n{method}\r\n{path}\r\n\r\n{body}";
        var keyBytes = Encoding.UTF8.GetBytes(apiSecret);
        var payloadBytes = Encoding.UTF8.GetBytes(raw);

        using var hmac = new HMACSHA256(keyBytes);
        var hashBytes = hmac.ComputeHash(payloadBytes);
        
        var sb = new StringBuilder();
        foreach (var b in hashBytes)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }

    private HttpRequestMessage CreateRequest(string method, string path, string body, LalamoveOptions opts)
    {
        var baseUrl = GetBaseUrl(opts.IsSandbox);
        var url = baseUrl + path;
        
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var signature = ComputeSignature(opts.ApiSecret, timestamp, method, path, body);

        request.Headers.Add("Market", "VN");
        request.Headers.Add("X-Request-ID", Guid.NewGuid().ToString());
        request.Headers.Add("Authorization", $"hmac {opts.ApiKey}:{timestamp}:{signature}");

        if (!string.IsNullOrEmpty(body))
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return request;
    }

    public async Task<(string QuotationId, long TotalFee, string SenderStopId, string RecipientStopId)> EstimateOrderFeeAsync(
        double senderLat, double senderLng, string senderAddress,
        double recipientLat, double recipientLng, string recipientAddress,
        LalamoveOptions opts)
    {
        var path = "/v3/quotations";
        
        var payload = new
        {
            data = new
            {
                serviceType = opts.ServiceType ?? "MOTORCYCLE",
                language = "vi_VN",
                stops = new[]
                {
                    new
                    {
                        coordinates = new
                        {
                            lat = senderLat.ToString("F6", System.Globalization.CultureInfo.InvariantCulture),
                            lng = senderLng.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)
                        },
                        address = senderAddress
                    },
                    new
                    {
                        coordinates = new
                        {
                            lat = recipientLat.ToString("F6", System.Globalization.CultureInfo.InvariantCulture),
                            lng = recipientLng.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)
                        },
                        address = recipientAddress
                    }
                }
            }
        };

        var body = JsonSerializer.Serialize(payload);
        var request = CreateRequest("POST", path, body, opts);

        var response = await _http.SendAsync(request);
        var contentStr = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Lalamove Estimate Fee failed: {Status} {Response}", response.StatusCode, contentStr);
            throw new Exception($"Lỗi ước tính phí Lalamove: {contentStr}");
        }

        using var doc = JsonDocument.Parse(contentStr);
        var root = doc.RootElement;
        
        if (!root.TryGetProperty("data", out var data))
        {
            throw new Exception("Không tìm thấy trường 'data' trong phản hồi Lalamove");
        }

        var quotationId = data.GetProperty("quotationId").GetString() ?? "";
        
        var stopsArray = data.GetProperty("stops");
        string senderStopId = "";
        string recipientStopId = "";
        
        if (stopsArray.ValueKind == JsonValueKind.Array && stopsArray.GetArrayLength() >= 2)
        {
            var s0 = stopsArray[0];
            if (s0.TryGetProperty("stopId", out var sId0)) senderStopId = sId0.GetString() ?? "";
            else if (s0.TryGetProperty("id", out var id0)) senderStopId = id0.GetString() ?? "";

            var s1 = stopsArray[1];
            if (s1.TryGetProperty("stopId", out var sId1)) recipientStopId = sId1.GetString() ?? "";
            else if (s1.TryGetProperty("id", out var id1)) recipientStopId = id1.GetString() ?? "";
        }

        long totalFee = 0;
        if (data.TryGetProperty("priceBreakdown", out var priceBreakdown) &&
            priceBreakdown.TryGetProperty("total", out var totalProp))
        {
            var totalStr = totalProp.GetString() ?? "0";
            if (double.TryParse(totalStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var feeVal))
            {
                totalFee = (long)Math.Round(feeVal);
            }
        }

        return (quotationId, totalFee, senderStopId, recipientStopId);
    }

    public async Task<(string OrderId, string ShareLink)> CreateOrderAsync(
        string quotationId, 
        string senderStopId, string recipientStopId,
        string senderName, string senderPhone,
        string recipientName, string recipientPhone,
        LalamoveOptions opts)
    {
        var path = "/v3/orders";

        var payload = new
        {
            data = new
            {
                quotationId = quotationId,
                sender = new
                {
                    stopId = string.IsNullOrEmpty(senderStopId) ? "0" : senderStopId,
                    name = senderName,
                    phone = senderPhone.StartsWith("+84") ? senderPhone : "+84" + senderPhone.TrimStart('0')
                },
                recipients = new[]
                {
                    new
                    {
                        stopId = string.IsNullOrEmpty(recipientStopId) ? "1" : recipientStopId,
                        name = recipientName,
                        phone = recipientPhone.StartsWith("+84") ? recipientPhone : "+84" + recipientPhone.TrimStart('0')
                    }
                }
            }
        };

        var body = JsonSerializer.Serialize(payload);
        var request = CreateRequest("POST", path, body, opts);

        var response = await _http.SendAsync(request);
        var contentStr = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Lalamove Create Order failed: {Status} {Response}", response.StatusCode, contentStr);
            throw new Exception($"Lỗi đặt đơn Lalamove: {contentStr}");
        }

        using var doc = JsonDocument.Parse(contentStr);
        var root = doc.RootElement;
        
        if (!root.TryGetProperty("data", out var data))
        {
            throw new Exception("Không tìm thấy trường 'data' trong phản hồi tạo đơn Lalamove");
        }

        var orderId = data.GetProperty("orderId").GetString() ?? "";

        // Thử lấy shareLink (tracking link)
        string shareLink = "";
        try
        {
            // Đợi 500ms trước khi gọi GET để đảm bảo Lalamove đã lưu đơn
            await Task.Delay(500);
            
            var detailPath = $"/v3/orders/{orderId}";
            var detailRequest = CreateRequest("GET", detailPath, "", opts);
            var detailResponse = await _http.SendAsync(detailRequest);
            var detailContentStr = await detailResponse.Content.ReadAsStringAsync();

            if (detailResponse.IsSuccessStatusCode)
            {
                using var detailDoc = JsonDocument.Parse(detailContentStr);
                if (detailDoc.RootElement.TryGetProperty("data", out var detailData) &&
                    detailData.TryGetProperty("shareLink", out var linkProp))
                {
                    shareLink = linkProp.GetString() ?? "";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không lấy được shareLink cho đơn Lalamove {OrderId}", orderId);
        }

        // Fallback tracking URL nếu không lấy được qua API
        if (string.IsNullOrEmpty(shareLink))
        {
            shareLink = opts.IsSandbox 
                ? $"https://web.sandbox.lalamove.com/tracking?order={orderId}" 
                : $"https://web.lalamove.com/tracking?order={orderId}";
        }

        return (orderId, shareLink);
    }

    public async Task<(double Lat, double Lng)> GeocodeAddressAsync(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return (0, 0);

        try
        {
            if (address.Contains("maps.app.goo.gl") || address.Contains("google.com/maps"))
            {
                var request = new HttpRequestMessage(HttpMethod.Get, address);
                var handler = new HttpClientHandler { AllowAutoRedirect = false };
                using var tempClient = new HttpClient(handler);
                
                var response = await tempClient.SendAsync(request);
                var location = response.Headers.Location?.ToString();
                
                if (string.IsNullOrEmpty(location) && response.IsSuccessStatusCode)
                {
                    location = response.RequestMessage?.RequestUri?.ToString();
                }

                if (!string.IsNullOrEmpty(location))
                {
                    var matchPin = System.Text.RegularExpressions.Regex.Match(location, @"3d(-?\d+\.\d+)!4d(-?\d+\.\d+)");
                    if (matchPin.Success)
                    {
                        return (double.Parse(matchPin.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), double.Parse(matchPin.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
                    }
                    
                    var matchAt = System.Text.RegularExpressions.Regex.Match(location, @"@(-?\d+\.\d+),(-?\d+\.\d+)");
                    if (matchAt.Success)
                    {
                        return (double.Parse(matchAt.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), double.Parse(matchAt.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
            }

            var currentQuery = address;
            if (!currentQuery.ToLower().Contains("hà nội") && !currentQuery.ToLower().Contains("ha noi") && !currentQuery.ToLower().Contains("hanoi"))
            {
                currentQuery += ", Hà Nội, Việt Nam";
            }
            while (!string.IsNullOrWhiteSpace(currentQuery))
            {
                var url = $"https://nominatim.openstreetmap.org/search?q={Uri.EscapeDataString(currentQuery)}&format=json&limit=1";
                var requestNom = new HttpRequestMessage(HttpMethod.Get, url);
                requestNom.Headers.UserAgent.ParseAdd("ChumChat/1.0");

                var responseNom = await _http.SendAsync(requestNom);
                if (responseNom.IsSuccessStatusCode)
                {
                    var elements = await responseNom.Content.ReadFromJsonAsync<JsonElement[]>();
                    if (elements != null && elements.Length > 0)
                    {
                        var first = elements[0];
                        if (first.TryGetProperty("lat", out var latStr) && first.TryGetProperty("lon", out var lonStr))
                        {
                            if (double.TryParse(latStr.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var lat) && 
                                double.TryParse(lonStr.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var lon))
                            {
                                return (lat, lon);
                            }
                        }
                    }
                }

                int firstCommaIndex = currentQuery.IndexOf(',');
                if (firstCommaIndex >= 0 && firstCommaIndex < currentQuery.Length - 1)
                {
                    currentQuery = currentQuery.Substring(firstCommaIndex + 1).Trim();
                }
                else
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi geocode địa chỉ");
        }

        return (0, 0);
    }

    public async Task<bool> CancelOrderAsync(string orderId, LalamoveOptions opts)
    {
        if (string.IsNullOrEmpty(orderId)) return false;

        var cleanOrderId = orderId.StartsWith("Lala:") ? orderId[5..] : orderId;

        var path = $"/v3/orders/{cleanOrderId}";
        var request = CreateRequest("DELETE", path, "", opts);

        var response = await _http.SendAsync(request);
        var contentStr = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Lalamove Cancel Order failed: {Status} {Response}", response.StatusCode, contentStr);
            throw new Exception($"Lỗi hủy đơn Lalamove: {contentStr}");
        }

        return true;
    }

    public async Task<(string QuotationId, long TotalFee, string SenderStopId, List<LalamoveMultiStopRecipient> Recipients)> EstimateMultiStopFeeAsync(
        double senderLat, double senderLng, string senderAddress,
        List<LalamoveMultiStopRecipient> recipients,
        LalamoveOptions opts)
    {
        var path = "/v3/quotations";
        var stopsList = new List<object>
        {
            new
            {
                coordinates = new
                {
                    lat = senderLat.ToString("F6", System.Globalization.CultureInfo.InvariantCulture),
                    lng = senderLng.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)
                },
                address = senderAddress
            }
        };

        foreach (var r in recipients)
        {
            stopsList.Add(new
            {
                coordinates = new
                {
                    lat = r.Lat.ToString("F6", System.Globalization.CultureInfo.InvariantCulture),
                    lng = r.Lng.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)
                },
                address = r.Address
            });
        }

        var payload = new
        {
            data = new
            {
                serviceType = opts.ServiceType ?? "MOTORCYCLE",
                language = "vi_VN",
                stops = stopsList.ToArray()
            }
        };

        var body = JsonSerializer.Serialize(payload);
        var request = CreateRequest("POST", path, body, opts);
        var response = await _http.SendAsync(request);
        var contentStr = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Lalamove MultiStop Estimate Fee failed: {Status} {Response}", response.StatusCode, contentStr);
            throw new Exception($"Lỗi tính phí ghép đơn Lalamove: {contentStr}");
        }

        using var doc = JsonDocument.Parse(contentStr);
        var root = doc.RootElement;
        var data = root.GetProperty("data");
        var quotationId = data.GetProperty("quotationId").GetString() ?? "";

        string senderStopId = "";
        var stopsArray = data.GetProperty("stops");
        if (stopsArray.ValueKind == JsonValueKind.Array && stopsArray.GetArrayLength() > 0)
        {
            var s0 = stopsArray[0];
            if (s0.TryGetProperty("id", out var id0)) senderStopId = id0.GetString() ?? "";
            else if (s0.TryGetProperty("stopId", out var sId0)) senderStopId = sId0.GetString() ?? "";

            int idx = 0;
            foreach (var stop in stopsArray.EnumerateArray().Skip(1))
            {
                string rStopId = "";
                if (stop.TryGetProperty("id", out var idProp)) rStopId = idProp.GetString() ?? "";
                else if (stop.TryGetProperty("stopId", out var sIdProp)) rStopId = sIdProp.GetString() ?? "";

                if (idx < recipients.Count)
                {
                    recipients[idx].StopId = rStopId;
                }
                idx++;
            }
        }

        long totalFee = 0;
        if (data.TryGetProperty("priceBreakdown", out var priceBreakdown) &&
            priceBreakdown.TryGetProperty("total", out var totalProp))
        {
            var totalStr = totalProp.GetString() ?? "0";
            if (double.TryParse(totalStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var feeVal))
            {
                totalFee = (long)Math.Round(feeVal);
            }
        }

        return (quotationId, totalFee, senderStopId, recipients);
    }

    public async Task<(string OrderId, string ShareLink)> CreateMultiStopOrderAsync(
        string quotationId,
        string senderStopId,
        string senderName, string senderPhone,
        List<LalamoveMultiStopRecipient> recipients,
        LalamoveOptions opts)
    {
        var path = "/v3/orders";
        var recList = recipients.Select(r => new
        {
            stopId = r.StopId,
            name = string.IsNullOrWhiteSpace(r.Name) ? "Khách hàng" : r.Name,
            phone = r.Phone.StartsWith("+84") ? r.Phone : "+84" + r.Phone.TrimStart('0')
        }).ToArray();

        var payload = new
        {
            data = new
            {
                quotationId = quotationId,
                sender = new
                {
                    stopId = senderStopId,
                    name = senderName,
                    phone = senderPhone.StartsWith("+84") ? senderPhone : "+84" + senderPhone.TrimStart('0')
                },
                recipients = recList
            }
        };

        var body = JsonSerializer.Serialize(payload);
        var request = CreateRequest("POST", path, body, opts);
        var response = await _http.SendAsync(request);
        var contentStr = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Lalamove MultiStop Create Order failed: {Status} {Response}", response.StatusCode, contentStr);
            throw new Exception($"Lỗi đặt chuyến Lalamove ghép: {contentStr}");
        }

        using var doc = JsonDocument.Parse(contentStr);
        var root = doc.RootElement;
        var data = root.GetProperty("data");
        var orderId = data.GetProperty("orderId").GetString() ?? "";

        string shareLink = "";
        try
        {
            await Task.Delay(500);
            var detailPath = $"/v3/orders/{orderId}";
            var detailRequest = CreateRequest("GET", detailPath, "", opts);
            var detailResponse = await _http.SendAsync(detailRequest);
            var detailContentStr = await detailResponse.Content.ReadAsStringAsync();

            if (detailResponse.IsSuccessStatusCode)
            {
                using var detailDoc = JsonDocument.Parse(detailContentStr);
                if (detailDoc.RootElement.TryGetProperty("data", out var dData) &&
                    dData.TryGetProperty("shareLink", out var sl))
                {
                    shareLink = sl.GetString() ?? "";
                }
            }
        }
        catch { }

        return ("Lala:" + orderId, shareLink);
    }
}

public class LalamoveMultiStopRecipient
{
    public int OrderId { get; set; }
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Address { get; set; } = "";
    public double Lat { get; set; }
    public double Lng { get; set; }
    public string StopId { get; set; } = "";
}
