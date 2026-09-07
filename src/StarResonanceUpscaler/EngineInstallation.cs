using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using Forms = System.Windows.Forms;

internal static class EngineInstallation
{
    internal const string DownloadUrl = "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesrgan-ncnn-vulkan-20220424-windows.zip";
    internal const string ArchiveHash = "ABC02804E17982A3BE33675E4D471E91EA374E65B70167ABC09E31ACB412802D";
    internal static string Root => Path.Combine(AppInfo.DataFolder, "engine-20220424");
    private static readonly string[] Files = { "realesrgan-ncnn-vulkan.exe", "vcomp140.dll", "models/realesrgan-x4plus-anime.bin", "models/realesrgan-x4plus-anime.param", "README_windows.md" };
    internal static bool IsReady => File.Exists(Path.Combine(Root, "installed.ok")) && Files.All(f => File.Exists(Path.Combine(Root, f)));

    internal static async Task InstallAsync(IProgress<string> progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(AppInfo.DataFolder);
        var staging = Path.Combine(AppInfo.DataFolder, "setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var archive = Path.Combine(staging, "download.zip");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            using var response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = File.Create(archive))
            {
                var buffer = new byte[81920];
                long received = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    received += count;
                    if (received > 200_000_000) throw new InvalidDataException("配布物のサイズが想定を超えています。");
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    progress.Report($"公式配布元から取得中… {received / 1048576} MB");
                }
            }
            progress.Report("配布物の整合性を確認しています…");
            await using (var stream = File.OpenRead(archive))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
                if (hash != ArchiveHash) throw new InvalidDataException("配布物の検証に失敗しました。インストールを中止しました。");
            }
            var extracted = Path.Combine(staging, "engine");
            Directory.CreateDirectory(extracted);
            using (var zip = ZipFile.OpenRead(archive))
            foreach (var file in Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = zip.GetEntry(file) ?? throw new InvalidDataException("必要なファイルがありません: " + file);
                var target = Path.Combine(extracted, file);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target);
            }
            File.WriteAllText(Path.Combine(extracted, "installed.ok"), ArchiveHash);
            cancellationToken.ThrowIfCancellationRequested();
            // Preserve an incomplete previous installation; never delete user data.
            if (Directory.Exists(Root)) Directory.Move(Root, Root + ".previous-" + Guid.NewGuid().ToString("N"));
            Directory.Move(extracted, Root);
        }
        finally
        {
            try { Directory.Delete(staging, true); } catch { /* A later retry uses a unique staging directory. */ }
        }
    }
}

internal sealed class EngineSetupForm : Forms.Form
{
    private readonly Forms.Label status = new() { Dock = Forms.DockStyle.Fill, Padding = new Forms.Padding(18), Text = "初回のみ、AI処理に必要なファイルをReal-ESRGAN公式GitHubから取得します。\nインターネット接続が必要です。写真の送信は行いません。\n\n第三者ソフトウェアには各配布元の利用条件が適用されます。\n同梱のライセンス案内をご確認ください。" };
    private readonly Forms.Button install = new() { Text = "同意してセットアップ", Dock = Forms.DockStyle.Bottom, Height = 42 };
    private readonly CancellationTokenSource cancellation = new();
    private bool busy;
    internal EngineSetupForm()
    {
        Text = AppInfo.Name + " — 初回セットアップ";
        ClientSize = new System.Drawing.Size(550, 245);
        StartPosition = Forms.FormStartPosition.CenterScreen;
        var notices = new Forms.Button { Text = "ライセンス案内を開く", Dock = Forms.DockStyle.Bottom, Height = 36 };
        notices.Click += (_, _) =>
        {
            var start = new System.Diagnostics.ProcessStartInfo("notepad.exe");
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "engine", "THIRD-PARTY-NOTICES.md"));
            System.Diagnostics.Process.Start(start);
        };
        Controls.Add(status);
        Controls.Add(notices);
        Controls.Add(install);
        install.Click += async (_, _) =>
        {
            busy = true;
            install.Enabled = false;
            try
            {
                await EngineInstallation.InstallAsync(new Progress<string>(text => status.Text = text), cancellation.Token);
                busy = false;
                DialogResult = Forms.DialogResult.OK;
                Close();
            }
            catch (OperationCanceledException) { busy = false; Close(); }
            catch (Exception ex)
            {
                busy = false;
                status.Text = "セットアップできませんでした。接続を確認して再試行してください。\n\n" + ex.Message;
                install.Enabled = true;
            }
        };
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; cancellation.Cancel(); status.Text = "キャンセルしています…"; } };
        FormClosed += (_, _) => cancellation.Dispose();
    }
}
