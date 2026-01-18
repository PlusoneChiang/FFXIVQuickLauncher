using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Serilog;
using XIVLauncher.Common.PlatformAbstractions;
using XIVLauncher.Common.Util;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;

#nullable enable

namespace XIVLauncher.Common.Dalamud
{
    public class DalamudUpdater
    {
        private readonly DirectoryInfo addonDirectory;
        private readonly DirectoryInfo assetRootDirectory;
        private readonly IUniqueIdCache? cache;

        private readonly TimeSpan defaultTimeout = TimeSpan.FromMinutes(15);

        private bool forceProxy = false;
        private DalamudVersionInfo? resolvedBranch;

        public DownloadState State { get; private set; } = DownloadState.Unknown;
        public bool IsStaging { get; private set; } = false;

        public Exception? EnsurementException { get; private set; }

        private FileInfo? runnerInternal;

        public FileInfo Runner
        {
            get
            {
                if (RunnerOverride != null)
                    return RunnerOverride;

                return runnerInternal ?? throw new InvalidOperationException("Runner not prepared yet");
            }
            private set => runnerInternal = value;
        }

        public DirectoryInfo Runtime { get; }

        public FileInfo? RunnerOverride { get; set; }

        public DirectoryInfo? AssetDirectory { get; private set; }

        public IDalamudLoadingOverlay? Overlay { get; set; }

        public string? RolloutBucket { get; }

        public event Action<DalamudVersionInfo?>? ResolvedBranchChanged;

        public DalamudVersionInfo? ResolvedBranch
        {
            get => resolvedBranch;
            private set
            {
                if (resolvedBranch == value)
                    return;

                resolvedBranch = value;

                try
                {
                    ResolvedBranchChanged?.Invoke(resolvedBranch);
                }
                catch
                {
                    // ignored
                }
            }
        }

        public enum DownloadState
        {
            Unknown,
            Running,
            Done,
            NoIntegrity, // fail with error message
        }

        public DalamudUpdater(DirectoryInfo addonDirectory, DirectoryInfo runtimeDirectory, DirectoryInfo assetRootDirectory, IUniqueIdCache? cache, string? dalamudRolloutBucket)
        {
            this.addonDirectory = addonDirectory;
            this.assetRootDirectory = assetRootDirectory;

            this.Runtime = runtimeDirectory;
            this.AssetDirectory = null;
            this.cache = cache;

            this.RolloutBucket = dalamudRolloutBucket;

            if (this.RolloutBucket == null)
            {
                var rng = new Random();
                this.RolloutBucket = rng.Next(0, 9) >= 7 ? "Canary" : "Control";
            }
        }

        public void SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep progress)
        {
            Overlay!.SetStep(progress);
        }

        public void ShowOverlay()
        {
            Overlay!.SetVisible();
        }

        public void CloseOverlay()
        {
            Overlay!.SetInvisible();
        }

        private void ReportOverlayProgress(long? size, long downloaded, double? progress)
        {
            Overlay!.ReportProgress(size, downloaded, progress);
        }

        public void Run(string? betaKind, string? betaKey, bool overrideForceProxy = false)
        {
            Log.Information("[DUPDATE] Starting... (forceProxy: {ForceProxy})", overrideForceProxy);
            this.State = DownloadState.Running;

            this.forceProxy = overrideForceProxy;

            this.ResolvedBranch = null;

            Task.Run(async () =>
            {
                const int MAX_TRIES = 10;

                var isUpdated = false;

                for (var tries = 0; tries < MAX_TRIES; tries++)
                {
                    try
                    {
                        Log.Information("[DUPDATE] Starting UpdateDalamud attempt {TryCnt}/{MaxTries}...", tries + 1, MAX_TRIES);
                        await UpdateDalamud(betaKind, betaKey).ConfigureAwait(false);
                        isUpdated = true;
                        Log.Information("[DUPDATE] UpdateDalamud completed successfully");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[DUPDATE] Update failed, try {TryCnt}/{MaxTries}...", tries + 1, MAX_TRIES);
                        this.EnsurementException = ex;
                        this.forceProxy = true;
                    }
                }

                Log.Information("[DUPDATE] Setting state to {State}", isUpdated ? "Done" : "NoIntegrity");
                this.State = isUpdated ? DownloadState.Done : DownloadState.NoIntegrity;
            });
        }

        public bool? ReCheckVersion(DirectoryInfo gamePath)
        {
            if (this.State != DownloadState.Done)
                return null;

            if (this.RunnerOverride != null)
                return true;

            var info = DalamudVersionInfo.Load(new FileInfo(Path.Combine(this.Runner.DirectoryName!,
                "version.json")));

            return Repository.Ffxiv.GetVer(gamePath) == info.SupportedGameVer;
        }

