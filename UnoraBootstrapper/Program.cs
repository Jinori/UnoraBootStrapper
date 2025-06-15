using System.Diagnostics;
using Newtonsoft.Json;
using System.Reflection;

namespace UnoraBootstrapper
{
    internal class ServerVersionInfo
    {
        public string Version { get; set; }
        public string FileName { get; set; }
    }

    internal class Program
    {
        private const string SERVER_BASE_URL = "http://unora.freeddns.org:5001/api/files";
        private const string LAUNCHER_APP_NAME = "UnoraLaunchpad"; // Extension will come from server or be determined
        private const string VERSION_ENDPOINT = "launcherversion";
        private const string LAUNCHER_DOWNLOAD_ENDPOINT = "getlauncher";
        private const string BACKUP_SUFFIX = ".bak";
        private static string LogPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UnoraBootstrapper.log");

        private static async Task<int> Main(string[] args)
        {
            await using var log = new StreamWriter(LogPath, true);
            log.AutoFlush = true;
            await log.WriteLineAsync($"OS Description: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
            await log.WriteLineAsync($"=== Bootstrapper started at {DateTime.Now} ===");
            try
            {
                if (args.Length < 1)
                {
                    await log.WriteLineAsync("ERROR: No launcher path provided.");
                    return 1;
                }

                var launcherPath = args[0];
                await log.WriteLineAsync($"Launcher path: {launcherPath}");
                var processId = args.Length > 1 && int.TryParse(args[1], out var pid) ? pid : 0;
                await log.WriteLineAsync($"Process ID: {processId}");
                var effectiveLauncherPath = launcherPath; // Initialize effective path

                // Wait for launcher to exit, if needed
                if (processId > 0)
                {
                    try
                    {
                        var proc = Process.GetProcessById(processId);
                        await log.WriteLineAsync($"Waiting for process {processId} to exit...");
                        proc.WaitForExit();
                        await Task.Delay(150); // Give Windows a split second to release file handle
                        await log.WriteLineAsync("Launcher process has exited.");
                    }
                    catch (ArgumentException)
                    {
                        await log.WriteLineAsync("Process already exited before bootstrapper started.");
                    }
                    catch (Exception ex)
                    {
                        await log.WriteLineAsync($"Error while waiting for process: {ex}");
                    }
                }

                var serverInfo = await GetServerVersionAsync(log);
                if (serverInfo == null)
                {
                    await log.WriteLineAsync("ERROR: Could not retrieve server version info. Aborting update check. Will attempt to launch local version.");
                }

                var localVersion = GetLocalLauncherVersion(launcherPath, log);
                await log.WriteLineAsync($"Local launcher version: {localVersion}");
                await log.WriteLineAsync($"Server launcher version: {serverInfo?.Version}, FileName: {serverInfo?.FileName}");

                bool needsUpdate = false;
                if (serverInfo == null) {
                    await log.WriteLineAsync("Skipping update check as server info is unavailable.");
                } else if (string.IsNullOrEmpty(localVersion)) {
                    await log.WriteLineAsync("Local version not found or unreadable. Update is needed.");
                    needsUpdate = true;
                } else if (!localVersion.Equals(serverInfo.Version, StringComparison.OrdinalIgnoreCase)) {
                    await log.WriteLineAsync($"Version mismatch. Local: '{localVersion}', Server: '{serverInfo.Version}'. Update is needed.");
                    needsUpdate = true;
                } else if (!Path.GetFileName(launcherPath).Equals(serverInfo.FileName, StringComparison.OrdinalIgnoreCase)) {
                    // This condition implies localVersion and serverInfo.Version are the same.
                    // We also check if the filename itself is different (e.g. UnoraLaunchpad.exe vs UnoraLaunchpad.dll)
                    // We use launcherPath here for the original local filename, not effectiveLauncherPath.
                    await log.WriteLineAsync($"Filename mismatch. Current local: '{Path.GetFileName(launcherPath)}', Server target: '{serverInfo.FileName}'. Update is needed.");
                    needsUpdate = true;
                } else {
                    await log.WriteLineAsync("Launcher is up to date (version and filename).");
                }

                if (needsUpdate && serverInfo != null)
                {
                    await log.WriteLineAsync("Updating launcher...");
                    await DownloadAndReplaceLauncherAsync(launcherPath, serverInfo.FileName, log);
                    effectiveLauncherPath = Path.Combine(Path.GetDirectoryName(launcherPath), serverInfo.FileName);
                    await log.WriteLineAsync($"Launcher updated. Effective path is now: {effectiveLauncherPath}");
                }
                else if (needsUpdate && serverInfo == null)
                {
                    await log.WriteLineAsync("Cannot update launcher as server information is missing.");
                }

                // Relaunch launcher using effectiveLauncherPath
                var finalLauncherPath = Path.GetFullPath(effectiveLauncherPath);
                await log.WriteLineAsync($"Preparing to launch: {finalLauncherPath}");

                if (!File.Exists(finalLauncherPath))
                {
                    await log.WriteLineAsync($"ERROR: Launcher file not found at {finalLauncherPath}. Cannot start application.");
                    // Attempt to run backup if it exists and original file was effective path? Or just error out.
                    // For now, error out as the update process should have placed the file.
                    return 1; // Indicate error
                }

                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.WorkingDirectory = Path.GetDirectoryName(finalLauncherPath);
                startInfo.UseShellExecute = false;

                if (finalLauncherPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    startInfo.FileName = "dotnet";
                    startInfo.Arguments = $""{finalLauncherPath}""; // Ensure quotes around path
                    await log.WriteLineAsync($"Relaunching as .NET DLL: {startInfo.FileName} {startInfo.Arguments}");
                }
                else
                {
                    startInfo.FileName = finalLauncherPath;
                    await log.WriteLineAsync($"Relaunching as executable: {startInfo.FileName}");
                }

                Process.Start(startInfo);

                await log.WriteLineAsync($"=== Bootstrapper finished OK at {DateTime.Now} ===");
                return 0;
            }
            catch (Exception ex)
            {
                await File.AppendAllTextAsync(LogPath, $"[Bootstrapper error at {DateTime.Now}] {ex}\n");
                Console.Error.WriteLine($"[Bootstrapper error] {ex}");
                Console.WriteLine("Press any key to exit.");
                Console.ReadKey();
                return 1;
            }
        }

        private static async Task<ServerVersionInfo> GetServerVersionAsync(StreamWriter log)
        {
            using var httpClient = new HttpClient();
            var url = $"{SERVER_BASE_URL}/{VERSION_ENDPOINT}";
            await log.WriteLineAsync($"Requesting server version info from: {url}");
            var json = await httpClient.GetStringAsync(url);
            // Assuming Newtonsoft.Json is used as per original code:
            var serverInfo = JsonConvert.DeserializeObject<ServerVersionInfo>(json);
            if (serverInfo == null || string.IsNullOrEmpty(serverInfo.Version) || string.IsNullOrEmpty(serverInfo.FileName))
            {
                await log.WriteLineAsync($"ERROR: Server returned incomplete version info. JSON: {json}");
                // Consider throwing an exception or returning a specific error state if appropriate
                // For now, let's throw to make it clear something is wrong, or handle it more gracefully
                // depending on desired robustness. For this task, logging and returning null might be acceptable
                // if the Main method can handle a null serverInfo.
                // Let's assume for now the main method will check for null.
                return null; // Or throw new InvalidOperationException("Server returned incomplete version info.");
            }
            await log.WriteLineAsync($"Server returned: Version='{serverInfo.Version}', FileName='{serverInfo.FileName}'");
            return serverInfo;
        }

        internal static string GetLocalLauncherVersion(string launcherPath, StreamWriter log)
        {
            if (!File.Exists(launcherPath))
            {
                log.WriteLineAsync($"Local launcher file not found at: {launcherPath}");
                return null;
            }

            // Try .NET Assembly version first if it's a DLL
            if (launcherPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var assembly = Assembly.LoadFrom(launcherPath); // Use LoadFrom for path-based loading
                    var version = assembly.GetName().Version?.ToString();
                    if (!string.IsNullOrEmpty(version))
                    {
                        log.WriteLineAsync($"Successfully read assembly version '{version}' from '{launcherPath}'");
                        return version;
                    }
                    log.WriteLineAsync($"Assembly version was null or empty for '{launcherPath}'.");
                }
                catch (Exception ex)
                {
                    log.WriteLineAsync($"Failed to read assembly version from '{launcherPath}': {ex.Message}. Will try FileVersionInfo.");
                }
            }

            // Fallback to FileVersionInfo (primarily for .exe files or if assembly load failed)
            try
            {
                var info = FileVersionInfo.GetVersionInfo(launcherPath);
                if (!string.IsNullOrEmpty(info.FileVersion))
                {
                    log.WriteLineAsync($"Successfully read FileVersionInfo '{info.FileVersion}' from '{launcherPath}'");
                    return info.FileVersion;
                }
                log.WriteLineAsync($"FileVersionInfo.FileVersion was null or empty for '{launcherPath}'.");
            }
            catch (Exception ex)
            {
                log.WriteLineAsync($"Failed to read FileVersionInfo from '{launcherPath}': {ex.Message}.");
            }

            log.WriteLineAsync($"Could not determine local launcher version for '{launcherPath}'.");
            return null;
        }

