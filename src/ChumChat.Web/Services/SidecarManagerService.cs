using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ChumChat.Web.Channels;

namespace ChumChat.Web.Services;

public class SidecarManagerService : IHostedService, IDisposable
{
    private readonly ChannelSettingsStore _store;
    private readonly ILogger<SidecarManagerService> _logger;
    private readonly IHostEnvironment _env;

    private Process? _zaloProcess;
    private Process? _messengerProcess;
    private bool _disposed;

    // TODO: Ideally we get the actual address the app is listening on.
    // For development/default, we assume http://localhost:5000
    private const string ChumChatUrl = "http://localhost:5000";

    public SidecarManagerService(ChannelSettingsStore store, ILogger<SidecarManagerService> logger, IHostEnvironment env)
    {
        _store = store;
        _logger = logger;
        _env = env;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("SidecarManager: Đang kiểm tra cấu hình để khởi động các sidecar ngầm...");

        var zaloOpts = _store.ZaloPersonal;
        if (!string.IsNullOrEmpty(zaloOpts.ApiKey))
        {
            _ = StartZaloSidecarAsync(zaloOpts);
        }

        var messengerOpts = _store.MessengerPersonal;
        if (!string.IsNullOrEmpty(messengerOpts.ApiKey))
        {
            _ = StartMessengerSidecarAsync(messengerOpts);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("SidecarManager: Đang tắt các tiến trình Node.js...");
        StopProcess(ref _zaloProcess);
        StopProcess(ref _messengerProcess);
        return Task.CompletedTask;
    }

    public async Task EnsureZaloSidecarRunningAsync()
    {
        var opts = _store.ZaloPersonal;
        
        // Nếu chưa có ApiKey (mới tạo), tự động sinh 1 chuỗi bí mật
        if (string.IsNullOrEmpty(opts.ApiKey))
        {
            opts.ApiKey = Guid.NewGuid().ToString("N");
        }

        // Tự động tìm port rảnh nếu URL đang trống hoặc không hợp lệ
        if (string.IsNullOrEmpty(opts.SidecarUrl) || !Uri.TryCreate(opts.SidecarUrl, UriKind.Absolute, out _))
        {
            var port = GetAvailablePort();
            opts.SidecarUrl = $"http://localhost:{port}";
        }

        await _store.SaveZaloPersonalAsync(opts);

        if (_zaloProcess == null || _zaloProcess.HasExited)
        {
            await StartZaloSidecarAsync(opts);
        }
    }

    public async Task StopZaloSidecarAsync()
    {
        StopProcess(ref _zaloProcess);
        var opts = _store.ZaloPersonal;
        opts.ApiKey = ""; // Xóa cấu hình để nó không tự chạy lại
        opts.SidecarUrl = "";
        await _store.SaveZaloPersonalAsync(opts);

        try
        {
            var sidecarDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sidecars", "zalo-personal");
            var credFile = Path.Combine(sidecarDir, "credentials.json");
            var qrFile = Path.Combine(sidecarDir, "qr.png");
            if (File.Exists(credFile)) File.Delete(credFile);
            if (File.Exists(qrFile)) File.Delete(qrFile);
        }
        catch { /* ignore */ }
    }

    public async Task EnsureMessengerSidecarRunningAsync()
    {
        var opts = _store.MessengerPersonal;

        if (string.IsNullOrEmpty(opts.ApiKey))
        {
            opts.ApiKey = Guid.NewGuid().ToString("N");
        }

        if (string.IsNullOrEmpty(opts.SidecarUrl) || !Uri.TryCreate(opts.SidecarUrl, UriKind.Absolute, out _))
        {
            var port = GetAvailablePort();
            opts.SidecarUrl = $"http://localhost:{port}";
        }

        await _store.SaveMessengerPersonalAsync(opts);

        if (_messengerProcess == null || _messengerProcess.HasExited)
        {
            await StartMessengerSidecarAsync(opts);
        }
    }

    public async Task StopMessengerSidecarAsync()
    {
        StopProcess(ref _messengerProcess);
        var opts = _store.MessengerPersonal;
        opts.ApiKey = "";
        opts.AppState = "";
        opts.SidecarUrl = "";
        await _store.SaveMessengerPersonalAsync(opts);
    }

    private string GetSidecarDirectory(string name)
    {
        // 1. Dùng cho Production (thư mục sidecars nằm cùng cấp với file chạy exe/dll sau khi publish)
        var prodPath = Path.Combine(_env.ContentRootPath, "sidecars", name);
        if (Directory.Exists(prodPath))
            return prodPath;

        // 2. Dùng cho Development (khi chạy debug trong VS/Rider/CLI)
        return Path.Combine(_env.ContentRootPath, "..", "..", "sidecars", name);
    }

    private async Task StartZaloSidecarAsync(ZaloPersonalOptions opts)
    {
        StopProcess(ref _zaloProcess);
        var uri = new Uri(opts.SidecarUrl);
        var directory = GetSidecarDirectory("zalo-personal");
        _zaloProcess = await StartNodeProcessAsync("zalo-personal", directory, uri.Port, opts.ApiKey);
    }

    private async Task StartMessengerSidecarAsync(MessengerPersonalOptions opts)
    {
        StopProcess(ref _messengerProcess);
        var uri = new Uri(opts.SidecarUrl);
        var directory = GetSidecarDirectory("messenger-personal");
        _messengerProcess = await StartNodeProcessAsync("messenger-personal", directory, uri.Port, opts.ApiKey);
    }

    private async Task<Process?> StartNodeProcessAsync(string name, string directory, int port, string apiKey)
    {
        var dirInfo = new DirectoryInfo(directory);
        if (!dirInfo.Exists)
        {
            _logger.LogError($"Thư mục sidecar {name} không tồn tại tại {dirInfo.FullName}");
            return null;
        }

        // Tự động chạy npm install nếu thư mục node_modules chưa tồn tại
        if (!Directory.Exists(Path.Combine(dirInfo.FullName, "node_modules")))
        {
            _logger.LogInformation($"[SidecarManager] Đang chạy npm install cho {name} lần đầu...");
            try
            {
                var npmProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = OperatingSystem.IsWindows() ? "npm.cmd" : "npm",
                        Arguments = "install",
                        WorkingDirectory = dirInfo.FullName,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                npmProcess.Start();
                await npmProcess.WaitForExitAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Không thể chạy npm install cho {name}");
            }
        }

        string nodeCmd = "node";
        if (!OperatingSystem.IsWindows())
        {
            var commonPaths = new[] { "/usr/bin/node", "/usr/local/bin/node", "/root/.nvm/versions/node", "/home/chumchat/.nvm/versions/node" };
            foreach (var p in commonPaths)
            {
                if (File.Exists(p)) { nodeCmd = p; break; }
            }
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = nodeCmd,
            Arguments = "index.js",
            WorkingDirectory = dirInfo.FullName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.EnvironmentVariables["PORT"] = port.ToString();
        startInfo.EnvironmentVariables["API_KEY"] = apiKey;
        startInfo.EnvironmentVariables["CHUMCHAT_URL"] = ChumChatUrl;

        var process = new Process { StartInfo = startInfo };
        
        process.OutputDataReceived += (sender, e) => { if (e.Data != null) _logger.LogInformation($"[{name}] {e.Data}"); };
        process.ErrorDataReceived += (sender, e) => { if (e.Data != null) _logger.LogError($"[{name}] {e.Data}"); };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _logger.LogInformation($"Đã khởi động sidecar {name} tại cổng {port} (PID: {process.Id})");
            return process;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Lỗi khi khởi động sidecar {name} với lệnh '{nodeCmd}'. Hãy đảm bảo Node.js đã được cài đặt.");
            return null;
        }
    }

    private void StopProcess(ref Process? process)
    {
        if (process != null && !process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch { /* ignore */ }
        }
        process?.Dispose();
        process = null;
    }

    private int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            StopProcess(ref _zaloProcess);
            StopProcess(ref _messengerProcess);
            _disposed = true;
        }
    }
}
