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
        var tray = TrayContext.Create();
        if (tray is not null) Forms.Application.Run(tray);
    }
}

internal sealed record AppSettings(string ScreenshotFolder, string BackupFolder, string OriginalFolder, bool RetouchEnabled, bool StartWithWindows, bool TtaEnabled, bool NotificationsEnabled)
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
            return new(screenshotRoot, Path.Combine(archiveRoot, "backup"), Path.Combine(archiveRoot, "original"), true, false, false, false);
        }
    }
}

internal sealed record SettingsLoadResult(AppSettings Settings, bool RequiresSetup, string? Warning, IReadOnlyList<string> InvalidFiles);

internal sealed record ProcessingNotice(string Message, bool IsError, string? TargetPath);

internal sealed record ProcessingBatchResult(
    DateTimeOffset CompletedAt,
    int Succeeded,
    int Failed,
    string? LastSuccessfulPath,
    string? LastFailedPath);

internal sealed record ReconfigureCapture(
    DateTime StartedAtUtc,
    string PreviousScreenshotFolder,
    IReadOnlyList<string> CandidatePaths);

internal static class SettingsStore
{
    private static readonly string[] CandidateFiles = { AppInfo.SettingsFile, AppInfo.LegacySettingsFile, AppInfo.OlderSettingsFile };
    internal static string BackupFile => AppInfo.SettingsFile + ".bak";

    public static SettingsLoadResult Load()
    {
        var found = false;
        var failures = new List<string>();
        foreach (var filePath in CandidateFiles)
        {
            if (!File.Exists(filePath)) continue;
            found = true;
            if (TryLoadValid(filePath, out var settings))
            {
                var warning = failures.Count == 0 ? null : "一部の設定ファイルを読み込めませんでした。利用する設定を確認してください。";
                return new(settings, failures.Count > 0, warning, failures);
            }
            failures.Add(filePath);
        }

        if (File.Exists(BackupFile) && TryLoadValid(BackupFile, out var backupSettings))
        {
            var backupWarning = failures.Count > 0
                ? "設定ファイルが破損しているため、バックアップから設定候補を復元しました。内容を確認してください。"
                : "設定のバックアップを復元候補として読み込みました。内容を確認してください。";
            return new(backupSettings, true, backupWarning, failures);
        }

        if (File.Exists(BackupFile)) failures.Add(BackupFile);

        var loadWarning = failures.Count == 0
            ? null
            : "設定ファイルを読み込めませんでした。初回設定を確認してください。";
        return new(AppSettings.Default, !found || failures.Count > 0, loadWarning, failures);
    }

    public static bool Save(AppSettings value)
    {
        Directory.CreateDirectory(AppInfo.DataFolder);
        var temporary = AppInfo.SettingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backupUpdated = true;
        try
        {
            if (TryLoadValid(AppInfo.SettingsFile, out _))
            {
                try { File.Copy(AppInfo.SettingsFile, BackupFile, true); }
                catch (Exception ex) { backupUpdated = false; Trace.WriteLine($"設定バックアップの更新に失敗しました: {ex.Message}"); }
            }
            File.WriteAllText(temporary, JsonSerializer.Serialize(Normalize(value), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, AppInfo.SettingsFile, true);
            try
            {
                File.Copy(AppInfo.SettingsFile, BackupFile, true);
                backupUpdated = true;
            }
            catch (Exception ex)
            {
                backupUpdated = false;
                Trace.WriteLine($"設定バックアップの更新に失敗しました: {ex.Message}");
            }
            return backupUpdated;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    public static void PreserveInvalidFiles(IEnumerable<string> files)
    {
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(file)) continue;
                var preserved = file + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N");
                File.Move(file, preserved, false);
            }
            catch { }
        }
    }

    private static bool TryLoadValid(string filePath, out AppSettings settings)
    {
        try
        {
            if (!File.Exists(filePath)) { settings = default!; return false; }
            var value = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(filePath));
            if (value is null || string.IsNullOrWhiteSpace(value.ScreenshotFolder) || string.IsNullOrWhiteSpace(value.BackupFolder) || string.IsNullOrWhiteSpace(value.OriginalFolder))
            {
                settings = default!;
                return false;
            }
            settings = Normalize(value);
            return true;
        }
        catch
        {
            settings = default!;
            return false;
        }
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
    private readonly Forms.Control _uiInvoker = new();
    private Forms.ToolStripMenuItem _monitorStatusItem = null!;
    private Forms.ToolStripMenuItem _processingStatusItem = null!;
    private Forms.ToolStripMenuItem _lastResultItem = null!;
    private Forms.ToolStripMenuItem _pauseItem = null!;
    private Forms.ToolStripMenuItem _settingsItem = null!;
    private Forms.ToolStripMenuItem _exitItem = null!;
    private readonly WatcherService _watcher;
    private AppSettings _settings;
    private string _monitorDisplay = "起動準備中";
    private string _processingDisplay = "待機中";
    private ProcessingBatchResult? _lastResult;
    private string? _notificationTargetPath;
    private bool _notificationTargetIsError;
    private bool _settingsChanging;

