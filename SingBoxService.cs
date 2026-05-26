using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Threading;

namespace SingBoxTrayApp;

public class ProxyNode
{
    public string Name { get; set; } = "";
    public int Delay { get; set; }
}

public class SingBoxService
{
    public static SingBoxService Instance { get; } = new SingBoxService();

    // volatile ensures visibility across threads; _processLock guards mutations
    private volatile Process? singBoxProcess;
    private readonly object _processLock = new();

    // High-performance log batching dispatch fields
    private readonly List<string> _pendingLogs = new();
    private bool _isLogDispatchPending = false;
    private readonly object _logLock = new();

    private readonly HttpClient httpClient;
    private readonly HttpClient latencyClient; // dedicated client for latency tests
    private readonly SemaphoreSlim _latencySemaphore = new(8); // limit concurrent latency tests
    private readonly string baseDir;
    private CancellationTokenSource? _statusCts;

    public string ConfigPath { get; }
    public string SingboxExe { get; }
    public string DefaultSubUrl { get; } = "https://sing.2005666.xyz";

    public string ActiveNodeName { get; private set; } = "未连接";
    public List<ProxyNode> ProxyNodes { get; private set; } = new();
    public Queue<string> LogBuffer { get; } = new(); // Queue for O(1) Enqueue/Dequeue

    public event Action? StatusChanged;
    public event Action<List<string>>? LogReceived;
    public DateTime? LastSyncTime { get; private set; }

    private SingBoxService()
    {
        baseDir = AppDomain.CurrentDomain.BaseDirectory;
        ConfigPath = Path.Combine(baseDir, "config.json");
        SingboxExe = ResolveSingboxExe(baseDir);

        httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        httpClient.DefaultRequestHeaders.Add("User-Agent", "sing-box");

        // Separate client for latency tests: per-request timeout of 5s
        // avoids global timeout contention with the main API client
        latencyClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        latencyClient.DefaultRequestHeaders.Add("User-Agent", "sing-box");

        CheckAndAttachProcess();
        StartStatusTimer();

        // Load last sync time from file if it exists
        try
        {
            string syncTimeFile = Path.Combine(baseDir, "sync_time.txt");
            if (File.Exists(syncTimeFile))
            {
                if (DateTime.TryParse(File.ReadAllText(syncTimeFile), out var lastSync))
                {
                    LastSyncTime = lastSync;
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Asynchronous, non-blocking dispatch to the WPF UI thread.
    /// Safely handles application shutdown scenarios.
    /// </summary>
    private void SafeBeginInvoke(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app != null && !app.Dispatcher.HasShutdownStarted)
        {
            app.Dispatcher.BeginInvoke(action);
        }
    }

    private void StartStatusTimer()
    {
        _statusCts = new CancellationTokenSource();
        var token = _statusCts.Token;

        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                if (singBoxProcess != null) await FetchStatusFromApiAsync();
                try { await Task.Delay(5000, token); }
                catch (TaskCanceledException) { break; }
            }
        }, token);
    }

    private void AppendLog(string message)
    {
        lock (LogBuffer)
        {
            LogBuffer.Enqueue(message);
            if (LogBuffer.Count > 300) LogBuffer.Dequeue(); // O(1) FIFO
        }

        lock (_logLock)
        {
            _pendingLogs.Add(message);
            if (_isLogDispatchPending) return;
            _isLogDispatchPending = true;
        }

        SafeBeginInvoke(() => {
            List<string> logsToDispatch;
            lock (_logLock)
            {
                logsToDispatch = new List<string>(_pendingLogs);
                _pendingLogs.Clear();
                _isLogDispatchPending = false;
            }
            LogReceived?.Invoke(logsToDispatch);
        });
    }

    private static string ResolveSingboxExe(string baseDir)
    {
        var rootExe = Path.Combine(baseDir, "sing-box.exe");
        if (File.Exists(rootExe)) return rootExe;

        var bundledExe = Path.Combine(baseDir, "sing-box-1.13.12-windows-amd64", "sing-box.exe");
        if (File.Exists(bundledExe)) return bundledExe;

        return rootExe;
    }

