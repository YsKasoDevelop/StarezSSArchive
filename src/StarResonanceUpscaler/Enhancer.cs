using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

static class Enhancer
{
    public static void Process(string input, string output, bool ttaEnabled)
    {
        if (File.Exists(output)) throw new IOException("出力先が既に存在します: " + output);
        var engine = EngineInstallation.Root;
        var exe = Path.Combine(engine, "realesrgan-ncnn-vulkan.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("engineフォルダが必要です", exe);
        var temp = Path.Combine(Path.GetTempPath(), "StarResonance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            // Snapshot the input while holding a read lock; the game cannot modify this copy.
            var source = Path.Combine(temp, "input.png");
            using (var read = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var copy = File.Create(source)) read.CopyTo(copy);
            var original = Read(source);
            var srPath = Path.Combine(temp, "sr.png");
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = engine };
            foreach (var arg in new[] { "-i", source, "-o", srPath, "-n", "realesrgan-x4plus-anime",
                "-s", "4", "-t", "128" })
                start.ArgumentList.Add(arg);
            if (ttaEnabled) start.ArgumentList.Add("-x");
            start.ArgumentList.Add("-m");
            start.ArgumentList.Add(Path.Combine(engine, "models"));
            using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("AI起動失敗");
            var error = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(600000)) { process.Kill(true); process.WaitForExit(); throw new IOException("AI処理タイムアウト"); }
            Task.WaitAll(error, stdout);
            if (process.ExitCode != 0 || !File.Exists(srPath)) throw new IOException("AI処理失敗: " + error.Result);
            var sr = Read(srPath);
            int w = original.PixelWidth, h = original.PixelHeight;
            if (sr.PixelWidth != w * 4 || sr.PixelHeight != h * 4) throw new IOException("AI出力サイズが不正です");
            var src = Bytes(original);
            var high = Bytes(sr);
            var result = EnhanceAtOriginalSize(src, high, w, h);
            var frame = BitmapSource.Create(w, h, original.DpiX, original.DpiY, PixelFormats.Bgra32, null, result, w * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using var file = new FileStream(output, FileMode.CreateNew);
            encoder.Save(file);
            Console.WriteLine($"AI 4x{(ttaEnabled ? " + TTA" : "")} → {w}×{h} / 適応型ディテール補正 / 元の色を保持");
        }
        finally
        {
            // This unique directory is owned by this invocation.
            Directory.Delete(temp, true);
        }
    }
    static byte[] EnhanceAtOriginalSize(byte[] source, byte[] high, int w, int h)
    {
        // Reduce the 4x luminance with a scale-aware Lanczos3 kernel. Chroma stays
        // from the original image so the AI pass cannot shift the scene's colors.
        var reducedLuma = ReduceLuminanceLanczos3(high, w, h);
        var sourceLuma = new double[w * h];
        for (int i = 0; i < sourceLuma.Length; i++) sourceLuma[i] = Luma(source, i * 4);

        // Feed more AI detail into edges and less into flat areas such as sky or skin.
        var flatReference = Blur(sourceLuma, w, h, .8);
        var candidate = (byte[])source.Clone();
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int pixel = y * w + x;
                double edgeMask = Math.Clamp((Math.Abs(sourceLuma[pixel] - flatReference[pixel]) - .35) / 3, 0, 1);
                double aiMix = .55 + .45 * edgeMask;
                double delta = Math.Clamp((reducedLuma[pixel] - sourceLuma[pixel]) * aiMix, -28, 28);
                AddLuminanceOffset(source, candidate, pixel * 4, delta);
            }
        });

        // A restrained, edge-aware unsharp pass makes the effect visible at the
        // original pixel size while limiting halos around cel-shaded outlines.
        var candidateLuma = new double[w * h];
        for (int i = 0; i < candidateLuma.Length; i++) candidateLuma[i] = Luma(candidate, i * 4);
        var fine = Blur(candidateLuma, w, h, .65);
        var broad = Blur(candidateLuma, w, h, 1.5);
        var result = (byte[])candidate.Clone();
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int pixel = y * w + x;
                double detail = candidateLuma[pixel] - fine[pixel];
                double edgeMask = Math.Clamp((Math.Abs(detail) - .35) / 2, 0, 1);
                double delta = (detail * 1.65 + (fine[pixel] - broad[pixel]) * .35) * edgeMask;
                if (delta > 0) delta *= .6;
                delta = Math.Clamp(delta, -15, 8);
                delta = LimitToLocalRange(candidateLuma, w, h, x, y, delta);
                AddLuminanceOffset(candidate, result, pixel * 4, delta);
            }
        });
        return result;
    }
    static double[] ReduceLuminanceLanczos3(byte[] high, int w, int h)
    {
        const int scale = 4;
        int highWidth = checked(w * scale), highHeight = checked(h * scale);
        var kernel = new double[24];
        double total = 0;
        for (int k = 0; k < kernel.Length; k++) total += kernel[k] = Lanczos3((k - 10 - 1.5) / scale);
        for (int k = 0; k < kernel.Length; k++) kernel[k] /= total;

        // Horizontal pass is kept as float to reduce memory pressure on 4K captures.
        var horizontal = new float[checked(w * highHeight)];
        Parallel.For(0, highHeight, y =>
        {
            for (int x = 0; x < w; x++)
            {
                double value = 0;
                for (int k = 0; k < kernel.Length; k++)
                    value += kernel[k] * Luma(high, (y * highWidth + Math.Clamp(x * scale + k - 10, 0, highWidth - 1)) * 4);
                horizontal[y * w + x] = (float)value;
            }
        });

        var reduced = new double[checked(w * h)];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                double value = 0;
                for (int k = 0; k < kernel.Length; k++)
                    value += kernel[k] * horizontal[Math.Clamp(y * scale + k - 10, 0, highHeight - 1) * w + x];
                reduced[y * w + x] = Math.Clamp(value, 0, 255);
            }
        });
        return reduced;
    }
    static double Lanczos3(double x)
    {
        x = Math.Abs(x);
        if (x < 1e-10) return 1;
        if (x >= 3) return 0;
        return Math.Sin(Math.PI * x) * Math.Sin(Math.PI * x / 3) / (Math.PI * Math.PI * x * x / 3);
    }
    static double LimitToLocalRange(double[] luminance, int w, int h, int x, int y, double delta)
    {
        int index = y * w + x;
        double low = luminance[index], high = luminance[index];
        for (int yy = Math.Max(0, y - 1); yy <= Math.Min(h - 1, y + 1); yy++)
        for (int xx = Math.Max(0, x - 1); xx <= Math.Min(w - 1, x + 1); xx++)
        {
            double value = luminance[yy * w + xx];
            low = Math.Min(low, value);
            high = Math.Max(high, value);
        }
        double target = Math.Clamp(luminance[index] + delta, Math.Max(0, low - 1), Math.Min(255, high + 1));
        return target - luminance[index];
    }
    static void AddLuminanceOffset(byte[] source, byte[] destination, int index, double delta)
    {
        double minimum = Math.Min(source[index], Math.Min(source[index + 1], source[index + 2]));
        double maximum = Math.Max(source[index], Math.Max(source[index + 1], source[index + 2]));
        delta = Math.Clamp(delta, -minimum, 255 - maximum);
        for (int c = 0; c < 3; c++) destination[index + c] = (byte)Math.Clamp(Math.Round(source[index + c] + delta), 0, 255);
    }
    static void SharpenDetails(byte[] pixels, int w, int h)
    {
        var luminance = new double[w * h];
        for (int i = 0; i < luminance.Length; i++) luminance[i] = Luma(pixels, i * 4);
        var fine = Blur(luminance, w, h, 1.0);
        var broad = Blur(luminance, w, h, 3.0);
        for (int i = 0; i < luminance.Length; i++)
        {
            double detail = luminance[i] - fine[i];
            // Suppress flat-area noise, strengthen real edges at the final pixel size.
            double mask = Math.Clamp((Math.Abs(detail) - .45) / 2.5, 0, 1);
            double delta = (detail * 1.25 + (fine[i] - broad[i]) * .22) * mask;
            // Bright halos look artificial on cel-shaded outlines.
            if (delta > 0) delta *= .4;
            delta = Math.Clamp(delta, -12, 5);
            int p = i * 4;
            double min = Math.Min(pixels[p], Math.Min(pixels[p+1], pixels[p+2]));
            double max = Math.Max(pixels[p], Math.Max(pixels[p+1], pixels[p+2]));
            delta = Math.Clamp(delta, -min, 255 - max);
            for (int c = 0; c < 3; c++) pixels[p+c] = (byte)Math.Clamp(Math.Round(pixels[p+c] + delta), 0, 255);
        }
    }
    static double[] Blur(double[] source, int w, int h, double sigma)
    {
        int radius = (int)Math.Ceiling(sigma * 3);
        var kernel = new double[radius * 2 + 1];
        double total = 0;
        for (int k = -radius; k <= radius; k++) total += kernel[k+radius] = Math.Exp(-k*k/(2*sigma*sigma));
        for (int k = 0; k < kernel.Length; k++) kernel[k] /= total;
        var horizontal = new double[source.Length];
        var output = new double[source.Length];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            for (int k = -radius; k <= radius; k++)
                horizontal[y*w+x] += source[y*w+Math.Clamp(x+k, 0, w-1)] * kernel[k+radius];
        });
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            for (int k = -radius; k <= radius; k++)
                output[y*w+x] += horizontal[Math.Clamp(y+k, 0, h-1)*w+x] * kernel[k+radius];
        });
        return output;
    }
    static BitmapSource Read(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        frame.Freeze();
        return frame;
    }
    static byte[] Bytes(BitmapSource image)
    {
        var bytes = new byte[checked(image.PixelWidth * image.PixelHeight * 4)];
        image.CopyPixels(bytes, image.PixelWidth * 4, 0);
        return bytes;
    }
    static double Luma(byte[] p, int i) => .114 * p[i] + .587 * p[i+1] + .299 * p[i+2];
}
