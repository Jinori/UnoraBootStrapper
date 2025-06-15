using System.Diagnostics;
using Newtonsoft.Json;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Net.Http;

namespace UnoraBootstrapper
{
    internal class Program
    {
        private const string SERVER_BASE_URL = "http://unora.freeddns.org:5001/api/files";
        private const string LAUNCHER_EXE_NAME = "UnoraLaunchpad.exe";
        private const string VERSION_ENDPOINT = "launcherversion";
        private const string LAUNCHER_DOWNLOAD_ENDPOINT = "getlauncher";
        private const string BACKUP_SUFFIX = ".bak";
        private static string LogPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UnoraBootstrapper.log");

        private static async Task<int> Main(string[] args)
        {
            using (var log = new StreamWriter(LogPath, true))
            {
                log.AutoFlush = true;
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

                var serverVersion = await GetServerVersionAsync(log);
                var localVersion = GetLocalLauncherVersion(launcherPath);

                await log.WriteLineAsync($"Local launcher version: {localVersion}");
                await log.WriteLineAsync($"Server launcher version: {serverVersion}");

                if (!localVersion.Equals(serverVersion, StringComparison.OrdinalIgnoreCase))
                {
                    await log.WriteLineAsync("Updating launcher...");
                    await DownloadAndReplaceLauncherAsync(launcherPath, log);
                    await log.WriteLineAsync("Launcher updated.");
                }
                else
                {
                    await log.WriteLineAsync("Launcher is up to date.");
                }

                // Relaunch launcher
                var launcherFullPath = Path.GetFullPath(launcherPath);
                await log.WriteLineAsync($"Relaunching: {launcherFullPath}");
                Process.Start(new ProcessStartInfo
                {
                    FileName = launcherFullPath,
                    WorkingDirectory = Path.GetDirectoryName(launcherFullPath),
                    UseShellExecute = true
                });

                await log.WriteLineAsync($"=== Bootstrapper finished OK at {DateTime.Now} ===");
                return 0;
            }
            catch (Exception ex)
            {
                File.AppendAllText(LogPath, $"[Bootstrapper error at {DateTime.Now}] {ex}\n");
                Console.Error.WriteLine($"[Bootstrapper error] {ex}");
                Console.WriteLine("Press any key to exit.");
                Console.ReadKey();
                return 1;
            }
        }


        private static async Task<string> GetServerVersionAsync(StreamWriter log)
        {
            using (var httpClient = new HttpClient())
            {
                var url = $"{SERVER_BASE_URL}/{VERSION_ENDPOINT}";
                await log.WriteLineAsync($"Requesting server version from: {url}");
                var json = await httpClient.GetStringAsync(url);
                dynamic obj = JsonConvert.DeserializeObject(json);
                var version = (string)obj.Version;
                await log.WriteLineAsync($"Server returned version: {version}");
                return version;
            }
        }

        private static string GetLocalLauncherVersion(string launcherPath)
        {
            if (!File.Exists(launcherPath)) return null;

            try
            {
                var info = FileVersionInfo.GetVersionInfo(launcherPath);
                return info.FileVersion;
            }
            catch
            {
                return null;
            }
        }

        private static async Task DownloadAndReplaceLauncherAsync(string launcherPath, StreamWriter log)
        {
            var tempPath = launcherPath + ".tmp";
            var downloadUrl = $"{SERVER_BASE_URL}/{LAUNCHER_DOWNLOAD_ENDPOINT}";
            await log.WriteLineAsync($"Downloading new launcher from: {downloadUrl}");
            using (var httpClient = new HttpClient())
            {
                using (var response = await httpClient.GetAsync(downloadUrl))
                {
                    response.EnsureSuccessStatusCode();
                    using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await response.Content.CopyToAsync(fs);
                    }
                }
            }
            await log.WriteLineAsync($"Download complete to temp: {tempPath}");

            if (File.Exists(launcherPath))
            {
                var backupPath = launcherPath + BACKUP_SUFFIX;
                if (File.Exists(backupPath))
                    File.Delete(backupPath);

                File.Move(launcherPath, backupPath);
                await log.WriteLineAsync($"Backed up old launcher to: {backupPath}");
            }

            if (File.Exists(launcherPath))
            {
                File.Delete(launcherPath);
            }
            File.Move(tempPath, launcherPath);
            await log.WriteLineAsync($"Replaced launcher with new version.");
        }
    }
}
