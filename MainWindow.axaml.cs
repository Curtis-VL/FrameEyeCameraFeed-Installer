using Avalonia.Controls;
using Avalonia.Threading;
using Renci.SshNet;
using Renci.SshNet.Common;
using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FrameEyeCameraFeed_Installer;

public partial class MainWindow : Window {
    const string SshUser = "steamos";
    const string DefaultHost = "frame";
    const string InstallCommand = "curl -fsSL https://github.com/Curtis-VL/FrameEyeCameraFeed/releases/latest/download/install.sh | sudo bash";
    const string UninstallCommand = InstallCommand + " -s -- --uninstall";

    static readonly Regex AnsiEscapes = new(@"\x1B(\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(\x07|\x1B\\)|[@-Z\\-_])");

    bool installerRunning = false;
    volatile bool cancelRequested = false;
    volatile bool showDetails = false;

    ShellStream? shell;
    SshClient? client;

    string pass = "";

    public MainWindow() {
        InitializeComponent();

        LogTextBlock.Text = "Enter your Frame's user password, then click Install / Update.";

        // The Security & privacy tab shows the same values the installer actually uses.
        InstallCommandText.Text = InstallCommand;
        SshTargetText.Text = $"ssh {SshUser}@{Host()}";
        HostInput.TextChanged += ((sender, e) => {
            SshTargetText.Text = $"ssh {SshUser}@{Host()}";
            VrcSetup.SetHeadsetHost(Host());
        });

        InstallButton.Click += ((sender, e) => {
            InstallUninstall(true);
        });
        UninstallButton.Click += ((sender, e) => {
            InstallUninstall(false);
        });
        CancelButton.Click += ((sender, e) => {
            cancelRequested = true;
        });

        StatusButton.Click += ((sender, e) => {
            if (Uri.TryCreate($"http://{Host()}:8090", UriKind.Absolute, out Uri? uri)) {
                Launcher.LaunchUriAsync(uri);
            }
        });

        DetailsCheck.IsCheckedChanged += ((sender, e) => {
            showDetails = DetailsCheck.IsChecked == true;
        });

        SetupGuideLink.Click += ((sender, e) => {
            MainTabs.SelectedItem = HelpTab;
        });
        SecurityLink.Click += ((sender, e) => {
            MainTabs.SelectedItem = SecurityTab;
        });
    }

    string Host() {
        return string.IsNullOrWhiteSpace(HostInput.Text) ? DefaultHost : HostInput.Text.Trim();
    }

    void Log(string message) {
        Dispatcher.UIThread.Post(() => {
            LogTextBlock.Text += (string.IsNullOrEmpty(LogTextBlock.Text) ? "" : "\n") + message;
            Dispatcher.UIThread.Post(LogScroll.ScrollToEnd, DispatcherPriority.Background);
        });
    }

    void SetBusy(bool busy, string status) {
        installerRunning = busy;

        StatusText.Text = status;
        BusyBar.IsVisible = busy;
        InstallButton.IsEnabled = !busy;
        UninstallButton.IsEnabled = !busy;
        VrcSetupCheck.IsEnabled = !busy;
        PassInput.IsEnabled = !busy;
        HostInput.IsEnabled = !busy;

        // Cancel only becomes clickable once the command is running, see InstallUninstall.
        CancelButton.IsVisible = busy;
        CancelButton.IsEnabled = false;
    }

    // Closes the connection, forgets the password and unlocks the UI again.
    void Finish(string status) {
        shell?.Dispose();
        shell = null;

        if (client != null) {
            if (client.IsConnected) client.Disconnect();
            client.Dispose();
            client = null;
        }

        pass = "";

        Dispatcher.UIThread.Post(() => {
            SetBusy(false, status);
        });
    }

