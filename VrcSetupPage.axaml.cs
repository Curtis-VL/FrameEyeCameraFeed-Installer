using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FrameEyeCameraFeed_Installer;

public partial class VrcSetupPage : UserControl {
    const string VrcftStoreUrl = "https://store.steampowered.com/app/3329480/VRCFaceTracking/";
    const string VrcftLaunchUrl = "steam://rungameid/3329480";
    const string EtvrReleasesUrl = "https://github.com/EyeTrackVR/EyeTrackVR/releases";
    const string DiscordUsername = "curtisvl";

    static readonly string[] StepTitles = {
        "Install the tracking module",
        "Turn on OSC in SteamVR",
        "Install VRCFaceTracking",
        "Install EyeTrackVR",
        "Set up EyeTrackVR",
        "Open VRCFaceTracking"
    };

    readonly StackPanel[] steps;
    readonly Border[] badges;
    readonly TextBlock[] labels;
    readonly bool[] stepDone = new bool[StepTitles.Length];

    int step = 0;

    // Set while the user is being asked what to do about the original EyeTrackVR module.
    VrcftModule.Release? pendingRelease;
    List<string> pendingConflicts = new();

    public VrcSetupPage() {
        InitializeComponent();

        // StepDone is the final tips page, it comes after the numbered steps and has no entry in the step list.
        steps = new[] { Step1, Step2, Step3, Step4, Step5, Step6, StepDone };
        badges = new Border[StepTitles.Length];
        labels = new TextBlock[StepTitles.Length];

        for (int i = 0; i < StepTitles.Length; i++) {
            badges[i] = new Border();
            badges[i].Classes.Add("step");
            labels[i] = new TextBlock { Text = StepTitles[i], FontSize = 13, VerticalAlignment = VerticalAlignment.Center };

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
            Grid.SetColumn(labels[i], 1);
            row.Children.Add(badges[i]);
            row.Children.Add(labels[i]);
            StepList.Children.Add(row);
        }

        ModulePathText.Text = VrcftModule.CustomLibsPath;
        SetHeadsetHost("frame");

        BackButton.Click += ((sender, e) => {
            ShowStep(step - 1);
        });
        NextButton.Click += ((sender, e) => {
            // The last two steps have nothing to tick off, moving on counts as done.
            if (step == 4 || step == 5) stepDone[step] = true;
            ShowStep(step + 1);
        });

        InstallModuleButton.Click += (async (sender, e) => {
            await InstallModule();
        });
        RemoveConflictsButton.Click += (async (sender, e) => {
            await ResolveConflicts(true);
        });
        KeepConflictsButton.Click += (async (sender, e) => {
            await ResolveConflicts(false);
        });
        ModuleReleasesLink.Click += ((sender, e) => {
            OpenUrl(VrcftModule.ReleasesUrl);
        });

        ApplySteamVrButton.Click += ((sender, e) => {
            ApplySteamVrSettings();
        });

        OpenVrcftStoreButton.Click += ((sender, e) => {
            OpenUrl(VrcftStoreUrl);
        });
        VrcftInstalledCheck.IsCheckedChanged += ((sender, e) => {
            stepDone[2] = VrcftInstalledCheck.IsChecked == true;
            ShowStep(step);
        });

        OpenEtvrButton.Click += ((sender, e) => {
            OpenUrl(EtvrReleasesUrl);
        });
        EtvrInstalledCheck.IsCheckedChanged += ((sender, e) => {
            stepDone[3] = EtvrInstalledCheck.IsChecked == true;
            ShowStep(step);
        });

        CopyLeftButton.Click += ((sender, e) => {
            CopyToClipboard(LeftUrlText.Text, CopyLeftButton);
        });
        CopyRightButton.Click += ((sender, e) => {
            CopyToClipboard(RightUrlText.Text, CopyRightButton);
        });

        LaunchVrcftButton.Click += ((sender, e) => {
            OpenUrl(VrcftLaunchUrl);
        });
        CopyDiscordButton.Click += ((sender, e) => {
            CopyToClipboard(DiscordUsername, CopyDiscordButton);
        });

        ShowStep(0);
    }

    // The camera addresses follow the headset address entered on the Install tab.
    public void SetHeadsetHost(string host) {
        LeftUrlText.Text = $"http://{host}:8090/1";
        RightUrlText.Text = $"http://{host}:8090/0";
    }

