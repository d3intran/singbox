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

    private Process? singBoxProcess;
    private readonly HttpClient httpClient;
    private readonly string baseDir;
    
    public string ConfigPath { get; }
    public string SingboxExe { get; }
    public string DefaultSubUrl { get; } = "https://sing.2005666.xyz";

    public string ActiveNodeName { get; private set; } = "未连接";
    public List<ProxyNode> ProxyNodes { get; private set; } = new();
    public List<string> LogBuffer { get; } = new();

    public event Action? StatusChanged;
    public event Action<string>? LogReceived;

    private SingBoxService()
    {
        baseDir = AppDomain.CurrentDomain.BaseDirectory;
        ConfigPath = Path.Combine(baseDir, "config.json");
        SingboxExe = ResolveSingboxExe(baseDir);
        httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        httpClient.DefaultRequestHeaders.Add("User-Agent", "sing-box");

        CheckAndAttachProcess();
        StartStatusTimer();
    }

    private void StartStatusTimer()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                if (singBoxProcess != null) await FetchStatusFromApiAsync();
                await Task.Delay(5000);
            }
        });
    }

    private void AppendLog(string message)
    {
        lock (LogBuffer)
        {
            LogBuffer.Add(message);
            if (LogBuffer.Count > 300) LogBuffer.RemoveAt(0);
        }
        System.Windows.Application.Current.Dispatcher.Invoke(() => LogReceived?.Invoke(message));
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
                    System.Windows.Application.Current.Dispatcher.Invoke(() => StatusChanged?.Invoke());
                }
            }
        }
        catch { }
    }

    public bool IsRunning => singBoxProcess != null;

    public double MemoryUsageMB
    {
        get
        {
            try
            {
                if (singBoxProcess != null && !singBoxProcess.HasExited)
                {
                    singBoxProcess.Refresh();
                    return singBoxProcess.WorkingSet64 / (1024.0 * 1024.0);
                }
                var processes = Process.GetProcessesByName("sing-box");
                if (processes.Length > 0)
                {
                    return processes[0].WorkingSet64 / (1024.0 * 1024.0);
                }
            }
            catch { }
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

            singBoxProcess = new Process { StartInfo = startInfo };
            singBoxProcess.EnableRaisingEvents = true;
            
            singBoxProcess.OutputDataReceived += (s, ev) => { 
                if (ev.Data != null) AppendLog($"[INFO] {ev.Data}");
            };
            singBoxProcess.ErrorDataReceived += (s, ev) => { 
                if (ev.Data != null) AppendLog($"[ERROR] {ev.Data}");
            };
            
            singBoxProcess.Exited += (s, ev) => {
                singBoxProcess = null;
                System.Windows.Application.Current.Dispatcher.Invoke(() => StatusChanged?.Invoke());
            };

            singBoxProcess.Start();
            singBoxProcess.BeginOutputReadLine();
            singBoxProcess.BeginErrorReadLine();

            System.Windows.Application.Current.Dispatcher.Invoke(() => StatusChanged?.Invoke());
            
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
            if (singBoxProcess != null)
            {
                try { singBoxProcess.Kill(); } catch { }
                singBoxProcess = null;
            }
            
            foreach (var p in Process.GetProcessesByName("sing-box"))
            {
                try { p.Kill(); } catch { }
            }
            
            ActiveNodeName = "未连接";
            ProxyNodes.Clear();
            System.Windows.Application.Current.Dispatcher.Invoke(() => StatusChanged?.Invoke());
        }
        catch { }
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
                System.Windows.Application.Current.Dispatcher.Invoke(() => StatusChanged?.Invoke());
            }
        }
        catch { }
    }

    public async Task RunLatencyTestsAsync()
    {
        if (singBoxProcess == null || ProxyNodes.Count == 0) return;
        
        var tasks = new List<Task>();
        foreach (var node in ProxyNodes)
        {
            if (node.Name == "auto" || node.Name == "DIRECT" || node.Name == "REJECT") continue;
            
            var currentNode = node;
            tasks.Add(Task.Run(async () => {
                string testUrl = $"http://127.0.0.1:9090/proxies/{Uri.EscapeDataString(currentNode.Name)}/delay?timeout=3000&url=http://www.gstatic.com/generate_204";
                try
                {
                    string res = await httpClient.GetStringAsync(testUrl);
                    using var doc = JsonDocument.Parse(res);
                    if (doc.RootElement.TryGetProperty("delay", out var delayEl))
                        currentNode.Delay = delayEl.GetInt32();
                    else
                        currentNode.Delay = -2;
                }
                catch { currentNode.Delay = -2; }
            }));
        }

        await Task.WhenAll(tasks);
        System.Windows.Application.Current.Dispatcher.Invoke(() => StatusChanged?.Invoke());
    }

    private void CheckAndAttachProcess()
    {
        var processes = Process.GetProcessesByName("sing-box");
        if (processes.Length > 0)
        {
            singBoxProcess = processes[0];
            singBoxProcess.EnableRaisingEvents = true;
            singBoxProcess.Exited += (s, e) => {
                singBoxProcess = null;
                System.Windows.Application.Current.Dispatcher.Invoke(() => StatusChanged?.Invoke());
            };
            _ = FetchStatusFromApiAsync();
        }
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
