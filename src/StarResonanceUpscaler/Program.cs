using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

internal static class AppInfo
{
    public const string Name = "スタレゾ SS アーカイブ";
    public const string Version = "1.00";
    public const string Description = "スクリーンショット自動バックアップ＆高画質化\nStar Resonance向け非公式ファンツール\n公式の承認・提携・保証はありません";
    public const string DataFolderName = "StarezSSArchive";
    public const string LegacyDataFolderName = "BPSRSSbackup";
    public const string OlderDataFolderName = "StarResonanceUpscaler";

    public static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataFolderName);
    public static string LegacyDataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyDataFolderName);
    public static string OlderDataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), OlderDataFolderName);
    public static string SettingsFile => Path.Combine(DataFolder, "settings.json");
    public static string LegacySettingsFile => Path.Combine(LegacyDataFolder, "settings.json");
    public static string OlderSettingsFile => Path.Combine(OlderDataFolder, "settings.json");
    public static string LogFile => Path.Combine(DataFolder, "app.log");
}

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 3 && string.Equals(args[0], "--sample", StringComparison.OrdinalIgnoreCase))
        {
            Enhancer.Process(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), false);
            return;
        }
        Forms.Application.EnableVisualStyles();
        Forms.Application.SetCompatibleTextRenderingDefault(false);
        // Keep the mutex name stable so an older BPSR SSbackup process cannot run beside this version.
        using var singleInstance = new Mutex(true, "Local\\BPSRSSbackup", out var created);
        if (!created) return;
        if (!RuntimeTerms.Accept()) return;
        if (!EngineInstallation.IsReady)
        {
            using var setup = new EngineSetupForm();
            if (setup.ShowDialog() != Forms.DialogResult.OK) return;
        }
        Forms.Application.Run(new TrayContext());
    }
}

internal sealed record AppSettings(string ScreenshotFolder, string BackupFolder, string OriginalFolder, bool RetouchEnabled, bool StartWithWindows, bool TtaEnabled)
{
    public static AppSettings Default
    {
        get
        {
            var screenshotRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "bokura", "StarASIA", "OriginalPhoto");
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrWhiteSpace(pictures)) pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
            var starAsia = Path.Combine(pictures, "StarASIA");
            var steam = Path.Combine(pictures, "StarASIA_STEAM");
            var archiveRoot = Directory.Exists(starAsia) ? starAsia : Directory.Exists(steam) ? steam : starAsia;
            return new(screenshotRoot, Path.Combine(archiveRoot, "backup"), Path.Combine(archiveRoot, "original"), true, false, false);
        }
    }
}

internal static class SettingsStore
{
    public static AppSettings Load()
    {
        try
        {
            foreach (var filePath in new[] { AppInfo.SettingsFile, AppInfo.LegacySettingsFile, AppInfo.OlderSettingsFile })
            {
                if (!File.Exists(filePath)) continue;
                var value = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(filePath));
                if (value is not null) return Normalize(value);
            }
        }
        catch { }
        return AppSettings.Default;
    }

    public static void Save(AppSettings value)
    {
        Directory.CreateDirectory(AppInfo.DataFolder);
        File.WriteAllText(AppInfo.SettingsFile, JsonSerializer.Serialize(Normalize(value), new JsonSerializerOptions { WriteIndented = true }));
    }

    private static AppSettings Normalize(AppSettings value) => value with
    {
        ScreenshotFolder = FullPathOr(value.ScreenshotFolder, AppSettings.Default.ScreenshotFolder),
        BackupFolder = FullPathOr(value.BackupFolder, AppSettings.Default.BackupFolder),
        OriginalFolder = FullPathOr(value.OriginalFolder, AppSettings.Default.OriginalFolder)
    };

    private static string FullPathOr(string? value, string fallback)
    {
        try { return string.IsNullOrWhiteSpace(value) ? fallback : Path.GetFullPath(value); }
        catch { return fallback; }
    }
}

internal sealed class TrayContext : Forms.ApplicationContext
{
    private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string RunName = "StarezSSArchive";
    private static readonly string[] LegacyRunNames = { "BPSRSSbackup", "StarResonanceUpscaler" };
    private readonly Forms.NotifyIcon _notifyIcon;
    private Forms.ToolStripMenuItem _statusItem = null!;
    private Forms.ToolStripMenuItem _pauseItem = null!;
    private readonly WatcherService _watcher;
    private AppSettings _settings;