    void ShowStep(int index) {
        step = Math.Clamp(index, 0, steps.Length - 1);
        bool finished = step == steps.Length - 1;

        for (int i = 0; i < steps.Length; i++) {
            steps[i].IsVisible = i == step;
        }

        for (int i = 0; i < badges.Length; i++) {
            badges[i].Classes.Set("current", i == step);
            badges[i].Classes.Set("done", stepDone[i] && i != step);
            badges[i].Child = new TextBlock { Text = stepDone[i] && i != step ? "✓" : (i + 1).ToString() };

            labels[i].FontWeight = i == step ? FontWeight.SemiBold : FontWeight.Normal;
            labels[i].Opacity = i == step || stepDone[i] ? 1 : 0.7;
        }

        StepCounter.Text = finished ? "" : $"Step {step + 1} of {badges.Length}";
        BackButton.IsVisible = step > 0;
        NextButton.IsVisible = !finished;

        // The two automatic steps can be skipped, the two "I have installed" steps have to be confirmed.
        bool needsConfirming = step == 2 || step == 3;
        NextButton.IsEnabled = !needsConfirming || stepDone[step];

        if (step == badges.Length - 1) {
            NextButton.Content = "Done";
        }
        else {
            NextButton.Content = step <= 1 && !stepDone[step] ? "Skip" : "Next";
        }
    }

    void OpenUrl(string url) {
        TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri(url));
    }

    async void CopyToClipboard(string? text, Button button) {
        IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null || text == null) return;

        await clipboard.SetTextAsync(text);

        object? label = button.Content;
        if (label as string == "Copied") return;

        button.Content = "Copied";
        await Task.Delay(1500);
        button.Content = label;
    }

    static void Log(SelectableTextBlock log, string message) {
        log.Text += (string.IsNullOrEmpty(log.Text) ? "" : "\n") + message;
    }

    async Task InstallModule() {
        ModuleLog.Text = "";
        ConflictPanel.IsVisible = false;
        InstallModuleButton.IsEnabled = false;

        try {
            if (VrcftModule.IsVrcftRunning()) {
                Log(ModuleLog, "VRCFaceTracking is running. Close it, then click Install module again.");
                return;
            }

            Log(ModuleLog, "Checking GitHub for the latest release...");
            VrcftModule.Release release = await VrcftModule.GetLatestRelease();
            Log(ModuleLog, $"Latest release: {release.Version} ({release.FileName})");

            List<string> conflicts = VrcftModule.FindConflicts(VrcftModule.CustomLibsPath, release.FileName);
            if (conflicts.Count > 0) {
                pendingRelease = release;
                pendingConflicts = conflicts;

                ConflictList.Text = string.Join("\n", conflicts);
                ConflictPanel.IsVisible = true;
                Log(ModuleLog, "Found the original EyeTrackVR module. Choose what to do with it above.");
                return;
            }

            await DownloadModule(release);
        }
        catch (Exception ex) {
            Log(ModuleLog, $"Couldn't install the module: {ex.Message}");
        }
        finally {
            InstallModuleButton.IsEnabled = true;
        }
    }

    async Task ResolveConflicts(bool remove) {
        if (pendingRelease == null) return;

        ConflictPanel.IsVisible = false;
        InstallModuleButton.IsEnabled = false;

        try {
            if (remove) {
                foreach (string path in pendingConflicts) {
                    VrcftModule.RemoveConflict(VrcftModule.CustomLibsPath, path);
                    Log(ModuleLog, $"Removed {path}");
                }
            }
            else {
                Log(ModuleLog, "Kept the original EyeTrackVR module. VRCFaceTracking may not work correctly until it is removed.");
            }

            await DownloadModule(pendingRelease);
        }
        catch (Exception ex) {
            Log(ModuleLog, $"Couldn't install the module: {ex.Message}");
        }
        finally {
            pendingRelease = null;
            InstallModuleButton.IsEnabled = true;
        }
    }

    async Task DownloadModule(VrcftModule.Release release) {
        Log(ModuleLog, $"Downloading {release.FileName}...");
        string path = await VrcftModule.Download(release, VrcftModule.CustomLibsPath);
        Log(ModuleLog, $"Installed {release.Version} to {path}");

        stepDone[0] = true;
        ShowStep(step);
    }

    void ApplySteamVrSettings() {
        SteamVrLog.Text = "";

        try {
            string? settingsFile = SteamVrSettings.FindSettingsFile();
            if (settingsFile == null) {
                Log(SteamVrLog, "Couldn't find SteamVR's settings file. Is SteamVR installed and has it been run once?");
                Log(SteamVrLog, "You can still set the three options by hand, see below.");
                return;
            }

            if (SteamVrSettings.IsSteamVrRunning()) {
                Log(SteamVrLog, "SteamVR is running (vrserver.exe). Close SteamVR, then click Apply settings again.");
                return;
            }

            List<string> changes = SteamVrSettings.Apply(settingsFile);
            if (changes.Count == 0) {
                Log(SteamVrLog, "All three settings were already correct, nothing was changed.");
            }
            else {
                Log(SteamVrLog, $"Changed in {settingsFile}:");
                foreach (string change in changes) {
                    Log(SteamVrLog, "  " + change);
                }
                Log(SteamVrLog, $"Backup of the previous settings: {SteamVrSettings.BackupPath(settingsFile)}");
            }

            stepDone[1] = true;
            ShowStep(step);
        }
        catch (Exception ex) {
            Log(SteamVrLog, $"Couldn't change the SteamVR settings: {ex.Message}");
        }
    }
}
