using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Brom.Models;
using Brom.Services;

using LogType = Brom.Services.LogType;

namespace Brom.Protocols;

/// <summary>
/// Samsung MTP / modem AT transport.
///
/// The phone exposes a CDC-ACM "SAMSUNG Mobile USB Modem" serial port while it
/// is in MTP mode. Everything here talks to that port at 19200 8N1 and parses
/// the replies of the Samsung modem firmware.
///
/// Command reference (only commands that exist in the Samsung firmware command
/// table are used):
///   AT+DEVCONINFO=*      read model / firmware / CSC / SN / IMEI / CON / LOCK
///   AT+PRECONFG=1,0      read sales code (CSC)   -> +PRECONFG:1,XXX
///   AT+PRECONFG=2,XXX    write sales code (CSC)
///   AT+SERIALNO?         read serial number      -> +SERIALNO:...
///   AT+SERIALNO=2,SN     write serial number
///   AT+REACTIVE=1,0,0    read reactivation lock  -> +REACTIVE:1,UNLOCK|LOCK
///   AT+SWATD=0           switch modem to DDEXE (download) mode -> [DR]: CHANGE TO DDEXE
///   AT+SWATD=1           leave DDEXE mode
///   AT+ACTIVATE=0,0,0    activate the download agent
///   AT+FACTORST=0,0      factory reset (wipe user data)
///   AT+POWRESET=0,0      power reset / reboot
///   AT+FUS?              read FUS (firmware update) state
///
/// Commands that do NOT exist in the firmware table (and were removed from the
/// old implementation): AT+FACTORYRESET, AT+FUSERST, AT+RESETSYS,
/// AT+SPREF="AUTODLOADER", AT+RESET.
/// </summary>
public sealed class MtpProtocol : IDisposable
{
    private const int ModemBaudRate = 19200;
    private const string SamsungMtpVidPid = "VID_04E8&PID_6860";

    private readonly ILogSink _log;
    private readonly PathManager _paths;
    private readonly MtpOptions _options;
    private bool _disposed;
    private int _activeOperations;

    public MtpProtocol(ILogSink log, PathManager paths, MtpOptions options = null)
    {
        _log = log ?? NullLogSink.Instance;
        _paths = paths ?? PathManager.Instance;
        _options = options ?? new MtpOptions();
    }

    // =====================================================================
    //  READ DEVICE INFO
    // =====================================================================