    public TrayContext()
    {
        _settings = SettingsStore.Load();
        _notifyIcon = new Forms.NotifyIcon { Icon = LoadIcon(), Visible = true, Text = AppInfo.Name };
        _notifyIcon.ContextMenuStrip = CreateMenu();
        _notifyIcon.DoubleClick += (_, _) => OpenFolder(_settings.ScreenshotFolder);
        _watcher = new WatcherService(_settings, Log, SetStatus);
        ApplyStartupRegistration(_settings.StartWithWindows);
        Log("タスクトレイ常駐を開始しました。");
    }

    private Forms.ContextMenuStrip CreateMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        _statusItem = new Forms.ToolStripMenuItem("状態: 起動中") { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("SSフォルダを開く", null, (_, _) => OpenFolder(_settings.ScreenshotFolder));
        _pauseItem = new Forms.ToolStripMenuItem("一時停止", null, (_, _) => TogglePause());
        menu.Items.Add(_pauseItem);
        menu.Items.Add("設定", null, (_, _) => ShowSettings());
        menu.Items.Add("バージョン情報", null, (_, _) => ShowAbout());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitApplication());
        return menu;
    }

    private void SetStatus(string text)
    {
        if (_statusItem.IsDisposed) return;
        if (_statusItem.GetCurrentParent()?.IsHandleCreated == true)
        {
            _statusItem.GetCurrentParent()!.BeginInvoke(() => _statusItem.Text = "状態: " + text);
        }
        else _statusItem.Text = "状態: " + text;
    }

    private void TogglePause()
    {
        var paused = _watcher.TogglePaused();
        _pauseItem.Text = paused ? "再開" : "一時停止";
    }

    private void ShowSettings()
    {
        using var form = new SettingsForm(_settings);
        if (form.ShowDialog() != Forms.DialogResult.OK || form.Result is null) return;
        try
        {
            _settings = form.Result;
            Directory.CreateDirectory(_settings.ScreenshotFolder);
            Directory.CreateDirectory(_settings.BackupFolder);
            Directory.CreateDirectory(_settings.OriginalFolder);
            SettingsStore.Save(_settings);
            ApplyStartupRegistration(_settings.StartWithWindows);
            _watcher.Configure(_settings);
            Log("設定を保存しました。");
        }
        catch (Exception ex) { Forms.MessageBox.Show($"設定を保存できませんでした。\n{ex.Message}", AppInfo.Name, Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error); }
    }

    private void ShowAbout()
    {
        using var form = new AboutForm();
        form.ShowDialog();
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe)) return System.Drawing.Icon.ExtractAssociatedIcon(exe) ?? System.Drawing.SystemIcons.Application;
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    private void OpenFolder(string path)
    {
        try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Forms.MessageBox.Show($"フォルダを開けませんでした。\n{ex.Message}", AppInfo.Name, Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error); }
    }

    private void ApplyStartupRegistration(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return;
            if (enabled)
            {
                var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(exe))
                {
                    var assemblyPath = Path.Combine(AppContext.BaseDirectory, "StarezSSArchive.dll");
                    var command = string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase)
                        ? $"\"{exe}\" \"{assemblyPath}\""
                        : $"\"{exe}\"";
                    key.SetValue(RunName, command);
                    foreach (var legacyRunName in LegacyRunNames) key.DeleteValue(legacyRunName, false);
                }
            }
            else
            {
                key.DeleteValue(RunName, false);
                foreach (var legacyRunName in LegacyRunNames) key.DeleteValue(legacyRunName, false);
            }
        }
        catch (Exception ex) { Log($"自動起動の設定に失敗しました: {ex.Message}"); }
    }

    private void ExitApplication()
    {
        _ = FinishExitAsync();
    }

    private async Task FinishExitAsync()
    {
        _pauseItem.Enabled = false;
        SetStatus("終了処理中（現在の画像を完了しています）");
        await _watcher.StopAsync(TimeSpan.FromSeconds(30));
        _notifyIcon.Visible = false;
        var icon = _notifyIcon.Icon;
        _notifyIcon.Dispose();
        icon?.Dispose();
        ExitThread();
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataFolder);
            File.AppendAllText(AppInfo.LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}

