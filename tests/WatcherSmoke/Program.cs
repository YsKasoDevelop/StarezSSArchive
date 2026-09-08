using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;

internal static class WatcherSmokeProgram
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(8);

    private static async Task Main()
    {
        var failures = new List<string>();
        await Run("transition capture and old/new folder scan", TransitionCaptureAndFolderScan, failures);
        await Run("transition candidates stay protected from retention", TransitionCandidatesStayProtected, failures);
        await Run("configured watcher stays gated until acceptance", ConfiguredWatcherStaysGated, failures);
        await Run("batch scan skips processed files", BatchScanSkipsProcessedFiles, failures);
        await Run("sample failure is reported instead of escaping", SampleFailureIsReported, failures);

        if (failures.Count == 0)
        {
            Console.WriteLine("PASS: watcher transition smoke tests");
            return;
        }

        foreach (var failure in failures) Console.Error.WriteLine("FAIL: " + failure);
        Environment.ExitCode = 1;
    }

    private static async Task Run(string name, Func<Task> test, ICollection<string> failures)
    {
        try
        {
            await test();
            Console.WriteLine("PASS: " + name);
        }
        catch (Exception ex)
        {
            failures.Add(name + " - " + ex.Message);
        }
    }

    private static async Task TransitionCaptureAndFolderScan()
    {
        var root = Directory.CreateTempSubdirectory("starez-watcher-transition-").FullName;
        try
        {
            var oldFolder = Directory.CreateDirectory(Path.Combine(root, "old")).FullName;
            var newFolder = Directory.CreateDirectory(Path.Combine(root, "new")).FullName;
            var backup = Directory.CreateDirectory(Path.Combine(root, "backup")).FullName;
            var original = Directory.CreateDirectory(Path.Combine(root, "original")).FullName;
            var settings = Settings(oldFolder, backup, original, true, true);
            var statuses = new ConcurrentQueue<string>();
            var notices = new ConcurrentQueue<ProcessingNotice>();
            using var watcher = new WatcherService(settings, _ => { }, statuses.Enqueue, _ => { }, notices.Enqueue, _ => { });

            var activePath = Path.Combine(oldFolder, "active.png");
            ReconfigureCapture capture;
            using (var active = new FileStream(activePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                active.WriteByte(1);
                active.Flush(true);
                await WaitUntil(() => statuses.Any(text => text.Contains("active.png", StringComparison.Ordinal)), "the active file entered processing");
                var draining = watcher.DrainForReconfigureAsync();
                await Task.Delay(100);
                var duringPath = Path.Combine(oldFolder, "during.png");
                await File.WriteAllBytesAsync(duringPath, new byte[] { 1 });
                await Task.Delay(100);
                active.Dispose();
                capture = await draining;
                Assert(capture.PreviousScreenshotFolder.Equals(oldFolder, StringComparison.OrdinalIgnoreCase), "capture kept the previous folder");
                Assert(capture.CandidatePaths.Contains(duringPath, StringComparer.OrdinalIgnoreCase), "the transition PNG was captured");
            }

            var next = Settings(newFolder, backup, original, true, true);
            Assert(watcher.Configure(next, acceptEvents: false), "new watcher configured while gated");
            var gatedPath = Path.Combine(newFolder, "gated.png");
            await File.WriteAllBytesAsync(gatedPath, new byte[] { 1 });
            await Task.Delay(700);
            Assert(!notices.Any(notice => string.Equals(notice.TargetPath, gatedPath, StringComparison.OrdinalIgnoreCase)), "gated file was not accepted before save completion");
            Assert(watcher.ResumeAcceptance(), "new watcher acceptance resumed");
            watcher.QueueReconfigureCapture(capture, next.ScreenshotFolder);
            await WaitUntil(() => notices.Any(notice => string.Equals(notice.TargetPath, Path.Combine(oldFolder, "during.png"), StringComparison.OrdinalIgnoreCase))
                && notices.Any(notice => string.Equals(notice.TargetPath, gatedPath, StringComparison.OrdinalIgnoreCase)), "captured files were processed after acceptance");
            await watcher.StopAsync(TimeSpan.FromSeconds(5));
        }
        finally { TryDelete(root); }
    }

    private static async Task TransitionCandidatesStayProtected()
    {
        var root = Directory.CreateTempSubdirectory("starez-watcher-retention-").FullName;
        try
        {
            var screenshot = Directory.CreateDirectory(Path.Combine(root, "screenshots")).FullName;
            var backup = Directory.CreateDirectory(Path.Combine(root, "backup")).FullName;
            var original = Directory.CreateDirectory(Path.Combine(root, "original")).FullName;
            var settings = Settings(screenshot, backup, original, false, false);
            using var watcher = new WatcherService(settings, _ => { }, _ => { }, _ => { }, _ => { }, _ => { });
            var protectedCandidate = Path.Combine(screenshot, "000-protected.png");
            var deferredCandidate = Path.Combine(screenshot, "001-deferred.png");
            await File.WriteAllBytesAsync(protectedCandidate, new byte[] { 1 });
            await File.WriteAllBytesAsync(deferredCandidate, new byte[] { 1 });
            for (var i = 2; i <= 102; i++)
            {
                var path = Path.Combine(screenshot, $"{i:000}-ordinary.png");
                await File.WriteAllBytesAsync(path, new byte[] { 1 });
                File.SetCreationTimeUtc(path, DateTime.UtcNow.AddMinutes(-10).AddSeconds(i));
            }
            File.SetCreationTimeUtc(protectedCandidate, DateTime.UtcNow.AddMinutes(-10));
            File.SetCreationTimeUtc(deferredCandidate, DateTime.UtcNow.AddMinutes(-9).AddSeconds(1));

            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            ((HashSet<string>)typeof(WatcherService).GetField("_reconfigureCandidates", flags)!.GetValue(watcher)!).Add(protectedCandidate);
            ((HashSet<string>)typeof(WatcherService).GetField("_deferredCandidates", flags)!.GetValue(watcher)!).Add(deferredCandidate);
            typeof(WatcherService).GetMethod("EnforceLimit", flags)!.Invoke(watcher, new object[] { settings, string.Empty });

            Assert(File.Exists(protectedCandidate), "transition candidate remained in the screenshot folder");
            Assert(File.Exists(deferredCandidate), "deferred candidate remained in the screenshot folder");
            Assert(File.Exists(Path.Combine(backup, "002-ordinary.png")), "an unprotected old file was moved to backup");
            watcher.Dispose();
        }
        finally { TryDelete(root); }
    }

    private static async Task ConfiguredWatcherStaysGated()
    {
        var root = Directory.CreateTempSubdirectory("starez-watcher-gate-").FullName;
        try
        {
            var first = Directory.CreateDirectory(Path.Combine(root, "first")).FullName;
            var second = Directory.CreateDirectory(Path.Combine(root, "second")).FullName;
            var backup = Directory.CreateDirectory(Path.Combine(root, "backup")).FullName;
            var original = Directory.CreateDirectory(Path.Combine(root, "original")).FullName;
            var notices = new ConcurrentQueue<ProcessingNotice>();
            using var watcher = new WatcherService(Settings(first, backup, original, true, true), _ => { }, _ => { }, _ => { }, notices.Enqueue, _ => { });
            var next = Settings(second, backup, original, true, true);
            Assert(watcher.Configure(next, acceptEvents: false), "watcher configured in gated mode");
            var gated = Path.Combine(second, "save-pending.png");
            await File.WriteAllBytesAsync(gated, new byte[] { 1 });
            await Task.Delay(700);
            Assert(!notices.Any(notice => string.Equals(notice.TargetPath, gated, StringComparison.OrdinalIgnoreCase)), "save-pending file was accepted while gated");
            Assert(watcher.ResumeAcceptance(), "acceptance resumed");
            var accepted = Path.Combine(second, "after-save.png");
            await File.WriteAllBytesAsync(accepted, new byte[] { 1 });
            await WaitUntil(() => notices.Any(notice => string.Equals(notice.TargetPath, accepted, StringComparison.OrdinalIgnoreCase)), "post-save file was accepted");
            await watcher.StopAsync(TimeSpan.FromSeconds(5));
        }
        finally { TryDelete(root); }
    }

    private static async Task BatchScanSkipsProcessedFiles()
    {
        var root = Directory.CreateTempSubdirectory("starez-watcher-batch-").FullName;
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, "batch")).FullName;
            var backup = Directory.CreateDirectory(Path.Combine(root, "backup")).FullName;
            var original = Directory.CreateDirectory(Path.Combine(root, "original")).FullName;
            var ledgerPath = Path.Combine(root, "processed.json");
            var processed = Path.Combine(folder, "processed.png");
            await File.WriteAllBytesAsync(processed, new byte[] { 1, 2, 3 });
            var ledger = new ProcessedLedger(ledgerPath, _ => { });
            ledger.Mark(processed);
            var statuses = new ConcurrentQueue<string>();
            using var watcher = new WatcherService(Settings(folder, backup, original, true, false, true), _ => { }, statuses.Enqueue, _ => { }, _ => { }, _ => { }, ledgerPath);
            await watcher.QueueUnprocessedAsync(folder);
            Assert(statuses.Any(status => status == "未処理画像はありません"), "processed file was skipped by the batch scan");
            Assert(File.Exists(processed), "processed file remained in place");
            await watcher.StopAsync(TimeSpan.FromSeconds(5));
        }
        finally { TryDelete(root); }
    }

    private static Task SampleFailureIsReported()
    {
        var root = Directory.CreateTempSubdirectory("starez-sample-error-").FullName;
        try
        {
            var missing = Path.Combine(root, "missing.png");
            var output = Path.Combine(root, "output.png");
            var exitCode = Program.RunSample(missing, output);
            Assert(exitCode != 0, "sample mode returned success for an invalid input");
            Assert(!File.Exists(output), "sample mode created an output after failure");
        }
        finally { TryDelete(root); }
        return Task.CompletedTask;
    }

    private static AppSettings Settings(string screenshot, string backup, string original, bool retouch, bool notifications, bool discardOriginal = false) =>
        new(screenshot, backup, original, retouch, false, false, notifications, screenshot, discardOriginal);

    private static async Task WaitUntil(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("Timed out while waiting for " + description + ".");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
