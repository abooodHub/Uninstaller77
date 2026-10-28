using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using WinUninstaller.Models;
using WinUninstaller.Services;

internal static class Program
{
    [STAThread] private static void Main()
    {
        var application = new Application();
        application.Run(new ValidationWindow());
    }
}

internal sealed class ValidationWindow : Window
{
    private const string UninstallParent = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string ProductKey = UninstallParent + @"\{B1477720-8D55-4E92-8C77-771177100001}_is1";
    private readonly TextBox _output = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Background = new SolidColorBrush(Color.FromRgb(20,24,28)), Foreground = Brushes.WhiteSmoke, BorderThickness = new Thickness(0), FontSize = 13, Padding = new Thickness(16) };
    private readonly List<object> _results = new();
    private readonly string _source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    private readonly string _run = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Uninstaller77Phase10", "Runs", Guid.NewGuid().ToString("N"));
    private readonly List<string> _fixtureRoots = new();
    private string _installed = "";
    private string _startedAt = DateTimeOffset.UtcNow.ToString("O");

    public ValidationWindow()
    {
        Title = "Uninstaller 77 — Isolated Windows VM Validation"; Width = 920; Height = 700;
        Background = new SolidColorBrush(Color.FromRgb(17,22,25)); Foreground = Brushes.WhiteSmoke;
        var layout = new DockPanel { Margin = new Thickness(20) };
        var title = new TextBlock { Text = "Uninstaller 77 · Phase 10 VM Validation", FontSize = 22, Margin = new Thickness(0,0,0,12) };
        DockPanel.SetDock(title, Dock.Top); layout.Children.Add(title);
        var button = new Button { Content = "Run isolated VM installation & engine tests", Padding = new Thickness(20,12,20,12), Margin = new Thickness(0,0,0,12) };
        DockPanel.SetDock(button, Dock.Top); layout.Children.Add(button);
        button.Click += async (_, _) => { button.IsEnabled = false; try { await RunAsync(); } catch (Exception ex) { Log("FATAL: " + ex); } finally { Export(); } };
        layout.Children.Add(_output); Content = layout;
        Log("This runner refuses to modify a physical host. Only its own product and GUID-scoped fixtures are targets.");
        Log("Snapshot prerequisite: Phase10-Before-Install-Tests-2026-10-25 (verified on host before this run).");
    }

    private void Log(string text)
    {
        _output.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\n"); _output.ScrollToEnd();
        if (Directory.Exists(_run)) File.AppendAllText(Path.Combine(_run, "validation.log"), $"[{DateTimeOffset.Now:O}] {text}\n");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private async Task Check(string name, Func<Task> test)
    {
        Log("RUN " + name); var clock = Stopwatch.StartNew();
        try { await test(); _results.Add(new { name, passed = true, elapsedMs = clock.ElapsedMilliseconds }); Log("PASS " + name); }
        catch (Exception ex) { _results.Add(new { name, passed = false, elapsedMs = clock.ElapsedMilliseconds, error = ex.ToString() }); Log("FAIL " + name + ": " + ex.Message); }
        Export();
    }

    private async Task<int> Execute(string exe, params string[] arguments)
    {
        Log("PROCESS " + exe + " " + string.Join(" ", arguments));
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new Exception("Process.Start returned null.");
        Log("PID " + process.Id);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));
        Log("EXIT " + process.ExitCode);
        return process.ExitCode;
    }

    private async Task RunAsync()
    {
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        var manufacturer = bios?.GetValue("SystemManufacturer")?.ToString() ?? "";
        Require(manufacturer.Contains("VMware", StringComparison.OrdinalIgnoreCase), "VMware guest not confirmed. No installation was attempted.");
        Require(Environment.OSVersion.Version.Build < 22000, "Expected the designated Windows 10 guest.");
        Directory.CreateDirectory(_run);
        File.WriteAllText(Path.Combine(_run,"environment.json"), JsonSerializer.Serialize(new { machine = Environment.MachineName,
            os = Environment.OSVersion.ToString(), manufacturer, root = _run, source = _source, process = Environment.ProcessPath,
            snapshot = "Phase10-Before-Install-Tests-2026-10-25" }, new JsonSerializerOptions { WriteIndented = true }));
        Log("GUEST CONFIRMED: " + Environment.MachineName + " · " + Environment.OSVersion + " · " + manufacturer);
        var package = Path.Combine(_run, "Packages"); Directory.CreateDirectory(package);
        foreach (var name in new[] { "Uninstaller77-1.1.0-Setup.exe", "Uninstaller77-1.0.0-Baseline-Setup.exe" })
            File.Copy(Path.Combine(_source, name), Path.Combine(package, name));
        _installed = Path.Combine(_run, "Installed", "Uninstaller 77");
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinUninstaller");
        var sentinelName = "phase10-retention-" + Guid.NewGuid().ToString("N") + ".txt";
        var sentinels = new[] { Path.Combine(data,"Logs",sentinelName), Path.Combine(data,"Quarantine",sentinelName) };
        foreach (var sentinel in sentinels) { Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!); File.WriteAllText(sentinel,"Preserve fixture evidence " + sentinelName); }

        await Check("1. Real per-user baseline installation", async () =>
        {
            Require(await Execute(Path.Combine(package,"Uninstaller77-1.0.0-Baseline-Setup.exe"), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/TASKS=desktopicon", "/DIR="+_installed, "/LOG="+Path.Combine(_run,"install-baseline.log")) == 0, "Baseline setup failed.");
            Require(File.Exists(Path.Combine(_installed,"WinUninstaller.exe")), "Installed EXE missing.");
        });
        await Check("2. Real upgrade to 1.1.0 and Installed Apps registration", async () =>
        {
            Require(await Execute(Path.Combine(package,"Uninstaller77-1.1.0-Setup.exe"), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG="+Path.Combine(_run,"upgrade.log")) == 0,"Upgrade failed.");
            using var key = Registry.CurrentUser.OpenSubKey(ProductKey);
            Require(key is not null && key.GetValue("DisplayName")?.ToString() == "Uninstaller 77" && key.GetValue("DisplayVersion")?.ToString() == "1.1.0", "Installed Apps identity/version incorrect.");
            Require(FileVersionInfo.GetVersionInfo(Path.Combine(_installed,"WinUninstaller.exe")).FileVersion == "1.1.0.0", "EXE version did not upgrade.");
            File.WriteAllText(Path.Combine(_run,"installed-apps.json"), JsonSerializer.Serialize(key!.GetValueNames().ToDictionary(name=>name,name=>key.GetValue(name)), new JsonSerializerOptions { WriteIndented=true }));
            Require(sentinels.All(File.Exists),"Data retention sentinel lost during upgrade.");
        });
        await Check("3. Start Menu shortcut launch", () => LaunchShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"Uninstaller 77.lnk")));
        await Check("4. Desktop shortcut launch", () => LaunchShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"Uninstaller 77.lnk")));
        await Check("5. Full installed engine regression suite", async () =>
        {
            var code = await Execute(Path.Combine(_installed,"WinUninstaller.exe"), "--run-tests");
            var log = Path.Combine(data,"Logs","test_results.log");
            if (File.Exists(log)) File.Copy(log,Path.Combine(_run,"engine-tests.log"),true);
            Require(code == 0,"Engine test suite returned " + code + ". See engine-tests.log.");
        });
        await Check("6. Three real fixture uninstallers run sequentially, including early-exit child", async () =>
        {
            var apps = new[] { await InstallFixture("success"), await InstallFixture("child"), await InstallFixture("success") };
            var engine = new UninstallService();
            var result = await BatchUninstallCoordinator.RunAsync(apps, app => engine.UninstallAsync(app), () => false);
            Require(result.Successful == 3 && !result.NeedsUserAttention,"Sequential removal result incorrect.");
            var logs = apps.Select(app => File.ReadAllLines(Path.Combine(app.InstallLocation,"execution.jsonl")).Select(line=>JsonDocument.Parse(line)).ToArray()).ToArray();
            for(var i=0;i<2;i++)
            {
                var end = logs[i].Last(row=>row.RootElement.GetProperty("stage").GetString()=="worker-exit").RootElement.GetProperty("timestamp").GetDateTimeOffset();
                var next = logs[i+1].First(row=>row.RootElement.GetProperty("stage").GetString() is "launcher-start" or "worker-start").RootElement.GetProperty("timestamp").GetDateTimeOffset();
                Require(next >= end,"Next uninstaller overlapped an active worker.");
            }
            Require(apps.All(app=>!Registered(app) && File.Exists(Path.Combine(app.InstallLocation,"leftover.txt"))),"Registration/remnant evidence incorrect.");
            File.WriteAllText(Path.Combine(_run,"sequential-results.json"),JsonSerializer.Serialize(result));
        });
        await Check("7. Real failure exit 1603 never becomes success", async () =>
        {
            var app = await InstallFixture("failure");
            var result = await new UninstallService().UninstallAsync(app);
            Require(!result.Success && result.ExitCode == 1603 && Registered(app),"Failure was lost or registration removed.");
            File.WriteAllText(Path.Combine(_run,"failure-result.json"),JsonSerializer.Serialize(result));
        });
        await Check("8. Stop queue waits for current real uninstaller; pending fixtures remain installed", async () =>
        {
            var apps = new[] { await InstallFixture("child"), await InstallFixture("success"), await InstallFixture("success") };
            var stop = false;
            var engine = new UninstallService();
            var result = await BatchUninstallCoordinator.RunAsync(apps, async app =>
            { stop = true; return await engine.UninstallAsync(app); }, () => stop);
            Require(result.Stopped && result.Successful == 1 && result.Skipped == 2 && Registered(apps[1]) && Registered(apps[2]),"Stop allowed pending work to start.");
            File.WriteAllText(Path.Combine(_run,"stop-result.json"),JsonSerializer.Serialize(result));
        });
        await Check("9. Delayed completion pauses queue and read-only retry resolves evidence", async () =>
        {
            var delayed = await InstallFixture("delayed"); var pending = await InstallFixture("success");
            var engine = new UninstallService(TimeSpan.FromMilliseconds(500)); var calls = 0;
            var result = await BatchUninstallCoordinator.RunAsync(new[]{delayed,pending}, app=>{ calls++; return engine.UninstallAsync(app); },()=>false);
            Require(calls==1 && result.NeedsUserAttention && result.Skipped==1 && Registered(pending),"Uncertain completion advanced the batch.");
            await Task.Delay(4500);
            var verified = await engine.RetryVerificationAsync(delayed);
            Require(verified.Success,"Delayed operation could not be verified after its actual completion.");
            File.WriteAllText(Path.Combine(_run,"uncertain-result.json"),JsonSerializer.Serialize(new { result, verified }));
        });
        await Check("10. Product uninstallation preserves user data", async () =>
        {
            Require(await Execute(Path.Combine(_installed,"unins000.exe"),"/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART","/LOG="+Path.Combine(_run,"product-uninstall.log"))==0,"Product uninstall failed.");
            await Task.Delay(1000);
            using var key=Registry.CurrentUser.OpenSubKey(ProductKey);
            Require(key is null && !File.Exists(Path.Combine(_installed,"WinUninstaller.exe")),"Product uninstall left registration or main executable.");
            Require(sentinels.All(File.Exists),"Product uninstall deleted retained profile data.");
        });
        await Check("11. Clean reinstall and provision UI-only fixture targets", async () =>
        {
            Require(await Execute(Path.Combine(package,"Uninstaller77-1.1.0-Setup.exe"),"/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART","/TASKS=desktopicon","/DIR="+_installed,"/LOG="+Path.Combine(_run,"reinstall.log"))==0,"Reinstall failed.");
            foreach(var behavior in new[]{"child","success","success"}) await InstallFixture(behavior);
        });
        Log("RUN FINISHED. Reports: " + _run);
        Log("Fixtures left for visible UI checks are named U77 Phase10 Fixture. Existing user software was not targeted.");
    }

    private async Task LaunchShortcut(string shortcut)
    {
        Require(File.Exists(shortcut),"Shortcut missing: " + shortcut);
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic link = shell.CreateShortcut(shortcut);
        Require(Path.GetFullPath((string)link.TargetPath).Equals(Path.Combine(_installed,"WinUninstaller.exe"),StringComparison.OrdinalIgnoreCase),"Shortcut target mismatch.");
        Process.Start(new ProcessStartInfo(shortcut){UseShellExecute=true});
        Process? application = null;
        for(var i=0;i<100;i++)
        {
            application = Process.GetProcessesByName("WinUninstaller").FirstOrDefault(process=>
            { try { return process.MainModule?.FileName?.Equals(Path.Combine(_installed,"WinUninstaller.exe"),StringComparison.OrdinalIgnoreCase)==true; } catch { return false; } });
            if(application?.MainWindowHandle != IntPtr.Zero && application is not null) break;
            await Task.Delay(200);
        }
        Require(application is not null && application.MainWindowHandle!=IntPtr.Zero,"Shortcut launch did not produce a window.");
        await Task.Delay(2500);
        Require(application!.CloseMainWindow(),"Application would not close gracefully.");
        await application.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); application.Dispose();
    }

    private async Task<InstalledApp> InstallFixture(string behavior)
    {
        var id=Guid.NewGuid().ToString("N");
        Require(await Execute(Path.Combine(_source,"fixture","Phase10Fixture.exe"),"--install",id,behavior)==0,"Fixture installation failed.");
        var key=@"HKCU\"+UninstallParent+@"\Uninstaller77_Phase10_"+id;
        var app=new RegistryDiscoveryService().DiscoverApps(false).Single(item=>item.RegistryKeyPath.Equals(key,StringComparison.OrdinalIgnoreCase));
        _fixtureRoots.Add(app.InstallLocation);
        return app;
    }
    private static bool Registered(InstalledApp app) { using var key=Registry.CurrentUser.OpenSubKey(app.RegistryKeyPath[5..]); return key is not null; }
    private void Export()
    {
        if(!Directory.Exists(_run)) return;
        File.WriteAllText(Path.Combine(_run,"results.json"),JsonSerializer.Serialize(new { startedAt=_startedAt, endedAt=DateTimeOffset.UtcNow, root=_run, results=_results },new JsonSerializerOptions{WriteIndented=true}));
        var fixtureEvidence=Path.Combine(_run,"FixtureEvidence"); Directory.CreateDirectory(fixtureEvidence);
        foreach(var root in _fixtureRoots.Distinct()) if(File.Exists(Path.Combine(root,"execution.jsonl")))
            File.Copy(Path.Combine(root,"execution.jsonl"),Path.Combine(fixtureEvidence,Path.GetFileName(root)+".jsonl"),true);
        // Export evidence only; fixture executables and test installations stay guest-local.
        try
        {
            var destination=Path.GetFullPath(Path.Combine(_source,"..","..","reports","PHASE10","VM-EVIDENCE",Path.GetFileName(_run)));
            Directory.CreateDirectory(destination);
            foreach(var file in Directory.EnumerateFiles(_run,"*",SearchOption.TopDirectoryOnly).Where(file=>Path.GetExtension(file) is ".json" or ".log"))
                File.Copy(file,Path.Combine(destination,Path.GetFileName(file)),true);
            Directory.CreateDirectory(Path.Combine(destination,"FixtureEvidence"));
            foreach(var file in Directory.EnumerateFiles(fixtureEvidence)) File.Copy(file,Path.Combine(destination,"FixtureEvidence",Path.GetFileName(file)),true);
        }
        catch { /* Guest evidence remains available if the shared folder is read-only. */ }
    }
}
