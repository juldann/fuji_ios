/*
 * This file is part of Fujify.
 *
 * Copyright (C) 2024 Isidore Paulin contact@ipweb.dev
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Fujify.Common;
using Fujify.Models;

namespace Fujify.Services
{
    public class UpdateService : IUpdateService
    {
        public const string PrimaryRepo = "juldann/fuji_ios";
        public const string FallbackRepo = "juldann/fuji_ios";

        private static readonly HttpClient _sharedHttpClient = new();
        private readonly ISettingsService _settingsService;
        private readonly HttpClient _httpClient;

        public static readonly Version FallbackVersion = new(2, 0, 0);

        public Version CurrentVersion
        {
            get
            {
                var asmVersion = Assembly.GetExecutingAssembly().GetName().Version;
                if (asmVersion != null && asmVersion >= FallbackVersion)
                {
                    return new Version(asmVersion.Major, asmVersion.Minor, Math.Max(0, asmVersion.Build));
                }
                return FallbackVersion;
            }
        }

        public string CurrentVersionString => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

        public UpdateService(ISettingsService? settingsService = null, HttpClient? httpClient = null)
        {
            _settingsService = settingsService ?? new SettingsService();
            _httpClient = httpClient ?? _sharedHttpClient;

            if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            {
                _httpClient.DefaultRequestHeaders.UserAgent.Add(
                    new ProductInfoHeaderValue("Fujifier-Updater", CurrentVersionString));
            }
        }

        public async Task<UpdateCheckResult> CheckForUpdateAsync(bool force = false, CancellationToken ct = default) { return await Task.FromResult(UpdateCheckResult.UpToDate(CurrentVersionString)); } public async Task<UpdateCheckResult> CheckForUpdateLegacyAsync(bool force = false, CancellationToken ct = default)
        {
            try
            {
                _settingsService.Settings.LastUpdateCheck = DateTime.UtcNow;
                _settingsService.Save();

                var release = await FetchLatestReleaseAsync(PrimaryRepo, ct)
                           ?? await FetchLatestReleaseAsync(FallbackRepo, ct);

                if (release == null)
                {
                    return UpdateCheckResult.Failure("Could not retrieve latest release information from GitHub.", CurrentVersionString);
                }

                if (release.Version == null)
                {
                    return UpdateCheckResult.Failure($"Could not parse release version from tag '{release.TagName}'.", CurrentVersionString);
                }

                bool isNewer = release.Version > CurrentVersion;

                if (!isNewer)
                {
                    return UpdateCheckResult.UpToDate(CurrentVersionString, release);
                }

                // Check if user previously chose to skip this specific version (unless forced)
                if (!force && !string.IsNullOrWhiteSpace(_settingsService.Settings.SkippedVersion))
                {
                    if (string.Equals(_settingsService.Settings.SkippedVersion, release.VersionString, StringComparison.OrdinalIgnoreCase))
                    {
                        return UpdateCheckResult.UpToDate(CurrentVersionString, release);
                    }
                }

                return UpdateCheckResult.UpdateAvailable(release, CurrentVersionString);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateService] Error checking for updates: {ex.Message}");
                return UpdateCheckResult.Failure($"Update check failed: {ex.Message}", CurrentVersionString);
            }
        }

        private async Task<UpdateInfo?> FetchLatestReleaseAsync(string repo, CancellationToken ct)
        {
            try
            {
                string url = $"https://api.github.com/repos/{repo}/releases/latest";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"[UpdateService] GitHub API returned status {response.StatusCode} for repo {repo}");
                    return null;
                }

                string json = await response.Content.ReadAsStringAsync(ct);
                return ParseReleaseJson(json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateService] Error fetching release from {repo}: {ex.Message}");
                return null;
            }
        }

        public static UpdateInfo? ParseReleaseJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Check for draft or prerelease
                if (root.TryGetProperty("draft", out var draftProp) && draftProp.GetBoolean())
                {
                    return null;
                }

                string tagName = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? string.Empty : string.Empty;
                string name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? tagName : tagName;
                string htmlUrl = root.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() ?? string.Empty : string.Empty;
                string body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? string.Empty : string.Empty;

                DateTime? publishedAt = null;
                if (root.TryGetProperty("published_at", out var pubProp) && pubProp.TryGetDateTime(out var dt))
                {
                    publishedAt = dt;
                }

                var info = new UpdateInfo
                {
                    TagName = tagName,
                    Version = ParseVersion(tagName),
                    Title = string.IsNullOrWhiteSpace(name) ? tagName : name,
                    ReleaseNotes = body,
                    ReleaseUrl = htmlUrl,
                    PublishedAt = publishedAt
                };

                if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var assetElem in assetsProp.EnumerateArray())
                    {
                        string assetName = assetElem.TryGetProperty("name", out var aName) ? aName.GetString() ?? string.Empty : string.Empty;
                        string downloadUrl = assetElem.TryGetProperty("browser_download_url", out var aUrl) ? aUrl.GetString() ?? string.Empty : string.Empty;
                        long size = assetElem.TryGetProperty("size", out var aSize) ? aSize.GetInt64() : 0;
                        string contentType = assetElem.TryGetProperty("content_type", out var aType) ? aType.GetString() ?? string.Empty : string.Empty;

                        if (!string.IsNullOrWhiteSpace(assetName) && !string.IsNullOrWhiteSpace(downloadUrl))
                        {
                            info.Assets.Add(new UpdateAsset
                            {
                                Name = assetName,
                                DownloadUrl = downloadUrl,
                                Size = size,
                                ContentType = contentType
                            });
                        }
                    }
                }

                info.MatchingAsset = SelectMatchingAsset(info.Assets);
                return info;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateService] Error parsing release JSON: {ex.Message}");
                return null;
            }
        }

        public static Version? ParseVersion(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            string clean = raw.Trim().TrimStart('v', 'V');
            int dashIndex = clean.IndexOfAny(new[] { '-', '+' });
            if (dashIndex > 0)
            {
                clean = clean.Substring(0, dashIndex);
            }

            var parts = clean.Split('.');
            if (parts.Length == 1 && int.TryParse(parts[0], out int major))
            {
                return new Version(major, 0, 0);
            }
            if (parts.Length == 2 && int.TryParse(parts[0], out major) && int.TryParse(parts[1], out int minor))
            {
                return new Version(major, minor, 0);
            }
            if (Version.TryParse(clean, out var parsed))
            {
                return parsed;
            }

            return null;
        }

        private static readonly Regex ArmRegex = new(@"(^|[^a-zA-Z0-9])(arm|arm64|aarch64)([^a-zA-Z0-9]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex X64Regex = new(@"(^|[^a-zA-Z0-9])(x64|x86_64|amd64|x86)([^a-zA-Z0-9]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static bool IsArmName(string name) => ArmRegex.IsMatch(name);
        private static bool IsX64Name(string name) => X64Regex.IsMatch(name);
        private static bool IsSetupOrInstaller(string name) =>
            name.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("installer", StringComparison.OrdinalIgnoreCase);

        public static UpdateAsset? SelectMatchingAsset(
            IEnumerable<UpdateAsset> assets,
            Architecture? targetArch = null,
            OSPlatform? targetPlatform = null)
        {
            var list = assets?.ToList();
            if (list == null || list.Count == 0) return null;

            var arch = targetArch ?? RuntimeInformation.ProcessArchitecture;
            bool isWindows = targetPlatform.HasValue ? targetPlatform.Value == OSPlatform.Windows : OperatingSystem.IsWindows();
            bool isMacOS = targetPlatform.HasValue ? targetPlatform.Value == OSPlatform.OSX : OperatingSystem.IsMacOS();
            bool isLinux = targetPlatform.HasValue ? targetPlatform.Value == OSPlatform.Linux : OperatingSystem.IsLinux();

            if (isWindows)
            {
                bool isArm = arch == Architecture.Arm64 || arch == Architecture.Arm;

                var winAssets = list.Where(a =>
                    a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                    a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
                    (a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                     (a.Name.Contains("win", StringComparison.OrdinalIgnoreCase) ||
                      a.Name.Contains("windows", StringComparison.OrdinalIgnoreCase)))
                ).ToList();

                if (isArm)
                {
                    // 1. ARM64 setup/installer exe
                    var armSetup = winAssets.FirstOrDefault(a =>
                        a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                        IsArmName(a.Name) &&
                        IsSetupOrInstaller(a.Name));
                    if (armSetup != null) return armSetup;

                    // 2. Any ARM64 exe
                    var armExe = winAssets.FirstOrDefault(a =>
                        a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                        IsArmName(a.Name));
                    if (armExe != null) return armExe;

                    // 3. ARM64 portable zip
                    var armZip = winAssets.FirstOrDefault(a =>
                        a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                        IsArmName(a.Name));
                    if (armZip != null) return armZip;

                    // Fallback to generic setup (x64 setup runs under emulation on ARM64 Windows)
                    var genericSetup = winAssets.FirstOrDefault(a =>
                        a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                        IsSetupOrInstaller(a.Name));
                    if (genericSetup != null) return genericSetup;

                    var anyExe = winAssets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                    if (anyExe != null) return anyExe;

                    return winAssets.FirstOrDefault();
                }
                else
                {
                    // Running on x64 / x86 - strictly exclude ARM binaries!
                    var x64Assets = winAssets.Where(a => !IsArmName(a.Name)).ToList();

                    // 1. Setup/installer exe with explicit x64/x86_64
                    var explicitX64Setup = x64Assets.FirstOrDefault(a =>
                        a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                        IsX64Name(a.Name) &&
                        IsSetupOrInstaller(a.Name));
                    if (explicitX64Setup != null) return explicitX64Setup;

                    // 2. Setup/installer exe (e.g. Fujifier-v2.1.1-Setup.exe)
                    var setupExe = x64Assets.FirstOrDefault(a =>
                        a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                        IsSetupOrInstaller(a.Name));
                    if (setupExe != null) return setupExe;

                    // 3. Any x64 exe
                    var x64Exe = x64Assets.FirstOrDefault(a =>
                        a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                        IsX64Name(a.Name));
                    if (x64Exe != null) return x64Exe;

                    // 4. Any exe (non-ARM)
                    var anyExe = x64Assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                    if (anyExe != null) return anyExe;

                    // 5. Explicit x64 portable zip
                    var x64Zip = x64Assets.FirstOrDefault(a =>
                        a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                        IsX64Name(a.Name));
                    if (x64Zip != null) return x64Zip;

                    // 6. Generic zip
                    return x64Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                }
            }

            if (isMacOS)
            {
                bool isArm = arch == Architecture.Arm64 || arch == Architecture.Arm;

                var macAssets = list.Where(a =>
                    (a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                     a.Name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase) ||
                     a.Name.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase)) &&
                    (a.Name.Contains("mac", StringComparison.OrdinalIgnoreCase) ||
                     a.Name.Contains("osx", StringComparison.OrdinalIgnoreCase) ||
                     a.Name.Contains("darwin", StringComparison.OrdinalIgnoreCase) ||
                     a.Name.Contains("applesilicon", StringComparison.OrdinalIgnoreCase))
                ).ToList();

                if (isArm)
                {
                    // 1. Apple Silicon / ARM64
                    var armAsset = macAssets.FirstOrDefault(a =>
                        a.Name.Contains("applesilicon", StringComparison.OrdinalIgnoreCase) ||
                        a.Name.Contains("apple-silicon", StringComparison.OrdinalIgnoreCase) ||
                        IsArmName(a.Name));
                    if (armAsset != null) return armAsset;

                    // 2. Universal build
                    var universalAsset = macAssets.FirstOrDefault(a => a.Name.Contains("universal", StringComparison.OrdinalIgnoreCase));
                    if (universalAsset != null) return universalAsset;

                    return macAssets.FirstOrDefault();
                }
                else
                {
                    // Intel x64 - exclude Apple Silicon / ARM builds
                    var intelAssets = macAssets.Where(a =>
                        !a.Name.Contains("applesilicon", StringComparison.OrdinalIgnoreCase) &&
                        !a.Name.Contains("apple-silicon", StringComparison.OrdinalIgnoreCase) &&
                        !IsArmName(a.Name)).ToList();

                    // 1. Explicit Intel / x64
                    var intelAsset = intelAssets.FirstOrDefault(a =>
                        a.Name.Contains("intel", StringComparison.OrdinalIgnoreCase) ||
                        IsX64Name(a.Name));
                    if (intelAsset != null) return intelAsset;

                    // 2. Universal build
                    var universalAsset = macAssets.FirstOrDefault(a => a.Name.Contains("universal", StringComparison.OrdinalIgnoreCase));
                    if (universalAsset != null) return universalAsset;

                    return intelAssets.FirstOrDefault();
                }
            }

            if (isLinux)
            {
                bool isArm = arch == Architecture.Arm64 || arch == Architecture.Arm;

                var linuxAssets = list.Where(a =>
                    a.Name.Contains("linux", StringComparison.OrdinalIgnoreCase) &&
                    (a.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
                     a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                     a.Name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase) ||
                     a.Name.EndsWith(".deb", StringComparison.OrdinalIgnoreCase))
                ).ToList();

                if (isArm)
                {
                    var armAsset = linuxAssets.FirstOrDefault(a => IsArmName(a.Name));
                    if (armAsset != null) return armAsset;
                    return linuxAssets.FirstOrDefault();
                }
                else
                {
                    var x64Assets = linuxAssets.Where(a => !IsArmName(a.Name)).ToList();
                    var explicitX64 = x64Assets.FirstOrDefault(a => IsX64Name(a.Name));
                    if (explicitX64 != null) return explicitX64;
                    return x64Assets.FirstOrDefault();
                }
            }

            return list.FirstOrDefault();
        }

        public async Task<string> DownloadUpdateAsync(UpdateAsset asset, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            if (asset == null || string.IsNullOrWhiteSpace(asset.DownloadUrl))
            {
                throw new ArgumentException("Asset download URL is invalid.", nameof(asset));
            }

            string tempDir = Path.Combine(Path.GetTempPath(), "Fujifier_Updates");
            Directory.CreateDirectory(tempDir);
            string destinationPath = Path.Combine(tempDir, asset.Name);

            using var response = await _httpClient.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? asset.Size;
            long totalRead = 0;

            using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            byte[] buffer = new byte[81920];
            int read;

            while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, read, ct);
                totalRead += read;

                if (totalBytes > 0 && progress != null)
                {
                    double ratio = Math.Clamp((double)totalRead / totalBytes, 0.0, 1.0);
                    progress.Report(ratio);
                }
            }

            progress?.Report(1.0);
            return destinationPath;
        }

        public bool LaunchInstallerAndExit(string installerFilePath)
        {
            if (string.IsNullOrWhiteSpace(installerFilePath) || !File.Exists(installerFilePath))
            {
                return false;
            }

            try
            {
                if (OperatingSystem.IsWindows() && installerFilePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = installerFilePath,
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                    return true;
                }

                // Fallback for non-windows or archives: open container directory / launcher
                CrossPlatformLauncher.OpenUrl(installerFilePath);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateService] Failed to launch installer: {ex.Message}");
                return false;
            }
        }
    }
}
