using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FrameEyeCameraFeed_Installer;

// Turns on Steam Link's OSC output in SteamVR's settings file (steamvr.vrsettings).
// These are the "Steam Link" options in SteamVR's settings window:
//   Enable OSC                                                -> driver_vrlink / useOSC
//   Share face tracking data to other apps on this PC via OSC -> driver_vrlink / useOSCFace
//   OSC Output Port                                           -> driver_vrlink / OSCOutPort
static class SteamVrSettings {
    const string Section = "driver_vrlink";
    public const int OscPort = 9015;

    static readonly JsonSerializerOptions WriteOptions = new() {
        WriteIndented = true,
        IndentSize = 3,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // SteamVR rewrites its settings file when it exits, so it must be closed before the file is edited.
    public static bool IsSteamVrRunning() {
        return Process.GetProcessesByName("vrserver").Length > 0;
    }

    public static string BackupPath(string settingsFile) {
        return settingsFile + ".frameeye-backup";
    }

    // SteamVR records where its config folder is in openvrpaths.vrpath.
    public static string? FindSettingsFile() {
        var folders = new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData };

        foreach (var folder in folders) {
            string vrpath = Path.Combine(Environment.GetFolderPath(folder), "openvr", "openvrpaths.vrpath");
            if (!File.Exists(vrpath)) continue;

            try {
                using var doc = JsonDocument.Parse(File.ReadAllText(vrpath));
                if (!doc.RootElement.TryGetProperty("config", out JsonElement config) || config.ValueKind != JsonValueKind.Array) continue;

                foreach (JsonElement entry in config.EnumerateArray()) {
                    string file = Path.Combine(entry.GetString() ?? "", "steamvr.vrsettings");
                    if (File.Exists(file)) return file;
                }
            }
            catch (JsonException) {
            }
        }

        return null;
    }

    // Applies the three OSC settings and returns a description of each one that had to change.
    // Nothing is written if they were all correct already. The previous file is kept at BackupPath.
    public static List<string> Apply(string settingsFile) {
        var readOptions = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
        if (JsonNode.Parse(File.ReadAllText(settingsFile), documentOptions: readOptions) is not JsonObject root) {
            throw new InvalidDataException("steamvr.vrsettings isn't in the expected format.");
        }

        if (root[Section] is not JsonObject section) {
            section = new JsonObject();
            root[Section] = section;
        }

        var changes = new List<string>();
        Set(section, "useOSC", JsonValue.Create(true), "Enable OSC", changes);
        Set(section, "useOSCFace", JsonValue.Create(true), "Share face tracking data via OSC", changes);
        Set(section, "OSCOutPort", JsonValue.Create(OscPort), "OSC Output Port", changes);

        if (changes.Count > 0) {
            File.Copy(settingsFile, BackupPath(settingsFile), true);

            string temp = settingsFile + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(WriteOptions));
            File.Move(temp, settingsFile, true);
        }

        return changes;
    }

    static void Set(JsonObject section, string key, JsonValue wanted, string label, List<string> changes) {
        string before = section[key]?.ToJsonString() ?? "not set";
        string after = wanted.ToJsonString();
        if (before == after) return;

        section[key] = wanted;
        changes.Add($"{label} ({key}): {before} -> {after}");
    }
}
