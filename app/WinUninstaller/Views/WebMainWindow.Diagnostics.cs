using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace WinUninstaller.Views;

public partial class WebMainWindow
{
    private readonly bool _measurePerformance = Environment.GetCommandLineArgs().Contains("--measure-performance");
    private string _performanceAction = "Idle";
    private readonly Dictionary<int, double> _cpuPrevious = new();
    private readonly Stopwatch _cpuClock = Stopwatch.StartNew();
    private double _cpuPreviousWall;
    private DispatcherTimer? _cpuTimer;

    private void StartCpuSampling(CoreWebView2Environment environment)
    {
        if (!_measurePerformance || _cpuTimer is not null) return;
        var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinUninstaller", "Logs", $"performance-{Environment.ProcessId}.jsonl");
        _cpuTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _cpuTimer.Tick += (_, _) =>
        {
            try
            {
                var ids = environment.GetProcessInfos().Select(info => info.ProcessId).Append(Environment.ProcessId).Distinct().ToArray();
                double deltaMs = 0; var errors = new List<string>();
                foreach (var id in ids)
                {
                    try
                    {
                        using var process = Process.GetProcessById(id);
                        var cpu = process.TotalProcessorTime.TotalMilliseconds;
                        if (_cpuPrevious.TryGetValue(id, out var previous)) deltaMs += Math.Max(0, cpu - previous);
                        _cpuPrevious[id] = cpu;
                    }
                    catch (Exception ex) { errors.Add($"{id}:{ex.GetType().Name}"); }
                }
                var wall = _cpuClock.Elapsed.TotalMilliseconds;
                var interval = wall - _cpuPreviousWall;
                if (_cpuPreviousWall > 0 && interval > 0)
                    File.AppendAllText(log, JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow,
                        action = _performanceAction, hostPid = Environment.ProcessId, processIds = ids,
                        intervalMs = interval, cpuMs = deltaMs, oneCorePercent = deltaMs / interval * 100,
                        machinePercent = deltaMs / interval / Environment.ProcessorCount * 100,
                        logicalProcessors = Environment.ProcessorCount, errors }) + Environment.NewLine);
                _cpuPreviousWall = wall;
            }
            catch (Exception ex) { WriteWebLog("CPU sampling failed: " + ex.Message); }
        };
        Closed += (_, _) => _cpuTimer.Stop();
        _cpuTimer.Start();
    }
}
