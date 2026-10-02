using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace FrameEyeCameraFeed_Installer;

// Installs the Steam Link version of the EyeTrackVR module for VRCFaceTracking from its latest GitHub release.
static class VrcftModule {
    public const string ReleasesUrl = "https://github.com/Curtis-VL/ETVRTrackingModule-SteamLink/releases";
    const string LatestReleaseApi = "https://api.github.com/repos/Curtis-VL/ETVRTrackingModule-SteamLink/releases/latest";
    const string DownloadPrefix = "https://github.com/Curtis-VL/ETVRTrackingModule-SteamLink/releases/download/";

    public record Release(string Version, string FileName, string DownloadUrl);

    static readonly HttpClient Http = CreateHttpClient();

    static HttpClient CreateHttpClient() {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        // GitHub's API rejects requests without a user agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FrameEyeCameraFeed-Installer");
        return http;
    }

    // VRCFaceTracking loads every module it finds in this folder and its sub-folders.
    public static string CustomLibsPath {
        get {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCFaceTracking", "CustomLibs");
        }
    }

    // The module files are locked while VRCFaceTracking is open.
    public static bool IsVrcftRunning() {
        return Process.GetProcessesByName("VRCFaceTracking").Length > 0;
    }

    public static async Task<Release> GetLatestRelease() {
        using HttpResponseMessage response = await Http.GetAsync(LatestReleaseApi);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string version = doc.RootElement.GetProperty("tag_name").GetString() ?? "";

        foreach (JsonElement asset in doc.RootElement.GetProperty("assets").EnumerateArray()) {
            string name = asset.GetProperty("name").GetString() ?? "";
            string url = asset.GetProperty("browser_download_url").GetString() ?? "";

            // Only accept a plain .dll file name, downloaded from this repository's own releases.
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(name) == name &&
                url.StartsWith(DownloadPrefix, StringComparison.Ordinal)) {
                return new Release(version, name, url);
            }
        }

        throw new InvalidOperationException("The latest release doesn't contain a module .dll file.");
    }

    // Finds other copies of the EyeTrackVR module, which conflict with this one. A copy with the same
    // name directly in CustomLibs isn't listed, the download replaces it. Modules installed from inside
    // VRCFaceTracking live in their own sub-folder with a module.json, those are returned as the folder.
    public static List<string> FindConflicts(string customLibs, string moduleFileName) {
        var conflicts = new List<string>();
        if (!Directory.Exists(customLibs)) return conflicts;

        string target = Path.Combine(customLibs, moduleFileName);

        foreach (string dll in Directory.EnumerateFiles(customLibs, "*.dll", SearchOption.AllDirectories)) {
            string name = Path.GetFileName(dll);
            bool isEtvr = name.Contains("ETVR", StringComparison.OrdinalIgnoreCase) ||
                          name.Contains("EyeTrackVR", StringComparison.OrdinalIgnoreCase);

            if (!isEtvr || string.Equals(dll, target, StringComparison.OrdinalIgnoreCase)) continue;

            string folder = Path.GetDirectoryName(dll) ?? customLibs;
            bool isModuleFolder = !string.Equals(folder, customLibs, StringComparison.OrdinalIgnoreCase) &&
                                  File.Exists(Path.Combine(folder, "module.json"));

            string conflict = isModuleFolder ? folder : dll;
            if (!conflicts.Contains(conflict)) conflicts.Add(conflict);
        }

        return conflicts;
    }

    public static void RemoveConflict(string customLibs, string path) {
        // Never delete anything outside the CustomLibs folder.
        string root = Path.GetFullPath(customLibs) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidOperationException($"{path} is not inside {customLibs}.");
        }

        if (Directory.Exists(path)) {
            Directory.Delete(path, true);
        }
        else {
            File.Delete(path);
        }
    }

    // Downloads the module into CustomLibs, replacing any existing file with the same name. Returns its path.
    public static async Task<string> Download(Release release, string customLibs) {
        Directory.CreateDirectory(customLibs);

        string target = Path.Combine(customLibs, release.FileName);
        string temp = target + ".download";

        try {
            using (HttpResponseMessage response = await Http.GetAsync(release.DownloadUrl)) {
                response.EnsureSuccessStatusCode();

                using FileStream file = File.Create(temp);
                await response.Content.CopyToAsync(file);
            }

            File.Move(temp, target, true);
        }
        finally {
            if (File.Exists(temp)) File.Delete(temp);
        }

        return target;
    }
}
