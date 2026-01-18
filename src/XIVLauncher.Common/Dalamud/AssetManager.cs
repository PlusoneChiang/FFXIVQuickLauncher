using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using Serilog;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common.Dalamud
{
    public class AssetManager
    {
        private const string ASSET_STORE_URL = "https://plusonechiang.github.io/XIV-on-Mac-in-TC/dalamud_asset.json";

        /// <summary>
        /// 將 GitHub Raw URL 轉換為 jsDelivr CDN URL 以加速下載
        /// https://raw.githubusercontent.com/user/repo/branch/path -> https://cdn.jsdelivr.net/gh/user/repo@branch/path
        /// </summary>
        private static string ConvertToJsDelivrUrl(string url)
        {
            const string githubRawPrefix = "https://raw.githubusercontent.com/";

            if (!url.StartsWith(githubRawPrefix, StringComparison.OrdinalIgnoreCase))
                return url;

            var path = url.Substring(githubRawPrefix.Length);
            var parts = path.Split('/', 4); // user, repo, branch, filepath

            if (parts.Length < 4)
                return url;

            var user = parts[0];
            var repo = parts[1];
            var branch = parts[2];
            var filepath = parts[3];

            return $"https://cdn.jsdelivr.net/gh/{user}/{repo}@{branch}/{filepath}";
        }

        internal class AssetInfo
        {
            [JsonPropertyName("version")]
            public int Version { get; set; }

            [JsonPropertyName("assets")]
            public IReadOnlyList<Asset> Assets { get; set; }

            [JsonPropertyName("packageUrl")]
            public string PackageUrl { get; set; }

            public class Asset
            {
                [JsonPropertyName("url")]
                public string Url { get; set; }

                [JsonPropertyName("fileName")]
                public string FileName { get; set; }
                
                [JsonPropertyName("hash")]
                public string Hash { get; set; }
            }
        }

        public static async Task<(DirectoryInfo AssetDir, int Version)> EnsureAssets(DalamudUpdater updater, DirectoryInfo baseDir)
        {
            using var metaClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(30),
            };

            metaClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
            };

            using var sha1 = SHA1.Create();

            Log.Verbose("[DASSET] Starting asset download");

            var (isRefreshNeeded, info) = await CheckAssetRefreshNeeded(metaClient, baseDir);

            // NOTE(goat): We should use a junction instead of copying assets to a new folder. There is no C# API for junctions in .NET Framework.

            var currentDir = new DirectoryInfo(Path.Combine(baseDir.FullName, info.Version.ToString()));
            var devDir = new DirectoryInfo(Path.Combine(baseDir.FullName, "dev"));

            // If we don't need a refresh, let's check if all hashes are good
            if (!isRefreshNeeded)
            {
                foreach (var entry in info.Assets)
                {
                    var filePath = Path.Combine(currentDir.FullName, entry.FileName);

                    if (!File.Exists(filePath))
                    {
                        Log.Error("[DASSET] {0} not found locally", entry.FileName);
                        isRefreshNeeded = true;
                        break;
                    }

                    if (string.IsNullOrEmpty(entry.Hash))
                        continue;

                    try
                    {
                        using var file = File.OpenRead(filePath);
                        var fileHash = sha1.ComputeHash(file);
                        var stringHash = BitConverter.ToString(fileHash).Replace("-", "");

                        if (stringHash != entry.Hash)
                        {
                            Log.Error("[DASSET] {0} has {1}, remote {2}, need refresh", entry.FileName, stringHash, entry.Hash);
                            isRefreshNeeded = true;
                            //break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[DASSET] Could not read asset");
                        isRefreshNeeded = true;
                        break;
                    }
                }
            }

            if (isRefreshNeeded)
            {
                // 確保目錄存在，但不刪除現有檔案
                if (!currentDir.Exists)
                    currentDir.Create();

                var packageUrl = info.PackageUrl;

                // 收集需要更新的檔案清單
                var filesToUpdate = new List<AssetInfo.Asset>();
                foreach (var entry in info.Assets)
                {
                    var filePath = Path.Combine(currentDir.FullName, entry.FileName);
                    var needsUpdate = false;

                    if (!File.Exists(filePath))
                    {
                        needsUpdate = true;
                        Log.Information("[DASSET] File missing, will download: {0}", entry.FileName);
                    }
                    else if (!string.IsNullOrEmpty(entry.Hash))
                    {
                        try
                        {
                            using var file = File.OpenRead(filePath);
                            var fileHash = sha1.ComputeHash(file);
                            var stringHash = BitConverter.ToString(fileHash).Replace("-", "");
                            if (stringHash != entry.Hash)
                            {
                                needsUpdate = true;
                                Log.Information("[DASSET] Hash mismatch, will download: {0} (local: {1}, remote: {2})", entry.FileName, stringHash, entry.Hash);
                            }
                        }
                        catch (Exception ex)
                        {
                            needsUpdate = true;
                            Log.Warning(ex, "[DASSET] Could not verify hash, will download: {0}", entry.FileName);
                        }
                    }

                    if (needsUpdate)
                        filesToUpdate.Add(entry);
                }

                Log.Information("[DASSET] {0} files need to be updated out of {1} total", filesToUpdate.Count, info.Assets.Count);

                // 如果需要更新的檔案超過總數的一半，或使用 packageUrl，則重新下載整個 package
                if (packageUrl != null && filesToUpdate.Count > info.Assets.Count / 2)
                {
                    Log.Information("[DASSET] Downloading full package due to many files needing update");
                    PlatformHelpers.DeleteAndRecreateDirectory(currentDir);
                    Thread.Sleep(1000);

                    var tempPath = PlatformHelpers.GetTempFileName();
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);

                    await updater.DownloadFile(packageUrl, tempPath, TimeSpan.FromMinutes(4));
                    using (var packageStream = File.OpenRead(tempPath))
                    using (var packageArc = new ZipArchive(packageStream, ZipArchiveMode.Read))
                    {
                        packageArc.ExtractToDirectory(currentDir.FullName);
                    }

                    try
                    {
                        PlatformHelpers.DeleteAndRecreateDirectory(devDir);
                        PlatformHelpers.CopyFilesRecursively(currentDir, devDir);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[DASSET] Could not copy to dev dir");
                    }

                    File.Delete(tempPath);
                }
                else if (filesToUpdate.Count > 0)
                {
                    // 僅下載需要更新的檔案
                    using var assetClient = new HttpClient
                    {
                        Timeout = TimeSpan.FromMinutes(30),
                    };

                    assetClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue
                    {
                        NoCache = true,
                    };

                    // 並行下載，最多同時 5 個
                    using var semaphore = new SemaphoreSlim(5);
                    var downloadTasks = filesToUpdate.Select(async entry =>
                    {
                        await semaphore.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            var destPath = Path.Combine(currentDir.FullName, entry.FileName);
                            var destDir = Path.GetDirectoryName(destPath);
                            if (!Directory.Exists(destDir))
                                Directory.CreateDirectory(destDir);

                            // 刪除舊檔案（如果存在）
                            if (File.Exists(destPath))
                                File.Delete(destPath);

                            var cdnUrl = ConvertToJsDelivrUrl(entry.Url);
                            Log.Information("[DASSET] Downloading: {0} from {1}", entry.FileName, cdnUrl);
                            await updater.DownloadFile(cdnUrl, destPath, TimeSpan.FromMinutes(4)).ConfigureAwait(false);
                            Log.Information("[DASSET] Downloaded: {0}", entry.FileName);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    });
                    await Task.WhenAll(downloadTasks).ConfigureAwait(false);

                    // 更新 dev 目錄中已變更的檔案
                    try
                    {
                        if (!devDir.Exists)
                            devDir.Create();
                        foreach (var entry in filesToUpdate)
                        {
                            var srcPath = Path.Combine(currentDir.FullName, entry.FileName);
                            var destPath = Path.Combine(devDir.FullName, entry.FileName);
                            var destDirPath = Path.GetDirectoryName(destPath);
                            if (!Directory.Exists(destDirPath))
                                Directory.CreateDirectory(destDirPath);
                            if (File.Exists(destPath))
                                File.Delete(destPath);
                            File.Copy(srcPath, destPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[DASSET] Could not copy updated files to dev dir");
                    }
                }
            }

            if (isRefreshNeeded)
                SetLocalAssetVer(baseDir, info.Version);

            Log.Verbose("[DASSET] Assets OK at {0}", currentDir.FullName);

            try
            {
                CleanUpOld(baseDir, devDir, currentDir);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DASSET] Could not clean up old assets");
            }

            return (currentDir, info.Version);
        }

        private static string GetAssetVerPath(DirectoryInfo baseDir)
        {
            return Path.Combine(baseDir.FullName, "asset.ver");
        }

        /// <summary>
        ///     Check if an asset update is needed. When this fails, just return false - the route to github
        ///     might be bad, don't wanna just bail out in that case
        /// </summary>
        /// <param name="baseDir">Base directory for assets</param>
        /// <returns>Update state</returns>
        private static async Task<(bool isRefreshNeeded, AssetInfo info)> CheckAssetRefreshNeeded(HttpClient client, DirectoryInfo baseDir)
        {
            var localVerFile = GetAssetVerPath(baseDir);
            var localVer = 0;

            try
            {
                if (File.Exists(localVerFile))
                    localVer = int.Parse(File.ReadAllText(localVerFile));
            }
            catch (Exception ex)
            {
                // This means it'll stay on 0, which will redownload all assets - good by me
                Log.Error(ex, "[DASSET] Could not read asset.ver");
            }
            

            var remoteVer = JsonSerializer.Deserialize(await client.GetStringAsync(ASSET_STORE_URL), AssetInfoJsonContext.Default.AssetInfo);

            Log.Verbose("[DASSET] Ver check - local:{0} remote:{1}", localVer, remoteVer.Version);

            var needsUpdate = remoteVer.Version > localVer;

            return (needsUpdate, remoteVer);
        }

        private static void SetLocalAssetVer(DirectoryInfo baseDir, int version)
        {
            try
            {
                var localVerFile = GetAssetVerPath(baseDir);
                File.WriteAllText(localVerFile, version.ToString());
            }
            catch (Exception e)
            {
                Log.Error(e, "[DASSET] Could not write local asset version");
            }
        }

        private static void CleanUpOld(DirectoryInfo baseDir, DirectoryInfo devDir, DirectoryInfo currentDir)
        {
            if (GameHelpers.CheckIsGameOpen())
                return;

            if (!baseDir.Exists)
                return;

            foreach (var toDelete in baseDir.GetDirectories())
            {
                if (toDelete.Name != devDir.Name && toDelete.Name != currentDir.Name)
                {
                    toDelete.Delete(true);
                    Log.Verbose("[DASSET] Cleaned out {Path}", toDelete.FullName);
                }
            }

            Log.Verbose("[DASSET] Finished cleaning");
        }
    }
    
    [JsonSerializable(typeof(AssetManager.AssetInfo))]
    internal partial class AssetInfoJsonContext: JsonSerializerContext
    {
    }
}
