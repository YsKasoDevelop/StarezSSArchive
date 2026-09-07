using System.IO;
using System.Security.Cryptography;

internal static class AppInfo
{
    public const string Name = "Setup test";
    public static string DataFolder { get; } = Path.Combine(Path.GetTempPath(), "StarezSSArchive-test-" + Guid.NewGuid().ToString("N"));
}

internal static class Program
{
    private static async Task Main()
    {
        if (EngineInstallation.IsReady) throw new Exception("Test is not isolated.");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try { await EngineInstallation.InstallAsync(new Progress<string>(), canceled.Token); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { Console.WriteLine("PASS: cancellation"); }
        if (EngineInstallation.IsReady) throw new Exception("Canceled installation is ready.");
        await EngineInstallation.InstallAsync(new Progress<string>(), CancellationToken.None);
        if (!EngineInstallation.IsReady) throw new Exception("Installation incomplete.");
        using var engine = File.OpenRead(Path.Combine(EngineInstallation.Root, "realesrgan-ncnn-vulkan.exe"));
        if (Convert.ToHexString(SHA256.HashData(engine)) != "07E49F7CBB4EDE01AE4DD4C399D3A7E5846E3D2085C3128EFF881E55CB7B1A0C") throw new Exception("Unexpected engine.");
        if (Directory.GetFiles(EngineInstallation.Root, "*", SearchOption.AllDirectories).Length != 6) throw new Exception("Unexpected files.");
        Console.WriteLine("PASS: official download, hash, extraction, no sample media/debug DLL, ready marker");
        var input = Path.Combine(AppInfo.DataFolder, "input.png");
        var output = Path.Combine(AppInfo.DataFolder, "output.png");
        using (var bitmap = new System.Drawing.Bitmap(64, 64))
        {
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.Clear(System.Drawing.Color.CornflowerBlue);
            graphics.FillEllipse(System.Drawing.Brushes.Gold, 8, 8, 40, 40);
            bitmap.Save(input, System.Drawing.Imaging.ImageFormat.Png);
        }
        var before = SHA256.HashData(File.ReadAllBytes(input));
        Enhancer.Process(input, output, false);
        using (var result = new System.Drawing.Bitmap(output))
            if (result.Width != 64 || result.Height != 64) throw new Exception("Output dimensions changed.");
        if (!before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(input)))) throw new Exception("Input changed.");
        Console.WriteLine("PASS: GPU inference, output dimensions, original unchanged");
        Console.WriteLine("Test files retained at: " + AppInfo.DataFolder);
    }
}