        private static async Task DownloadAndReplaceLauncherAsync(string currentLauncherPath, string newLauncherFileName, StreamWriter log)
        {
            var launcherDir = Path.GetDirectoryName(currentLauncherPath);
            var newLauncherFullPath = Path.Combine(launcherDir, newLauncherFileName);
            var tempPath = newLauncherFullPath + ".tmp";

            var downloadUrl = $"{SERVER_BASE_URL}/{LAUNCHER_DOWNLOAD_ENDPOINT}";
            await log.WriteLineAsync($"Downloading new launcher '{newLauncherFileName}' from: {downloadUrl} to '{tempPath}'");
            using var httpClient = new HttpClient();

            using (var response = await httpClient.GetAsync(downloadUrl))
            {
                response.EnsureSuccessStatusCode();
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await response.Content.CopyToAsync(fs);
                }
            }
            await log.WriteLineAsync($"Download complete to temp: {tempPath}");

            // Backup the currently running launcher, if it exists
            if (File.Exists(currentLauncherPath))
            {
                var backupPath = currentLauncherPath + BACKUP_SUFFIX;
                if (File.Exists(backupPath)) 
                    File.Delete(backupPath);
                
                File.Move(currentLauncherPath, backupPath);
                await log.WriteLineAsync($"Backed up '{currentLauncherPath}' to: '{backupPath}'");
            }
            else
            {
                await log.WriteLineAsync($"Current launcher path '{currentLauncherPath}' not found. Skipping backup.");
            }

            // Move the new launcher into place
            File.Move(tempPath, newLauncherFullPath, true);
            await log.WriteLineAsync($"Moved new launcher from '{tempPath}' to '{newLauncherFullPath}'.");
        }
    }
}