        private static string GetBetaTrackName(string betaKind) =>
            string.IsNullOrEmpty(betaKind) ? "staging" : betaKind;

        private async Task<(DalamudVersionInfo release, DalamudVersionInfo? staging)> GetVersionInfo(string? betaKind, string? betaKey)
        {
            Log.Information("[DUPDATE] GetVersionInfo started");
            
            using var client = new HttpClient
            {
                Timeout = this.defaultTimeout,
            };

            client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
            };

            // ===== 使用自訂 Dalamud 版本來源（用於不相容官方版本的遊戲版本）=====
            var customVersionUrl = "https://plusonechiang.github.io/XIV-on-Mac-in-TC/dalamud_version.json";
            Log.Information("[DUPDATE] Using custom Dalamud source: {Url}", customVersionUrl);
            Log.Information("[DUPDATE] Starting HTTP request...");
            
            var customVersionJson = await client.GetStringAsync(customVersionUrl).ConfigureAwait(false);
            Log.Information("[DUPDATE] HTTP request completed, JSON length: {Length}", customVersionJson?.Length ?? 0);
            
            var customVersion = JsonSerializer.Deserialize(customVersionJson, DalamudJsonContext.Default.DalamudVersionInfo);
            Log.Information("[DUPDATE] JSON deserialized successfully");
            
            if (customVersion == null)
            {
                Log.Error("[DUPDATE] customVersion is null after deserialization");
                throw new DalamudIntegrityException("Failed to parse custom Dalamud version JSON");
            }
            
            Log.Information("[DUPDATE] Loaded custom Dalamud version: {Version} for game {GameVer}", 
                customVersion.AssemblyVersion, customVersion.SupportedGameVer);
            
            return (customVersion, null);
            // ===== 結束自訂來源 =====

