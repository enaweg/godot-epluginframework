#:property TargetFramework=net10.0
#:property PublishAot=false
// Packages addons/ePlugin like a release, to try a build before it is tagged: the framework's files and a freshly
// published progress helper, in <output>/ePlugin-v<version>-test-<commit>-<timestamp>.zip. CI packages pull requests
// (.github/workflows/ci-pr-build.yml) and tagged releases (.github/workflows/ci-release.yml) with it too.
//
// Usage: dotnet run tools/package_release.cs -- [output directory, default out/] [--name <archive.zip>] [--helper <folder>]
// Locally built helper binaries in addons/ePlugin/progress are left out; the archive gets the one published here, or the
// one already published to --helper. The release workflow passes the helper it smoke-tested, so exactly that ships. The
// archive is read back and compared with the sources, so a filter cannot silently drop framework code.
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

const string Usage = "Usage: dotnet run tools/package_release.cs -- [output directory] [--name <archive.zip>] [--helper <folder>]";
string? outputArgument = null, name = null, helper = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--name" when i + 1 < args.Length:
            name = args[++i];
            break;
        case "--helper" when i + 1 < args.Length:
            helper = Path.GetFullPath(args[++i]);
            break;
        default:
            if (args[i].StartsWith("--", StringComparison.Ordinal) || outputArgument is not null)
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }
            outputArgument = args[i];
            break;
    }
}

var scriptDirectory = AppContext.GetData("EntryPointFileDirectoryPath") as string ?? Environment.CurrentDirectory;
var repository = Path.GetFullPath(Path.Combine(scriptDirectory, ".."));
var addon = Path.Combine(repository, "src/eplugin-framework/addons/ePlugin");
var output = Path.GetFullPath(outputArgument ?? Path.Combine(repository, "out"));

var version = Regex.Match(File.ReadAllText(Path.Combine(addon, "plugin.cfg")), "version=\"([^\"]+)\"").Groups[1].Value;
if (version.Length == 0)
{
    Console.Error.WriteLine("error: plugin.cfg has no version");
    return 1;
}
var (gitStatus, commit) = Run("git", ["-C", repository, "rev-parse", "--short", "HEAD"], captureOutput: true);
if (gitStatus != 0)
{
    Console.Error.WriteLine("error: cannot read the checkout's revision");
    return 1;
}

Directory.CreateDirectory(output);
var archivePath = Path.Combine(output, name ?? $"ePlugin-v{version}-test-{commit.Trim()}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
var stage = Path.Combine(Path.GetTempPath(), "ePlugin-release-" + Guid.NewGuid().ToString("N"));
var stageAddon = Path.Combine(stage, "addons/ePlugin");
var helperFiles = new Regex(@"^progress/ActivationProgress\.(dll|pdb|deps\.json|runtimeconfig\.json)$");

try
{
    // hidden files too, like a release archive of the folder
    var sources = Directory.EnumerateFiles(addon, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
        .Select(file => Path.GetRelativePath(addon, file).Replace('\\', '/'))
        .Where(relative => !helperFiles.IsMatch(relative))
        .ToArray();
    foreach (var relative in sources)
    {
        var destination = Path.Combine(stageAddon, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(Path.Combine(addon, relative), destination);
    }

    if (helper is not null)
    {
        Directory.CreateDirectory(Path.Combine(stageAddon, "progress"));
        foreach (var file in Directory.EnumerateFiles(helper).Where(file => helperFiles.IsMatch("progress/" + Path.GetFileName(file))))
            File.Copy(file, Path.Combine(stageAddon, "progress", Path.GetFileName(file)), true);
    }
    else
    {
        var (publishStatus, _) = Run("dotnet", ["publish", Path.Combine(repository, "src/activation-progress/ActivationProgress.csproj"),
            "--configuration", "Release", "-p:DebugType=None", "--output", Path.Combine(stageAddon, "progress")]);
        if (publishStatus != 0)
        {
            Console.Error.WriteLine("error: publishing the progress helper failed");
            return publishStatus;
        }
    }

    using (var archive = new ZipArchive(new FileStream(archivePath, FileMode.CreateNew), ZipArchiveMode.Create))
    {
        foreach (var file in Directory.EnumerateFiles(stage, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            archive.CreateEntryFromFile(file, Path.GetRelativePath(stage, file).Replace('\\', '/'), CompressionLevel.Optimal);
    }

    using (var archive = ZipFile.OpenRead(archivePath))
    {
        foreach (var relative in sources)
        {
            var entry = archive.GetEntry("addons/ePlugin/" + relative);
            if (entry is null)
                throw new InvalidDataException($"Missing source file in archive: {relative}");
            using var source = File.OpenRead(Path.Combine(addon, relative));
            using var packed = entry.Open();
            if (!SHA256.HashData(source).AsSpan().SequenceEqual(SHA256.HashData(packed)))
                throw new InvalidDataException($"Archive content differs from source: {relative}");
        }
        foreach (var file in new[] { "ActivationProgress.dll", "ActivationProgress.runtimeconfig.json" })
        {
            if (archive.GetEntry("addons/ePlugin/progress/" + file) is null)
                throw new InvalidDataException($"Missing progress helper file: {file}");
        }
    }

    Console.WriteLine($"Verified {sources.Length} source files and the published helper.");
    Console.WriteLine(archivePath);
    return 0;
}
catch (InvalidDataException exception)
{
    Console.Error.WriteLine("error: " + exception.Message);
    File.Delete(archivePath);
    return 1;
}
finally
{
    if (Directory.Exists(stage))
        Directory.Delete(stage, true);
}

static (int ExitCode, string Output) Run(string command, IEnumerable<string> arguments, bool captureOutput = false)
{
    var start = new ProcessStartInfo(command) { RedirectStandardOutput = captureOutput };
    foreach (var argument in arguments)
        start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var text = captureOutput ? process.StandardOutput.ReadToEnd() : "";
    process.WaitForExit();
    return (process.ExitCode, text);
}