internal sealed class AboutForm : Forms.Form
{
    public AboutForm()
    {
        Text = $"{AppInfo.Name} - バージョン情報";
        StartPosition = Forms.FormStartPosition.CenterScreen;
        FormBorderStyle = Forms.FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = Forms.AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = Forms.AutoSizeMode.GrowAndShrink;
        Width = 520;
        Height = 300;

        var table = new Forms.TableLayoutPanel
        {
            Dock = Forms.DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = Forms.AutoSizeMode.GrowAndShrink,
            Padding = new Forms.Padding(20),
            ColumnCount = 1,
            RowCount = 5
        };
        for (var i = 0; i < 5; i++) table.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));

        var title = new Forms.Label
        {
            Text = AppInfo.Name,
            AutoSize = true,
            Font = new System.Drawing.Font(System.Drawing.FontFamily.GenericSansSerif, 17, System.Drawing.FontStyle.Bold),
            Margin = new Forms.Padding(0, 0, 0, 8)
        };
        table.Controls.Add(title, 0, 0);
        table.Controls.Add(new Forms.Label { Text = $"バージョン {AppInfo.Version}", AutoSize = true, Margin = new Forms.Padding(0, 0, 0, 12) }, 0, 1);
        table.Controls.Add(new Forms.Label { Text = AppInfo.Description, AutoSize = true, MaximumSize = new System.Drawing.Size(385, 0), Margin = new Forms.Padding(0, 0, 0, 10) }, 0, 2);
        table.Controls.Add(new Forms.Label { Text = "オープンソース（MIT License）", AutoSize = true, Margin = new Forms.Padding(0, 0, 0, 12) }, 0, 3);

        var close = new Forms.Button { Text = "OK", Width = 90, DialogResult = Forms.DialogResult.OK, Anchor = Forms.AnchorStyles.Right };
        var buttons = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Fill, FlowDirection = Forms.FlowDirection.RightToLeft, WrapContents = false };
        buttons.Controls.Add(close);
        table.Controls.Add(buttons, 0, 4);
        Controls.Add(table);
        AcceptButton = close;
        CancelButton = close;
    }
}

internal sealed class SettingsForm : Forms.Form
{
    private readonly Forms.TextBox _screenshotBox = new();
    private readonly Forms.TextBox _backupBox = new();
    private readonly Forms.TextBox _originalBox = new();
    private readonly Forms.CheckBox _retouchBox = new() { Text = "レタッチ機能を有効にする", AutoSize = true };
    private readonly Forms.CheckBox _ttaBox = new() { Text = "TTAを有効にする（処理時間が長くなります）", AutoSize = true };
    private readonly Forms.CheckBox _startupBox = new() { Text = "Windowsログオン時にタスクトレイで起動する", AutoSize = true };
    public AppSettings? Result { get; private set; }