    public async Task FetchStatusFromApiAsync()
    {
        try
        {
            string json = await httpClient.GetStringAsync("http://127.0.0.1:9090/proxies");
            if (!string.IsNullOrEmpty(json))
            {
                ParseProxies(json, out string active, out List<ProxyNode> nodes);
                if (nodes.Count > 0)
                {
                    ActiveNodeName = active;
                    ProxyNodes = nodes;
                    SafeBeginInvoke(() => StatusChanged?.Invoke());
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[WARN] 状态获取失败: {ex.Message}");
        }
    }

    public bool IsRunning => singBoxProcess != null;

    public double MemoryUsageMB
    {
        get
        {
            try
            {
                var proc = singBoxProcess; // snapshot volatile field
                if (proc != null && !proc.HasExited)
                {
                    proc.Refresh();
                    return proc.WorkingSet64 / (1024.0 * 1024.0);
                }
                var processes = Process.GetProcessesByName("sing-box");
                if (processes.Length > 0)
                {
                    return processes[0].WorkingSet64 / (1024.0 * 1024.0);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[WARN] 内存读取失败: {ex.Message}");
            }
            return 0;
        }
    }

    public async Task StartAsync()
    {
        if (singBoxProcess != null) return;

        if (!File.Exists(SingboxExe))
        {
            System.Windows.MessageBox.Show($"未找到 {SingboxExe}！", "错误", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return;
        }

        if (!File.Exists(ConfigPath))
        {
            System.Windows.MessageBox.Show($"未找到 {ConfigPath}！请先拉取配置。", "错误", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = SingboxExe,
                Arguments = "run -c \"" + ConfigPath + "\"",
                WorkingDirectory = baseDir,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var process = new Process { StartInfo = startInfo };
            process.EnableRaisingEvents = true;
            
            process.OutputDataReceived += (s, ev) => { 
                if (ev.Data != null) AppendLog($"[INFO] {ev.Data}");
            };
            process.ErrorDataReceived += (s, ev) => { 
                if (ev.Data != null) AppendLog($"[ERROR] {ev.Data}");
            };
            
            process.Exited += (s, ev) =>
            {
                lock (_processLock)
                {
                    singBoxProcess = null;
                }
                SafeBeginInvoke(() => StatusChanged?.Invoke());
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            lock (_processLock)
            {
                singBoxProcess = process;
            }

            SafeBeginInvoke(() => StatusChanged?.Invoke());
            
            await Task.Delay(2000);
            await FetchStatusFromApiAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"启动失败: {ex.Message}", "错误", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    public void Stop()
    {
        try
        {
            lock (_processLock)
            {
                if (singBoxProcess != null)
                {
                    try { singBoxProcess.Kill(); } catch { }
                    singBoxProcess = null;
                }
            }
            
            foreach (var p in Process.GetProcessesByName("sing-box"))
            {
                try { p.Kill(); } catch { }
            }
            
            ActiveNodeName = "未连接";
            ProxyNodes.Clear();
            SafeBeginInvoke(() => StatusChanged?.Invoke());
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] 停止内核时异常: {ex.Message}");
        }
    }

    public async Task UpdateConfigAsync()
    {
        string url = DefaultSubUrl.Trim();
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;

        if (!url.EndsWith("/f") && !url.Contains("?")) url = url.TrimEnd('/') + "/f";

        try
        {
            // Use a separate HttpClient with longer timeout for config download
            using var dlClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            dlClient.DefaultRequestHeaders.Add("User-Agent", "sing-box");
            string configJson = await dlClient.GetStringAsync(url);
            if (configJson.Trim().StartsWith("{"))
            {
                // Auto-optimize rule_sets to avoid bootstrap lock and bypass GFW
                configJson = configJson.Replace("\"download_detour\": \"proxy\"", "\"download_detour\": \"direct\"");
                configJson = configJson.Replace("fastly.jsdelivr.net", "testingcf.jsdelivr.net");
                configJson = configJson.Replace("cdn.jsdelivr.net", "testingcf.jsdelivr.net");

                File.WriteAllText(ConfigPath, configJson);

                // Save and persist last sync time
                LastSyncTime = DateTime.Now;
                try
                {
                    File.WriteAllText(Path.Combine(baseDir, "sync_time.txt"), LastSyncTime.Value.ToString("o"));
                }
                catch { }

                System.Windows.MessageBox.Show("配置拉取成功！", "成功", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                if (singBoxProcess != null)
                {
                    Stop();
                    await Task.Delay(500);
                    await StartAsync();
                }
            }
            else
            {
                System.Windows.MessageBox.Show("拉取失败：返回内容不符合 JSON 规范。", "错误", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"更新失败: {ex.Message}", "错误", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    public async Task SwitchNodeAsync(string nodeName)
    {
        try
        {
            string url = "http://127.0.0.1:9090/proxies/proxy";
            var content = new StringContent($"{{\"name\":\"{nodeName}\"}}", System.Text.Encoding.UTF8, "application/json");
            var response = await httpClient.PutAsync(url, content);
            
            if (response.IsSuccessStatusCode)
            {
                ActiveNodeName = nodeName;
                SafeBeginInvoke(() => StatusChanged?.Invoke());
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[WARN] 切换节点失败: {ex.Message}");
        }
    }

    public async Task RunLatencyTestsAsync()
    {
        if (singBoxProcess == null || ProxyNodes.Count == 0) return;
        
        var tasks = new List<Task>();
        foreach (var node in ProxyNodes)
        {
            if (node.Name == "auto" || node.Name == "DIRECT" || node.Name == "REJECT") continue;
            
            var currentNode = node;
            tasks.Add(Task.Run(async () =>
            {
                // SemaphoreSlim limits concurrency to prevent request storms
                await _latencySemaphore.WaitAsync();
                try
                {
                    string testUrl = $"http://127.0.0.1:9090/proxies/{Uri.EscapeDataString(currentNode.Name)}/delay?timeout=3000&url=http://www.gstatic.com/generate_204";
                    string res = await latencyClient.GetStringAsync(testUrl);
                    using var doc = JsonDocument.Parse(res);
                    if (doc.RootElement.TryGetProperty("delay", out var delayEl))
                        currentNode.Delay = delayEl.GetInt32();
                    else
                        currentNode.Delay = -2;
                }
                catch { currentNode.Delay = -2; }
                finally { _latencySemaphore.Release(); }
            }));
        }

        await Task.WhenAll(tasks);
        SafeBeginInvoke(() => StatusChanged?.Invoke());
    }

    private void CheckAndAttachProcess()
    {
        var processes = Process.GetProcessesByName("sing-box");
        if (processes.Length > 0)
        {
            var proc = processes[0];
            lock (_processLock)
            {
                singBoxProcess = proc;
            }
            proc.EnableRaisingEvents = true;
            proc.Exited += (s, e) =>
            {
                lock (_processLock)
                {
                    singBoxProcess = null;
                }
                SafeBeginInvoke(() => StatusChanged?.Invoke());
            };
            _ = FetchStatusFromApiAsync();
        }
    }

    /// <summary>
    /// Clean shutdown: cancel background timers and release resources.
    /// Must be called before Application.Shutdown().
    /// </summary>
    public void Shutdown()
    {
        _statusCts?.Cancel();
        _statusCts?.Dispose();
        _latencySemaphore.Dispose();
        httpClient.Dispose();
        latencyClient.Dispose();
    }

    /// <summary>
    /// Parse the Clash API /proxies response using System.Text.Json for reliability.
    /// The response format is: {"proxies": {"proxy": {"now": "...", "all": [...], ...}, "nodeName": {"history": [{"delay": N}], ...}, ...}}
    /// </summary>
    private void ParseProxies(string json, out string activeNode, out List<ProxyNode> nodes)
    {
        activeNode = "";
        nodes = new List<ProxyNode>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // The Clash API wraps everything under "proxies"
            JsonElement proxiesObj;
            if (!root.TryGetProperty("proxies", out proxiesObj))
            {
                // Maybe it's flat (some versions)
                proxiesObj = root;
            }

            // Find the selector named "proxy" 
            JsonElement selectorEl;
            if (!proxiesObj.TryGetProperty("proxy", out selectorEl))
            {
                // Log for debugging
                File.WriteAllText(Path.Combine(baseDir, "parse_debug.log"), 
                    "No 'proxy' key found. Available keys: " + string.Join(", ", EnumerateKeys(proxiesObj)));
                return;
            }

            // Get current active node
            if (selectorEl.TryGetProperty("now", out var nowEl))
                activeNode = nowEl.GetString() ?? "";

            // Get all node names from the selector
            if (selectorEl.TryGetProperty("all", out var allEl) && allEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in allEl.EnumerateArray())
                {
                    string? name = item.GetString();
                    if (string.IsNullOrEmpty(name) || name == "DIRECT" || name == "REJECT") continue;

                    int delay = -1;

                    // Try to get delay from individual proxy history
                    if (proxiesObj.TryGetProperty(name, out var nodeEl))
                    {
                        if (nodeEl.TryGetProperty("history", out var historyEl) && 
                            historyEl.ValueKind == JsonValueKind.Array)
                        {
                            JsonElement lastHistory = default;
                            foreach (var h in historyEl.EnumerateArray())
                                lastHistory = h;
                            
                            if (lastHistory.ValueKind == JsonValueKind.Object && 
                                lastHistory.TryGetProperty("delay", out var delayEl))
                            {
                                delay = delayEl.GetInt32();
                            }
                        }
                    }

                    nodes.Add(new ProxyNode { Name = name, Delay = delay });
                }
            }
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(baseDir, "parse_error.log"), ex.ToString());
        }
    }

    private static IEnumerable<string> EnumerateKeys(JsonElement obj)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in obj.EnumerateObject())
                yield return prop.Name;
        }
    }
}