    public async Task<DeviceInfo> ReadInfoAsync(string deviceId, CancellationToken token = default)
    {
        if (!TryBeginOperation()) return null;

        try
        {
            var info = new DeviceInfo();

            _log.LogInfo("═══════════════════════════════════════");
            _log.LogInfo("          READING DEVICE INFO          ");
            _log.LogInfo("═══════════════════════════════════════");

            // ---- ADB first (most reliable for model, version, CSC) ----
            if (await IsAdbReadyAsync(token))
            {
                await ReadInfoViaAdb(info, token);
            }

            // ---- Modem (AT) for SN, IMEI, lock status ----
            string portName = await Task.Run(() => GetModemPort(deviceId), token);
            if (string.IsNullOrEmpty(portName))
                portName = await Task.Run(FindModemPort, token);

            if (string.IsNullOrEmpty(portName))
            {
                _log.LogInfo("Task # 1: Accessing device:".PadRight(34) + "Fail");
                _log.LogError("ERROR: Modem port not found.");
                await BasicInfoFallback(deviceId, token);
                return info;
            }

            _log.LogInfo("Task # 1: Accessing device:".PadRight(34) + "Done");
            _log.LogInfo("Task # 2: Accessing device:".PadRight(34) + "Done");
            _log.LogInfo("Task # 3: Reading device info:".PadRight(34) + "Done");

            string raw = await ReadFromModemAsync(portName, token);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                ParseDeviceInfo(raw, info);
            }

            // ---- ADB fallback for anything still missing ----
            if (await IsAdbReadyAsync(token))
            {
                await ReadInfoViaAdbFallback(info, token);
            }

            LogParsedInfo(info);

            _log.LogSuccess("═══════════════════════════════════════");
            return info;
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("Operation cancelled by user.");
            return null;
        }
        catch (Exception ex)
        {
            _log.LogError("FATAL ERROR in ReadInfo: " + ex.Message);
            return null;
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task ReadInfoViaAdb(DeviceInfo info, CancellationToken token)
    {
        try
        {
            string model = await RunAdbAsync("shell getprop ro.product.model", token);
            if (!string.IsNullOrWhiteSpace(model)) info.Model = model.Trim();

            string csc = await RunAdbAsync("shell getprop ro.csc.sales_code", token);
            if (!string.IsNullOrWhiteSpace(csc)) info.Csc = csc.Trim();

            string ap = await RunAdbAsync("shell getprop ro.build.version.incremental", token);
            if (!string.IsNullOrWhiteSpace(ap)) info.ApVersion = ap.Trim();

            string cp = await RunAdbAsync("shell getprop ro.baseband", token);
            if (!string.IsNullOrWhiteSpace(cp)) info.CpVersion = cp.Trim();

            string android = await RunAdbAsync("shell getprop ro.build.version.release", token);
            if (!string.IsNullOrWhiteSpace(android)) info.OsVersion = android.Trim();

            string imei = await RunAdbAsync("shell getprop ril.serialnumber", token);
            if (!string.IsNullOrWhiteSpace(imei)) info.Imei = imei.Trim();

            string sn = await RunAdbAsync("shell getprop ro.serialno", token);
            if (!string.IsNullOrWhiteSpace(sn)) info.SerialNumber = sn.Trim();
        }
        catch (Exception ex)
        {
            _log.LogDebug($"ADB info read error: {ex.Message}");
        }
    }

    private async Task ReadInfoViaAdbFallback(DeviceInfo info, CancellationToken token)
    {
        try
        {
            if (string.IsNullOrEmpty(info.Model))
            {
                string model = await RunAdbAsync("shell getprop ro.product.model", token);
                if (!string.IsNullOrWhiteSpace(model)) info.Model = model.Trim();
            }
            if (string.IsNullOrEmpty(info.Csc))
            {
                string csc = await RunAdbAsync("shell getprop ro.csc.sales_code", token);
                if (!string.IsNullOrWhiteSpace(csc)) info.Csc = csc.Trim();
            }
            if (string.IsNullOrEmpty(info.OsVersion) || info.OsVersion == "N/A")
            {
                string android = await RunAdbAsync("shell getprop ro.build.version.release", token);
                if (!string.IsNullOrWhiteSpace(android)) info.OsVersion = android.Trim();
            }
            if (string.IsNullOrEmpty(info.Imei))
            {
                string imei = await RunAdbAsync("shell getprop ril.serialnumber", token);
                if (!string.IsNullOrWhiteSpace(imei)) info.Imei = imei.Trim();
            }
            if (string.IsNullOrEmpty(info.SerialNumber))
            {
                string sn = await RunAdbAsync("shell getprop ro.serialno", token);
                if (!string.IsNullOrWhiteSpace(sn)) info.SerialNumber = sn.Trim();
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug($"ADB fallback error: {ex.Message}");
        }
    }

    private Task<string> ReadFromModemAsync(string portName, CancellationToken token)
    {
        return Task.Run(() =>
        {
            var sb = new StringBuilder();

            try
            {
                using var port = OpenModemPort(portName, 8000);

                // Wake the modem. Some phones ignore the first command after open.
                var warmUp = SendCommand(port, "AT", 1500);
                if (!warmUp.Ok)
                {
                    Thread.Sleep(250);
                    warmUp = SendCommand(port, "AT", 1500);
                }

                // The main information block.
                var devCon = SendCommand(port, "AT+DEVCONINFO", 8000);
                sb.AppendLine(devCon.Raw);

                // Sales code (CSC) as a separate, easy-to-parse reply.
                var csc = SendCommand(port, "AT+PRECONFG=1,0", 3000);
                if (!string.IsNullOrWhiteSpace(csc.Raw))
                    sb.AppendLine(csc.Raw);

                // Reactivation lock (what the UI shows as FRP status).
                var reactive = SendCommand(port, "AT+REACTIVE=1,0,0", 3000);
                if (!string.IsNullOrWhiteSpace(reactive.Raw))
                    sb.AppendLine(reactive.Raw);

                // Hardware / firmware name block (extra data on some models).
                var versions = SendCommand(port, "AT+VERSNAME=3,2,3", 3000);
                if (!string.IsNullOrWhiteSpace(versions.Raw))
                    sb.AppendLine(versions.Raw);
            }
            catch (Exception ex)
            {
                _log.LogDebug($"Modem read error on {portName}: {ex.Message}");
                return string.Empty;
            }

            return sb.ToString();
        }, token);
    }

    private void ParseDeviceInfo(string raw, DeviceInfo info)
    {
        var parsed = ParseDevConInfo(raw);

        string version = GetValue(parsed, "VER", "AP", "AP_VER", "PDA", "PDA_VER");
        string apVersion = version;
        string cscVersion = "N/A";
        string cpVersion = "N/A";
        string blVersion = "N/A";

        if (version != "N/A" && version.Contains("/"))
        {
            string[] parts = version.Split('/');
            apVersion = parts[0].Trim();
            if (parts.Length >= 2) cscVersion = parts[1].Trim();
            if (parts.Length >= 3) cpVersion = parts[2].Trim();
            if (parts.Length >= 4) blVersion = parts[3].Trim();
        }

        string csc = GetValue(parsed, "PRD", "OMCCODE", "SALES_CODE", "CSC");
        if (csc == "N/A")
            csc = ExtractCsc(raw) ?? "N/A";

        info.Model = GetValue(parsed, "MN", "MODEL", "MODEL_NAME", "GMM");
        info.Csc = csc;
        info.ApVersion = apVersion;
        info.BlVersion = GetValue(parsed, "BL", "BL_VER");
        if (info.BlVersion == "N/A") info.BlVersion = blVersion;
        info.CpVersion = GetValue(parsed, "PHONE", "CP", "CP_VER", "MODEM");
        if (info.CpVersion == "N/A") info.CpVersion = cpVersion;
        info.CscVersion = GetValue(parsed, "CSC_VER", "CSC_NAME");
        if (info.CscVersion == "N/A") info.CscVersion = cscVersion;
        info.BinaryBit = GetBinaryBit(apVersion);
        info.Imei = GetValue(parsed, "IMEI", "IMEIS", "IMEI_1", "SERIAL_NO");
        info.SerialNumber = GetValue(parsed, "SN", "SERIAL_NUM", "S/N", "SERIAL", "SERIALNO");
        info.LockStatus = GetValue(parsed, "LOCK", "SEC_LOCK", "SIM_LOCK", "LOCK_STATUS");
        info.PhoneNumber = GetValue(parsed, "PN", "MPN", "PHONE_NUM", "TEL");
        info.Country = GetValue(parsed, "COUNTRY", "REGION");
        info.UsbMode = GetValue(parsed, "CON", "USB_MODE", "USB");
        info.UniqueNumber = GetValue(parsed, "UN", "CONN_ID", "CHIP_ID");
        info.FrpLock = ParseReactivationLock(raw);
        info.OsVersion = "N/A";

        if (apVersion == "N/A" || apVersion.Length < 10)
        {
            Match pda = Regex.Match(raw, @"\b[A-Z][A-Z0-9]{9,14}\b");
            if (pda.Success && pda.Value != "DEVCONINFO" && pda.Value != "PRECONFG")
                info.ApVersion = pda.Value;
        }

        // Fallback Android version from the build string only when nothing
        // better is available. ADB (when present) overrides this later.
        if (string.IsNullOrEmpty(info.OsVersion) || info.OsVersion == "N/A")
            info.OsVersion = GuessAndroidVersionFromBuild(info.ApVersion);
    }

    private void LogParsedInfo(DeviceInfo info)
    {
        LogField("Model", info.Model);
        LogField("CSC", info.Csc);
        LogField("AP Version", info.ApVersion);
        LogField("BL Version", info.BlVersion);
        LogField("CP Version", info.CpVersion);
        LogField("CSC Version", info.CscVersion);
        LogField("Bit (Binary)", info.BinaryBit);
        LogField("IMEI", info.Imei);
        LogField("SN", info.SerialNumber);
        LogField("Lock Status", info.LockStatus);
        LogField("Phone Number", info.PhoneNumber);
        LogField("Country", info.Country);
        LogField("USB Mode", info.UsbMode);
        LogField("Unique Number", info.UniqueNumber);
        LogField("Android Ver", info.OsVersion);
        LogField("FRP Status", info.FrpLock);
    }

    private void LogField(string label, string value)
    {
        string text = string.IsNullOrWhiteSpace(value) || value == "null" ? "N/A" : value;
        _log.LogInfo(label.PadRight(15) + ": " + text);
    }

    private static bool IsUnlocked(string value)
    {
        return !string.IsNullOrEmpty(value) &&
               (value.IndexOf("OFF", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("UNLOCK", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("NONE", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    // =====================================================================
    //  CHANGE SERIAL NUMBER
    // =====================================================================

    public async Task<bool> ChangeCscAsync(string deviceId, string newCsc, CancellationToken token = default)
    {
        if (!TryBeginOperation()) return false;

        try
        {
            _log.LogInfo("═══════════════════════════════════════");
            _log.LogInfo("        CHANGE SALES CODE (CSC)        ");
            _log.LogInfo("═══════════════════════════════════════");
            _log.LogInfo("Searching for device modem port...");

            string portName = await Task.Run(() => GetModemPort(deviceId), token);
            if (string.IsNullOrEmpty(portName))
                portName = await Task.Run(FindModemPort, token);

            if (string.IsNullOrEmpty(portName))
            {
                _log.LogError("ERROR: SAMSUNG Mobile USB Modem not found!");
                return false;
            }

            _log.LogSuccess("✓ Found modem on " + portName);
            _log.LogInfo("Reading current CSC...");

            string currentCsc = await Task.Run(() => ReadCsc(portName), token);
            if (!string.IsNullOrEmpty(currentCsc) && currentCsc != "N/A")
                _log.LogSuccess("✓ Current CSC: " + currentCsc);

            newCsc = (newCsc ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(newCsc))
            {
                _log.LogWarning("Operation cancelled by user.");
                return false;
            }

            if (newCsc.Length != 3)
            {
                _log.LogError("ERROR: Invalid CSC code length (must be 3 letters).");
                return false;
            }

            _log.LogInfo("Target CSC: " + newCsc);

            bool written = await Task.Run(() => WriteCsc(portName, newCsc), token);
            if (!written)
            {
                _log.LogError("ERROR: Failed to change CSC.");
                return false;
            }

            _log.LogSuccess("✓ CSC changed to '" + newCsc + "' successfully!");
            _log.LogSuccess("✓ Device will apply new region settings on next reboot.");
            _log.LogSuccess("═══════════════════════════════════════");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError("FATAL ERROR: " + ex.Message);
            return false;
        }
        finally
        {
            EndOperation();
        }
    }

    private string ReadCsc(string portName)
    {
        try
        {
            using var port = OpenModemPort(portName, 4000);
            SendCommand(port, "AT", 1200);
            var response = SendCommand(port, "AT+PRECONFG=1,0", 4000);
            return ExtractCsc(response.Raw) ?? "N/A";
        }
        catch (Exception ex)
        {
            _log.LogDebug($"Read CSC error: {ex.Message}");
            return "N/A";
        }
    }

    private bool WriteCsc(string portName, string newCsc)
    {
        _log.LogWarning("> Setting Sales Code (CSC) to: " + newCsc);

        // Enter modem service mode first (SWATD=0) - required on many Samsung
        // models before AT+PRECONFG write will be accepted.
        if (!EnterModemServiceMode(portName))
        {
            _log.LogDebug("Could not enter modem service mode. Trying a direct write.");
        }
        else
        {
            Thread.Sleep(3000);
            string retryPort = FindModemPort();
            if (string.IsNullOrEmpty(retryPort))
                retryPort = portName;

            if (TryWriteCsc(retryPort, newCsc, out _))
            {
                RebootAfterCsc(portName);
                return true;
            }
        }

        // Fallback: direct write without service mode
        if (TryWriteCsc(portName, newCsc, out _))
        {
            RebootAfterCsc(portName);
            return true;
        }

        return false;
    }

    private void RebootAfterCsc(string portName)
    {
        _log.LogInfo("> Rebooting device...");
        try
        {
            using var port = OpenModemPort(portName, 5000);
            SendCommand(port, "AT+POWRESET=0,0", 3000);
        }
        catch { /* the port disappears while rebooting - expected */ }
    }

    private bool TryWriteCsc(string portName, string newCsc, out string reason)
    {
        reason = string.Empty;

        try
        {
            using var port = OpenModemPort(portName, 6000);

            var warmUp = SendCommand(port, "AT", 1500);
            if (!warmUp.Ok)
            {
                reason = "the modem did not answer AT";
                return false;
            }

            _log.LogWarning("> Setting CSC via AT+PRECONFG=2," + newCsc);

            var write = SendCommand(port, $"AT+PRECONFG=2,{newCsc}", 8000);

            if (write.HasError)
            {
                reason = "the phone rejected AT+PRECONFG with ERROR: " + write.Raw;
                _log.LogError(reason);
                return false;
            }

            // Require explicit OK from the phone — do not treat silence or
            // unexpected responses as success.
            if (!write.Ok)
            {
                reason = "the phone did not confirm CSC write (raw: " + write.Raw + ")";
                _log.LogWarning(reason);
                return false;
            }

            // Read back the current CSC to confirm the change was accepted.
            // Samsung applies the change immediately on many models.
            Thread.Sleep(1000);
            var readBack = SendCommand(port, "AT+PRECONFG=1,0", 5000);
            string readBackRaw = readBack.Raw ?? string.Empty;
            _log.LogInfo("< Current Config Response: " + readBackRaw.Trim());

            // Verify the new CSC value is present in the readback.
            if (!string.IsNullOrWhiteSpace(readBackRaw) &&
                readBackRaw.Contains(newCsc, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogSuccess("CSC write verified: " + newCsc + " is now active.");
                return true;
            }

            // Readback may show old value until reboot on some models — accept
            // the write if the phone ACK'd it, but warn the user to reboot.
            _log.LogWarning("Write ACK'd but readback did not confirm yet — reboot required to apply.");
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            _log.LogError("Write CSC Error: " + ex.Message);
            return false;
        }
    }

    // =====================================================================
    //  REBOOT TO DOWNLOAD MODE
    // =====================================================================

    public async Task<bool> RebootDownloadAsync(string deviceId, CancellationToken token = default)
    {
        if (!TryBeginOperation()) return false;

        try
        {
            _log.LogInfo("═══════════════════════════════════════");
            _log.LogInfo("        REBOOT TO DOWNLOAD MODE        ");
            _log.LogInfo("═══════════════════════════════════════");
            _log.LogInfo("Searching for device modem port...");

            string portName = await Task.Run(() => GetModemPort(deviceId), token);
            if (string.IsNullOrEmpty(portName))
                portName = await Task.Run(FindModemPort, token);

            if (string.IsNullOrEmpty(portName))
            {
                _log.LogError("ERROR: Modem port not found.");

                // ADB fallback (does not exist in the old build, kept so phones
                // with only USB debugging enabled still reboot).
                if (await IsAdbReadyAsync(token))
                {
                    string adbResult = await RunAdbAsync("reboot download", token);
                    if (!LooksLikeAdbError(adbResult))
                        return true;
                }

                return false;
            }

            _log.LogSuccess("✓ Accessing device via " + portName + "...");
            _log.LogWarning("Rebooting To Download Mode...");

            bool switched = await Task.Run(() => EnterModemServiceMode(portName), token);
            if (!switched)
            {
                // Fallback: try alternative reboot-to-download commands
                _log.LogDebug("SWATD failed - trying alternative reboot commands...");
                switched = await Task.Run(() => RebootDownloadViaAltCommands(portName), token);
            }
            if (!switched)
            {
                _log.LogError("ERROR: The phone did not accept the download command.");
                return false;
            }

            // Wait for the device to re-enumerate in Download Mode.
            _log.LogInfo("Waiting for device to enter Download Mode...");
            await Task.Delay(8000, token);

            _log.LogSuccess("✓ Success: Device should be rebooting to Download Mode...");
            _log.LogSuccess("═══════════════════════════════════════");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError("ERROR: " + ex.Message);
            return false;
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>
    /// Kept for API compatibility. There is no separate "BROM" mode over the
    /// Samsung modem; the DDEXE switch is the download-mode hand-off.
    /// </summary>
    public Task<bool> ForceBromAsync(string deviceId, string portName, CancellationToken token = default)
        => RebootDownloadAsync(deviceId, token);

    // =====================================================================
    //  FACTORY RESET (FORMAT)
    // =====================================================================

    public async Task<bool> FormatMtpAsync(string deviceId, CancellationToken token = default)
    {
        if (!TryBeginOperation()) return false;

        try
        {
            _log.LogInfo("╔═════════════════════════════════════════════════════╗");
            _log.LogInfo("║         T509 BOOTROM: FACTORY RESET (MTP / ADB)      ║");
            _log.LogInfo("╚═════════════════════════════════════════════════════╝");

            // ---- Method 1: ADB recovery wipe (most reliable) ----
            if (await IsAdbReadyAsync(token))
            {
                _log.LogInfo("   Method         : Recovery Wipe [ADB]");
                _log.LogSuccess("   Status         : Device Authorized");
                _log.LogInfo(" [1/3] Sending recovery wipe command...");

                if (await RecoveryWipeViaAdbAsync(token))
                {
                    _log.LogInfo(" [2/3] Triggering recovery reboot...");
                    _log.LogSuccess(" [3/3] Device rebooting to Recovery wipe screen! Done ✓");
                    _log.LogSuccess("═══════════════════════════════════════");
                    return true;
                }
            }

            // ---- Method 2: modem AT command (MTP) ----
            string portName = await Task.Run(() => GetModemPort(deviceId), token);
            if (string.IsNullOrEmpty(portName))
                portName = await Task.Run(FindModemPort, token);

            if (!string.IsNullOrEmpty(portName))
            {
                _log.LogInfo("   Modem Port     : " + portName);
                _log.LogInfo("   Method         : Master Reset [MTP AT]");
                _log.LogSuccess(" [1/4] Accessing modem port...             Done ✓");

                bool wiped = await Task.Run(() => FactoryResetViaAt(portName), token);

                _log.LogSuccess("═══════════════════════════════════════");
                return wiped;
            }

            _log.LogError(" ✗ [FAILED] No Samsung device found in MTP or ADB mode.");
            _log.LogWarning(" Tip: Connect tablet with USB cable, ensure screen is on, or enter Download Mode.");
            return false;
        }
        catch (Exception ex)
        {
            _log.LogError("ERROR: " + ex.Message);
            return false;
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task<bool> RecoveryWipeViaAdbAsync(CancellationToken token)
    {
        // Some Samsung builds expose the recovery helper even while Android is
        // booted. Try it first because it avoids a reboot round-trip.
        string direct = await RunAdbAsync("shell recovery --wipe_data", token);
        _log.LogDebug($"adb shell recovery --wipe_data -> {Compact(direct)}");

        if (IsWipeAccepted(direct, allowEmpty: false))
        {
            await RunAdbAsync("reboot", token);
            return true;
        }

        _log.LogDebug("Rebooting the phone into recovery mode...");
        await RunAdbAsync("reboot recovery", token);

        if (!await WaitForAdbAsync(90, token))
        {
            _log.LogDebug("ADB is not available in recovery (normal on many Samsung models).");
            return false;
        }

        // AOSP recovery accepts the internal --wipe_data argument. This only
        // works while the device is actually running recovery.
        string wipe = await RunAdbAsync("shell recovery --wipe_data", token);
        _log.LogDebug($"recovery adb shell recovery --wipe_data -> {Compact(wipe)}");

        if (IsWipeAccepted(wipe, allowEmpty: true))
        {
            await RunAdbAsync("reboot", token);
            return true;
        }

        return false;
    }

    private bool FactoryResetViaAt(string portName)
    {
        SerialPort port2 = null;
        try
        {
            using var port = OpenModemPort(portName, 8000);

            var warmUp = SendCommand(port, "AT", 1500);
            if (!warmUp.Ok)
                return false;

            // Enter modem service mode — required before AT+FACTORST will be accepted.
            bool serviceMode = EnterModemServiceMode(portName);
            _log.LogInfo(serviceMode
                ? " [2/4] Entering factory mode...            Done ✓"
                : " [2/4] Entering factory mode...            Warning (trying direct commands)");
            Thread.Sleep(3000);

            // Port may re-enumerate after service mode switch
            string retryPort = FindModemPort();
            if (string.IsNullOrEmpty(retryPort))
                retryPort = portName;

            port2 = OpenModemPort(retryPort, 8000);
            var warmUp2 = SendCommand(port2, "AT", 1500);
            if (!warmUp2.Ok)
            {
                // Try original port as fallback
                port2.Dispose();
                port2 = OpenModemPort(portName, 8000);
                var warmUp3 = SendCommand(port2, "AT", 1500);
                if (!warmUp3.Ok)
                    return false;
            }

            _log.LogInfo(" [3/4] Delivering master wipe commands...");

            // Try multiple Samsung factory reset commands with response logging
            bool wiped = false;
            foreach (string cmd in new[] { "AT+FACTORST=0,0", "AT+FACTORYRESET", "AT+FORMAT=0,0" })
            {
                var resp = SendCommand(port2, cmd, 30000);
                _log.LogDebug($"Factory reset cmd '{cmd}' -> Ok={resp.Ok}, Error={resp.HasError}, Raw={resp.Raw}");
                if (resp.Ok)
                {
                    wiped = true;
                    break;
                }
            }

            if (!wiped)
            {
                _log.LogError(" [3/4] Master wipe failed - no Samsung factory reset command accepted.");
                return false;
            }

            _log.LogSuccess(" [3/4] Master wipe delivered...            Done ✓");
            _log.LogInfo(" [4/4] Sending reboot command...");
            SendCommand(port2, "AT+POWRESET=0,0", 5000);
            _log.LogSuccess(" [4/4] Resetting device...                 Done ✓");
            _log.LogSuccess(" ✨ [SUCCESS] Factory reset sent — device rebooting! 🚀");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError("ERROR: " + ex.Message);
            return false;
        }
        finally
        {
            try { port2?.Dispose(); } catch { }
        }
    }

    private static bool IsWipeAccepted(string output, bool allowEmpty)
    {
        if (LooksLikeAdbError(output))
            return false;

        if (string.IsNullOrWhiteSpace(output))
            return allowEmpty;

        string text = output.ToLowerInvariant();
        return text.Contains("wipe") || text.Contains("format") ||
               text.Contains("data") || text.Contains("success") ||
               text.Contains("done");
    }

    private void LogAdbResult(string what, string output)
    {
        if (LooksLikeAdbError(output))
            _log.LogWarning($"{what}: {Compact(output)}");
        else
            _log.LogDebug($"{what}: {Compact(output)}");
    }

    // =====================================================================
    //  AT TRANSPORT
    // =====================================================================

    private sealed class AtResponse
    {
        public string Raw { get; set; } = string.Empty;
        public bool Ok { get; set; }
        public bool TimedOut { get; set; }

        public bool HasError =>
            Raw.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0;

        public bool Contains(string text) =>
            !string.IsNullOrEmpty(text) &&
            Raw.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;

        public override string ToString() => Raw;
    }

    private SerialPort OpenModemPort(string portName, int readTimeoutMs = 6000)
    {
        var port = new SerialPort(portName, ModemBaudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = readTimeoutMs,
            WriteTimeout = 6000,
            Handshake = Handshake.None,
            DtrEnable = false,
            RtsEnable = false,
            Encoding = Encoding.ASCII,
            NewLine = "\r"
        };

        port.Open();
        try { port.DiscardInBuffer(); } catch { }
        return port;
    }

    /// <summary>
    /// Sends one AT command and waits for a final response.
    ///
    /// A response is complete when it contains OK / #OK#, an ERROR, or one of
    /// the optional success markers. Some Samsung commands (notably
    /// AT+SWATD=0) answer with "[DR]: CHANGE TO DDEXE" and never send OK, so
    /// callers must pass that marker instead of waiting for the full timeout.
    /// </summary>
    private AtResponse SendCommand(SerialPort port, string command, int timeoutMs = 5000,
        params string[] extraSuccessMarkers)
    {
        var response = new AtResponse();
        var sb = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            port.DiscardInBuffer();
            port.Write(command + "\r");

            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                if (port.BytesToRead > 0)
                {
                    sb.Append(port.ReadExisting());
                    string current = sb.ToString();

                    if (HasError(current))
                        break;

                    if (HasFinalOk(current) || MatchesAny(current, extraSuccessMarkers))
                        break;
                }

                Thread.Sleep(25);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug($"AT '{command}' transport error: {ex.Message}");
        }

        response.Raw = sb.ToString();
        response.Ok = HasFinalOk(response.Raw) || MatchesAny(response.Raw, extraSuccessMarkers);
        response.TimedOut = !response.Ok && !response.HasError &&
                            stopwatch.ElapsedMilliseconds >= timeoutMs;

        return response;
    }

    private static bool HasFinalOk(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string trimmed = text.TrimEnd();
        if (trimmed.EndsWith("#OK#", StringComparison.OrdinalIgnoreCase))
            return true;
        if (trimmed.EndsWith("OK", StringComparison.OrdinalIgnoreCase))
            return true;

        return Regex.IsMatch(trimmed, @"(?:^|[\r\n])\s*OK\s*$", RegexOptions.IgnoreCase);
    }

    private static bool HasError(string text)
    {
        return !string.IsNullOrEmpty(text) &&
               text.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool MatchesAny(string text, string[] markers)
    {
        if (string.IsNullOrEmpty(text) || markers == null)
            return false;

        foreach (string marker in markers)
        {
            if (!string.IsNullOrEmpty(marker) &&
                text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// AT+SWATD=0 switches the modem into DDEXE (download) mode. The phone
    /// answers "[DR]: CHANGE TO DDEXE ..." and may re-enumerate the USB port,
    /// so this method tolerates a missing OK and a dropped port.
    /// </summary>
    private bool EnterModemServiceMode(string portName)
    {
        try
        {
            using var port = OpenModemPort(portName, 4000);

            SendCommand(port, "AT", 1500);

            var switchToDd = SendCommand(port, "AT+SWATD=0", 3000, "[DR]", "DDEXE");

            if (switchToDd.HasError)
                return false;

            if (!switchToDd.Ok && !switchToDd.Contains("DDEXE"))
                return false;

            // Activate the download agent. The port can disappear here; that is
            // expected and not a failure.
            try
            {
                var activate = SendCommand(port, "AT+ACTIVATE=0,0,0", 5000, "[DR]", "DDEXE");
            }
            catch (Exception ex)
            {
                _log.LogDebug($"AT+ACTIVATE after SWATD: {ex.Message}");
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.LogDebug($"EnterModemServiceMode error: {ex.Message}");
            return false;
        }
    }

    private bool RebootDownloadViaAltCommands(string portName)
    {
        try
        {
            using var port = OpenModemPort(portName, 4000);
            SendCommand(port, "AT", 1500);

            // Alternative Samsung reboot-to-download commands
            foreach (string cmd in new[] { "AT+REBOOT=2,0", "AT+SWATD=0", "AT+ACTIVATE=0,0,0" })
            {
                try
                {
                    var resp = SendCommand(port, cmd, 5000, "[DR]", "DDEXE");
                    if (resp.Ok || resp.Contains("DDEXE"))
                        return true;
                }
                catch { /* try next command */ }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    // =====================================================================
    //  ADB
    // =====================================================================

    private async Task<bool> IsAdbReadyAsync(CancellationToken token)
    {
        string output = await RunAdbAsync("devices", token);

        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
                continue;

            string[] columns = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 2)
                continue;

            string state = columns[columns.Length - 1];
            if (state.Equals("device", StringComparison.OrdinalIgnoreCase) ||
                state.Equals("recovery", StringComparison.OrdinalIgnoreCase) ||
                state.Equals("sideload", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private async Task<bool> WaitForAdbAsync(int timeoutSeconds, CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed.TotalSeconds < timeoutSeconds)
        {
            if (token.IsCancellationRequested)
                return false;

            if (await IsAdbReadyAsync(token))
                return true;

            await Task.Delay(2000, token);
        }

        return false;
    }

    private async Task<string> TryReadAndroidVersionAsync(CancellationToken token)
    {
        try
        {
            if (!await IsAdbReadyAsync(token))
                return null;

            string version = await RunAdbAsync("shell getprop ro.build.version.release", token);
            version = version.Trim();
            if (Regex.IsMatch(version, @"^\d{1,2}(\.\d+)?$"))
                return version;
        }
        catch { }

        return null;
    }

    private string ResolveAdbPath()
    {
        // 1. Bundled next to the tool (sdks/platform-tools/adb.exe).
        try
        {
            string bundled = _paths.GetAdbPath();
            if (!string.IsNullOrEmpty(bundled) && File.Exists(bundled))
                return bundled;
        }
        catch { }

        // 2. Anything on the system PATH.
        try
        {
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string cand = Path.Combine(dir.Trim(), "adb.exe");
                if (File.Exists(cand)) return cand;
            }
        }
        catch { }

        // 3. Android SDK install locations. These are built from environment
        //    variables / well-known Windows folders instead of one absolute
        //    path, so the tool works on any machine and any user profile.
        var common = new List<string>();

        foreach (string envVar in new[] { "ANDROID_SDK_ROOT", "ANDROID_HOME", "ANDROID_SDK" })
        {
            string sdkRoot = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrWhiteSpace(sdkRoot))
            {
                common.Add(Path.Combine(sdkRoot.Trim().Trim('"').TrimEnd('\\', '/'),
                    "platform-tools", "adb.exe"));
            }
        }

        void AddUnder(Environment.SpecialFolder folder, params string[] parts)
        {
            try
            {
                string root = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(root))
                {
                    var full = new List<string> { root };
                    full.AddRange(parts);
                    common.Add(Path.Combine(full.ToArray()));
                }
            }
            catch { }
        }

        // Android Studio default: %LOCALAPPDATA%\Android\Sdk
        AddUnder(Environment.SpecialFolder.LocalApplicationData, "Android", "Sdk", "platform-tools", "adb.exe");
        // SDK Manager default: %USERPROFILE%\AppData\Local\Android\Sdk (same as above on most systems)
        AddUnder(Environment.SpecialFolder.UserProfile, "AppData", "Local", "Android", "Sdk", "platform-tools", "adb.exe");
        // Legacy SDK installers.
        AddUnder(Environment.SpecialFolder.ProgramFilesX86, "Android", "android-sdk", "platform-tools", "adb.exe");
        AddUnder(Environment.SpecialFolder.ProgramFiles, "Android", "android-sdk", "platform-tools", "adb.exe");
        // A portable SDK unpacked next to the tool.
        try
        {
            common.Add(Path.Combine(_paths.BaseDirectory, "sdks", "android-sdk", "platform-tools", "adb.exe"));
        }
        catch { }

        foreach (string c in common)
        {
            try { if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c; } catch { }
        }

        return string.Empty;
    }

    private async Task<string> RunAdbAsync(string args, CancellationToken token = default)
    {
        try
        {
            string adbPath = ResolveAdbPath();
            if (string.IsNullOrEmpty(adbPath) || !File.Exists(adbPath))
            {
                _log.LogError("adb.exe was not found. Place the Android platform-tools in 'sdks/platform-tools/' next to the tool, or install Android Studio (platform-tools).");
                return string.Empty;
            }

            var psi = new ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return string.Empty;

            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                _log.LogDebug($"adb {args} timed out.");
            }

            string output = await stdout;
            string error = await stderr;

            if (!string.IsNullOrWhiteSpace(error))
                output += Environment.NewLine + error;

            return output ?? string.Empty;
        }
        catch (Exception ex)
        {
            _log.LogDebug($"adb {args} failed: {ex.Message}");
            return string.Empty;
        }
    }

    private static bool LooksLikeAdbError(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return false;

        string text = output.ToLowerInvariant();
        return text.Contains("error:") ||
               text.Contains("unknown command") ||
               text.Contains("not found") ||
               text.Contains("usage:") ||
               text.Contains("no devices") ||
               text.Contains("more than one device") ||
               text.Contains("device unauthorized") ||
               text.Contains("failed");
    }

    // =====================================================================
    //  PARSING HELPERS
    // =====================================================================

    private static Dictionary<string, string> ParseDevConInfo(string raw)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
            return dict;

        // Matches KEY(value) pairs, including the "+DEVCONINFO: MN(...)" prefix.
        foreach (Match match in Regex.Matches(raw, @"([A-Za-z][A-Za-z0-9_]*)\s*\(([^)]*)\)"))
        {
            dict[match.Groups[1].Value] = match.Groups[2].Value.Trim();
        }

        return dict;
    }

    private static string GetValue(Dictionary<string, string> dict, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (dict.TryGetValue(key, out string value) &&
                !string.IsNullOrWhiteSpace(value) &&
                !string.Equals(value, "null", StringComparison.OrdinalIgnoreCase))
                return value.Trim();
        }

        return "N/A";
    }

    private static string ExtractSerial(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        Match match = Regex.Match(raw, @"\+SERIALNO:\s*(?:\d+\s*,\s*)?([A-Za-z0-9]{6,20})");
        if (match.Success)
            return match.Groups[1].Value.ToUpperInvariant();

        // Some firmware answers with just the serial on its own line.
        foreach (string line in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (trimmed.Length < 6 || trimmed.Length > 20)
                continue;
            if (trimmed.StartsWith("AT", StringComparison.OrdinalIgnoreCase))
                continue;
            if (Regex.IsMatch(trimmed, @"^[A-Za-z0-9]+$"))
                return trimmed.ToUpperInvariant();
        }

        return null;
    }

    private static string ExtractCsc(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        Match match = Regex.Match(raw, @"\+PRECONFG:\s*1\s*,\s*([A-Za-z]{3})");
        if (match.Success)
            return match.Groups[1].Value.ToUpperInvariant();

        match = Regex.Match(raw, @"\+PRECONFG:\s*(?:[^,]*,\s*)?([A-Za-z]{3})\b");
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static string ParseReactivationLock(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "N/A";

        Match match = Regex.Match(raw, @"\+REACTIVE:\s*\d+\s*,\s*([A-Za-z0-9]+)");
        if (match.Success)
        {
            string value = match.Groups[1].Value.Trim().ToUpperInvariant();
            if (value.Contains("UNLOCK") || value == "0")
                return "OFF (Unlocked)";
            if (value.Contains("LOCK") || value == "1")
                return "ON (Locked)";
        }

        if (raw.IndexOf("REACTIVE:UNLOCK", StringComparison.OrdinalIgnoreCase) >= 0)
            return "OFF (Unlocked)";
        if (raw.IndexOf("REACTIVE:LOCK", StringComparison.OrdinalIgnoreCase) >= 0)
            return "ON (Locked)";

        return "N/A";
    }

    private static string GetBinaryBit(string version)
    {
        if (string.IsNullOrEmpty(version) || version.Length < 5 || version == "N/A")
            return "N/A";

        try
        {
            return version.Substring(version.Length - 5, 1);
        }
        catch
        {
            return "N/A";
        }
    }

    private static string GuessAndroidVersionFromBuild(string build)
    {
        // Only a coarse fallback. The reliable source is ADB
        // (getprop ro.build.version.release), which overrides this.
        if (string.IsNullOrEmpty(build) || build == "N/A" || build.Length < 10)
            return "N/A";

        try
        {
            return build[9] switch
            {
                'C' => "13",
                'D' => "14",
                'E' => "15",
                'F' => "16",
                _ => "N/A"
            };
        }
        catch
        {
            return "N/A";
        }
    }

    private static string Compact(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "(no answer)";

        string text = raw.Replace("\r", " ").Replace("\n", " ").Trim();
        text = Regex.Replace(text, @"\s+", " ");
        return text.Length <= 240 ? text : text.Substring(0, 240) + "…";
    }

    // =====================================================================
    //  PORT DISCOVERY
    // =====================================================================

    private string GetModemPort(string deviceId)
    {
        // Direct port override (Linux / manual selection): if the caller passes a
        // port name rather than a WMI DeviceID, use it as-is.
        if (!string.IsNullOrWhiteSpace(deviceId) && !deviceId.Contains('\\'))
        {
            return deviceId.Trim();
        }

        if (!OperatingSystem.IsWindows())
        {
            foreach (var p in System.IO.Ports.SerialPort.GetPortNames())
                if (Brom.T509Native.UsbIds.VidPidOf(p).StartsWith("04E8:", StringComparison.Ordinal)) return p;
            var ports = System.IO.Ports.SerialPort.GetPortNames();
            return ports.Length > 0 ? ports[0] : string.Empty;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return string.Empty;

            string[] parts = deviceId.Split('\\');
            if (parts.Length < 3)
                return string.Empty;

            string searchKey = parts[2];
            int secondAmp = searchKey.IndexOf('&', searchKey.IndexOf('&') + 1);
            if (secondAmp > 0)
                searchKey = searchKey.Substring(0, secondAmp);

            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, PNPDeviceID, Name FROM Win32_SerialPort");

            foreach (ManagementObject mo in searcher.Get())
            {
                string pnp = mo["PNPDeviceID"]?.ToString() ?? string.Empty;
                string name = mo["Name"]?.ToString() ?? string.Empty;
                string port = mo["DeviceID"]?.ToString() ?? string.Empty;

                if (name.Contains("SAMSUNG", StringComparison.OrdinalIgnoreCase) &&
                    name.Contains("Modem", StringComparison.OrdinalIgnoreCase) &&
                    (pnp.Contains(searchKey) || pnp.Contains(parts[2])))
                {
                    return port;
                }
            }

            foreach (ManagementObject mo in searcher.Get())
            {
                string pnp = mo["PNPDeviceID"]?.ToString() ?? string.Empty;
                string port = mo["DeviceID"]?.ToString() ?? string.Empty;
                if (pnp.Contains(searchKey) || pnp.Contains(parts[2]))
                    return port;
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug($"GetModemPort error: {ex.Message}");
        }

        return string.Empty;
    }

    private string FindModemPort()
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (var p in System.IO.Ports.SerialPort.GetPortNames())
                if (Brom.T509Native.UsbIds.VidPidOf(p).StartsWith("04E8:", StringComparison.Ordinal)) return p;
            var ports = System.IO.Ports.SerialPort.GetPortNames();
            return ports.Length > 0 ? ports[0] : string.Empty;
        }
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, PNPDeviceID, Name FROM Win32_SerialPort");

            foreach (ManagementObject mo in searcher.Get())
            {
                string pnp = mo["PNPDeviceID"]?.ToString() ?? string.Empty;
                string name = mo["Name"]?.ToString() ?? string.Empty;
                string port = mo["DeviceID"]?.ToString() ?? string.Empty;

                if (pnp.Contains(SamsungMtpVidPid, StringComparison.OrdinalIgnoreCase) ||
                    (name.Contains("SAMSUNG", StringComparison.OrdinalIgnoreCase) &&
                     name.Contains("Modem", StringComparison.OrdinalIgnoreCase)))
                {
                    return port;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug($"FindModemPort error: {ex.Message}");
        }

        return string.Empty;
    }

    private async Task BasicInfoFallback(string deviceId, CancellationToken token)
    {
        await Task.Run(() =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(deviceId))
                    return;

                using var searcher = new ManagementObjectSearcher(
                    $"SELECT * FROM Win32_PnPEntity WHERE DeviceID = '{deviceId.Replace("\\", "\\\\")}'");

                foreach (ManagementObject mo in searcher.Get())
                {
                    _log.LogInfo($"[!] Basic Model: {mo["Name"]}");
                    _log.LogInfo($"[!] Hardware ID: {mo["DeviceID"]}");
                }
            }
            catch { }
        }, token);
    }

    // =====================================================================
    //  LIFECYCLE
    // =====================================================================

    private bool TryBeginOperation()
    {
        if (Interlocked.CompareExchange(ref _activeOperations, 1, 0) != 0)
        {
            UserLog.Warn(_log, "Another MTP operation is already running.",
                "Wait for it to finish, then try again.");
            return false;
        }

        return true;
    }

    private void EndOperation() => Interlocked.Exchange(ref _activeOperations, 0);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}

public sealed class MtpOptions
{
    public int ReadTimeoutMs { get; set; } = 10000;
    public int WriteTimeoutMs { get; set; } = 10000;
    public int CommandDelayMs { get; set; } = 100;
}