    public SettingsForm(AppSettings settings)
    {
        Text = $"{AppInfo.Name} - 設定";
        StartPosition = Forms.FormStartPosition.CenterScreen;
        FormBorderStyle = Forms.FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; Width = 760; Height = 360;
        var table = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, Padding = new Forms.Padding(12), ColumnCount = 3, RowCount = 8 };
        table.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Absolute, 180));
        table.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100));
        table.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Absolute, 90));
        foreach (var height in new[] { 38, 38, 38, 34, 34, 34, 1, 42 }) table.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, height));
        table.Controls.Add(new Forms.Label { Text = "スクリーンショット監視先", AutoSize = true, Anchor = Forms.AnchorStyles.Left }, 0, 0);
        AddPathRow(table, _screenshotBox, settings.ScreenshotFolder, 0);
        table.Controls.Add(new Forms.Label { Text = "バックアップ保存先", AutoSize = true, Anchor = Forms.AnchorStyles.Left }, 0, 1);
        AddPathRow(table, _backupBox, settings.BackupFolder, 1);
        table.Controls.Add(new Forms.Label { Text = "加工前の原本保存先", AutoSize = true, Anchor = Forms.AnchorStyles.Left }, 0, 2);
        AddPathRow(table, _originalBox, settings.OriginalFolder, 2);
        table.Controls.Add(_retouchBox, 1, 3); table.SetColumnSpan(_retouchBox, 2);
        table.Controls.Add(_ttaBox, 1, 4); table.SetColumnSpan(_ttaBox, 2);
        table.Controls.Add(_startupBox, 1, 5); table.SetColumnSpan(_startupBox, 2);
        _retouchBox.Checked = settings.RetouchEnabled; _ttaBox.Checked = settings.TtaEnabled; _startupBox.Checked = settings.StartWithWindows;
        var buttons = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Fill, FlowDirection = Forms.FlowDirection.RightToLeft };
        var ok = new Forms.Button { Text = "保存", Width = 90 }; ok.Click += (_, _) => Commit();
        var cancel = new Forms.Button { Text = "キャンセル", Width = 90, DialogResult = Forms.DialogResult.Cancel };
        buttons.Controls.Add(ok); buttons.Controls.Add(cancel); table.Controls.Add(buttons, 0, 7); table.SetColumnSpan(buttons, 3);
        Controls.Add(table); AcceptButton = ok; CancelButton = cancel;
    }

    private static void AddPathRow(Forms.TableLayoutPanel table, Forms.TextBox box, string value, int row)
    {
        box.Text = value; box.Dock = Forms.DockStyle.Fill; table.Controls.Add(box, 1, row);
        var button = new Forms.Button { Text = "参照...", Dock = Forms.DockStyle.Fill };
        button.Click += (_, _) => { using var dialog = new Forms.FolderBrowserDialog { SelectedPath = Directory.Exists(box.Text) ? box.Text : string.Empty, Description = "保存先フォルダを選択してください" }; if (dialog.ShowDialog() == Forms.DialogResult.OK) box.Text = dialog.SelectedPath; };
        table.Controls.Add(button, 2, row);
    }

    private void Commit()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_screenshotBox.Text) || string.IsNullOrWhiteSpace(_backupBox.Text) || string.IsNullOrWhiteSpace(_originalBox.Text)) throw new InvalidOperationException("すべてのフォルダを指定してください。");
            var screenshot = Path.GetFullPath(_screenshotBox.Text.Trim());
            var backup = Path.GetFullPath(_backupBox.Text.Trim());
            var original = Path.GetFullPath(_originalBox.Text.Trim());
            if (Overlaps(screenshot, backup) || Overlaps(screenshot, original) || Overlaps(backup, original)) throw new InvalidOperationException("監視先・バックアップ先・原本保存先は重ならない別フォルダを指定してください。");
            Result = new AppSettings(screenshot, backup, original, _retouchBox.Checked, _startupBox.Checked, _ttaBox.Checked);
            DialogResult = Forms.DialogResult.OK; Close();
        }
        catch (Exception ex) { Forms.MessageBox.Show($"保存先を確認してください。\n{ex.Message}", "設定", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning); }
    }

    private static bool Overlaps(string a, string b)
    {
        a = a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        b = b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class WatcherService : IDisposable
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _suppressUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Action<string> _log;
    private readonly Action<string> _status;
    private readonly object _idleGate = new();
    private TaskCompletionSource _idle = CompletedSource();
    private FileSystemWatcher? _watcher;
    private AppSettings _settings;
    private bool _disposed;
    private bool _paused;
    private int _active;

    public WatcherService(AppSettings settings, Action<string> log, Action<string> status) { _settings = settings; _log = log; _status = status; Configure(settings); }

    public void Configure(AppSettings settings)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _settings = settings;
            Directory.CreateDirectory(settings.ScreenshotFolder); Directory.CreateDirectory(settings.BackupFolder); Directory.CreateDirectory(settings.OriginalFolder);
            _watcher?.Dispose();
            _watcher = new FileSystemWatcher(settings.ScreenshotFolder, "*.png") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite, IncludeSubdirectories = false, EnableRaisingEvents = true };
            _watcher.Created += (_, e) => Queue(e.FullPath); _watcher.Changed += (_, e) => Queue(e.FullPath); _watcher.Renamed += (_, e) => Queue(e.FullPath);
            _status(_paused ? "一時停止中" : "待機中");
        }
    }

    public bool TogglePaused()
    {
        lock (_gate) _paused = !_paused;
        if (_paused) _status("一時停止中");
        else
        {
            _status("再開処理中");
            AppSettings settings; lock (_gate) settings = _settings;
            _ = Task.Run(() => { try { foreach (var file in Directory.EnumerateFiles(settings.ScreenshotFolder, "*.png")) Queue(file); } catch (Exception ex) { _log($"再開時の確認に失敗しました: {ex.Message}"); } });
        }
        return _paused;
    }

    private void Queue(string path)
    {
        AppSettings settings; lock (_gate) settings = _settings;
        if (_disposed || _paused || !settings.RetouchEnabled || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return;
        if (_suppressUntil.TryGetValue(path, out var until) && until > DateTime.UtcNow) return;
        if (File.Exists(Path.Combine(settings.OriginalFolder, Path.GetFileName(path)))) return;
        if (!_pending.TryAdd(path, 0)) return;
        if (Interlocked.Increment(ref _active) == 1) lock (_idleGate) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _status($"処理待ち（{_active}件）");
        _ = Task.Run(async () => { await _serial.WaitAsync(); try { await ProcessOne(path, settings); } catch (Exception ex) { _log($"処理失敗: {Path.GetFileName(path)} - {ex.Message}"); _status($"失敗: {Path.GetFileName(path)}"); } finally { _pending.TryRemove(path, out _); _serial.Release(); if (Interlocked.Decrement(ref _active) == 0) lock (_idleGate) _idle.TrySetResult(); } });
    }

    private async Task ProcessOne(string path, AppSettings settings)
    {
        await WaitUntilStable(path); if (!File.Exists(path)) return;
        var originalPath = Path.Combine(settings.OriginalFolder, Path.GetFileName(path));
        if (File.Exists(originalPath)) return;
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".retouching.tmp";
        var creation = File.GetCreationTimeUtc(path); _suppressUntil[path] = DateTime.UtcNow.AddMinutes(2);
        try
        {
            _status($"処理中: {Path.GetFileName(path)}");
            Enhancer.Process(path, temp, settings.TtaEnabled); File.SetCreationTimeUtc(temp, creation); MoveAcrossVolumes(path, originalPath);
            try { File.Move(temp, path, false); } catch { if (!File.Exists(path)) MoveAcrossVolumes(originalPath, path); throw; }
            EnforceLimit(settings); _log($"処理完了: {Path.GetFileName(path)}"); _status("待機中");
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static async Task WaitUntilStable(string path)
    {
        long previous = -1;
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(500); if (!File.Exists(path)) return;
            var current = new FileInfo(path).Length;
            if (current > 0 && current == previous) { try { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return; } catch (IOException) { } }
            previous = current;
        }
        throw new IOException("ファイルの書き込み完了を確認できませんでした。");
    }

    private void EnforceLimit(AppSettings settings)
    {
        var files = new DirectoryInfo(settings.ScreenshotFolder).GetFiles("*.png").OrderBy(f => f.CreationTimeUtc).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        while (files.Count > 100)
        {
            var oldest = files[0]; var destination = Path.Combine(settings.BackupFolder, oldest.Name); var suffix = 1;
            while (File.Exists(destination)) destination = Path.Combine(settings.BackupFolder, $"{Path.GetFileNameWithoutExtension(oldest.Name)}_{suffix++}{oldest.Extension}");
            MoveAcrossVolumes(oldest.FullName, destination); files.RemoveAt(0); _log($"backupへ移動: {oldest.Name}");
        }
    }

    private static void MoveAcrossVolumes(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try { File.Move(source, destination, false); } catch (IOException) { File.Copy(source, destination, false); File.Delete(source); }
    }

    public async Task StopAsync(TimeSpan timeout)
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _watcher?.Dispose(); }
        Task idle; lock (_idleGate) idle = _idle.Task;
        try { await idle.WaitAsync(timeout); } catch (TimeoutException) { _log("終了待ち時間を超えたため終了しました。"); }
    }

    public void Dispose() => StopAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();

    private static TaskCompletionSource CompletedSource() { var value = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); value.SetResult(); return value; }
}
