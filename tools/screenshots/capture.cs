#:property TargetFramework=net10.0
#:property PublishAot=false
#:property AllowUnsafeBlocks=true
// Regenerates the documentation screenshots in docs/images.
//
// Starts the Godot editor on the sample project with a throw-away editor configuration (English, default theme, no
// local directories of your own), opens every ePlugin dialog with sample data, and captures each window with
// its frame. Needs a desktop session with X11 or Xwayland, GODOT_BIN pointing at a Godot .NET editor, and the .NET 10
// SDK (git for the progress helper build). Windows appear on screen while it runs; don't touch the mouse or keyboard until the editor closes.
//
//   dotnet run tools/screenshots/capture.cs [-- output directory, default docs/images]
//
// EPLUGIN_SCREENSHOTS_SCALE (default 0.5) scales the images, e.g. 0.5 for a 200% display.
// EPLUGIN_SCREENSHOTS_DARK=1 switches GNOME to its dark style while capturing, so window frames match the editor.
//
// DocScreenshots.cs runs this program again for every window (`window --out <png> ...`, see WindowCapture).
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

try
{
    return args is ["window", .. var rest] ? WindowCapture.Run(rest) : Screenshots.Run(args);
}
catch (CaptureException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

sealed class CaptureException(string message) : Exception(message);

/// <summary>Runs the editor with DocScreenshots.cs as an autoload and restores the project afterwards.</summary>
static class Screenshots
{
    // Paths below this directory show up in the screenshots, so it has a short, neutral name.
    private const string Samples = "/tmp/eplugin-docs";

    public static int Run(string[] args)
    {
        var here = AppContext.GetData("EntryPointFileDirectoryPath") as string ?? Environment.CurrentDirectory;
        var repo = Path.GetFullPath(Path.Combine(here, "../.."));
        var project = Path.Combine(repo, "src/eplugin-framework");
        var output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(repo, "docs/images"));
        var godot = Environment.GetEnvironmentVariable("GODOT_BIN");
        if (string.IsNullOrEmpty(godot))
            throw new CaptureException("Set GODOT_BIN to a Godot .NET editor binary.");

        var work = Directory.CreateTempSubdirectory("eplugin-screenshots-").FullName;
        string? colorScheme = null;
        // Ctrl+C also reaches the editor, which closes; the project is restored below.
        Console.CancelKeyPress += (_, e) => e.Cancel = true;
        try
        {
            File.Copy(Path.Combine(project, "project.godot"), Path.Combine(work, "project.godot"));
            File.Copy(Path.Combine(project, "addons/eplugin-state.json"), Path.Combine(work, "eplugin-state.json"));
            Directory.CreateDirectory(output);
            Directory.CreateDirectory(Path.Combine(work, "config/godot"));

            // English editor UI regardless of the system language; everything else keeps Godot's defaults.
            File.WriteAllText(Path.Combine(work, "config/godot/editor_settings-4.7.tres"), """
                [gd_resource type="EditorSettings" format=3]

                [resource]
                interface/editor/editor_language = "en"

                """);

            // Example releases of the sample plugins for the local directory: 1.1.0 of three plugins and an older 1.0.5.
            if (Directory.Exists(Samples))
                Directory.Delete(Samples, true);
            var releases = Path.Combine(Samples, "releases");
            WriteReleases(Path.Combine(project, "addons"), releases);

            // The failed final build the update failure dialog reports.
            var journal = Path.Combine(Samples, "my-game/.godot/eplugin/updates/20261011-143205-a1b2c3");
            Directory.CreateDirectory(journal);
            File.WriteAllText(Path.Combine(journal, "final-build.log"), """
                  Determining projects to restore...
                  All projects are up-to-date for restore.
                Scripts/Player.cs(42,17): error CS0117: 'SampleLogger' does not contain a definition for 'LogInfo'
                Scripts/Hud.cs(18,9): error CS0246: The type or namespace name 'SampleOptions' could not be found
                Build FAILED.
                    2 Error(s)

                """);

            // The driver joins the project for this run only.
            File.Copy(Path.Combine(here, "DocScreenshots.cs"), Path.Combine(project, "DocScreenshots.cs"), true);
            File.AppendAllText(Path.Combine(project, "project.godot"),
                "\n[autoload]\n\nDocScreenshots=\"*res://DocScreenshots.cs\"\n");
            Check(Execute("dotnet", ["build", Path.Combine(project, "EPluginFramework.sln"), "-nologo", "-v", "quiet",
                "-clp:ErrorsOnly"]), "Building the project");
            // The progress window is a separate helper; publish it like a release does when it is missing.
            var progress = Path.Combine(project, "addons/ePlugin/progress");
            if (!File.Exists(Path.Combine(progress, "ActivationProgress.dll")))
            {
                Check(Execute("dotnet", ["publish", Path.Combine(repo, "src/activation-progress/ActivationProgress.csproj"),
                    "-c", "Release", "-nologo", "-v", "quiet", "-clp:ErrorsOnly", "-o", progress]), "Publishing the progress helper");
            }

            var environment = new Dictionary<string, string>
            {
                ["LC_ALL"] = "en_US.UTF-8",
                ["LANG"] = "en_US.UTF-8",
                // X11 for Godot and the GTK progress helper, so their windows can be captured on Wayland desktops too
                // (through Xwayland).
                ["GDK_BACKEND"] = "x11",
                ["XDG_CONFIG_HOME"] = Path.Combine(work, "config"),
                ["EPLUGIN_SCREENSHOTS_OUT"] = output,
                ["EPLUGIN_SCREENSHOTS_CAPTURE"] = Environment.ProcessPath!,
                ["EPLUGIN_SCREENSHOTS_RELEASES"] = releases,
                ["EPLUGIN_SCREENSHOTS_JOURNAL"] = journal,
            };
            if (Environment.GetEnvironmentVariable("EPLUGIN_SCREENSHOTS_DARK") == "1")
            {
                colorScheme = Output("gsettings", ["get", "org.gnome.desktop.interface", "color-scheme"]).Trim();
                Execute("gsettings", ["set", "org.gnome.desktop.interface", "color-scheme", "prefer-dark"]);
                // The GTK 3 progress helper does not follow the color scheme by itself.
                environment["GTK_THEME"] = "Adwaita:dark";
            }

            RunEditor(godot, project, environment, TimeSpan.FromMinutes(10));
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EPLUGIN_SCREENSHOTS_KEEP_RAW"))
                && Directory.Exists(Path.Combine(output, "raw")))
                Directory.Delete(Path.Combine(output, "raw"), true);
            return 0;
        }
        finally
        {
            File.Delete(Path.Combine(project, "DocScreenshots.cs"));
            File.Delete(Path.Combine(project, "DocScreenshots.cs.uid"));
            if (File.Exists(Path.Combine(work, "project.godot")))
                File.Copy(Path.Combine(work, "project.godot"), Path.Combine(project, "project.godot"), true);
            if (File.Exists(Path.Combine(work, "eplugin-state.json")))
                File.Copy(Path.Combine(work, "eplugin-state.json"), Path.Combine(project, "addons/eplugin-state.json"), true);
            File.Delete(Path.Combine(project, "addons/eplugin-update-sites.json"));
            if (!string.IsNullOrEmpty(colorScheme))
                Execute("gsettings", ["set", "org.gnome.desktop.interface", "color-scheme", colorScheme]);
            Directory.Delete(work, true);
            if (Directory.Exists(Samples))
                Directory.Delete(Samples, true);
        }
    }

    private static void WriteReleases(string addons, string releases)
    {
        Directory.CreateDirectory(releases);
        foreach (var slug in new[] { "sample_plugin", "sample_dependant_plugin", "sample_optional_plugin" })
        {
            foreach (var version in new[] { "1.0.5", "1.1.0" })
            {
                using var archive = ZipFile.Open(Path.Combine(releases, $"{slug}-{version}.zip"), ZipArchiveMode.Create);
                var plugin = Path.Combine(addons, slug);
                foreach (var file in Directory.EnumerateFiles(plugin, "*", SearchOption.AllDirectories))
                {
                    var target = $"addons/{slug}/{Path.GetRelativePath(plugin, file).Replace('\\', '/')}";
                    if (Path.GetFileName(file) != "plugin.cfg")
                    {
                        archive.CreateEntryFromFile(file, target, CompressionLevel.Optimal);
                        continue;
                    }
                    var text = Regex.Replace(File.ReadAllText(file), "^version=\".*\"$", $"version=\"{version}\"", RegexOptions.Multiline);
                    using var writer = new StreamWriter(archive.CreateEntry(target, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                    writer.Write(text);
                }
            }
        }
    }

    /// <summary>Runs the editor and shows only DocScreenshots' lines of its output.</summary>
    private static void RunEditor(string godot, string project, Dictionary<string, string> environment, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(godot)
        {
            ArgumentList = { "--editor", "--display-driver", "x11", "--path", project },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var (name, value) in environment)
            start.Environment[name] = value;
        using var editor = Process.Start(start)!;
        void Show(object sender, DataReceivedEventArgs e)
        {
            if (e.Data?.Contains("DocScreenshots", StringComparison.Ordinal) == true)
                Console.WriteLine(e.Data);
        }
        editor.OutputDataReceived += Show;
        editor.ErrorDataReceived += Show;
        editor.BeginOutputReadLine();
        editor.BeginErrorReadLine();
        if (!editor.WaitForExit(timeout))
        {
            Console.Error.WriteLine($"The editor did not finish within {timeout.TotalMinutes:0} minutes.");
            editor.Kill(true);
        }
        editor.WaitForExit();
    }

    private static int Execute(string command, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(command);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        return process.ExitCode;
    }

    private static string Output(string command, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(command) { RedirectStandardOutput = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? text : "";
    }

    private static void Check(int exitCode, string step)
    {
        if (exitCode != 0)
            throw new CaptureException($"{step} failed with exit code {exitCode}.");
    }
}

/// <summary>
/// Captures an X11 window with its window manager frame and saves it as a documentation screenshot.
///
/// The frame is the window manager's parent of the client window. On GNOME it is an ARGB window, so the rounded corners
/// stay transparent. The image is trimmed, gets a soft drop shadow on a transparent background, and is scaled down for
/// the documentation. Works with X11 and Xwayland windows.
///
///   window --out &lt;png&gt; (--window &lt;X11 id&gt; | --title &lt;title&gt;) [--crop x,y,w,h [--highlight x,y,w,h]] [--scale f]
///
/// --crop captures a part of the client window without the frame; --highlight outlines a part of it.
/// --scale defaults to EPLUGIN_SCREENSHOTS_SCALE, else 0.5 (for a 200% display).
/// </summary>
static class WindowCapture
{
    // Godot's default editor accent color
    private const uint Accent = 0xFFFF9E56; // A, B, G, R: (86, 158, 255, 255)

    public static int Run(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index + 1 < args.Length; index += 2)
            options[args[index].TrimStart('-')] = args[index + 1];
        if (!options.TryGetValue("out", out var output) || args.Length % 2 != 0)
            throw new CaptureException("Usage: window --out <png> (--window <id> | --title <title>) [--crop x,y,w,h] [--highlight x,y,w,h] [--scale f]");
        var scale = double.Parse(options.GetValueOrDefault("scale")
            ?? Environment.GetEnvironmentVariable("EPLUGIN_SCREENSHOTS_SCALE") ?? "0.5", CultureInfo.InvariantCulture);

        using var display = new X11Display();
        var client = options.TryGetValue("title", out var title)
            ? display.FindByTitle(title, TimeSpan.FromSeconds(10))
            : ParseWindow(options.GetValueOrDefault("window") ?? throw new CaptureException("Pass --window or --title."));
        var frame = display.FrameOf(client);
        var image = display.Grab(frame);
        var raw = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "raw");
        Directory.CreateDirectory(raw);
        image.SavePng(Path.Combine(raw, Path.GetFileName(output)));

        var unit = 1 / scale;
        if (options.TryGetValue("crop", out var crop))
        {
            var (x, y, width, height) = Rect(crop);
            // Stay inside the client window, so none of the frame shows up at the edges.
            var size = display.Geometry(client);
            x = Math.Max(x, 0);
            y = Math.Max(y, 0);
            width = Math.Min(width, size.Width - x);
            height = Math.Min(height, size.Height - y);
            var (dx, dy) = display.ClientOffset(client, frame);
            image = image.Crop(x + dx, y + dy, width, height);
            if (options.TryGetValue("highlight", out var highlight))
            {
                var (hx, hy, hw, hh) = Rect(highlight);
                image.RoundedOutline(hx - x, hy - y, hx - x + hw - 1, hy - y + hh - 1, (int)(4 * unit),
                    Math.Max(1, (int)(1.5 * unit)), Accent);
            }
            image.Round((int)(6 * unit));
        }
        else if (image.OpaqueBounds(8) is { } bounds)
        {
            // Drop the transparent border some frames keep around the window.
            image = image.Crop(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }
        image = image.WithShadow((int)(12 * unit), (int)(4 * unit), 0.55);
        if (scale != 1)
            image = image.Resize((int)Math.Round(image.Width * scale), (int)Math.Round(image.Height * scale));
        image.SavePng(output);
        Console.WriteLine($"{output} {image.Width}x{image.Height}");
        return 0;
    }

    private static nuint ParseWindow(string value) =>
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? nuint.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : nuint.Parse(value, CultureInfo.InvariantCulture);

    private static (int X, int Y, int Width, int Height) Rect(string value)
    {
        var parts = value.Split(',').Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        return parts.Length == 4 ? (parts[0], parts[1], parts[2], parts[3]) : throw new CaptureException($"Not x,y,width,height: {value}");
    }
}

/// <summary>
/// An image with straight (not premultiplied) alpha, one pixel per uint in R, G, B, A byte order, and the few
/// operations the screenshots need. Nothing here reads image files, so no image library is needed.
/// </summary>
sealed class RgbaImage(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public uint[] Pixels { get; } = new uint[width * height];

    private static uint Pack(int r, int g, int b, int a) => (uint)(r | g << 8 | b << 16 | a << 24);
    private static byte Alpha(uint pixel) => (byte)(pixel >> 24);

    public RgbaImage Crop(int x, int y, int width, int height)
    {
        // Like Pillow, parts outside the image become transparent.
        var cropped = new RgbaImage(Math.Max(0, width), Math.Max(0, height));
        for (var row = 0; row < cropped.Height; row++)
        {
            for (var column = 0; column < cropped.Width; column++)
            {
                int sx = x + column, sy = y + row;
                if (sx >= 0 && sy >= 0 && sx < Width && sy < Height)
                    cropped.Pixels[row * cropped.Width + column] = Pixels[sy * Width + sx];
            }
        }
        return cropped;
    }

    /// <summary>Whether a pixel lies in the rounded rectangle from (x0, y0) to (x1, y1), both inclusive.</summary>
    private static bool InRoundedRect(int px, int py, int x0, int y0, int x1, int y1, int radius)
    {
        if (px < x0 || px > x1 || py < y0 || py > y1)
            return false;
        radius = Math.Max(0, Math.Min(radius, Math.Min(x1 - x0, y1 - y0) / 2));
        var dx = px - Math.Clamp(px, x0 + radius, x1 - radius);
        var dy = py - Math.Clamp(py, y0 + radius, y1 - radius);
        return dx * dx + dy * dy <= radius * radius;
    }

    /// <summary>Makes the corners outside a rounded rectangle of the whole image transparent.</summary>
    public void Round(int radius)
    {
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                if (!InRoundedRect(x, y, 0, 0, Width - 1, Height - 1, radius))
                    Pixels[y * Width + x] &= 0x00FFFFFF;
            }
        }
    }

    public void RoundedOutline(int x0, int y0, int x1, int y1, int radius, int lineWidth, uint color)
    {
        for (var y = Math.Max(0, y0); y <= Math.Min(Height - 1, y1); y++)
        {
            for (var x = Math.Max(0, x0); x <= Math.Min(Width - 1, x1); x++)
            {
                if (InRoundedRect(x, y, x0, y0, x1, y1, radius)
                    && !InRoundedRect(x, y, x0 + lineWidth, y0 + lineWidth, x1 - lineWidth, y1 - lineWidth, radius - lineWidth))
                    Pixels[y * Width + x] = color;
            }
        }
    }

    /// <summary>The smallest rectangle holding every pixel more opaque than <paramref name="threshold"/>.</summary>
    public (int X, int Y, int Width, int Height)? OpaqueBounds(byte threshold)
    {
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                if (Alpha(Pixels[y * Width + x]) <= threshold)
                    continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }
        return right < 0 ? null : (left, top, right - left + 1, bottom - top + 1);
    }

    /// <summary>The image on a transparent canvas over a soft black shadow, moved down by <paramref name="offset"/>.</summary>
    public RgbaImage WithShadow(int blur, int offset, double opacity)
    {
        var margin = blur * 2;
        var canvas = new RgbaImage(Width + 2 * margin, Height + 2 * margin + offset);
        var shadow = new float[canvas.Width * canvas.Height];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
                shadow[(y + margin + offset) * canvas.Width + x + margin] = (int)(Alpha(Pixels[y * Width + x]) * opacity);
        }
        GaussianBlur(shadow, canvas.Width, canvas.Height, blur);
        for (var index = 0; index < shadow.Length; index++)
            canvas.Pixels[index] = Pack(0, 0, 0, Math.Clamp((int)Math.Round(shadow[index]), 0, 255));
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var target = (y + margin) * canvas.Width + x + margin;
                canvas.Pixels[target] = Over(Pixels[y * Width + x], canvas.Pixels[target]);
            }
        }
        return canvas;
    }

    /// <summary>Porter-Duff "over" of two straight-alpha pixels.</summary>
    private static uint Over(uint top, uint bottom)
    {
        var topAlpha = Alpha(top) / 255.0;
        var bottomAlpha = Alpha(bottom) / 255.0 * (1 - topAlpha);
        var alpha = topAlpha + bottomAlpha;
        if (alpha <= 0)
            return 0;
        int Channel(int shift) =>
            (int)Math.Round((((top >> shift) & 0xFF) * topAlpha + ((bottom >> shift) & 0xFF) * bottomAlpha) / alpha);
        return Pack(Channel(0), Channel(8), Channel(16), (int)Math.Round(alpha * 255));
    }

    /// <summary>Approximates a Gaussian blur with standard deviation <paramref name="sigma"/> by three box blurs, like Pillow.</summary>
    private static void GaussianBlur(float[] values, int width, int height, double sigma)
    {
        if (sigma <= 0)
            return;
        // Box widths whose three passes have the wanted variance (Kovesi, "Fast almost-Gaussian filtering").
        var ideal = Math.Sqrt(12 * sigma * sigma / 3 + 1);
        var lower = (int)Math.Floor(ideal);
        if (lower % 2 == 0)
            lower--;
        var larger = Math.Round((12 * sigma * sigma - 3 * lower * lower - 12 * lower - 9) / (-4.0 * lower - 4));
        var buffer = new float[values.Length];
        for (var pass = 0; pass < 3; pass++)
        {
            var radius = ((pass < larger ? lower : lower + 2) - 1) / 2;
            BoxBlur(values, buffer, width, height, radius, horizontal: true);
            BoxBlur(buffer, values, width, height, radius, horizontal: false);
        }
    }

    private static void BoxBlur(float[] source, float[] target, int width, int height, int radius, bool horizontal)
    {
        int lines = horizontal ? height : width, length = horizontal ? width : height;
        var step = horizontal ? 1 : width;
        var scale = 1f / (2 * radius + 1);
        for (var line = 0; line < lines; line++)
        {
            var start = horizontal ? line * width : line;
            // Pixels beyond the edges count as transparent, so the window's shadow fades out evenly.
            double sum = 0;
            for (var offset = 0; offset <= Math.Min(radius, length - 1); offset++)
                sum += source[start + offset * step];
            for (var position = 0; position < length; position++)
            {
                target[start + position * step] = (float)(sum * scale);
                var entering = position + radius + 1;
                var leaving = position - radius;
                if (entering < length)
                    sum += source[start + entering * step];
                if (leaving >= 0)
                    sum -= source[start + leaving * step];
            }
        }
    }

    /// <summary>A Lanczos (a = 3) resampled copy, filtered in premultiplied alpha so transparent pixels don't darken edges.</summary>
    public RgbaImage Resize(int width, int height)
    {
        var premultiplied = new float[Pixels.Length * 4];
        for (var index = 0; index < Pixels.Length; index++)
        {
            var pixel = Pixels[index];
            var alpha = Alpha(pixel) / 255f;
            premultiplied[4 * index] = (pixel & 0xFF) * alpha;
            premultiplied[4 * index + 1] = ((pixel >> 8) & 0xFF) * alpha;
            premultiplied[4 * index + 2] = ((pixel >> 16) & 0xFF) * alpha;
            premultiplied[4 * index + 3] = Alpha(pixel);
        }
        var horizontal = Resample(premultiplied, Width, Height, width, horizontal: true);
        var both = Resample(horizontal, width, Height, height, horizontal: false);
        var resized = new RgbaImage(width, height);
        for (var index = 0; index < resized.Pixels.Length; index++)
        {
            var alpha = Math.Clamp(both[4 * index + 3], 0, 255);
            int Channel(int offset) => alpha <= 0 ? 0 : (int)Math.Clamp(Math.Round(both[4 * index + offset] * 255 / alpha), 0, 255);
            resized.Pixels[index] = Pack(Channel(0), Channel(1), Channel(2), (int)Math.Round(alpha));
        }
        return resized;
    }

    private static float[] Resample(float[] source, int width, int height, int size, bool horizontal)
    {
        var length = horizontal ? width : height;
        var scale = (double)length / size;
        var filterScale = Math.Max(scale, 1);
        var support = 3 * filterScale;
        var result = new float[(horizontal ? size * height : width * size) * 4];
        Span<double> sum = stackalloc double[4];
        for (var output = 0; output < size; output++)
        {
            var center = (output + 0.5) * scale;
            var first = Math.Max((int)(center - support + 0.5), 0);
            var last = Math.Min((int)(center + support + 0.5), length);
            var weights = new double[last - first];
            for (var input = first; input < last; input++)
                weights[input - first] = Lanczos((input - center + 0.5) / filterScale);
            var total = weights.Sum();
            if (total != 0)
            {
                for (var index = 0; index < weights.Length; index++)
                    weights[index] /= total;
            }
            var lines = horizontal ? height : width;
            for (var line = 0; line < lines; line++)
            {
                sum.Clear();
                for (var input = first; input < last; input++)
                {
                    var from = 4 * (horizontal ? line * width + input : input * width + line);
                    for (var channel = 0; channel < 4; channel++)
                        sum[channel] += source[from + channel] * weights[input - first];
                }
                var to = 4 * (horizontal ? line * size + output : output * width + line);
                for (var channel = 0; channel < 4; channel++)
                    result[to + channel] = (float)sum[channel];
            }
        }
        return result;
    }

    private static double Lanczos(double x)
    {
        static double Sinc(double value) => value == 0 ? 1 : Math.Sin(Math.PI * value) / (Math.PI * value);
        return x is > -3 and < 3 ? Sinc(x) * Sinc(x / 3) : 0;
    }

    /// <summary>Writes an 8-bit RGBA PNG, choosing each row's filter like libpng and compressing as small as zlib can.</summary>
    public void SavePng(string path)
    {
        using var file = File.Create(path);
        file.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, Width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), Height);
        header[8] = 8; // bits per channel
        header[9] = 6; // RGBA
        WriteChunk(file, "IHDR", header);

        var stride = Width * 4;
        var bytes = MemoryMarshal.AsBytes(Pixels.AsSpan()).ToArray();
        if (!BitConverter.IsLittleEndian)
            throw new CaptureException("Writing PNG files needs a little-endian machine.");
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var candidate = new byte[stride];
            var best = new byte[stride];
            for (var y = 0; y < Height; y++)
            {
                var row = bytes.AsSpan(y * stride, stride);
                var previous = y > 0 ? bytes.AsSpan((y - 1) * stride, stride) : new byte[stride];
                byte bestFilter = 0;
                var bestCost = long.MaxValue;
                for (byte filter = 0; filter < 5; filter++)
                {
                    long cost = 0;
                    for (var x = 0; x < stride; x++)
                    {
                        int left = x >= 4 ? row[x - 4] : 0, up = previous[x], upLeft = x >= 4 ? previous[x - 4] : 0;
                        var predicted = filter switch
                        {
                            1 => left,
                            2 => up,
                            3 => (left + up) / 2,
                            4 => Paeth(left, up, upLeft),
                            _ => 0,
                        };
                        candidate[x] = (byte)(row[x] - predicted);
                        cost += Math.Abs((int)(sbyte)candidate[x]);
                    }
                    if (cost >= bestCost)
                        continue;
                    bestCost = cost;
                    bestFilter = filter;
                    candidate.CopyTo(best, 0);
                }
                zlib.WriteByte(bestFilter);
                zlib.Write(best);
            }
        }
        WriteChunk(file, "IDAT", compressed.ToArray());
        WriteChunk(file, "IEND", []);
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        var estimate = left + up - upLeft;
        int toLeft = Math.Abs(estimate - left), toUp = Math.Abs(estimate - up), toUpLeft = Math.Abs(estimate - upLeft);
        return toLeft <= toUp && toLeft <= toUpLeft ? left : toUp <= toUpLeft ? up : upLeft;
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        stream.Write(number);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var value in typeBytes.Concat(data))
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(number, crc ^ 0xFFFFFFFF);
        stream.Write(number);
    }
}

