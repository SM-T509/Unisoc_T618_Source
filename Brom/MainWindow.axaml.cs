using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Brom.Models;
using Brom.Protocols;
using Brom.Services;
using Brom.T509Native;

using LogType = Brom.Services.LogType;

namespace Brom;

public partial class MainWindow : Window
{
    public class DeviceInfo
    {
        public string DeviceName { get; set; } = "";
        public string DeviceType { get; set; } = "";
        public string Port { get; set; } = "";
        public string VID { get; set; } = "";
        public string PID { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public string SerialNumber { get; set; } = "";

        public override string ToString()
        {
            string serial = string.IsNullOrEmpty(SerialNumber) ? "" : $" [{SerialNumber}]";
            string port = string.IsNullOrEmpty(Port) || Port == "N/A" ? "" : $" ({Port})";
            return $"{DeviceType}: {DeviceName}{serial}{port}";
        }
    }

    public class DeviceDetector
    {
        private const string SAMSUNG_VID = "04E8";
        private const string SAMSUNG_MTP_PID1 = "6860";
        private const string SAMSUNG_MTP_PID2 = "685E";
        private const string SAMSUNG_MTP_PID3 = "685C";

        private const string BROM_VID = "1782";
        private const string BROM_PID = "4D00";
        private const string DIAG_PID = "4D01";

        public static List<DeviceInfo> GetDevices()
        {
            List<DeviceInfo> devices = new List<DeviceInfo>();

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                        "SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE '%USB%'"))
                    {
                        foreach (ManagementObject item in searcher.Get())
                        {
                            string deviceId = item["DeviceID"]?.ToString();
                            string name = item["Name"]?.ToString();
                            if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(name))
                            {
                                continue;
                            }

                            string vid = ExtractVid(deviceId);
                            string pid = ExtractPid(deviceId);
                            if (string.IsNullOrEmpty(vid) || string.IsNullOrEmpty(pid)
                                || name.ToLower().Contains("cdc composite")
                                || name.ToLower().Contains("composite device"))
                            {
                                continue;
                            }

                            DeviceInfo info = null;
                            string serial = ExtractSerial(deviceId);

                            if (vid.Equals(SAMSUNG_VID, StringComparison.OrdinalIgnoreCase)
                                && (pid.Equals(SAMSUNG_MTP_PID1, StringComparison.OrdinalIgnoreCase)
                                    || pid.Equals(SAMSUNG_MTP_PID2, StringComparison.OrdinalIgnoreCase)
                                    || pid.Equals(SAMSUNG_MTP_PID3, StringComparison.OrdinalIgnoreCase)))
                            {
                                bool isAdb = name.ToUpper().Contains("ADB");
                                info = new DeviceInfo
                                {
                                    DeviceName = name,
                                    DeviceType = isAdb ? "Samsung ADB Mode" : "Samsung MTP Mode",
                                    VID = vid,
                                    PID = pid,
                                    DeviceId = deviceId,
                                    SerialNumber = serial
                                };
                            }
                            else if (vid.Equals(BROM_VID, StringComparison.OrdinalIgnoreCase))
                            {
                                string type = "BROM Device";
                                if (pid.Equals(BROM_PID, StringComparison.OrdinalIgnoreCase))
                                {
                                    type = "BROM Mode";
                                }
                                else if (pid.Equals(DIAG_PID, StringComparison.OrdinalIgnoreCase)
                                         || name.Contains("SPRD U2S Diag", StringComparison.OrdinalIgnoreCase))
                                {
                                    type = "Diag Mode";
                                }

                                string cleanName = Regex.Replace(name, @"\s*\(COM\d+\)", "", RegexOptions.IgnoreCase).Trim();
                                info = new DeviceInfo
                                {
                                    DeviceName = cleanName,
                                    DeviceType = type,
                                    VID = vid,
                                    PID = pid,
                                    DeviceId = deviceId,
                                    SerialNumber = "",
                                    Port = GetComPortFromName(name) ?? GetGenericComPort(deviceId)
                                };
                            }

                            if (info != null)
                            {
                                devices.Add(info);
                            }
                        }
                    }

