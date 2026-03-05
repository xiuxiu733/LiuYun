using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LiuYun.Services
{
    public sealed class UpdateService
    {
        public const string LatestManifestUrl = "https://liuyun.cn-nb1.rains3.com/latest.json";

        private static readonly HttpClient SharedHttpClient = new()
        {
            Timeout = TimeSpan.FromMinutes(15)
        };

        public async Task<UpdateCheckResult> CheckForUpdatesAsync(string currentVersion, CancellationToken cancellationToken = default)
        {
            try
            {
                UpdateDiagnostics.Log("UpdateService.Check", $"Start. currentVersion={currentVersion}, manifestUrl={LatestManifestUrl}");
                using HttpResponseMessage response = await SharedHttpClient.GetAsync(LatestManifestUrl, cancellationToken).ConfigureAwait(false);
                UpdateDiagnostics.Log("UpdateService.Check", $"Manifest response status={(int)response.StatusCode} {response.StatusCode}");
                if (!response.IsSuccessStatusCode)
                {
                    return UpdateCheckResult.CreateFail($"HTTP {(int)response.StatusCode}");
                }

                string manifestJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                string preview = manifestJson.Length > 500 ? manifestJson.Substring(0, 500) + "..." : manifestJson;
                UpdateDiagnostics.Log("UpdateService.Check", $"Manifest payload preview={preview}");

                UpdateManifest? manifest = JsonSerializer.Deserialize<UpdateManifest>(
                    manifestJson,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
                {
                    UpdateDiagnostics.Log("UpdateService.Check", "Manifest invalid: missing version.");
                    return UpdateCheckResult.CreateFail("latest.json missing version");
                }

                int compare = CompareVersions(manifest.Version, currentVersion);
                UpdateDiagnostics.Log(
                    "UpdateService.Check",
                    $"Parsed manifest. version={manifest.Version}, compare={compare}");
                return UpdateCheckResult.CreateSuccess(currentVersion, manifest.Version, compare > 0, manifest);
            }
            catch (Exception ex)
            {
                UpdateDiagnostics.Log("UpdateService.Check", $"Exception: {ex}");
                return UpdateCheckResult.CreateFail(ex.Message);
            }
        }

        private static int CompareVersions(string remote, string local)
        {
            int[] remoteParts = ParseVersion(remote);
            int[] localParts = ParseVersion(local);
            int length = Math.Max(remoteParts.Length, localParts.Length);

            for (int i = 0; i < length; i++)
            {
                int r = i < remoteParts.Length ? remoteParts[i] : 0;
                int l = i < localParts.Length ? localParts[i] : 0;
                if (r != l)
                {
                    return r.CompareTo(l);
                }
            }

            return 0;
        }

        private static int[] ParseVersion(string version)
        {
            string[] parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
            int[] parsed = new int[parts.Length];

            for (int i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], out parsed[i]))
                {
                    parsed[i] = 0;
                }
            }

            return parsed;
        }
    }

    public sealed class UpdateManifest
    {
        public string Version { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
    }

    public sealed class UpdateCheckResult
    {
        public bool Success { get; private set; }
        public string ErrorMessage { get; private set; } = string.Empty;
        public string CurrentVersion { get; private set; } = string.Empty;
        public string RemoteVersion { get; private set; } = string.Empty;
        public bool HasUpdate { get; private set; }
        public UpdateManifest? Manifest { get; private set; }

        public static UpdateCheckResult CreateSuccess(string currentVersion, string remoteVersion, bool hasUpdate, UpdateManifest manifest)
        {
            return new UpdateCheckResult
            {
                Success = true,
                CurrentVersion = currentVersion,
                RemoteVersion = remoteVersion,
                HasUpdate = hasUpdate,
                Manifest = manifest
            };
        }

        public static UpdateCheckResult CreateFail(string errorMessage)
        {
            return new UpdateCheckResult
            {
                Success = false,
                ErrorMessage = errorMessage
            };
        }
    }
}
