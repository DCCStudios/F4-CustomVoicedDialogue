using System.Diagnostics;

// Tiny self-update helper: waits for the main app to exit, copies the
// extracted update over the install directory, and relaunches.
//
//   CustomVoicedDialogue.Updater <appPid> <sourceDir> <targetDir> <relaunchExe>

if (args.Length != 4)
{
    Console.Error.WriteLine("usage: CustomVoicedDialogue.Updater <appPid> <sourceDir> <targetDir> <relaunchExe>");
    return 2;
}

var pid = int.Parse(args[0]);
var sourceDirectory = Path.GetFullPath(args[1]);
var targetDirectory = Path.GetFullPath(args[2]);
var relaunchExe = Path.GetFullPath(args[3]);

try
{
    using var process = Process.GetProcessById(pid);
    process.WaitForExit(30000);
}
catch (ArgumentException)
{
    // Already exited.
}

// Retry the copy briefly: antivirus or slow handles can hold files open.
for (var attempt = 0; ; attempt++)
{
    try
    {
        CopyDirectory(sourceDirectory, targetDirectory);
        break;
    }
    catch (IOException) when (attempt < 10)
    {
        Thread.Sleep(1000);
    }
}

Process.Start(new ProcessStartInfo(relaunchExe) { UseShellExecute = true, WorkingDirectory = targetDirectory });
return 0;

static void CopyDirectory(string source, string target)
{
    Directory.CreateDirectory(target);
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(source, file);
        // Never overwrite the running updater itself.
        if (relative.StartsWith("CustomVoicedDialogue.Updater", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }
        var destination = Path.Combine(target, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(file, destination, overwrite: true);
    }
}
