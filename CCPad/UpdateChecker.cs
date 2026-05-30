using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CCPad
{
    internal sealed class ReleaseInfo
    {
        public string Version { get; init; } = "";
        public string? AssetUrl { get; init; }
        public string? AssetName { get; init; }
    }

    internal static class UpdateChecker
    {
        // Fork: points at this fork's releases, not upstream nuomiaa/CCPad,
        // so auto-update pulls this fork's installers.
        private const string ReleasesApiUrl =
            "https://api.github.com/repos/vv-l/CCPad/releases/latest";

        public const string ReleasesPageUrl =
            "https://github.com/vv-l/CCPad/releases";

        private static readonly HttpClient _http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CCPad-UpdateCheck/1.0");
            client.Timeout = TimeSpan.FromSeconds(10);
            return client;
        }

        /// <summary>
        /// Returns release info if a newer version exists, or null when
        /// up-to-date / on error.
        /// </summary>
        public static async Task<ReleaseInfo?> CheckAsync()
        {
            try
            {
                var json = await _http.GetStringAsync(ReleasesApiUrl);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var tagName = root.GetProperty("tag_name").GetString();
                if (tagName == null) return null;

                var remote = tagName.TrimStart('v', 'V');
                var current = GetCurrentVersion();

                if (!Version.TryParse(remote, out var remoteVer) ||
                    !Version.TryParse(current, out var currentVer) ||
                    remoteVer <= currentVer)
                {
                    return null;
                }

                // Find installer asset matching current architecture
                string? assetUrl = null;
                string? assetName = null;
                var archTag = RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.X64 => "x64",
                    Architecture.X86 => "x86",
                    Architecture.Arm64 => "arm64",
                    _ => "x64"
                };

                if (root.TryGetProperty("assets", out var assets))
                {
                    var exts = new[] { ".exe", ".msix", ".msixbundle", ".msi", ".zip" };
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.GetProperty("name").GetString() ?? "";
                        if (exts.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                            && name.Contains(archTag, StringComparison.OrdinalIgnoreCase))
                        {
                            assetUrl = asset.GetProperty("browser_download_url").GetString();
                            assetName = name;
                            break;
                        }
                    }
                }

                return new ReleaseInfo
                {
                    Version = remote,
                    AssetUrl = assetUrl,
                    AssetName = assetName
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Downloads the asset to a temp folder and returns the local file path.
        /// Reports progress as 0..100.
        /// </summary>
        public static async Task<string> DownloadAssetAsync(
            string url, string fileName,
            IProgress<int>? progress = null,
            CancellationToken ct = default)
        {
            var dir = Path.Combine(Path.GetTempPath(), "CCPad_Update");
            Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, fileName);

            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var totalBytes = resp.Content.Headers.ContentLength ?? -1;
            long received = 0;

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            var buf = new byte[81920];
            int read;
            while ((read = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct);
                received += read;
                if (totalBytes > 0)
                    progress?.Report((int)(received * 100 / totalBytes));
            }

            progress?.Report(100);
            return filePath;
        }

        public static void LaunchInstaller(string filePath)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = filePath,
                Arguments = "/SILENT /CLOSEAPPLICATIONS",
                UseShellExecute = true
            });
        }

        public static string GetCurrentVersion()
        {
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "0.0.0";
        }
    }
}