/// <summary>The few Xlib calls the capture needs; X errors (e.g. a window that just closed) only fail the call.</summary>
sealed unsafe partial class X11Display : IDisposable
{
    private const string Library = "libX11.so.6";
    private const int IsViewable = 2;
    private const int ZPixmap = 2;
    private readonly nint _display;
    private readonly nuint _root;

    public X11Display()
    {
        XSetErrorHandler(&IgnoreError);
        _display = XOpenDisplay(0);
        if (_display == 0)
            throw new CaptureException("Cannot open the X display; set DISPLAY to an X11 or Xwayland display.");
        _root = XDefaultRootWindow(_display);
    }

    public void Dispose() => XCloseDisplay(_display);

    [UnmanagedCallersOnly]
    private static int IgnoreError(nint display, nint error) => 0;

    /// <summary>The window manager frame around a client window, or the window itself when it has none.</summary>
    public nuint FrameOf(nuint window)
    {
        while (true)
        {
            if (XQueryTree(_display, window, out _, out var parent, out var children, out _) == 0)
                throw new CaptureException($"Window {window} does not exist.");
            if (children != 0)
                XFree(children);
            if (parent == _root || parent == 0)
                return window;
            window = parent;
        }
    }

    /// <summary>The client window with this exact title, waiting for it to appear.</summary>
    public nuint FindByTitle(string title, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            var stack = new Stack<nuint>([_root]);
            while (stack.TryPop(out var window))
            {
                if (Title(window) == title && XGetWindowAttributes(_display, window, out var attributes) != 0
                    && attributes.MapState == IsViewable)
                    return window;
                if (XQueryTree(_display, window, out _, out _, out var children, out var count) == 0)
                    continue;
                for (var index = 0; index < count; index++)
                    stack.Push(((nuint*)children)[index]);
                if (children != 0)
                    XFree(children);
            }
            Thread.Sleep(250);
        }
        throw new CaptureException($"No window titled '{title}'.");
    }

    private string? Title(nuint window) =>
        Property(window, XInternAtom(_display, "_NET_WM_NAME", false)) ?? Property(window, 39 /* WM_NAME */);

    private string? Property(nuint window, nuint property)
    {
        if (XGetWindowProperty(_display, window, property, 0, 1024, false, 0 /* AnyPropertyType */, out _, out var format,
                out var items, out _, out var data) != 0 || data == 0)
            return null;
        try
        {
            return format == 8 ? Encoding.UTF8.GetString((byte*)data, (int)items) : null;
        }
        finally
        {
            XFree(data);
        }
    }

    public (int Width, int Height, int Depth) Geometry(nuint window)
    {
        if (XGetGeometry(_display, window, out _, out _, out _, out var width, out var height, out _, out var depth) == 0)
            throw new CaptureException($"Window {window} does not exist.");
        return ((int)width, (int)height, (int)depth);
    }

    /// <summary>Where the client window sits inside its frame.</summary>
    public (int X, int Y) ClientOffset(nuint client, nuint frame)
    {
        if (client == frame)
            return (0, 0);
        XTranslateCoordinates(_display, client, frame, 0, 0, out var x, out var y, out _);
        return (x, y);
    }

    public RgbaImage Grab(nuint window)
    {
        var (width, height, depth) = Geometry(window);
        var image = XGetImage(_display, window, 0, 0, (uint)width, (uint)height, nuint.MaxValue, ZPixmap);
        if (image == null)
            throw new CaptureException($"Cannot read the pixels of window {window}.");
        try
        {
            if (image->BitsPerPixel != 32)
                throw new CaptureException($"Unsupported X image format: {image->BitsPerPixel} bits per pixel.");
            var result = new RgbaImage(width, height);
            for (var y = 0; y < height; y++)
            {
                var row = image->Data + (nint)y * image->BytesPerLine;
                for (var x = 0; x < width; x++)
                {
                    int b = row[4 * x], g = row[4 * x + 1], r = row[4 * x + 2];
                    // ARGB windows hold premultiplied alpha; reading it as straight alpha darkens anti-aliased edges.
                    var a = depth == 32 ? row[4 * x + 3] : 255;
                    if (a is not (0 or 255))
                        (r, g, b) = (Unpremultiply(r, a), Unpremultiply(g, a), Unpremultiply(b, a));
                    result.Pixels[y * width + x] = (uint)(r | g << 8 | b << 16 | a << 24);
                }
            }
            return result;
        }
        finally
        {
            XDestroyImage(image);
        }
    }

    private static int Unpremultiply(int value, int alpha) => Math.Min(255, (value * 255 + alpha / 2) / alpha);

    [StructLayout(LayoutKind.Sequential)]
    private struct XWindowAttributes
    {
        public int X, Y, Width, Height, BorderWidth, Depth;
        public nint Visual;
        public nuint Root;
        public int Class, BitGravity, WinGravity, BackingStore;
        public nuint BackingPlanes, BackingPixel;
        public int SaveUnder;
        public nuint Colormap;
        public int MapInstalled, MapState;
        public nint AllEventMasks, YourEventMask, DoNotPropagateMask;
        public int OverrideRedirect;
        public nint Screen;
    }

    /// <summary>The leading fields of XImage; the rest are never read.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct XImage
    {
        public int Width, Height, XOffset, Format;
        public byte* Data;
        public int ByteOrder, BitmapUnit, BitmapBitOrder, BitmapPad, Depth, BytesPerLine, BitsPerPixel;
    }

    [LibraryImport(Library)]
    private static partial nint XOpenDisplay(nint name);

    [LibraryImport(Library)]
    private static partial int XCloseDisplay(nint display);

    [LibraryImport(Library)]
    private static partial nint XSetErrorHandler(delegate* unmanaged<nint, nint, int> handler);

    [LibraryImport(Library)]
    private static partial nuint XDefaultRootWindow(nint display);

    [LibraryImport(Library)]
    private static partial int XQueryTree(nint display, nuint window, out nuint root, out nuint parent, out nint children, out uint count);

    [LibraryImport(Library)]
    private static partial int XFree(nint data);

    [LibraryImport(Library)]
    private static partial int XGetWindowAttributes(nint display, nuint window, out XWindowAttributes attributes);

    [LibraryImport(Library)]
    private static partial int XGetGeometry(nint display, nuint drawable, out nuint root, out int x, out int y, out uint width,
        out uint height, out uint border, out uint depth);

    [LibraryImport(Library)]
    private static partial XImage* XGetImage(nint display, nuint drawable, int x, int y, uint width, uint height, nuint planeMask,
        int format);

    [LibraryImport(Library)]
    private static partial int XDestroyImage(XImage* image);

    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool XTranslateCoordinates(nint display, nuint source, nuint destination, int x, int y, out int destinationX,
        out int destinationY, out nuint child);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nuint XInternAtom(nint display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

    [LibraryImport(Library)]
    private static partial int XGetWindowProperty(nint display, nuint window, nuint property, nint offset, nint length,
        [MarshalAs(UnmanagedType.Bool)] bool delete, nuint type, out nuint actualType, out int actualFormat, out nuint items,
        out nuint bytesAfter, out nint data);
}