    void InstallUninstall(bool install) {
        if (installerRunning) return;

        LogTextBlock.Text = "";

        if (PassInput.Text == null || PassInput.Text == "") {
            LogTextBlock.Text = "Please enter your Frame's user password, set in the Frame's developer settings.\nIt is only used to sign in to the headset - it is not stored or sent anywhere else.";
            PassInput.Focus();
            return;
        }

        pass = PassInput.Text;
        string host = Host();
        bool continueToVrcSetup = install && VrcSetupCheck.IsChecked == true;

        cancelRequested = false;
        SetBusy(true, install ? "Installing..." : "Uninstalling...");

        Log($"Connecting to {SshUser}@{host} over SSH...");

        Task.Run(() => {
            client = new SshClient(host, SshUser, pass);
            client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);

            try {
                client.Connect();
            }
            catch (SshAuthenticationException) {
                Log("The Frame rejected the password.");
                Log("Use the user password set in the Frame's developer settings, not your Steam or PC password.");
                Finish("Password rejected");
                return;
            }
            catch (Exception ex) {
                Log($"Error connecting to the Frame: {ex.Message}");
                Log("Is it powered on and connected to the same network as your PC?");
                Log("See the 'Setup & troubleshooting' tab for more help.");
                Finish("Couldn't connect");
                return;
            }

            Log("Connected to the Frame!");

            try {
                shell = client.CreateShellStream("xterm", 80, 24, 800, 600, 1024);
                shell.WriteLine(install ? InstallCommand : UninstallCommand);

                Log($"Command sent, waiting for {(install ? "install" : "uninstall")} to finish...");
                Dispatcher.UIThread.Post(() => {
                    CancelButton.IsEnabled = true;
                });

                string status = ReadLoop(host);
                Finish(status);

                if (status == "Installed" && continueToVrcSetup) {
                    Log("Continuing on the 'VRChat setup' tab...");
                    Dispatcher.UIThread.Post(() => {
                        MainTabs.SelectedItem = VrcTab;
                    });
                }
            }
            catch (Exception ex) {
                Log($"Connection error: {ex.Message}");
                Finish("Failed");
            }
        });
    }

    // Reads the headset's output until the installer reports it is done. Returns the final status.
    private string ReadLoop(string host) {
        if (shell == null || client == null)
            return "Failed";

        var buffer = new byte[4096];
        string tail = "";

        while (client.IsConnected) {
            if (cancelRequested) {
                Log("Cancelled. If the install had already started, run Install / Update again to complete it.");
                return "Cancelled";
            }

            if (shell.DataAvailable) {
                int bytesRead = shell.Read(buffer, 0, buffer.Length);

                if (bytesRead > 0) {
                    string output = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    if (showDetails) {
                        string cleaned = CleanOutput(output);
                        if (cleaned != "") Log(cleaned);
                    }

                    if (output.Contains("[sudo] password for") ||
                        output.Contains("password:")) {
                        shell.WriteLine(pass);
                    }

                    // Match against this read plus the end of the previous one, a message can be split across two reads.
                    string recent = tail + output;
                    tail = recent.Length > 64 ? recent.Substring(recent.Length - 64) : recent;

                    if (recent.Contains("Installed and running")) {
                        Log("Installation completed successfully!");
                        Log("Click 'Open status page' to check the status page.");
                        Log($"Camera feeds at: http://{host}:8090/0 http://{host}:8090/1");
                        Log("(For better reliability, set the Frame to a static IP address in your router DHCP settings instead of using the 'frame' hostname)");
                        return "Installed";
                    }

                    if (recent.Contains("framestream removed")) {
                        Log("Uninstalled successfully!");
                        Log("Click 'Open status page' to confirm the software is no longer running.");
                        return "Uninstalled";
                    }
                }
            }
            else {
                Thread.Sleep(50);
            }
        }

        Log("The connection to the Frame was lost before it finished.");
        return "Connection lost";
    }

    // Makes raw terminal output readable: drops colour codes, and never shows the password.
    string CleanOutput(string output) {
        output = AnsiEscapes.Replace(output, "").Replace("\r", "");
        if (pass != "") output = output.Replace(pass, "********");
        return output.Trim('\n');
    }
}