                    var grouped = (from d in devices
                                   group d by d.SerialNumber into g
                                   select g.OrderByDescending(d => GetTypePriority(d.DeviceType, d.DeviceName)).First()).ToList();

                    foreach (var device in grouped)
                    {
                        if (device.DeviceType.Contains("Samsung"))
                        {
                            device.Port = GetModemPort(device.DeviceId);
                        }
                    }
                    devices = grouped;

                    // Also list every serial port so non-BROM/misidentified
                    // devices can be selected manually.
                    foreach (var port in SerialPort.GetPortNames())
                    {
                        if (devices.Any(d => string.Equals(d.Port, port, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        string vidPid = UsbIds.VidPidOf(port);
                        string type = "Serial Port";
                        if (vidPid == "1782:4D00") type = "BROM Mode";
                        else if (vidPid == "1782:4D01") type = "Diag Mode";
                        else if (vidPid.StartsWith("04E8:", StringComparison.Ordinal)) type = "Samsung MTP Mode";
                        devices.Add(new DeviceInfo
                        {
                            DeviceName = port,
                            DeviceType = type,
                            Port = port,
                            VID = vidPid.Length >= 4 ? vidPid.Substring(0, 4) : "",
                            PID = vidPid.Length >= 9 ? vidPid.Substring(5, 4) : "",
                            DeviceId = port,
                            SerialNumber = ""
                        });
                    }
                }
                catch
                {
                    // ignore
                }
            }
            else
            {
                foreach (var port in SerialPort.GetPortNames())
                {
                    string vidPid = UsbIds.VidPidOf(port);
                    string type = "Serial Port";
                    if (vidPid == "1782:4D00" || Brom.Protocols.SpdMode.ProbeBromPort(port)) type = "BROM Mode";
                    else if (vidPid == "1782:4D01") type = "Diag Mode";
                    else if (vidPid.StartsWith("04E8:", StringComparison.Ordinal)) type = "Samsung MTP Mode";

                    devices.Add(new DeviceInfo
                    {
                        DeviceName = port,
                        DeviceType = type,
                        Port = port,
                        VID = vidPid.Length >= 4 ? vidPid.Substring(0, 4) : "",
                        PID = vidPid.Length >= 9 ? vidPid.Substring(5, 4) : "",
                        DeviceId = port,
                        SerialNumber = ""
                    });
                }

                try
                {
                    var psi = new ProcessStartInfo("adb", "devices")
                    {
                        RedirectStandardOutput = true,
                        UseShellExecute = false
                    };
                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        string output = proc.StandardOutput.ReadToEnd();
                        proc.WaitForExit();
                        foreach (var line in output.Split('\n'))
                        {
                            var parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 2 && parts[1] == "device")
                            {
                                devices.Add(new DeviceInfo
                                {
                                    DeviceName = parts[0],
                                    DeviceType = "Samsung ADB Mode",
                                    Port = "",
                                    VID = "04E8",
                                    PID = "",
                                    DeviceId = parts[0],
                                    SerialNumber = parts[0]
                                });
                            }
                        }
                    }
                }
                catch { }
            }

            return devices;
        }

        private static string ExtractVid(string deviceId)
        {
            Match m = Regex.Match(deviceId, @"VID_([0-9A-F]{4})", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : "";
        }

        private static string ExtractPid(string deviceId)
        {
            Match m = Regex.Match(deviceId, @"PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : "";
        }

        private static string ExtractSerial(string deviceId)
        {
            string[] parts = deviceId.Split('\\');
            if (parts.Length >= 3)
            {
                string key = parts[2];
                int amp = key.IndexOf('&');
                if (amp > 0) key = key.Substring(0, amp);
                return key;
            }
            return "";
        }

        private static string GetComPortFromName(string deviceName)
        {
            Match m = Regex.Match(deviceName, @"\((COM\d+)\)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
        }

        private static string GetGenericComPort(string deviceId)
        {
            return null;
        }

        private static int GetTypePriority(string deviceType, string deviceName)
        {
            if (deviceType.Contains("MTP")) return 3;
            if (deviceType.Contains("ADB")) return 2;
            return 1;
        }

        private static string GetModemPort(string deviceId)
        {
            if (!OperatingSystem.IsWindows())
            {
                foreach (var p in SerialPort.GetPortNames())
                    if (UsbIds.VidPidOf(p).StartsWith("04E8:", StringComparison.Ordinal)) return p;
                var ports = SerialPort.GetPortNames();
                return ports.Length > 0 ? ports[0] : string.Empty;
            }
            // Windows WMI not re-implemented here; reuse MtpProtocol via empty (fallback)
            return string.Empty;
        }
    }

    private CancellationTokenSource _operationCts;
    private DispatcherTimer _deviceTimer;
    private TextBlock? _lastLogBlock;

    private static readonly Avalonia.Media.Color V15_LOG_GREEN = Avalonia.Media.Color.FromRgb(74, 222, 128);
    private static readonly Avalonia.Media.Color V15_LOG_LINE = Avalonia.Media.Color.FromRgb(176, 184, 197);

    public MainWindow()
    {
        InitializeComponent();

        _operationCts = new CancellationTokenSource();

        BuildActions();
        searchBox.TextChanged += (_, __) => RefreshDevices();
        RefreshDevices();

        _deviceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _deviceTimer.Tick += (_, __) => RefreshDevices();
        _deviceTimer.Start();

        Closed += (_, __) => { _deviceTimer.Stop(); _operationCts?.Cancel(); };
    }

    private void BuildActions()
    {
        actionsPanel.Children.Clear();
        actionsPanel.Children.Add(MakeButton("Read Info", brom_readInfoBtn_Click));
        actionsPanel.Children.Add(MakeButton("Force BROM", brom_forceBtn_Click));
        actionsPanel.Children.Add(MakeButton("Fix Handshake", brom_fixHandshakeBtn_Click));
        actionsPanel.Children.Add(MakeButton("FRP + MDM", brom_frpMdmBtn_Click));
        actionsPanel.Children.Add(MakeButton("Exit BROM (No Format)", brom_exitBtn_Click));
        var stop = MakeButton("Stop All", btnStopAll_Click);
        actionsPanel.Children.Add(stop);
    }

    private Button MakeButton(string text, EventHandler<RoutedEventArgs> handler)
    {
        var btn = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 2, 0, 2) };
        btn.Click += handler;
        return btn;
    }

    private void OnRefreshDevices(object? sender, RoutedEventArgs e) => RefreshDevices();

    private void RefreshDevices()
    {
        try
        {
            var devices = DeviceDetector.GetDevices();
            string filter = (searchBox.Text ?? "").Trim();
            if (!string.IsNullOrEmpty(filter))
            {
                devices = devices.Where(d => d.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            var selected = devicesComboBox.SelectedItem;
            devicesComboBox.Items.Clear();
            if (devices.Count > 0)
            {
                foreach (var d in devices) devicesComboBox.Items.Add(d);
                devicesComboBox.SelectedIndex = 0;
            }
            else
            {
                devicesComboBox.Items.Add("No devices found");
                devicesComboBox.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            LogOutput("Device Detection Error: " + ex.Message, LogType.Error);
        }
    }

    private void ResetCts()
    {
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
    }

    private void BeginV15LogFlow(string flow) { }
    private void EndV15LogFlow() { }

    private void ClearLog()
    {
        logPanel.Children.Clear();
        _lastLogBlock = null;
    }

    private void LogOutput(string msg, LogType type = LogType.Info)
    {
        if (string.IsNullOrEmpty(msg)) return;
        if (type == LogType.Debug) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => LogOutput(msg, type));
            return;
        }

        bool newline = true;
        if (msg.StartsWith("[PARTIAL]"))
        {
            msg = msg.Substring(9);
            newline = false;
        }

        if (TryAppendV15StructuredLog(msg, newline))
        {
            return;
        }

        Color color = type switch
        {
            LogType.Error => Colors.Red,
            LogType.Success => Avalonia.Media.Color.FromRgb(74, 222, 128),
            LogType.Warning => Colors.Orange,
            _ => Colors.White,
        };

        if (!newline && _lastLogBlock != null)
        {
            _lastLogBlock.Text += msg;
            return;
        }

        var tb = new TextBlock
        {
            Text = msg + (newline ? Environment.NewLine : ""),
            Foreground = new SolidColorBrush(color),
            TextWrapping = TextWrapping.Wrap
        };
        logPanel.Children.Add(tb);
        _lastLogBlock = tb;
    }

    private bool TryAppendV15StructuredLog(string msg, bool newline, bool bold = false)
    {
        string trimmed = msg.Trim();
        if (trimmed.Length == 0) return false;

        bool separator = trimmed.Length >= 3 && trimmed.All(c => c is '-' or '=' or '*' or '_' or ' ' or '#');
        if (separator)
        {
            bool green = false;
            var tb = new TextBlock
            {
                Text = msg + (newline ? Environment.NewLine : ""),
                Foreground = new SolidColorBrush(green ? Avalonia.Media.Color.FromRgb(74, 222, 128) : Avalonia.Media.Color.FromRgb(176, 184, 197)),
                TextWrapping = TextWrapping.Wrap
            };
            logPanel.Children.Add(tb);
            _lastLogBlock = tb;
            return true;
        }

        if (trimmed.Contains("READING DEVICE INFO", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("FORCE BROM", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("CONVERT TO BROM", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("FIX HANDSHAKE", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("SPL REFRESH", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("REBOOT TO BROM", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("FRP + MDM", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("BROM ENTRY", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("BROM BOOT", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("BROM CONNECT", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("BROM:", StringComparison.OrdinalIgnoreCase))
        {
            var tb = new TextBlock
            {
                Text = msg + (newline ? Environment.NewLine : ""),
                Foreground = new SolidColorBrush(Colors.White),
                FontWeight = FontWeight.Bold,
                TextWrapping = TextWrapping.Wrap
            };
            logPanel.Children.Add(tb);
            _lastLogBlock = tb;
            return true;
        }

        return false;
    }

    private void AppendLogText(string text, Avalonia.Media.Color color, bool bold = false)
    {
        // compatibility shim
        LogOutput(text, LogType.Info);
    }

    private void SetControlsEnabled(bool enabled)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetControlsEnabled(enabled));
            return;
        }
        devicesComboBox.IsEnabled = enabled;
        foreach (var c in actionsPanel.Children)
            if (c is Control ctrl) ctrl.IsEnabled = enabled;
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var tcs = new TaskCompletionSource<bool>();
        var dlg = new Window { Title = title, Width = 460, Height = 200, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var sp = new StackPanel { Margin = new Thickness(12), Spacing = 12 };
        sp.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var yes = new Button { Content = "Yes" };
        var no = new Button { Content = "No" };
        yes.Click += (_, __) => { tcs.TrySetResult(true); dlg.Close(); };
        no.Click += (_, __) => { tcs.TrySetResult(false); dlg.Close(); };
        btns.Children.Add(no);
        btns.Children.Add(yes);
        sp.Children.Add(btns);
        dlg.Content = sp;
        dlg.Closing += (_, __) => tcs.TrySetResult(false);
        dlg.Show(this);
        return await tcs.Task;
    }

    private async Task<string?> PromptAsync(string title, string prompt, string initial, int maxLen, Func<string, string?> validator)
    {
        var tcs = new TaskCompletionSource<string?>();
        var dlg = new Window { Title = title, Width = 460, Height = 220, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var sp = new StackPanel { Margin = new Thickness(12), Spacing = 12 };
        sp.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        var tb = new TextBox { Text = initial, MaxLength = maxLen };
        sp.Children.Add(tb);
        var err = new TextBlock { Foreground = Brushes.Red, TextWrapping = TextWrapping.Wrap };
        sp.Children.Add(err);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var ok = new Button { Content = "OK" };
        var cancel = new Button { Content = "Cancel" };
        ok.Click += (_, __) =>
        {
            string v = (tb.Text ?? "").Trim().ToUpperInvariant();
            string? e = validator(v);
            if (e != null) { err.Text = e; return; }
            tcs.TrySetResult(v);
            dlg.Close();
        };
        cancel.Click += (_, __) => { tcs.TrySetResult(null); dlg.Close(); };
        btns.Children.Add(cancel);
        btns.Children.Add(ok);
        sp.Children.Add(btns);
        dlg.Content = sp;
        dlg.Closing += (_, __) => tcs.TrySetResult(null);
        dlg.Show(this);
        return await tcs.Task;
    }

    private void btnStopAll_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            LogOutput("Stopping all operations...", LogType.Warning);
            _operationCts?.Cancel();
            T509Native.T509Engine.CancelAllHandlers();
            LogOutput("Stop command sent.", LogType.Success);
        }
        catch (Exception ex)
        {
            LogOutput("Stop Error: " + ex.Message, LogType.Error);
        }
        finally
        {
            SetControlsEnabled(true);
        }
    }

    private void btnSnapLog_Click(object? sender, RoutedEventArgs e) => TakeScreenshot();

    private void TakeScreenshot()
    {
        try
        {
            LogOutput("Screenshot not available on this platform.", LogType.Warning);
        }
        catch (Exception ex)
        {
            LogOutput("Screenshot Error: " + ex.Message, LogType.Error);
        }
    }

    private async void brom_readInfoBtn_Click(object? sender, RoutedEventArgs e)
    {
        ClearLog();
        BeginV15LogFlow("brom-info");

        if (devicesComboBox.SelectedItem is DeviceInfo selected
            && (selected.DeviceType.Contains("MTP") || selected.DeviceType.Contains("ADB")))
        {
            SetControlsEnabled(false);
            try
            {
                await MtpMode.ReadInfoAsync(selected.DeviceId, LogOutput);
            }
            finally
            {
                EndV15LogFlow();
                SetControlsEnabled(true);
            }
            return;
        }

        string port = (devicesComboBox.SelectedItem is DeviceInfo deviceInfo) ? deviceInfo.Port : null;
        if (string.IsNullOrWhiteSpace(port) || port == "N/A")
        {
            port = SprdProtocol.FindBromPort() ?? SprdProtocol.FindDiagPort();
        }

        if (string.IsNullOrWhiteSpace(port))
        {
            LogOutput("No MTP/ADB device or BROM/Diag COM port detected. Connect the device and try again.", LogType.Error);
            EndV15LogFlow();
            return;
        }

        SetControlsEnabled(false);
        ResetCts();
        try
        {
            await SprdProtocol.ReadInfoAsync(LogOutput, port, _operationCts.Token);
        }
        catch (OperationCanceledException)
        {
            LogOutput(SpdMode.SpdField("Result", "Cancelled"), LogType.Warning);
        }
        finally
        {
            EndV15LogFlow();
            SetControlsEnabled(true);
        }
    }

    private async void brom_forceBtn_Click(object? sender, RoutedEventArgs e)
    {
        ClearLog();
        BeginV15LogFlow("brom-force");
        SetControlsEnabled(false);
        ResetCts();
        try
        {
            object selectedItem = devicesComboBox.SelectedItem;
            bool ok = await SprdProtocol.ForceBromAsync(
                LogOutput,
                (selectedItem is DeviceInfo deviceInfo) ? deviceInfo.Port : null,
                _operationCts.Token);

            if (ok)
            {
                LogOutput("Force BROM completed successfully.", LogType.Success);
            }
            else
            {
                LogOutput("Force BROM failed. Make sure the device is in BROM mode and retry.", LogType.Error);
            }
        }
        catch (OperationCanceledException)
        {
            LogOutput("Force BROM cancelled.", LogType.Warning);
        }
        catch (Exception ex)
        {
            OpLog.Exception("BROM", "ForceBrom", ex, "ui-force-brom");
            LogOutput("Force BROM error: " + ex.Message, LogType.Error);
        }
        finally
        {
            EndV15LogFlow();
            SetControlsEnabled(true);
        }
    }

    private async void brom_fixHandshakeBtn_Click(object? sender, RoutedEventArgs e)
    {
        bool confirm = await ConfirmAsync("Brom - Fix Handshake", "Fix Handshake uploads the boot SPL and recovery stub to RAM to repair\nthe BROM handshake. The device stays in BROM. Continue?");
        if (!confirm) return;

        await RunFixHandshakeAsync(rebootToBrom: true);
    }

    private async Task RunFixHandshakeAsync(bool rebootToBrom)
    {
        ClearLog();
        BeginV15LogFlow(rebootToBrom ? "brom-fix" : "brom-fix-android");
        SetControlsEnabled(false);
        ResetCts();
        try
        {
            string port = null;
            if (devicesComboBox.SelectedItem is DeviceInfo selected
                && !string.IsNullOrWhiteSpace(selected.Port) && selected.Port != "N/A"
                && selected.DeviceType != null && selected.DeviceType.Contains("BROM"))
            {
                port = selected.Port;
            }

            bool ok = await SprdProtocol.FixHandshakeAsync(LogOutput, rebootToBrom, port, _operationCts.Token);
            if (!ok)
            {
                LogOutput("Fix Handshake failed. Make sure the device is in BROM mode and retry.", LogType.Error);
            }
        }
        catch (OperationCanceledException)
        {
            LogOutput("Fix Handshake cancelled.", LogType.Warning);
        }
        catch (Exception ex)
        {
            OpLog.Exception("BROM", "Handshake", ex, "ui-fix-handshake");
            LogOutput("Fix Handshake error: " + ex.Message, LogType.Error);
        }
        finally
        {
            EndV15LogFlow();
            SetControlsEnabled(true);
        }
    }

    private async void brom_frpMdmBtn_Click(object? sender, RoutedEventArgs e)
    {
        ClearLog();
        BeginV15LogFlow("brom-frp-mdm");
        SetControlsEnabled(false);
        ResetCts();
        try
        {
            string port = null;
            if (devicesComboBox.SelectedItem is DeviceInfo selected
                && !string.IsNullOrWhiteSpace(selected.Port) && selected.Port != "N/A"
                && selected.DeviceType != null && selected.DeviceType.Contains("BROM"))
            {
                port = selected.Port;
            }

            if (string.IsNullOrWhiteSpace(port))
            {
                port = SprdProtocol.FindBromPort() ?? SprdProtocol.FindDiagPort();
                if (string.IsNullOrWhiteSpace(port))
                {
                    LogOutput("No BROM/Diag port detected.", LogType.Error);
                    return;
                }
            }

            LogOutput("Flashing FRP + MDM...", LogType.Info);
            bool ok = await SprdProtocol.FlashT509FrpMdmAsync(LogOutput, port, _operationCts.Token);
            if (ok)
            {
                LogOutput("FRP + MDM done.", LogType.Success);
            }
            else
            {
                LogOutput("FRP + MDM failed.", LogType.Error);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            OpLog.Exception("BROM", "FrpMdm", ex, "ui-frp-mdm");
            LogOutput(ex.Message, LogType.Error);
        }
        finally
        {
            EndV15LogFlow();
            SetControlsEnabled(true);
        }
    }

    private async void brom_exitBtn_Click(object? sender, RoutedEventArgs e)
    {
        ClearLog();
        BeginV15LogFlow("brom-exit");
        SetControlsEnabled(false);
        ResetCts();
        try
        {
            string port = null;
            if (devicesComboBox.SelectedItem is DeviceInfo selected
                && !string.IsNullOrWhiteSpace(selected.Port) && selected.Port != "N/A"
                && selected.DeviceType != null && selected.DeviceType.Contains("BROM"))
            {
                port = selected.Port;
            }

            if (string.IsNullOrWhiteSpace(port))
            {
                port = SprdProtocol.FindBromPort() ?? SprdProtocol.FindDiagPort();
                if (string.IsNullOrWhiteSpace(port))
                {
                    LogOutput("No BROM/Diag port detected.", LogType.Error);
                    return;
                }
            }

            bool ok = await SprdProtocol.FlashSplOnlyAsync(LogOutput, port, _operationCts.Token);
            if (ok)
            {
                LogOutput("splloader.bin flashed.", LogType.Success);
            }
            else
            {
                LogOutput("splloader flash failed.", LogType.Error);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            OpLog.Exception("BROM", "ExitBrom", ex, "ui-exit-brom");
        }
        finally
        {
            EndV15LogFlow();
            SetControlsEnabled(true);
        }
    }
}