            /* ===== 官方來源已註解（使用自訂版本） =====
            var versionInfoJsonRelease = await client.GetStringAsync(DalamudLauncher.REMOTE_BASE + $"release&bucket={this.RolloutBucket}").ConfigureAwait(false);

            DalamudVersionInfo versionInfoRelease = JsonSerializer.Deserialize(versionInfoJsonRelease, DalamudJsonContext.Default.DalamudVersionInfo);
            
            if (versionInfoRelease == null)
                throw new DalamudIntegrityException("Failed to parse official version JSON");

            DalamudVersionInfo? versionInfoStaging = null;

            if (!string.IsNullOrEmpty(betaKey))
            {
                var versionInfoJsonStaging = await client.GetAsync(DalamudLauncher.REMOTE_BASE + GetBetaTrackName(betaKind ?? "")).ConfigureAwait(false);

                if (versionInfoJsonStaging.StatusCode != HttpStatusCode.BadRequest)
                    versionInfoStaging = JsonSerializer.Deserialize(await versionInfoJsonStaging.Content.ReadAsStringAsync().ConfigureAwait(false), DalamudJsonContext.Default.DalamudVersionInfo);
            }

            return (versionInfoRelease, versionInfoStaging);
            ===== 結束官方來源註解 ===== */
        }

        private async Task UpdateDalamud(string? betaKind, string? betaKey)
        {
            var (versionInfoRelease, versionInfoStaging) = await GetVersionInfo(betaKind, betaKey).ConfigureAwait(false);

            // 直接使用返回的版本（已經是自訂版本）
            var remoteVersionInfo = versionInfoRelease;
            Log.Information("[DUPDATE] Using Dalamud version: {Version}", remoteVersionInfo.AssemblyVersion);

            /* ===== Staging 判斷邏輯已註解（使用自訂版本） =====
            if (versionInfoStaging?.Key != null && versionInfoStaging.Key == betaKey)
            {
                remoteVersionInfo = versionInfoStaging;
                IsStaging = true;
                Log.Information("[DUPDATE] Using staging version {Kind} with key {Key} ({Hash})", betaKind, betaKey, remoteVersionInfo.AssemblyVersion);
            }
            else
            {
                Log.Information("[DUPDATE] Using release version ({Hash})", remoteVersionInfo.AssemblyVersion);
            }
            ===== 結束 Staging 判斷註解 ===== */

            // Update resolved branch to reflect what the server actually selected
            this.ResolvedBranch = remoteVersionInfo;

            var versionInfoJson = JsonSerializer.Serialize(remoteVersionInfo, DalamudJsonContext.Default.DalamudVersionInfo);

            var addonPath = new DirectoryInfo(Path.Combine(this.addonDirectory.FullName, "Hooks"));
            var currentVersionPath = new DirectoryInfo(Path.Combine(addonPath.FullName, remoteVersionInfo.AssemblyVersion));
            var runtimePaths = new DirectoryInfo[]
            {
                new(Path.Combine(this.Runtime.FullName, "host", "fxr", remoteVersionInfo.RuntimeVersion)),
                new(Path.Combine(this.Runtime.FullName, "shared", "Microsoft.NETCore.App", remoteVersionInfo.RuntimeVersion)),
                new(Path.Combine(this.Runtime.FullName, "shared", "Microsoft.WindowsDesktop.App", remoteVersionInfo.RuntimeVersion)),
            };

            if (!currentVersionPath.Exists || !IsIntegrity(currentVersionPath))
            {
                Log.Information("[DUPDATE] Not found, redownloading");

                SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep.Dalamud);

                try
                {
                    await DownloadDalamud(currentVersionPath, remoteVersionInfo).ConfigureAwait(false);
                    CleanUpOld(addonPath, remoteVersionInfo.AssemblyVersion);

                    // This is a good indicator that we should clear the UID cache
                    cache?.Reset();
                }
                catch (Exception ex)
                {
                    throw new DalamudIntegrityException("Could not download Dalamud", ex);
                }
            }

            if (remoteVersionInfo.RuntimeRequired)
            {
                Log.Information("[DUPDATE] Now starting for .NET Runtime {0}", remoteVersionInfo.RuntimeVersion);

                var versionFile = new FileInfo(Path.Combine(this.Runtime.FullName, "version"));
                var localVersion = GetLocalRuntimeVersion(versionFile);

                Log.Information("[DUPDATE] Local runtime version: {Version}", localVersion);

                var runtimeNeedsUpdate = localVersion != remoteVersionInfo.RuntimeVersion;

                if (!this.Runtime.Exists)
                    Directory.CreateDirectory(this.Runtime.FullName);

                // var isRuntimeIntegrity = true;

                // Only check runtime hashes if we don't need to update it
                // if (!runtimeNeedsUpdate)
                // {
                //     try
                //     {
                //         isRuntimeIntegrity = await CheckRuntimeHashes(Runtime, localVersion).ConfigureAwait(false);
                //     }
                //     catch (Exception ex)
                //     {
                //         Log.Error(ex, "[DUPDATE] Could not check runtime integrity.");
                //     }
                // }

                if (runtimePaths.Any(p => !p.Exists) || runtimeNeedsUpdate)
                {
                    Log.Information("[DUPDATE] Not found, outdated or no integrity: {LocalVer} - {RemoteVer}", localVersion, remoteVersionInfo.RuntimeVersion);

                    SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep.Runtime);

                    try
                    {
                        Log.Verbose("[DUPDATE] Now download runtime...");
                        await DownloadRuntime(this.Runtime, remoteVersionInfo.RuntimeVersion).ConfigureAwait(false);
                        File.WriteAllText(versionFile.FullName, remoteVersionInfo.RuntimeVersion);
                    }
                    catch (Exception ex)
                    {
                        throw new DalamudIntegrityException("Could not ensure runtime", ex);
                    }
                }
            }

            Log.Information("[DUPDATE] Now ensure assets...");

            var assetVer = 0;

            try
            {
                this.SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep.Assets);
                this.ReportOverlayProgress(null, 0, null);
                Log.Information("[DUPDATE] Calling AssetManager.EnsureAssets...");
                var assetResult = await AssetManager.EnsureAssets(this, this.assetRootDirectory).ConfigureAwait(false);
                Log.Information("[DUPDATE] AssetManager.EnsureAssets completed");
                AssetDirectory = assetResult.AssetDir;
                assetVer = assetResult.Version;
            }
            catch (Exception ex)
            {
                throw new DalamudIntegrityException("Could not ensure assets", ex);
            }

            if (!IsIntegrity(currentVersionPath))
            {
                throw new DalamudIntegrityException("No integrity after ensurement");
            }

            WriteVersionJson(currentVersionPath, versionInfoJson);

            Log.Information("[DUPDATE] All set for {GameVersion} with {DalamudVersion}({RuntimeVersion}, {AssetVersion})", remoteVersionInfo.SupportedGameVer, remoteVersionInfo.AssemblyVersion, remoteVersionInfo.RuntimeVersion, assetVer);

            Runner = new FileInfo(Path.Combine(currentVersionPath.FullName, "Dalamud.Injector.exe"));
            SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep.Starting);
            ReportOverlayProgress(null, 0, null);
        }

        private static bool CanRead(FileInfo info)
        {
            try
            {
                using var stream = info.OpenRead();
                stream.ReadByte();
            }
            catch
            {
                return false;
            }

            return true;
        }

        private static bool IsIntegrity(DirectoryInfo addonPath)
        {
            var files = addonPath.GetFiles();

            try
            {
                if (!CanRead(files.First(x => x.Name == "Dalamud.Injector.exe"))
                    || !CanRead(files.First(x => x.Name == "Dalamud.dll"))
                    || !CanRead(files.First(x => x.Name == "ImGuiScene.dll")))
                {
                    Log.Error("[DUPDATE] Can't open files for read");
                    return false;
                }

                var hashesPath = Path.Combine(addonPath.FullName, "hashes.json");

                if (!File.Exists(hashesPath))
                {
                    Log.Error("[DUPDATE] No hashes.json");
                    return false;
                }

                return CheckIntegrity(addonPath, File.ReadAllText(hashesPath));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DUPDATE] No dalamud integrity");
                return false;
            }
        }

        private static bool CheckIntegrity(DirectoryInfo directory, string hashesJson)
        {
            try
            {
                Log.Verbose("[DUPDATE] Checking integrity of {Directory}", directory.FullName);

                var hashes = JsonSerializer.Deserialize(hashesJson, DalamudJsonContext.Default.DictionaryStringString);

                foreach (var hash in hashes)
                {
                    var file = Path.Combine(directory.FullName, hash.Key.Replace("\\", "/"));
                    using var fileStream = File.OpenRead(file);
                    using var md5 = MD5.Create();

                    var hashed = BitConverter.ToString(md5.ComputeHash(fileStream)).ToUpperInvariant().Replace("-", string.Empty);

                    if (hashed != hash.Value)
                    {
                        Log.Error("[DUPDATE] Integrity check failed for {0} ({1} - {2})", file, hash.Value, hashed);
                        return false;
                    }

                    Log.Verbose("[DUPDATE] Integrity check OK for {0} ({1})", file, hashed);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DUPDATE] Integrity check failed");
                return false;
            }

            return true;
        }

        private static void CleanUpOld(DirectoryInfo addonPath, string currentVer)
        {
            if (GameHelpers.CheckIsGameOpen())
                return;

            if (!addonPath.Exists)
                return;

            foreach (var directory in addonPath.GetDirectories())
            {
                if (directory.Name == "dev" || directory.Name == currentVer) continue;

                try
                {
                    directory.Delete(true);
                }
                catch
                {
                    // ignored
                }
            }
        }

        private static void WriteVersionJson(DirectoryInfo addonPath, string info)
        {
            File.WriteAllText(Path.Combine(addonPath.FullName, "version.json"), info);
        }

        private async Task DownloadDalamud(DirectoryInfo addonPath, DalamudVersionInfo version)
        {
            Log.Information("[DUPDATE] DownloadDalamud started, URL: {Url}", version.DownloadUrl);
            
            // Ensure directory exists
            if (!addonPath.Exists)
            {
                Log.Information("[DUPDATE] Creating addon directory: {Path}", addonPath.FullName);
                addonPath.Create();
            }
            else
            {
                Log.Information("[DUPDATE] Deleting existing addon directory: {Path}", addonPath.FullName);
                addonPath.Delete(true);
                addonPath.Create();
            }

            var downloadPath = PlatformHelpers.GetTempFileName();
            Log.Information("[DUPDATE] Download temp path: {Path}", downloadPath);

            if (File.Exists(downloadPath))
                File.Delete(downloadPath);

            Log.Information("[DUPDATE] Starting file download...");
            await this.DownloadFile(version.DownloadUrl, downloadPath, this.defaultTimeout).ConfigureAwait(false);
            Log.Information("[DUPDATE] File download completed, size: {Size} bytes", new FileInfo(downloadPath).Length);
            Log.Information("[DUPDATE] File download completed, size: {Size} bytes", new FileInfo(downloadPath).Length);
            
            Log.Information("[DUPDATE] Starting archive extraction...");

            // 檢查檔案類型，7z 需要使用 SevenZipArchive
            var is7z = version.DownloadUrl.EndsWith(".7z", StringComparison.OrdinalIgnoreCase);

            if (is7z)
            {
                Log.Information("[DUPDATE] Detected 7z archive, using SevenZipArchive...");
                using var fileStream = File.OpenRead(downloadPath);
                using var archive = SevenZipArchive.Open(fileStream);
                foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                {
                    entry.WriteToDirectory(addonPath.FullName, new ExtractionOptions()
                    {
                        ExtractFullPath = true,
                        Overwrite = true
                    });
                }
            }
            else
            {
                Log.Information("[DUPDATE] Using ReaderFactory for standard archive...");
                using var archive = ReaderFactory.Open(downloadPath);
                await archive.WriteAllToDirectoryAsync(addonPath.FullName, new ExtractionOptions()
                {
                    ExtractFullPath = true,
                    Overwrite = true
                });
            }
            Log.Information("[DUPDATE] Archive extraction completed");

            File.Delete(downloadPath);
            Log.Information("[DUPDATE] Temp file deleted");

            try
            {
                var devPath = new DirectoryInfo(Path.Combine(addonPath.FullName, "..", "dev"));

                PlatformHelpers.DeleteAndRecreateDirectory(devPath);
                PlatformHelpers.CopyFilesRecursively(addonPath, devPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DUPDATE] Could not copy to dev folder.");
            }
        }

        private string GetLocalRuntimeVersion(FileInfo versionFile)
        {
            // This is the version we first shipped. We didn't write out a version file, so we can't check it.
            var localVersion = "5.0.6";

            try
            {
                if (versionFile.Exists)
                    localVersion = File.ReadAllText(versionFile.FullName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DUPDATE] Could not read local runtime version.");
            }

            return localVersion;
        }

        private async Task<bool> CheckRuntimeHashes(DirectoryInfo runtimePath, string version)
        {
            var hashesFile = new FileInfo(Path.Combine(runtimePath.FullName, $"hashes-{version}.json"));
            string? runtimeHashes = null;

            if (!hashesFile.Exists)
            {
                Log.Verbose("[DUPDATE] Hashes file does not exist, redownloading...");

                try
                {
                    using var client = new HttpClient();
                    runtimeHashes = await client.GetStringAsync($"https://kamori.goats.dev/Dalamud/Release/Runtime/Hashes/{version}").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[DUPDATE] Could not download hashes for runtime v{Version}", version);
                    return false;
                }

                File.WriteAllText(hashesFile.FullName, runtimeHashes);
            }
            else
            {
                runtimeHashes = File.ReadAllText(hashesFile.FullName);
            }

            return CheckIntegrity(runtimePath, runtimeHashes);
        }

        private async Task DownloadRuntime(DirectoryInfo runtimePath, string version)
        {
            // Ensure directory exists
            if (!runtimePath.Exists)
            {
                runtimePath.Create();
            }
            else
            {
                runtimePath.Delete(true);
                runtimePath.Create();
            }

            // Wait for it to be gone, thanks Windows
            Thread.Sleep(1000);

            // var dotnetUrl = $"https://kamori.goats.dev/Dalamud/Release/Runtime/DotNet/{version}";
            // var desktopUrl = $"https://kamori.goats.dev/Dalamud/Release/Runtime/WindowsDesktop/{version}";


            var dotnetUrl = $"https://dotnetcli.azureedge.net/dotnet/Runtime/{version}/dotnet-runtime-{version}-win-x64.zip";
            var desktopUrl = $"https://dotnetcli.azureedge.net/dotnet/WindowsDesktop/{version}/windowsdesktop-runtime-{version}-win-x64.zip";

            var downloadPath = PlatformHelpers.GetTempFileName();

            if (File.Exists(downloadPath))
                File.Delete(downloadPath);

            await this.DownloadFile(dotnetUrl, downloadPath, this.defaultTimeout).ConfigureAwait(false);
            using (var runtimeFile = File.Open(downloadPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var runtimeZip = ReaderFactory.Open(runtimeFile))
            {
                await runtimeZip.WriteAllToDirectoryAsync(runtimePath.FullName, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
            }

            await this.DownloadFile(desktopUrl, downloadPath, this.defaultTimeout).ConfigureAwait(false);
            using (var desktopFile = File.Open(downloadPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var desktopZip = ReaderFactory.Open(desktopFile))
            {
                await desktopZip.WriteAllToDirectoryAsync(runtimePath.FullName, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
            }
            File.Delete(downloadPath);
        }

        public async Task DownloadFile(string url, string path, TimeSpan timeout)
        {
            if (this.forceProxy && url.Contains("/File/Get/"))
            {
                url = url.Replace("/File/Get/", "/File/GetProxy/");
            }
            
            Log.Information("[DUPDATE] Starting download from {Url} to {Path}", url, path);

            using var downloader = new HttpClientDownloadWithProgress(url, path);
            downloader.ProgressChanged += this.ReportOverlayProgress;

            await downloader.Download(timeout).ConfigureAwait(false);
        }
    }

    public class DalamudIntegrityException : Exception
    {
        public DalamudIntegrityException(string msg, Exception? inner = null)
            : base(msg, inner)
        {
        }
    }
}

#nullable restore