    public static TrayContext? Create()
    {
        var loaded = SettingsStore.Load();
        SettingsStore.PreserveInvalidFiles(loaded.InvalidFiles);
        if (loaded.Warning is not null)
        {
            Forms.MessageBox.Show(loaded.Warning, AppInfo.Name, Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
        }

        if (loaded.RequiresSetup)
        {
            using var form = new SettingsForm(loaded.Settings, true);
            if (form.ShowDialog() != Forms.DialogResult.OK || form.Result is null) return null;
            try
            {
                if (!SettingsStore.Save(form.Result)) Log("設定は保存されましたが、バックアップを更新できませんでした。");
            }
            catch (Exception ex)
            {
                Forms.MessageBox.Show($"設定を保存できませんでした。\n{ex.Message}", AppInfo.Name, Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
                return null;
            }
            try { return new TrayContext(form.Result); }
            catch (Exception ex)
            {
                Forms.MessageBox.Show($"監視を開始できませんでした。\n{ex.Message}", AppInfo.Name, Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
                return null;
            }
        }

        try { return new TrayContext(loaded.Settings); }
        catch (Exception ex)
        {
            Forms.MessageBox.Show($"監視を開始できませんでした。\n{ex.Message}", AppInfo.Name, Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
            return null;
        }
    }

    private TrayContext(AppSettings settings)
    {
        _settings = settings;
        _notifyIcon = new Forms.NotifyIcon { Icon = LoadIcon(), Visible = true, Text = AppInfo.Name };
        _notifyIcon.ContextMenuStrip = CreateMenu();
        _uiInvoker.CreateControl();
        _notifyIcon.DoubleClick += (_, _) => OpenFolder(_settings.ScreenshotFolder);
        _notifyIcon.BalloonTipClicked += (_, _) => OpenNotificationTarget();
        _watcher = new WatcherService(_settings, Log, SetProcessingStatus, SetMonitorStatus, ShowNotification, RecordBatchResult);
        ApplyStartupRegistration(_settings.StartWithWindows);
        Log("タスクトレイ常駐を開始しました。");
    }

    private Forms.ContextMenuStrip CreateMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        _monitorStatusItem = new Forms.ToolStripMenuItem("監視: 起動準備中") { Enabled = false };
        _processingStatusItem = new Forms.ToolStripMenuItem("処理: 待機中") { Enabled = false };
        _lastResultItem = new Forms.ToolStripMenuItem("直近の処理: なし") { Enabled = false };
        _lastResultItem.Click += (_, _) => OpenLastResult();
        menu.Items.Add(_monitorStatusItem);
        menu.Items.Add(_processingStatusItem);
        menu.Items.Add(_lastResultItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("SSフォルダを開く", null, (_, _) => OpenFolder(_settings.ScreenshotFolder));
        _pauseItem = new Forms.ToolStripMenuItem("一時停止", null, (_, _) => TogglePause());
        menu.Items.Add(_pauseItem);
        _settingsItem = new Forms.ToolStripMenuItem("設定", null, (_, _) => ShowSettings());
        menu.Items.Add(_settingsItem);
        menu.Items.Add("バージョン情報", null, (_, _) => ShowAbout());
        menu.Items.Add(new Forms.ToolStripSeparator());
        _exitItem = new Forms.ToolStripMenuItem("終了", null, (_, _) => ExitApplication());
        menu.Items.Add(_exitItem);
        return menu;
    }

    private void SetMonitorStatus(string text)
    {
        RunOnUiThread(() =>
        {
            if (_monitorStatusItem.IsDisposed) return;
            _monitorDisplay = text;
            _monitorStatusItem.Text = "監視: " + text;
            UpdateNotifyIconText();
        });
    }

    private void SetProcessingStatus(string text)
    {
        RunOnUiThread(() =>
        {
            if (_processingStatusItem.IsDisposed) return;
            _processingDisplay = text;
            _processingStatusItem.Text = "処理: " + text;
            UpdateNotifyIconText();
        });
    }

    private void RecordBatchResult(ProcessingBatchResult result)
    {
        RunOnUiThread(() =>
        {
            if (_lastResultItem.IsDisposed) return;
            _lastResult = result;
            _lastResultItem.Text = $"直近の処理: {result.CompletedAt:HH:mm:ss} 成功{result.Succeeded}件・失敗{result.Failed}件";
            _lastResultItem.Enabled = true;
        });
    }

    private void ShowNotification(ProcessingNotice notice)
    {
        RunOnUiThread(() =>
        {
            _notificationTargetPath = notice.TargetPath;
            _notificationTargetIsError = notice.IsError;
            _notifyIcon.BalloonTipTitle = AppInfo.Name;
            _notifyIcon.BalloonTipText = notice.Message;
            _notifyIcon.BalloonTipIcon = notice.IsError ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Info;
            _notifyIcon.ShowBalloonTip(notice.IsError ? 5000 : 3500);
        });
    }

    private void RunOnUiThread(Action action)
    {
        void SafeAction()
        {
            try { action(); }
            catch (InvalidOperationException) { }
        }

        if (_uiInvoker.IsHandleCreated && !_uiInvoker.IsDisposed && _uiInvoker.InvokeRequired)
        {
            try { _uiInvoker.BeginInvoke(SafeAction); }
            catch (InvalidOperationException) { }
            return;
        }
        SafeAction();
    }

    private void UpdateNotifyIconText()
    {
        _notifyIcon.Text = FitNotifyIconText($"{AppInfo.Name}: {_monitorDisplay} / {_processingDisplay}");
    }

    private void OpenNotificationTarget()
    {
        try
        {
            OpenResult(_notificationTargetPath, _notificationTargetIsError);
        }
        catch (Exception ex)
        {
            Forms.MessageBox.Show($"結果を開けませんでした。\n{ex.Message}", AppInfo.Name, Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
        }
    }

    private void OpenLastResult()
    {
        try
        {
            if (_lastResult is null) return;
            OpenResult(_lastResult.Failed > 0 ? _lastResult.LastFailedPath : _lastResult.LastSuccessfulPath, _lastResult.Failed > 0);
        }
        catch (Exception ex)
        {
            Forms.MessageBox.Show($"直近の結果を開けませんでした。\n{ex.Message}", AppInfo.Name, Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
        }
    }

    private void OpenResult(string? path, bool isError)
    {
        if (isError)
        {
            OpenLog();
            return;
        }
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            SelectFile(path);
            return;
        }
        OpenFolder(_settings.ScreenshotFolder);
    }

    private void OpenLog()
    {
        try
        {
            if (!File.Exists(AppInfo.LogFile)) Log("ログを開きました。");
            Process.Start(new ProcessStartInfo(AppInfo.LogFile) { UseShellExecute = true });
        }
        catch (Exception ex) { Forms.MessageBox.Show($"ログを開けませんでした。\n{ex.Message}", AppInfo.Name, Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error); }
    }

    private static void SelectFile(string path)
    {
        Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{path}\"", UseShellExecute = true });
    }

    private static string FitNotifyIconText(string text)
    {
        const int maxLength = 63;
        return text.Length <= maxLength ? text : text[..(maxLength - 3)] + "...";
    }

    private void TogglePause()
    {
        var paused = _watcher.TogglePaused();
        _pauseItem.Text = paused ? "再開" : "一時停止";
    }

    private async void ShowSettings()
    {
        if (_settingsChanging) return;
        using var form = new SettingsForm(_settings);
        if (form.ShowDialog() != Forms.DialogResult.OK || form.Result is null) return;

        _settingsChanging = true;
        _pauseItem.Enabled = false;
        _settingsItem.Enabled = false;
        _exitItem.Enabled = false;
        var previous = _settings;
        try
        {
            var capture = await _watcher.DrainForReconfigureAsync();
            try
            {
                if (!_watcher.Configure(form.Result, acceptEvents: false)) throw new InvalidOperationException("監視を再構成できませんでした。");
                if (!SettingsStore.Save(form.Result)) Log("設定は保存されましたが、バックアップを更新できませんでした。");
                _settings = form.Result;
                ApplyStartupRegistration(form.Result.StartWithWindows);
                if (!_watcher.ResumeAcceptance()) throw new InvalidOperationException("新しい監視を開始できませんでした。");
                _watcher.QueueReconfigureCapture(capture, form.Result.ScreenshotFolder);
            }
            catch
            {
                _settings = previous;
                try
                {
                    if (!_watcher.Configure(previous)) throw new InvalidOperationException("以前の監視設定を復元できませんでした。");
                    _watcher.QueueReconfigureCapture(capture, form.Result.ScreenshotFolder);
                }
                catch (Exception rollback) { Log($"設定変更のロールバックに失敗しました: {rollback.Message}"); }
                throw;
            }
            Log("設定を保存しました。");
        }
        catch (Exception ex)
        {
            Forms.MessageBox.Show($"設定を反映できませんでした。\n{ex.Message}", AppInfo.Name, Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
        }
        finally
        {
            _settingsChanging = false;
            _pauseItem.Enabled = true;
            _settingsItem.Enabled = true;
            _exitItem.Enabled = true;
        }
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
        _settingsItem.Enabled = false;
        _exitItem.Enabled = false;
        SetMonitorStatus("終了処理中");
        SetProcessingStatus("現在の画像を完了しています");
        await _watcher.StopAsync(TimeSpan.FromSeconds(30));
        _notifyIcon.Visible = false;
        var icon = _notifyIcon.Icon;
        _notifyIcon.Dispose();
        icon?.Dispose();
        _uiInvoker.Dispose();
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
    private readonly Forms.CheckBox _notificationsBox = new() { Text = "処理完了・エラーを通知する", AutoSize = true };
    public AppSettings? Result { get; private set; }

    public SettingsForm(AppSettings settings, bool firstRun = false)
    {
        Text = firstRun ? $"{AppInfo.Name} - 初回設定" : $"{AppInfo.Name} - 設定";
        StartPosition = Forms.FormStartPosition.CenterScreen;
        FormBorderStyle = Forms.FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; Width = 760; Height = 400;
        var table = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, Padding = new Forms.Padding(12), ColumnCount = 3, RowCount = 9 };
        table.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Absolute, 180));
        table.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100));
        table.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Absolute, 90));
        foreach (var height in new[] { 38, 38, 38, 34, 34, 34, 34, 1, 42 }) table.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Absolute, height));
        table.Controls.Add(new Forms.Label { Text = "スクリーンショット監視先", AutoSize = true, Anchor = Forms.AnchorStyles.Left }, 0, 0);
        AddPathRow(table, _screenshotBox, settings.ScreenshotFolder, 0);
        table.Controls.Add(new Forms.Label { Text = "バックアップ保存先", AutoSize = true, Anchor = Forms.AnchorStyles.Left }, 0, 1);
        AddPathRow(table, _backupBox, settings.BackupFolder, 1);
        table.Controls.Add(new Forms.Label { Text = "加工前の原本保存先", AutoSize = true, Anchor = Forms.AnchorStyles.Left }, 0, 2);
        AddPathRow(table, _originalBox, settings.OriginalFolder, 2);
        table.Controls.Add(_retouchBox, 1, 3); table.SetColumnSpan(_retouchBox, 2);
        table.Controls.Add(_ttaBox, 1, 4); table.SetColumnSpan(_ttaBox, 2);
        table.Controls.Add(_startupBox, 1, 5); table.SetColumnSpan(_startupBox, 2);
        table.Controls.Add(_notificationsBox, 1, 6); table.SetColumnSpan(_notificationsBox, 2);
        _retouchBox.Checked = settings.RetouchEnabled; _ttaBox.Checked = settings.TtaEnabled; _startupBox.Checked = settings.StartWithWindows; _notificationsBox.Checked = settings.NotificationsEnabled;
        var buttons = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Fill, FlowDirection = Forms.FlowDirection.RightToLeft };
        var ok = new Forms.Button { Text = firstRun ? "保存して開始" : "保存", Width = 110 }; ok.Click += (_, _) => Commit();
        var cancel = new Forms.Button { Text = "キャンセル", Width = 90, DialogResult = Forms.DialogResult.Cancel };
        buttons.Controls.Add(ok); buttons.Controls.Add(cancel); table.Controls.Add(buttons, 0, 8); table.SetColumnSpan(buttons, 3);
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
            ValidateWritableFolder("監視先", screenshot);
            ValidateWritableFolder("バックアップ保存先", backup);
            ValidateWritableFolder("原本保存先", original);
            Result = new AppSettings(screenshot, backup, original, _retouchBox.Checked, _startupBox.Checked, _ttaBox.Checked, _notificationsBox.Checked);
            DialogResult = Forms.DialogResult.OK; Close();
        }
        catch (Exception ex) { Forms.MessageBox.Show($"保存先を確認してください。\n{ex.Message}", "設定", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning); }
    }

    private static void ValidateWritableFolder(string label, string path)
    {
        Directory.CreateDirectory(path);
        var probe = Path.Combine(path, $".starezssarchive-write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            stream.WriteByte(0);
            stream.Flush(true);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{label}に書き込めません。別のフォルダを指定してください。", ex);
        }
        finally
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { }
        }
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
    private readonly Action<string> _processingStatus;
    private readonly Action<string> _monitorStatus;
    private readonly Action<ProcessingNotice> _notify;
    private readonly Action<ProcessingBatchResult> _batchResult;
    private TaskCompletionSource _idle = CompletedSource();
    private FileSystemWatcher? _watcher;
    private AppSettings _settings;
    private bool _disposed;
    private bool _paused;
    private bool _accepting = true;
    private DateTime _pausedSinceUtc;
    private int _active;
    private string? _processingPath;
    private int _batchSucceeded;
    private int _batchFailed;
    private string? _lastSuccessfulPath;
    private string? _lastFailedPath;
    private bool _batchNotificationsEnabled;
    private bool _reconfiguring;
    private FileSystemWatcher? _reconfigureWatcher;
    private readonly HashSet<string> _reconfigureSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reconfigureCandidates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deferredCandidates = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _monitoringSinceUtc;
    private long _configurationGeneration;
    private readonly HashSet<FileSystemWatcher> _recoveringWatchers = new();
    private static readonly TimeSpan[] RecoveryDelays = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) };

    public WatcherService(AppSettings settings, Action<string> log, Action<string> processingStatus, Action<string> monitorStatus, Action<ProcessingNotice> notify, Action<ProcessingBatchResult> batchResult)
    {
        _settings = settings;
        _log = log;
        _processingStatus = processingStatus;
        _monitorStatus = monitorStatus;
        _notify = notify;
        _batchResult = batchResult;
        Configure(settings);
    }

    public bool Configure(AppSettings settings, FileSystemWatcher? expectedWatcher = null, long? expectedGeneration = null, bool acceptEvents = true)
    {
        var next = CreateWatcher(settings);
        FileSystemWatcher? previous;
        var paused = false;
        var active = 0;
        lock (_gate)
        {
            if (_disposed)
            {
                next.Dispose();
                return false;
            }
            if (expectedGeneration.HasValue && (_configurationGeneration != expectedGeneration.Value || !ReferenceEquals(_watcher, expectedWatcher)))
            {
                next.Dispose();
                return false;
            }
            previous = _watcher;
            var previousSettings = _settings;
            var previousMonitoringSince = _monitoringSinceUtc;
            var previousAccepting = _accepting;
            _settings = settings;
            _watcher = next;
            _monitoringSinceUtc = DateTime.UtcNow;
            _accepting = acceptEvents;
            try
            {
                next.EnableRaisingEvents = true;
                previous?.Dispose();
                _configurationGeneration++;
                paused = _paused;
                active = _active;
            }
            catch
            {
                _watcher = previous;
                _settings = previousSettings;
                _monitoringSinceUtc = previousMonitoringSince;
                _accepting = previousAccepting;
                next.Dispose();
                throw;
            }
        }
        _monitorStatus(acceptEvents ? (paused ? "一時停止中" : "監視中") : "設定保存中");
        _processingStatus(active == 0 ? "待機中" : QueuedStatus());
        return true;
    }

    public bool ResumeAcceptance()
    {
        bool paused;
        lock (_gate)
        {
            if (_disposed || _watcher is null) return false;
            _accepting = true;
            paused = _paused;
        }
        _monitorStatus(paused ? "一時停止中" : "監視中");
        return true;
    }

    private FileSystemWatcher CreateWatcher(AppSettings settings)
    {
        Directory.CreateDirectory(settings.ScreenshotFolder);
        Directory.CreateDirectory(settings.BackupFolder);
        Directory.CreateDirectory(settings.OriginalFolder);
        var watcher = new FileSystemWatcher(settings.ScreenshotFolder, "*.png")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            IncludeSubdirectories = false,
            EnableRaisingEvents = false
        };
        watcher.Created += OnWatcherChanged;
        watcher.Changed += OnWatcherChanged;
        watcher.Renamed += OnWatcherRenamed;
        watcher.Error += OnWatcherError;
        return watcher;
    }

    private void OnWatcherChanged(object? sender, FileSystemEventArgs e)
    {
        if (sender is FileSystemWatcher watcher) Queue(e.FullPath, watcher);
    }

    private void OnWatcherRenamed(object? sender, RenamedEventArgs e)
    {
        if (sender is FileSystemWatcher watcher) Queue(e.FullPath, watcher);
    }

    private void OnWatcherError(object? sender, ErrorEventArgs e)
    {
        if (sender is not FileSystemWatcher watcher) return;
        AppSettings settings;
        DateTime monitoringSince;
        long generation;
        var shouldRecover = false;
        lock (_gate)
        {
            if (_disposed || _reconfiguring || !_accepting || !ReferenceEquals(_watcher, watcher)) return;
            settings = _settings;
            monitoringSince = _monitoringSinceUtc;
            generation = _configurationGeneration;
            shouldRecover = _recoveringWatchers.Add(watcher);
        }
        if (!shouldRecover) return;
        _ = Task.Run(() => RecoverWatcherAsync(watcher, settings, monitoringSince, generation, e.GetException()));
    }

    private async Task RecoverWatcherAsync(FileSystemWatcher failedWatcher, AppSettings settings, DateTime monitoringSince, long generation, Exception exception)
    {
        var watcher = failedWatcher;
        var attempt = 0;
        var notified = false;
        try
        {
            _log($"監視エラー: {exception.Message}");
            while (true)
            {
                if (!IsCurrentConfiguration(watcher, generation)) return;
                if (attempt > 0)
                {
                    var delay = RecoveryDelays[Math.Min(attempt - 1, RecoveryDelays.Length - 1)];
                    _monitorStatus($"監視エラー: {delay.TotalSeconds:0}秒後に再接続を再試行します（{attempt + 1}回目）");
                    await Task.Delay(delay);
                    if (!IsCurrentConfiguration(watcher, generation)) return;
                }

                attempt++;
                _monitorStatus(attempt == 1 ? "監視を再接続中" : $"監視を再接続中（{attempt}回目）");
                try
                {
                    if (!Configure(settings, watcher, generation)) return;
                    lock (_gate)
                    {
                        if (_disposed || !_accepting || _watcher is null) return;
                        watcher = _watcher;
                        generation = _configurationGeneration;
                    }
                    foreach (var file in Directory.EnumerateFiles(settings.ScreenshotFolder, "*.png"))
                    {
                        if (!IsCurrentConfiguration(watcher, generation)) return;
                        if (ChangedSince(file, monitoringSince)) Queue(file);
                    }
                    _log("監視を再接続しました。");
                    if (GetActiveCount() == 0) _processingStatus("待機中");
                    return;
                }
                catch (Exception ex)
                {
                    _log($"監視の再接続に失敗しました（{attempt}回目）: {ex.Message}");
                    if (!notified && settings.NotificationsEnabled)
                    {
                        _notify(new ProcessingNotice("監視フォルダを再接続できませんでした。再接続を再試行しています。", true, null));
                        notified = true;
                    }
                }
            }
        }
        finally
        {
            lock (_gate) _recoveringWatchers.Remove(failedWatcher);
        }
    }

    private bool IsCurrentConfiguration(FileSystemWatcher watcher, long generation)
    {
        lock (_gate) return !_disposed && !_reconfiguring && _accepting && _configurationGeneration == generation && ReferenceEquals(_watcher, watcher);
    }

    private static bool ChangedSince(string path, DateTime since)
    {
        try
        {
            var info = new FileInfo(path);
            return info.CreationTimeUtc >= since || info.LastWriteTimeUtc >= since;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public bool TogglePaused()
    {
        DateTime resumeSince;
        AppSettings settings;
        bool paused;
        lock (_gate)
        {
            if (_disposed) return _paused;
            _paused = !_paused;
            paused = _paused;
            if (paused)
            {
                _pausedSinceUtc = DateTime.UtcNow;
                resumeSince = default;
                settings = _settings;
            }
            else
            {
                resumeSince = _pausedSinceUtc == default ? DateTime.UtcNow : _pausedSinceUtc;
                _pausedSinceUtc = default;
                settings = _settings;
            }
        }
        _monitorStatus(paused ? "一時停止中" : "監視中");
        if (!paused)
        {
            _processingStatus("再開時の新規画像を確認中");
            _ = Task.Run(() =>
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(settings.ScreenshotFolder, "*.png"))
                    {
                        if (ChangedSince(file, resumeSince)) Queue(file);
                    }
                    if (GetActiveCount() == 0) _processingStatus("待機中");
                }
                catch (Exception ex) { _log($"再開時の確認に失敗しました: {ex.Message}"); }
            });
        }
        return paused;
    }

    private void Queue(string path, FileSystemWatcher? sourceWatcher = null)
    {
        AppSettings settings;
        string queuedStatus;
        lock (_gate)
        {
            if (_disposed || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return;
            if (_reconfiguring)
            {
                if (sourceWatcher is not null && ReferenceEquals(_reconfigureWatcher, sourceWatcher))
                {
                    _reconfigureSeen.Add(path);
                    _reconfigureCandidates.Add(path);
                }
                return;
            }
            if (sourceWatcher is not null && !ReferenceEquals(_watcher, sourceWatcher)) return;
            if (!_accepting || _paused || !_settings.RetouchEnabled || !File.Exists(path)) return;
            settings = _settings;
            if (_suppressUntil.TryGetValue(path, out var until) && until > DateTime.UtcNow) return;
            if (File.Exists(Path.Combine(settings.OriginalFolder, Path.GetFileName(path)))) return;
            if (!_pending.TryAdd(path, 0)) return;
            if (_active == 0)
            {
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _batchSucceeded = 0;
                _batchFailed = 0;
                _lastSuccessfulPath = null;
                _lastFailedPath = null;
                _batchNotificationsEnabled = settings.NotificationsEnabled;
            }
            _active++;
            queuedStatus = QueuedStatusLocked();
        }
        _processingStatus(queuedStatus);
        _ = Task.Run(async () =>
        {
            await _serial.WaitAsync();
            var processed = false;
            var failed = false;
            ProcessingBatchResult? completedBatch = null;
            var batchNotificationsEnabled = false;
            try
            {
                lock (_gate) _processingPath = path;
                _processingStatus(ProcessingStatus(path, GetActiveCount()));
                processed = await ProcessOne(path, settings);
            }
            catch (Exception ex)
            {
                failed = true;
                _log($"処理失敗: {Path.GetFileName(path)} - {ex.Message}");
                if (settings.NotificationsEnabled)
                    _notify(new ProcessingNotice($"画像処理に失敗しました。\n{Path.GetFileName(path)}", true, path));
                _processingStatus($"処理失敗: {Path.GetFileName(path)}（待機中 {Math.Max(0, GetActiveCount() - 1)}件）");
            }
            finally
            {
                lock (_gate)
                {
                    if (string.Equals(_processingPath, path, StringComparison.OrdinalIgnoreCase)) _processingPath = null;
                    if (processed)
                    {
                        _batchSucceeded++;
                        _lastSuccessfulPath = path;
                    }
                    if (failed)
                    {
                        _batchFailed++;
                        _lastFailedPath = path;
                    }
                    _pending.TryRemove(path, out _);
                    _active--;
                    if (_active == 0)
                    {
                        if (_batchSucceeded + _batchFailed > 0)
                        {
                            completedBatch = new ProcessingBatchResult(DateTimeOffset.Now, _batchSucceeded, _batchFailed, _lastSuccessfulPath, _lastFailedPath);
                            batchNotificationsEnabled = _batchNotificationsEnabled;
                        }
                        _idle.TrySetResult();
                    }
                }
                _serial.Release();
                if (completedBatch is not null)
                {
                    var result = completedBatch;
                    if (GetActiveCount() == 0) _processingStatus("待機中");
                    _batchResult(result);
                    if (batchNotificationsEnabled)
                    {
                        var message = result.Failed == 0
                            ? $"画像処理が完了しました。\n成功 {result.Succeeded}件"
                            : $"画像処理が完了しました。\n成功 {result.Succeeded}件・失敗 {result.Failed}件";
                        _notify(new ProcessingNotice(message, result.Failed > 0, result.LastFailedPath ?? result.LastSuccessfulPath));
                    }
                }
                else if (GetActiveCount() == 0)
                {
                    _processingStatus("待機中");
                }
            }
        });
    }

    private async Task<bool> ProcessOne(string path, AppSettings settings)
    {
        await WaitUntilStable(path); if (!File.Exists(path)) return false;
        var originalPath = Path.Combine(settings.OriginalFolder, Path.GetFileName(path));
        if (File.Exists(originalPath)) return false;
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".retouching.tmp";
        var creation = File.GetCreationTimeUtc(path); _suppressUntil[path] = DateTime.UtcNow.AddMinutes(2);
        try
        {
            Enhancer.Process(path, temp, settings.TtaEnabled); File.SetCreationTimeUtc(temp, creation); MoveAcrossVolumes(path, originalPath);
            try { File.Move(temp, path, false); } catch { if (!File.Exists(path)) MoveAcrossVolumes(originalPath, path); throw; }
            EnforceLimit(settings, path);
            _log($"処理完了: {Path.GetFileName(path)}");
            var waiting = Math.Max(0, GetActiveCount() - 1);
            _processingStatus($"処理完了: {Path.GetFileName(path)}（待機中 {waiting}件）");
            return true;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private string QueuedStatus()
    {
        lock (_gate) return QueuedStatusLocked();
    }

    private string QueuedStatusLocked()
    {
        string? processing;
        processing = _processingPath;
        var active = _active;
        return processing is null
            ? $"処理待ち（{active}件）"
            : ProcessingStatus(processing, active);
    }

    private int GetActiveCount()
    {
        lock (_gate) return _active;
    }

    private string ProcessingStatus(string path) => ProcessingStatus(path, GetActiveCount());

    private static string ProcessingStatus(string path, int active) =>
        $"処理中: {Path.GetFileName(path)}（待機中 {Math.Max(0, active - 1)}件）";

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

    private void EnforceLimit(AppSettings settings, string currentPath)
    {
        lock (_gate)
        {
            var protectedPaths = new HashSet<string>(_pending.Keys, StringComparer.OrdinalIgnoreCase);
            protectedPaths.UnionWith(_reconfigureCandidates);
            protectedPaths.UnionWith(_deferredCandidates);
            protectedPaths.Remove(currentPath);
            var files = new DirectoryInfo(settings.ScreenshotFolder)
                .GetFiles("*.png")
                .Where(file => !protectedPaths.Contains(file.FullName))
                .OrderBy(f => f.CreationTimeUtc)
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            while (files.Count > 100)
            {
                var oldest = files[0]; var destination = Path.Combine(settings.BackupFolder, oldest.Name); var suffix = 1;
                while (File.Exists(destination)) destination = Path.Combine(settings.BackupFolder, $"{Path.GetFileNameWithoutExtension(oldest.Name)}_{suffix++}{oldest.Extension}");
                MoveAcrossVolumes(oldest.FullName, destination); files.RemoveAt(0); _log($"backupへ移動: {oldest.Name}");
            }
        }
    }

    private static void MoveAcrossVolumes(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try { File.Move(source, destination, false); } catch (IOException) { File.Copy(source, destination, false); File.Delete(source); }
    }

    public async Task<ReconfigureCapture> DrainForReconfigureAsync()
    {
        Task idle;
        DateTime startedAtUtc;
        string previousScreenshotFolder;
        FileSystemWatcher? previous;
        lock (_gate)
        {
            if (_disposed) return new(DateTime.UtcNow, _settings.ScreenshotFolder, Array.Empty<string>());
            startedAtUtc = DateTime.UtcNow;
            previousScreenshotFolder = _settings.ScreenshotFolder;
            _reconfiguring = true;
            _reconfigureWatcher = _watcher;
            _reconfigureSeen.Clear();
            _reconfigureCandidates.Clear();
            _configurationGeneration++;
            idle = _idle.Task;
        }

        _monitorStatus("設定変更待ち");
        _processingStatus($"設定変更待ち（残り {GetActiveCount()}件）");
        while (!idle.IsCompleted)
        {
            await Task.WhenAny(idle, Task.Delay(250));
            if (!idle.IsCompleted) _processingStatus($"設定変更待ち（残り {GetActiveCount()}件）");
        }

        lock (_gate)
        {
            previous = _reconfigureWatcher;
            _accepting = false;
            _watcher = null;
        }
        previous?.Dispose();

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(previousScreenshotFolder, "*.png"))
            {
                if (ChangedSince(file, startedAtUtc)) candidates.Add(file);
            }
        }
        catch (Exception ex) { _log($"設定変更中の画像確認に失敗しました: {ex.Message}"); }

        lock (_gate)
        {
            candidates.UnionWith(_reconfigureCandidates);
            _deferredCandidates.UnionWith(candidates);
            _reconfiguring = false;
            _reconfigureWatcher = null;
            _reconfigureSeen.Clear();
            _reconfigureCandidates.Clear();
        }
        return new(startedAtUtc, previousScreenshotFolder, candidates.ToArray());
    }

    public void QueueReconfigureCapture(ReconfigureCapture capture, string folderToScan)
    {
        var paths = new HashSet<string>(capture.CandidatePaths, StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var folder in new[] { capture.PreviousScreenshotFolder, folderToScan }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.png"))
                {
                    if (ChangedSince(file, capture.StartedAtUtc)) paths.Add(file);
                }
            }
        }
        catch (Exception ex) { _log($"設定変更後の画像確認に失敗しました: {ex.Message}"); }

        lock (_gate)
        {
            if (_disposed) return;
            _deferredCandidates.UnionWith(paths);
        }
        try
        {
            foreach (var path in paths) Queue(path);
        }
        finally
        {
            lock (_gate) _deferredCandidates.ExceptWith(paths);
        }
        if (GetActiveCount() == 0) _processingStatus("待機中");
    }

    public async Task StopAsync(TimeSpan timeout)
    {
        FileSystemWatcher? previous;
        Task idle;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _accepting = false;
            previous = _watcher;
            _watcher = null;
            idle = _idle.Task;
        }
        previous?.Dispose();
        try { await idle.WaitAsync(timeout); } catch (TimeoutException) { _log("終了待ち時間を超えたため終了しました。"); }
    }

    public void Dispose() => StopAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();

    private static TaskCompletionSource CompletedSource() { var value = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); value.SetResult(); return value; }
}
