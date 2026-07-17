using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChumChat.Web.Services;

public class AhaMoveOptions
{
    public string Mobile { get; set; } = "84949597688";
    public string ApiKey { get; set; } = "c1f70cb324987bdd894a63056fb96baf1493d762";
    public string Token { get; set; } = "";
    
    // Sender Information
    public string SenderName { get; set; } = "CÔNG TY TNHH SẢN XUẤT VÀ THƯƠNG MẠI CHUM CHUM";
    public string SenderMobile { get; set; } = "0949597688";
    public string SenderAddress { get; set; } = "7/28 Thành Thái, Phường 14, Quận 10, Thành phố Hồ Chí Minh";
    public double SenderLat { get; set; } = 10.76975346;
    public double SenderLng { get; set; } = 106.6636615;
}

public class AhaMoveService
{
    private readonly HttpClient _http;
    private readonly ILogger<AhaMoveService> _logger;

    public AhaMoveService(HttpClient http, ILogger<AhaMoveService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<string> AuthenticateAsync(string mobile, string apiKey, string name)
    {
        var url = $"https://apistg.ahamove.com/v1/partner/register_account?api_key={apiKey}&mobile={mobile}&name={Uri.EscapeDataString(name)}";
        var response = await _http.GetAsync(url);

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        return result.GetProperty("token").GetString() ?? throw new Exception("Token is null");
    }

    public async Task<long> EstimateOrderFeeAsync(AhaMoveOrderRequest request, string token)
    {
        var requestMsg = new HttpRequestMessage(HttpMethod.Post, "https://partner-apistg.ahamove.com/v3/orders/estimates")
        {
            Content = JsonContent.Create(request)
        };
        requestMsg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await _http.SendAsync(requestMsg);
        
        var contentStr = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("AhaMove Estimate Fee failed: {Status} {Response}", response.StatusCode, contentStr);
            throw new Exception($"Lỗi ước tính phí AhaMove: {contentStr}");
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (result.TryGetProperty("total_fee", out var totalFee))
        {
            return totalFee.GetInt64();
        }
        throw new Exception("Không tìm thấy total_fee trong phản hồi của AhaMove.");
    }

    public async Task<AhaMoveOrderResponse> CreateOrderAsync(AhaMoveOrderRequest request, string token)
    {
        var requestMsg = new HttpRequestMessage(HttpMethod.Post, "https://partner-apistg.ahamove.com/v3/orders")
        {
            Content = JsonContent.Create(request)
        };
        requestMsg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await _http.SendAsync(requestMsg);
        
        var contentStr = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("AhaMove Create Order failed: {Status} {Response}", response.StatusCode, contentStr);
            throw new Exception($"Ahamove error: {contentStr}");
        }

        var result = JsonSerializer.Deserialize<AhaMoveOrderResponse>(contentStr, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return result ?? throw new Exception("Failed to parse AhaMove response");
    }

    public async Task<(double Lat, double Lng)> GeocodeAddressAsync(string address)
    {
        try
        {
            // Support parsing Google Maps URL directly
            if (address.Contains("maps.app.goo.gl") || address.Contains("google.com/maps"))
            {
                var request = new HttpRequestMessage(HttpMethod.Get, address);
                // Don't follow redirects automatically to capture the Location header
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
                    // Regex to extract 3d and 4d (exact pin location)
                    var matchPin = System.Text.RegularExpressions.Regex.Match(location, @"3d(-?\d+\.\d+)!4d(-?\d+\.\d+)");
                    if (matchPin.Success)
                    {
                        return (double.Parse(matchPin.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), double.Parse(matchPin.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
                    }
                    
                    // Regex to extract @lat,lng
                    var matchAt = System.Text.RegularExpressions.Regex.Match(location, @"@(-?\d+\.\d+),(-?\d+\.\d+)");
                    if (matchAt.Success)
                    {
                        return (double.Parse(matchAt.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), double.Parse(matchAt.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
            }

            // Simple Nominatim integration as a fallback for geocoding
            var url = $"https://nominatim.openstreetmap.org/search?q={Uri.EscapeDataString(address)}&format=json&limit=1";
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
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to geocode address: {Address}", address);
        }

        // Return a default coordinate in HCM if failed (center of HCM)
        return (10.762622, 106.660172);
    }
}

public class AhaMoveOrderRequest
{
    [JsonPropertyName("order_time")]
    public int OrderTime { get; set; } = 0;

    [JsonPropertyName("path")]
    public List<AhaMovePath> Path { get; set; } = new();

    [JsonPropertyName("group_service_id")]
    public string GroupServiceId { get; set; } = "BIKE"; // AhaMove will automatically choose SGN-BIKE or HAN-BIKE

    [JsonPropertyName("payment_method")]
    public string PaymentMethod { get; set; } = "CASH"; // Sender or Receiver pays in cash

    [JsonPropertyName("remarks")]
    public string Remarks { get; set; } = "";

    [JsonPropertyName("items")]
    public List<AhaMoveItem> Items { get; set; } = new();
}

public class AhaMovePath
{
    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lng")]
    public double Lng { get; set; }

    [JsonPropertyName("address")]
    public string Address { get; set; } = "";

    [JsonPropertyName("short_address")]
    public string ShortAddress { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("mobile")]
    public string Mobile { get; set; } = "";

    [JsonPropertyName("remarks")]
    public string Remarks { get; set; } = "";

    [JsonPropertyName("cod")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long Cod { get; set; } // Cash on delivery
}

public class AhaMoveItem
{
    [JsonPropertyName("_id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("num")]
    public int Num { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("price")]
    public long Price { get; set; }
}

public class AhaMoveOrderResponse
{
    [JsonPropertyName("order_id")]
    public string OrderId { get; set; } = "";

    [JsonPropertyName("shared_link")]
    public string SharedLink { get; set; } = "";
}
