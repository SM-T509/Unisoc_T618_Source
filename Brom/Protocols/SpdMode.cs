using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Management;
using Microsoft.Win32;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Brom.Core;
using Brom.Models;
using Brom.Protocols.Sprd;
using Brom.Services;

namespace Brom.Protocols;

public sealed class SpdMode : IDisposable
{
    private const int BROM_VID = 0x1782;
    private const int BROM_PID = 0x4D00;
    private const int DIAG_PID = 0x4D01;

    private readonly ILogSink _log;
    private readonly PathManager _paths;
    private readonly SpdOptions _options;
    private UsbTransport _transport;
    private SprdBromProtocol _brom;
    private SprdFdlProtocol _fdl;
    private bool _disposed;
    private bool _fdlActive;
    private CancellationTokenSource _cts;

    public SpdMode(ILogSink log, PathManager paths = null, SpdOptions options = null)
    {
        _log = log ?? NullLogSink.Instance;
        _paths = paths ?? PathManager.Instance;
        _options = options ?? new SpdOptions();
    }

    /// <summary>
    /// Same 12-char label column and 43-column cap as the T509 engine block, so
    /// the Force BROM / Exit BROM logs line up with Fix Handshake and FRP + MDM.
    /// 43 columns is what fits the 309 px pane at the minimum window.
    /// </summary>
    internal static string SpdField(string label, string value)
    {
        string line = label.PadRight(12) + ": " + value;
        return line.Length <= 43 ? line : line.Substring(0, 40) + "...";
    }

    public async Task<bool> InitializeAsync(string fdl1Path, string fdl2Path,
        uint loadAddress1, uint loadAddress2, uint execAddress,
        CancellationToken token = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _log.LogInfo("Initializing BROM flashing session...");

        string port = FindBromPort();
        if (string.IsNullOrEmpty(port))
        {
            _log.LogError("No BROM port detected (VID 1782 PID 4D00). Ensure the device is in BROM mode.");
            return false;
        }

        _log.LogInfo($"Found BROM port: {port}");
        _transport = new UsbTransport(_log, port, 115200,
            _options.ConnectTimeoutMs, _options.OperationTimeoutMs,
            dtrEnable: true, rtsEnable: true);
        _transport.Open();

        string fdl1Full = _paths.FindFile(fdl1Path, _paths.GetBromLoadersPath(),
            _paths.GetBinariesPath(), _paths.BaseDirectory);
        string fdl2Full = _paths.FindFile(fdl2Path, _paths.GetBromLoadersPath(),
            _paths.GetBinariesPath(), _paths.BaseDirectory);

        if (string.IsNullOrEmpty(fdl1Full) || string.IsNullOrEmpty(fdl2Full))
        {
            _log.LogError("FDL loaders not found. Check the loaders/T509 folder.");
            return false;
        }

        _brom = new SprdBromProtocol(_log, _transport.UnderlyingPort, new SprdBromOptions
        {
            BaudRate = 115200,
            ConnectTimeoutMs = _options.ConnectTimeoutMs,
            CommandTimeoutMs = _options.OperationTimeoutMs,
            AckTimeoutMs = _options.AckTimeoutMs
        });

        if (!await _brom.InitializeAsync(token))
        {
            _log.LogError("BROM handshake failed - no sync response from device.");
            return false;
        }

        _log.LogInfo($"Sending FDL1: {Path.GetFileName(fdl1Full)} to 0x{loadAddress1:X8}");
        _log.LogInfo($"Sending FDL2: {Path.GetFileName(fdl2Full)} to 0x{loadAddress2:X8}");

        if (!await _brom.SendLoaderAsync(fdl1Full, fdl2Full, loadAddress1, loadAddress2, execAddress, token))
        {
            _log.LogError("FDL loader transmission failed.");
            return false;
        }

        _log.LogSuccess("FDL loaders transferred. Device now in FDL mode.");

        _brom.Dispose();
        _brom = null;

        _fdl = new SprdFdlProtocol(_log, _transport.UnderlyingPort, new SprdFdlOptions
        {
            ResponseTimeoutMs = _options.OperationTimeoutMs,
            FileReceiveTimeoutMs = _options.StreamTimeoutMs,
            BaudRate = 115200,
            MaxPacketSize = 4096
        });

        if (!await _fdl.InitializeAsync(token))
        {
            _log.LogError("FDL protocol initialization failed.");
            return false;
        }

        _fdlActive = true;
        _log.LogSuccess("BROM session initialized successfully (FDL active).");
        return true;
    }

    public async Task<bool> ForceBromAsync(string targetPort = null, CancellationToken token = default)
    {
        _log.LogInfo(SpdField("Mode", "forcing BROM entry"));

        // The UI passes the selected device's COM port. It was ignored before,
        // so the kick always ran against whatever the WMI query found first.
        string selected = NormalizePort(targetPort);

        // 1. Direct BROM check. The selected port is honoured, but only as a
        //    BROM node: writing SPD to an unrelated modem would produce the
        //    "semaphore timeout" noise this button exists to avoid.
        string brom = FindBromPort();
        if (string.IsNullOrEmpty(brom) && selected != null && Brom.T509Native.UsbIds.IsBrom(selected))
            brom = selected;
        if (!string.IsNullOrEmpty(brom) && IsActualBromPort(brom))
        {
            _log.LogInfo(SpdField("Port", brom + " - already in BROM"));
            _log.LogSuccess(SpdField("Result", "BROM active on " + brom));
            return true;
        }

        // 2. Diag kick. The instance helper sends the auth + FRACTAL + HDLC
        //    autoloader sequence, which is stronger than a bare auth write.
        string diag = FindDiagPort();
        if (!string.IsNullOrEmpty(diag))
        {
            _log.LogInfo(SpdField("Diag", "kick -> BROM"));
            await KickDiagToBromAsync(diag, token);
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(250, token);
                brom = FindBromPort();
                if (!string.IsNullOrEmpty(brom))
                {
                    _log.LogSuccess(SpdField("Result", "BROM on " + brom));
                    return true;
                }
            }
        }

        // 3. Find modem port. When the operator selected a port explicitly it
        //    is used even if WMI did not label it as a modem.
        string port = FindModemPort() ?? selected;
        if (string.IsNullOrEmpty(port))
        {
            for (int i = 0; i < 6; i++)
            {
                await Task.Delay(500, token);
                brom = FindBromPort();
                if (!string.IsNullOrEmpty(brom))
                {
                    _log.LogSuccess(SpdField("Result", "BROM on " + brom));
                    return true;
                }
                port = FindModemPort();
                if (!string.IsNullOrEmpty(port)) break;
            }
        }

        if (string.IsNullOrEmpty(port))
        {
            brom = FindBromPort();
            if (!string.IsNullOrEmpty(brom))
            {
                _log.LogSuccess(SpdField("Result", "BROM on " + brom));
                return true;
            }
            _log.LogError(SpdField("Result", "Failed - no modem port."));
            return false;
        }

        byte[] hdlcAutodloader = new byte[]
        {
            0x7E, 0, 0, 0, 0, 0x20, 0,
            0x68, 0, 0x41, 0x54, 0x2B, 0x53, 0x50, 0x52, 0x45,
            0x46, 0x3D, 0x22, 0x41, 0x55, 0x54, 0x4F,
            0x44, 0x4C, 0x4F, 0x41, 0x44, 0x45, 0x22,
            0x0D, 0x0A, 0x7E
        };
        byte[] hdlcDiagSwitch = new byte[] { 0x7E, 0, 0, 0, 0, 8, 0, 0xFE, 0x82, 0x7E };

        string[] atCmds = new string[]
        {
            "AT",
            "AT+SPLBOOT",
            "AT+SPBTEST",
            "AT+SPRDDEBUG=1",
            "AT+REBOOT=BROM",
            "AT+FDL=1",
            "AT+SWATD=0",
            "AT+ACTIVATE=0,0,0",
            "AT+DUMPCTRL=1,0",
            "AT+SPREF=\"AUTODLOADER\"",
            "AT+REBOOT",
        };

        try
        {
            await Task.Run(() =>
            {
                try
                {
                    using var ser = new SerialPort(port, 115200, Parity.None, 8, StopBits.One)
                    {
                        ReadTimeout = 300,
                        WriteTimeout = 800,
                        DtrEnable = true,
                        RtsEnable = true
                    };
                    ser.Open();

                    foreach (var cmd in atCmds)
                    {
                        try
                        {
                            ser.DiscardInBuffer();
                            ser.WriteLine(cmd);
                            System.Threading.Thread.Sleep(100);
                            string rep = ser.ReadExisting();
                            if (!string.IsNullOrWhiteSpace(rep))
                            {
                                // The device answers with \r\n-terminated lines;
                                // flatten them so one AT line stays on one log
                                // line instead of interleaving with the next.
                                string reply = System.Text.RegularExpressions.Regex.Replace(
                                    rep.Trim(), @"\s+", " ");
                                _log.LogDebug(SpdField("AT", $"{cmd} -> {reply}"));
                            }
                        }
                        catch { }
                    }

                    try
                    {
                        ser.Write(hdlcDiagSwitch, 0, hdlcDiagSwitch.Length);
                        System.Threading.Thread.Sleep(40);
                        ser.Write(hdlcAutodloader, 0, hdlcAutodloader.Length);
                        System.Threading.Thread.Sleep(40);
                    }
                    catch { }

                    try { ser.WriteLine("AT+REBOOT"); ser.BaseStream.Flush(); } catch { }
                    try { ser.Close(); } catch { }
                }
                catch (Exception ex)
                {
                    _log.LogDebug($"AT attempt on {port} failed: {ex.Message}");
                }
            }, token);
        }
        catch { }

        // Polling loop up to 20 seconds
        DateTime bromDeadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < bromDeadline)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(400, token);
            brom = FindBromPort();
            if (!string.IsNullOrEmpty(brom))
            {
                _log.LogSuccess(SpdField("Result", "BROM on " + brom));
                return true;
            }
        }

        // Fallback barrage
        string[] barrageCmds = new string[]
        {
            "AT+SPREF=\"AUTODLOADER\"", "AT+FUS=1", "AT+REACTIVE=1,0,0",
            "AT+SPREF=\"AUTODLOADER\"", "AT+DUMPCTRL=1,0", "AT+SWATD=0", "AT+ACTIVATE=0,0,0"
        };
        for (int i = 0; i < 8; i++)
        {
            await Task.Delay(350, token);
            brom = FindBromPort();
            if (!string.IsNullOrEmpty(brom))
            {
                _log.LogSuccess(SpdField("Result", "BROM on " + brom));
                return true;
            }
            try
            {
                await Task.Run(() =>
                {
                    try
                    {
                        using var bser = new SerialPort(port, 19200, Parity.None, 8, StopBits.One)
                        {
                            ReadTimeout = 300,
                            WriteTimeout = 500,
                            DtrEnable = true,
                            RtsEnable = true
                        };
                        bser.Open();
                        foreach (var bcmd in barrageCmds)
                        {
                            bser.Write(bcmd + "\r");
                            System.Threading.Thread.Sleep(20);
                        }
                        bser.Write(hdlcAutodloader, 0, hdlcAutodloader.Length);
                        bser.Close();
                    }
                    catch { }
                }, token);
            }
            catch { }
        }

        brom = FindBromPort();
        if (!string.IsNullOrEmpty(brom))
        {
            _log.LogSuccess(SpdField("Result", "BROM on " + brom));
            return true;
        }

        _log.LogError(SpdField("Result", "Failed - no BROM port"));
        _log.LogWarning(SpdField("Tip", "Vol+ + Vol- on USB plug"));
        return false;
    }

    private async Task KickDiagToBromAsync(string diagPort, CancellationToken token)
    {
        try
        {
            using var sp = new SerialPort(diagPort, 115200, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 1000,
                WriteTimeout = 1000,
                DtrEnable = true,
                RtsEnable = true
            };
            sp.Open();

            string[] auths = { "736D61727473616D2E763121", "736D61727473616D702E763121" };
            foreach (var auth in auths)
            {
                try
                {
                    byte[] b = Convert.FromHexString(auth);
                    sp.Write(b, 0, b.Length);
                    await Task.Delay(40, token);
                }
                catch { }
            }

            byte[] fractal = Convert.FromHexString("A04652414354414C1001");
            for (int i = 0; i < 2; i++)
            {
                try
                {
                    sp.Write(fractal, 0, fractal.Length);
                    await Task.Delay(40, token);
                }
                catch { }
            }

            byte[] hdlcAutodloader = new byte[]
            {
                0x7E, 0, 0, 0, 0, 0x20, 0,
                0x68, 0, 0x41, 0x54, 0x2B, 0x53, 0x50, 0x52, 0x45,
                0x46, 0x3D, 0x22, 0x41, 0x55, 0x54, 0x4F, 0x44,
                0x4C, 0x4F, 0x41, 0x44, 0x45, 0x22, 0x0D, 0x0A, 0x7E
            };
            try { sp.Write(hdlcAutodloader, 0, hdlcAutodloader.Length); await Task.Delay(100, token); } catch { }
            sp.Close();
        }
        catch { }
    }

    public Task<bool> IsBromModeActiveAsync(CancellationToken token = default)
    {
        string port = FindBromPort();
        return Task.FromResult(!string.IsNullOrEmpty(port));
    }

    public async Task<List<PartitionInfo>> GetPartitionsAsync(CancellationToken token = default)
    {
        if (!_fdlActive)
        {
            _log.LogError("FDL session not active. Call InitializeAsync first.");
            return new List<PartitionInfo>();
        }

        return await _fdl.GetPartitionsAsync(token);
    }

    public async Task<bool> ReadPartitionAsync(string name, string outputPath, uint lun,
        CancellationToken token = default)
    {
        if (!_fdlActive)
        {
            _log.LogError("FDL session not active.");
            return false;
        }
        return await _fdl.ReadPartitionAsync(name, outputPath, lun, token);
    }

    public async Task<bool> WritePartitionAsync(string name, string filePath, uint lun,
        CancellationToken token = default)
    {
        if (!_fdlActive)
        {
            _log.LogError("FDL session not active.");
            return false;
        }
        return await _fdl.WritePartitionAsync(name, filePath, lun, token);
    }

    public async Task<bool> ErasePartitionAsync(string name, uint lun,
        CancellationToken token = default)
    {
        if (!_fdlActive)
        {
            _log.LogError("FDL session not active.");
            return false;
        }
        return await _fdl.ErasePartitionAsync(name, lun, token);
    }

    public async Task<bool> ResetAsync(CancellationToken token = default)
    {
        if (_transport == null || !_transport.IsOpen)
        {
            _log.LogError("Reset failed - no open BROM session.");
            return false;
        }

        // Framed by the stack's own encoder: the hand-written
        // "7E 00 05 00 00 FF FA 7E" carries a byte-swapped checksum and is
        // rejected by the device.
        byte[] reset = Brom.T509Native.T509Engine.BuildCommandFrame(BSL_CMD_NORMAL_RESET);
        bool sent = await _transport.WriteAsync(reset, token);
        await Task.Delay(500, token);
        _log.Log(sent ? "Device reset command sent." : "Reset write failed.",
            sent ? LogType.Info : LogType.Error);
        return sent;
    }

    public async Task<bool> FormatUserDataAndFrpAsync(CancellationToken token = default)
    {
        if (!_fdlActive)
        {
            _log.LogError("FDL session not active.");
            return false;
        }

        _log.LogWarning("Formatting userdata, frp, and misc partitions...");
        var parts = await _fdl.GetPartitionsAsync(token);
        foreach (var part in parts)
        {
            token.ThrowIfCancellationRequested();
            string name = part.Name.ToLowerInvariant();
            if (name.Contains("frp") || name.Contains("userdata") ||
                name.Contains("persistent") || name.Contains("misc"))
            {
                _log.LogInfo($"Erasing {part.Name}...");
                await _fdl.ErasePartitionAsync(part.Name, (uint)part.Lun, token);
            }
        }
        _log.LogSuccess("Format complete.");
        return true;
    }

    public async Task<bool> ReadInfoAsync(CancellationToken token = default)
    {
        string port = FindBromPort() ?? FindDiagPort();
        if (string.IsNullOrEmpty(port))
        {
            _log.LogError("No BROM/Diag port detected.");
            return false;
        }

        _log.LogInfo($"Reading device info from {port}...");

        // The old fallback wrote a single 0x7E flag and then printed
        // "device info read" without ever reading an answer - a fabricated
        // success. Ask the device for its stage instead and report what it
        // actually said.
        (bool ok, string stage, string error) probe = await Task.Run(() =>
        {
            SprdProtocolHandler handler = null;
            try
            {
                handler = new SprdProtocolHandler(port, new HdlcEncoder(), 115200)
                {
                    Verbose = false,
                    Retries = 1
                };
                var utils = new SprdFlashUtils(handler, m => { /* quiet probe */ })
                {
                    Timeout = 3000
                };
                var stages = utils.ConnectToDevice();
                return (true, stages.Stage.ToString(), (string)null);
            }
            catch (Exception ex)
            {
                return (false, (string)null, ex.Message);
            }
            finally
            {
                try { handler?.Dispose(); } catch { }
            }
        }, token);

        if (!probe.ok)
        {
            _log.LogError(SpdField("Result", "no answer - " + ShortReason(probe.error)));
            _log.LogWarning(SpdField("Tip", "replug; hold Vol+/Vol- for BROM"));
            return false;
        }

        _log.LogInfo(SpdField("Port", port));
        _log.LogSuccess(SpdField("Stage", probe.stage + " - device answered"));
        return true;
    }

    /// <summary>One-line reason for a log column that is 43 characters wide.</summary>
    private static string ShortReason(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "unknown";
        string text = message.Trim();
        int cut = text.IndexOfAny(new[] { '\r', '\n' });
        if (cut > 0) text = text.Substring(0, cut);
        return text.Length <= 24 ? text : text.Substring(0, 21) + "...";
    }

    public async Task<bool> ExitBromAsync(string targetPort = null, CancellationToken token = default)
    {
        string port = NormalizePort(targetPort) ?? FindBromPort() ?? FindDiagPort();
        if (string.IsNullOrEmpty(port))
        {
            _log.LogError(SpdField("Result", "Failed - no BROM port"));
            return false;
        }

        _log.LogInfo(SpdField("Port", port + " - reset only"));
        try
        {
            using var sp = new SerialPort(port, 115200, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 2000,
                WriteTimeout = 2000,
                DtrEnable = true,
                RtsEnable = true
            };
            sp.Open();
            byte[] resetCmd = Brom.T509Native.T509Engine.BuildCommandFrame(BSL_CMD_NORMAL_RESET);
            sp.Write(resetCmd, 0, resetCmd.Length);
            sp.BaseStream.Flush();
            await Task.Delay(500, token);
            _log.LogSuccess(SpdField("Result", "Done - device rebooting"));
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(SpdField("Result", "Failed - " + ex.Message));
            return false;
        }
    }

    public void Cancel()
    {
        _cts?.Cancel();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _brom?.Dispose();
        _fdl?.Dispose();
        _transport?.Dispose();
    }

    /// <summary>Normalises a COM name coming from the device list
    /// (<c>com21</c> → <c>COM21</c>); anything else is not a serial port.</summary>
    private static string NormalizePort(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string port = value.Trim().ToUpperInvariant();
        return port.StartsWith("COM", StringComparison.Ordinal) && port.Length > 3 ? port : null;
    }

    private bool IsActualBromPort(string port)
    {
        try
        {
            using var sp = new SerialPort(port, 115200, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 1000,
                WriteTimeout = 1000,
                DtrEnable = true,
                RtsEnable = true
            };
            sp.Open();
            sp.DiscardInBuffer();
            byte[] sync = { 0x7E };
            sp.Write(sync, 0, sync.Length);
            sp.BaseStream.Flush();
            System.Threading.Thread.Sleep(300);
            if (sp.BytesToRead > 0)
            {
                int b = sp.ReadByte();
                sp.Close();
                return b == 0x7E;
            }
            sp.Close();
        }
        catch { }
        return false;
    }

    public static bool ProbeBromPort(string port)
    {
        if (string.IsNullOrWhiteSpace(port)) return false;
        if (Brom.T509Native.UsbIds.IsBrom(port)) return true;
        if (Brom.T509Native.UsbIds.IsSpreadtrum(port)) return true;
        try
        {
            using var sp = new SerialPort(port, 115200, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 500,
                WriteTimeout = 500,
                DtrEnable = true,
                RtsEnable = true
            };
            sp.Open();
            sp.DiscardInBuffer();
            byte[] sync = { 0x7E };
            sp.Write(sync, 0, sync.Length);
            sp.BaseStream.Flush();
            System.Threading.Thread.Sleep(200);
            if (sp.BytesToRead > 0)
            {
                int b = sp.ReadByte();
                sp.Close();
                return b == 0x7E;
            }
            sp.Close();
        }
        catch { }
        return false;
    }

    private string FindModemPort()
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (var p in System.IO.Ports.SerialPort.GetPortNames())
                if (Brom.T509Native.UsbIds.VidPidOf(p).StartsWith("04E8:", StringComparison.Ordinal)) return p;
            var ports = System.IO.Ports.SerialPort.GetPortNames();
            return ports.Length > 0 ? ports[0] : null;
        }
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT DeviceID, PNPDeviceID, Name FROM Win32_SerialPort");
            foreach (ManagementObject mo in searcher.Get())
            {
                string pnp = mo["PNPDeviceID"]?.ToString() ?? "";
                string name = mo["Name"]?.ToString() ?? "";
                string port = mo["DeviceID"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(port)) continue;
                if (pnp.Contains("PID_6860", StringComparison.OrdinalIgnoreCase) ||
                    pnp.Contains("PID_685D", StringComparison.OrdinalIgnoreCase) ||
                    (name.Contains("SAMSUNG", StringComparison.OrdinalIgnoreCase) && name.Contains("Modem", StringComparison.OrdinalIgnoreCase)))
                {
                    return port;
                }
            }
            // Fallback: any port with "Modem"
            foreach (ManagementObject mo in searcher.Get())
            {
                string name = mo["Name"]?.ToString() ?? "";
                string port = mo["DeviceID"]?.ToString() ?? "";
                if (name.Contains("Modem", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(port))
                    return port;
            }
        }
        catch { }
        return null;
    }

    internal string FindBromPort()
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (var p in System.IO.Ports.SerialPort.GetPortNames())
                if (Brom.T509Native.UsbIds.IsBrom(p)) return p;
            foreach (var p in System.IO.Ports.SerialPort.GetPortNames())
                if (Brom.T509Native.UsbIds.IsSpreadtrum(p)) return p;
            foreach (var p in System.IO.Ports.SerialPort.GetPortNames())
                if (SpdMode.ProbeBromPort(p)) return p;
            return null;
        }
        // 1. Registry SERIALCOMM (fast, matches Python)
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (key != null)
            {
                foreach (string valueName in key.GetValueNames())
                {
                    if (valueName.Contains("SPRD", StringComparison.OrdinalIgnoreCase) ||
                        valueName.Contains("SPREADTRUM", StringComparison.OrdinalIgnoreCase))
                    {
                        string val = key.GetValue(valueName)?.ToString();
                        if (!string.IsNullOrWhiteSpace(val)) return val.Trim().ToUpperInvariant();
                    }
                }
            }
        }
        catch { }

        // 2. Win32_SerialPort - prioritize BROM mode (VID_1782 PID_4D00)
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, PNPDeviceID, Name FROM Win32_SerialPort");
            foreach (ManagementObject mo in searcher.Get())
            {
                string pnp = mo["PNPDeviceID"]?.ToString() ?? "";
                string name = mo["Name"]?.ToString() ?? "";
                string port = mo["DeviceID"]?.ToString() ?? "";

                // Exact match for BROM mode
                if (pnp.Contains("VID_1782", StringComparison.OrdinalIgnoreCase) &&
                    pnp.Contains("PID_4D00", StringComparison.OrdinalIgnoreCase))
                    return port;
            }
        }
        catch { }

        // 3. Win32_SerialPort - SPRD name match (non-Diag, non-Modem)
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, PNPDeviceID, Name FROM Win32_SerialPort");
            foreach (ManagementObject mo in searcher.Get())
            {
                string pnp = mo["PNPDeviceID"]?.ToString() ?? "";
                string name = mo["Name"]?.ToString() ?? "";
                string port = mo["DeviceID"]?.ToString() ?? "";

                if (name.Contains("SPRD", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("Diag", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("Modem", StringComparison.OrdinalIgnoreCase))
                    return port;
            }
        }
        catch { }

        // 4. Win32_PnPEntity fallback (extract COM from name) - BROM mode
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%)%'");
            foreach (ManagementObject mo in searcher.Get())
            {
                string pnp = mo["PNPDeviceID"]?.ToString() ?? "";
                string name = mo["Name"]?.ToString() ?? "";

                if (pnp.Contains("VID_1782", StringComparison.OrdinalIgnoreCase) &&
                    pnp.Contains("PID_4D00", StringComparison.OrdinalIgnoreCase))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(name ?? "", @"\((COM\d+)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success) return match.Groups[1].Value.ToUpperInvariant();
                }
            }
        }
        catch { }

        // 5. Win32_PnPEntity fallback - SPRD name match
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%)%'");
            foreach (ManagementObject mo in searcher.Get())
            {
                string pnp = mo["PNPDeviceID"]?.ToString() ?? "";
                string name = mo["Name"]?.ToString() ?? "";

                if (name.Contains("SPRD", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("Diag", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("Modem", StringComparison.OrdinalIgnoreCase))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(name ?? "", @"\((COM\d+)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success) return match.Groups[1].Value.ToUpperInvariant();
                }
            }
        }
        catch { }

        // Last resort: probe each serial port for an actual BROM handshake.
        foreach (var p in System.IO.Ports.SerialPort.GetPortNames())
            if (SpdMode.ProbeBromPort(p)) return p;

        return null;
    }

    internal string FindDiagPort()
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (var p in System.IO.Ports.SerialPort.GetPortNames())
                if (Brom.T509Native.UsbIds.VidPidOf(p) == "1782:4D01") return p;
            return null;
        }
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, PNPDeviceID, Name FROM Win32_SerialPort");
            foreach (ManagementObject mo in searcher.Get())
            {
                string pnp = mo["PNPDeviceID"]?.ToString() ?? "";
                string name = mo["Name"]?.ToString() ?? "";
                string port = mo["DeviceID"]?.ToString() ?? "";

                if (pnp.Contains("VID_1782", StringComparison.OrdinalIgnoreCase) &&
                    pnp.Contains("PID_4D01", StringComparison.OrdinalIgnoreCase))
                    return port;

                if (name.Contains("Diag", StringComparison.OrdinalIgnoreCase) &&
                    name.Contains("SPRD", StringComparison.OrdinalIgnoreCase))
                    return port;
            }
        }
        catch { }
        return null;
    }
}

public static class SprdProtocol
{
    public static async Task<bool> ForceBromAsync(Action<string, LogType> logOutput, string targetPort = null,
        CancellationToken token = default, string title = "T509 FORCE BROM")
    {
        // Official bridge output for "t509-force-brom": the box borders are
        // filtered out by the engine, so only the title and the two task rows
        // ever reach the Activity Log.
        ILogSink quiet = NullLogSink.Instance;
        using var op = OpLog.Begin("BROM", "ForceBrom", "force-brom");
        op.Info("title=" + title + " port=" + (string.IsNullOrWhiteSpace(targetPort) ? "auto" : targetPort));

        logOutput?.Invoke(" FORCE BROM", LogType.Info);
        try
        {
            bool ok = false;
            // Try up to 2 times: the device may need a few seconds to enumerate
            // as a BROM node after the Diag kick / port switch.
            for (int attempt = 0; attempt < 2 && !ok; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(3000, token);

                using var spd = new SpdMode(quiet, PathManager.Instance);
                ok = await spd.ForceBromAsync(targetPort, token);
            }
            logOutput?.Invoke("Task # 1: Accessing device:".PadRight(32) + "Done", LogType.Info);
            logOutput?.Invoke("Task # 2: Forcing BROM mode:".PadRight(32) + (ok ? "Done" : "Fail"),
                LogType.Info);
            op.Complete(ok);
            return ok;
        }
        catch (OperationCanceledException)
        {
            op.Complete(false, "cancelled");
            return false;
        }
        catch (Exception ex)
        {
            logOutput?.Invoke("Task # 1: Accessing device:".PadRight(32) + "Done", LogType.Info);
            logOutput?.Invoke("Task # 2: Forcing BROM mode:".PadRight(32) + "Fail", LogType.Info);
            logOutput?.Invoke(" ✗ [FAILED] Bridge error: " + ex.Message, LogType.Error);
            op.Exception(ex, "force-brom");
            op.Complete(false, ex.GetType().Name);
            return false;
        }
    }

    public static async Task ReadInfoAsync(Action<string, LogType> logOutput, string selectedDeviceId = null,
        CancellationToken token = default)
    {
        ILogSink sink = logOutput != null ? new DelegatedLogSink(logOutput) : NullLogSink.Instance;
        using var op = OpLog.Begin("T509", "ReadInfo", "read-info");

        // A caller-supplied port is honoured first: the page already resolved it
        // from the selected device, and re-discovering it could pick another
        // node when several are attached.
        string requested = string.IsNullOrWhiteSpace(selectedDeviceId) ? null : selectedDeviceId.Trim();

        // Live BROM: pull the real identity codes through the in-process stack
        // (chip type / chip UID / flash UID / flash info), not just a sync byte.
        string brom = FindBromPort();
        if (!string.IsNullOrWhiteSpace(requested) && brom == null &&
            requested.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
        {
            brom = requested.ToUpperInvariant();
        }

        if (!string.IsNullOrWhiteSpace(brom))
        {
            string port = brom;
            bool ok = await Task.Run(() =>
                Brom.T509Native.T509Engine.ReadCodes(sink, port, token), token);
            if (ok)
            {
                op.Complete(true, "port=" + port);
                return;
            }
            sink.LogWarning("Code read-out failed - using basic probe.");
            op.Warn("code read-out failed on " + port);
        }

        using var spd = new SpdMode(sink, PathManager.Instance);
        bool fallback = await spd.ReadInfoAsync(token);
        op.Complete(fallback);
    }

    public static string FindBromPort()
    {
        using var spd = new SpdMode(NullLogSink.Instance, PathManager.Instance);
        return spd.FindBromPort();
    }

    public static string FindDiagPort()
    {
        using var spd = new SpdMode(NullLogSink.Instance, PathManager.Instance);
        return spd.FindDiagPort();
    }

    public static async Task<bool> RunBridgeCommandAsync(string command, Action<string, LogType> logOutput, string[] extraArgs = null)
    {
        using var spd = new SpdMode(
            logOutput != null ? new DelegatedLogSink(logOutput) : NullLogSink.Instance,
            PathManager.Instance);

        switch (command.ToLowerInvariant())
        {
            case "t509-force-brom":
            case "force-brom":
                return await spd.ForceBromAsync(null, CancellationToken.None);
            case "t509-fix-brom":
            case "fix-brom":
            case "fix-handshake":
                // The bridge command used to only enter BROM, which is the
                // FORCE BROM action - "fix handshake" never ran the takeover.
                return await FixHandshakeAsync(logOutput, rebootToBrom: true);
            case "t509-format-all":
            case "format-all":
            case "flash-frp":
            case "fix-softbrick":
            case "fix-storage":
                return await spd.FormatUserDataAndFrpAsync(CancellationToken.None);
            case "t509-info":
            case "read-info":
                // Return the real result; this used to answer "true" even when
                // the probe reported no device.
                return await spd.ReadInfoAsync(CancellationToken.None);
            case "exit-brom":
                return await spd.ExitBromAsync(null, CancellationToken.None);
            case "exit-force":
            case "exit--force":
                return await SprdProtocol.ExitForceAsync(logOutput);
            default:
                logOutput?.Invoke($"Unknown command: {command}", LogType.Warning);
                return false;
        }
    }

    public static void KickDiagToBrom(string diagPort, Action<string, LogType>? log)
    {
        log?.Invoke($"[SPRD Diag] Sending Force BROM commands to {diagPort}...", LogType.Warning);
        // Kick diag to brom - simplified
        try
        {
            using var sp = new System.IO.Ports.SerialPort(diagPort, 115200, System.IO.Ports.Parity.None, 8, System.IO.Ports.StopBits.One)
            {
                ReadTimeout = 1000,
                WriteTimeout = 1000,
                DtrEnable = true,
                RtsEnable = true
            };
            sp.Open();
            // Send auth bytes
            byte[] auth1 = Convert.FromHexString("736D61727473616D2E763121");
            sp.Write(auth1, 0, auth1.Length);
            System.Threading.Thread.Sleep(40);
            byte[] auth2 = Convert.FromHexString("736D61727473616D702E763121");
            sp.Write(auth2, 0, auth2.Length);
            System.Threading.Thread.Sleep(40);
            sp.Close();
        }
        catch { }
    }

    /// <summary>
    /// The BROM page "Fix Handshake" action: upload the captured boot SPL from
    /// RAM and let the device chain-boot Android.
    ///
    /// <paramref name="rebootToBrom"/> keeps the historical name, but this
    /// wrapper is not an FDL1 takeover: <c>true</c> and <c>false</c> both end
    /// in <see cref="Brom.T509Native.T509FlashService.FixHandshake"/>
    /// with <c>rebootToBrom: false</c>. The real download-mode takeover is
    /// <see cref="Brom.T509Native.T509Engine.EnterDownloadMode"/> used by
    /// the BROM tab.
    /// </summary>
    public static Task<bool> FixHandshakeAsync(Action<string, LogType>? logOutput,
        bool rebootToBrom = false, string? targetPort = null, CancellationToken token = default)
    {
        return rebootToBrom
            ? EnterDownloadModeAsync(logOutput, targetPort, 30, token)
            : FixHandshakeToAndroidAsync(logOutput, targetPort, token);
    }

    /// <summary>
    /// Historical name kept for callers: uploads the boot SPL + CVE stub from
    /// SRAM and lets the device chain-boot Android. Nothing is written to
    /// eMMC. This does <b>not</b> leave the SoC in FDL1; use
    /// <see cref="Brom.T509Native.T509Engine.EnterDownloadMode"/> for a
    /// download-mode takeover.
    /// </summary>
    private static async Task<bool> EnterDownloadModeAsync(Action<string, LogType>? logOutput,
        string? targetPort, int bromWaitSeconds = 60, CancellationToken token = default)
    {
        // Every line in the Activity Log must come from the engine. The old
        // version printed a fixed success script ("Downloading handshake
        // files...", "Connection success.", "Handshake executed
        // successfully...") while the real engine output was discarded into
        // NullLogSink, so a failed run still looked successful.
        ILogSink sink = logOutput != null ? new DelegatedLogSink(logOutput) : NullLogSink.Instance;
        using var op = OpLog.Begin("T509", "Handshake", "fix-handshake");
        op.Info("port=" + (string.IsNullOrWhiteSpace(targetPort) ? "auto" : targetPort));
        try
        {
            op.Stage("resolve BROM port");
            string port = string.IsNullOrWhiteSpace(targetPort)
                ? await EnsureBromPortAsync(logOutput, token)
                : targetPort;
            if (string.IsNullOrWhiteSpace(port))
            {
                sink.LogError("No BROM/Diag port detected.");
                op.Complete(false, "no BROM port");
                return false;
            }

            op.Stage("run Fix Handshake on " + port);
            bool ok = await Task.Run(() =>
                Brom.T509Native.T509FlashService.FixHandshake(
                    sink, port, rebootToBrom: false, token), token);

            op.Complete(ok, "port=" + port);
            return ok;
        }
        catch (OperationCanceledException)
        {
            sink.LogWarning("Fix Handshake cancelled.");
            op.Complete(false, "cancelled");
            return false;
        }
        catch (Exception ex)
        {
            sink.LogError("Fix Handshake failed - " + ex.Message);
            op.Exception(ex, "fix-handshake");
            op.Complete(false, ex.GetType().Name);
            return false;
        }
    }

    /// <summary>Legacy path: upload the captured SPL and let the device boot
    /// Android. Not wired to any button in the V1.5 UI.</summary>
    private static async Task<bool> FixHandshakeToAndroidAsync(Action<string, LogType>? logOutput,
        string? targetPort, CancellationToken token = default)
    {
        string separator = new string('─', 43);
        try
        {
            ILogSink sink = logOutput != null ? new DelegatedLogSink(logOutput) : NullLogSink.Instance;
            string port = string.IsNullOrWhiteSpace(targetPort)
                ? await EnsureBromPortAsync(logOutput, token)
                : targetPort;
            if (string.IsNullOrWhiteSpace(port))
            {
                sink.LogError("No BROM port - put the device in BROM.");
                return false;
            }

            sink.LogInfo("T509 FIX HANDSHAKE - BOOT ANDROID");
            sink.LogInfo(separator);

            bool ok = await Task.Run(() =>
                Brom.T509Native.T509FlashService.FixHandshake(sink, port, false, token), token);

            sink.LogInfo(separator);
            return ok;
        }
        catch (Exception ex)
        {
            logOutput?.Invoke($"Fix handshake error: {ex.Message}", LogType.Error);
            return false;
        }
    }

    /// <summary>
    /// Kept for callers that still use the old name: this is exactly the body
    /// of the Fix Handshake button.
    /// </summary>
    public static Task<bool> RefreshSplToBromAsync(Action<string, LogType>? logOutput,
        string? targetPort = null, int bromWaitSeconds = 30, CancellationToken token = default)
        => EnterDownloadModeAsync(logOutput, targetPort, bromWaitSeconds, token);

    /// <summary>
    /// BROM page "FRP + MDM": writes bins/t509/misc.bin, persistent.bin,
    /// sec_efs.bin and splloader.bin (last) in one verified FDL2 session.
    /// </summary>
    public static async Task<bool> FlashT509FrpMdmAsync(Action<string, LogType>? logOutput,
        string? targetPort = null, CancellationToken token = default)
    {
        // Same framed-block treatment as Fix Handshake: bold title, one rule
        // under it, the engine's aligned fields, and a closing rule.
        string separator = new string('─', 43);
        ILogSink sink = logOutput != null ? new DelegatedLogSink(logOutput) : NullLogSink.Instance;
        using var op = OpLog.Begin("T509", "FrpMdm", "frp-mdm");
        op.Info("port=" + (string.IsNullOrWhiteSpace(targetPort) ? "auto" : targetPort));
        try
        {
            sink.LogInfo("T509 FRP + MDM");
            sink.LogInfo(separator);

            op.Stage("resolve download-mode port");
            string port = string.IsNullOrWhiteSpace(targetPort)
                ? await EnsureDownloadPortAsync(logOutput, token)
                : targetPort;
            if (string.IsNullOrWhiteSpace(port))
            {
                sink.LogError("No BROM port - put the device in BROM.");
                sink.LogInfo(separator);
                op.Complete(false, "no BROM port");
                return false;
            }

            op.Stage("write + verify on " + port);
            bool ok = await Task.Run(() =>
                Brom.T509Native.T509FlashService.FlashFrpMdm(sink, port, allowSecEfs: true, token), token);

            sink.LogInfo(separator);
            op.Complete(ok, "port=" + port);
            return ok;
        }
        catch (OperationCanceledException)
        {
            sink.LogWarning(SpdMode.SpdField("Result", "Cancelled - partial writes possible"));
            sink.LogInfo(separator);
            op.Complete(false, "cancelled");
            return false;
        }
        catch (Exception ex)
        {
            sink.LogError(SpdMode.SpdField("Result", "Failed - " + ex.Message));
            sink.LogInfo(separator);
            op.Exception(ex, "frp-mdm");
            op.Complete(false, ex.GetType().Name);
            return false;
        }
    }

    public static async Task<bool> FlashSplOnlyAsync(Action<string, LogType>? logOutput,
        string? targetPort = null, CancellationToken token = default)
    {
        logOutput?.Invoke("T509 - flashing splloader.bin only...", LogType.Info);
        try
        {
            ILogSink sink = logOutput != null ? new DelegatedLogSink(logOutput) : NullLogSink.Instance;
            string port = string.IsNullOrWhiteSpace(targetPort)
                ? await EnsureDownloadPortAsync(logOutput, token)
                : targetPort;
            if (string.IsNullOrWhiteSpace(port))
            {
                sink.LogError("No BROM port - put the device in BROM.");
                return false;
            }

            sink.LogInfo($"Using BROM port {port}.");
            bool ok = await Task.Run(() =>
                Brom.T509Native.T509FlashService.FlashSplloaderOnly(sink, port, token), token);

            sink.Log(ok ? "splloader flashed." : "splloader flash failed.",
                ok ? LogType.Success : LogType.Error);
            return ok;
        }
        catch (OperationCanceledException)
        {
            logOutput?.Invoke(SpdMode.SpdField("Result", "Cancelled"), LogType.Warning);
            return false;
        }
        catch (Exception ex)
        {
            logOutput?.Invoke($"splloader flash error: {ex.Message}", LogType.Error);
            OpLog.Exception("T509", "FlashSpl", ex, "spl-only");
            return false;
        }
    }

    /// <summary>
    /// Generic FRP removal: misc wipe-BCB + clean persistent +
    /// splloader (last), all through the same in-process T509 engine.
    /// sec_efs is intentionally NOT touched here.
    /// </summary>
    public static async Task<bool> FormatAllAsync(Action<string, LogType>? logOutput,
        string? targetPort = null, CancellationToken token = default)
    {
        // The engine is the only source of Activity Log lines. The previous
        // version printed a fixed "Loader 1/2/3 ... success" script and threw
        // the real engine output into NullLogSink, so failures still looked
        // like a completed flash.
        ILogSink sink = logOutput != null ? new DelegatedLogSink(logOutput) : NullLogSink.Instance;
        using var op = OpLog.Begin("BROM", "RemoveFrp", "remove-frp");
        try
        {
            op.Stage("resolve download-mode port");
            string port = targetPort ?? await EnsureDownloadPortAsync(logOutput, token);
            if (string.IsNullOrWhiteSpace(port))
            {
                sink.LogError("No Device Connected");
                op.Complete(false, "no port");
                return false;
            }

            string bins = PathManager.Instance.GetT509BinsPath();
            var jobs = new System.Collections.Generic.List<(string Name, string Path)>
            {
                ("misc", Path.Combine(bins, "misc.bin")),
                ("persistent", Path.Combine(bins, "persistent.bin")),
                ("splloader", Path.Combine(bins, "splloader.bin")),
            };

            op.Stage("write + verify on " + port);
            bool ok = await Task.Run(() =>
                Brom.T509Native.T509FlashService.FlashPartitions(
                    sink, port, jobs, reboot: true, token), token);

            op.Complete(ok, "port=" + port);
            return ok;
        }
        catch (OperationCanceledException)
        {
            sink.LogWarning("Format & FRP cancelled - partial writes possible.");
            op.Complete(false, "cancelled");
            return false;
        }
        catch (Exception ex)
        {
            sink.LogError("Format & FRP failed - " + ex.Message);
            op.Exception(ex, "remove-frp");
            op.Complete(false, ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// Reuses an already-live BROM when present; otherwise runs the existing
    /// AT/DIAG kick and waits for the 1782:4D00 node to enumerate.
    /// </summary>
    private static async Task<string> EnsureBromPortAsync(Action<string, LogType>? logOutput,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();

        string port = await Task.Run(() => Brom.T509Native.T509FlashService.FindBromPort(2), token);
        if (!string.IsNullOrWhiteSpace(port))
            return port;

        // The USB descriptor can change after a chain-boot; fall back to the
        // live CONNECT/echo probe before trying to kick a DIAG port.
        port = await Task.Run(() => Brom.T509Native.T509FlashService.FindBromPort(3), token);
        if (!string.IsNullOrWhiteSpace(port))
            return port;

        // A loader already running on this device does not necessarily keep the
        // BROM PID, so look for any live Spreadtrum node before kicking.
        port = await Task.Run(() => FindLiveSpreadtrumPort(), token);
        if (!string.IsNullOrWhiteSpace(port))
            return port;

        ILogSink sink = logOutput != null ? new DelegatedLogSink(logOutput) : NullLogSink.Instance;
        sink.LogWarning("No live BROM - trying the DIAG kick...");
        using (var spd = new SpdMode(sink, PathManager.Instance))
            await spd.ForceBromAsync(null, token);

        return await Task.Run(() => Brom.T509Native.T509FlashService.FindBromPort(20), token);
    }

    /// <summary>
    /// Finds the port a flash job must talk to.
    ///
    /// A flash button never starts a takeover: the device has to be brought up
    /// by Force BROM (or Fix Handshake) first, because re-running the
    /// exploit while a loader is already running would drop the session
    /// mid-write. So this only looks for an existing download-mode node and
    /// reports how to get one when there is none.
    /// </summary>
    private static async Task<string> EnsureDownloadPortAsync(Action<string, LogType>? logOutput,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();

        string port = await Task.Run(() => Brom.T509Native.T509FlashService.FindBromPort(2), token);
        if (!string.IsNullOrWhiteSpace(port))
            return port;

        port = await Task.Run(() => Brom.T509Native.T509FlashService.FindBromPort(3), token);
        if (!string.IsNullOrWhiteSpace(port))
            return port;

        // A running FDL1 does not always keep PID 4D00. Accept any Spreadtrum
        // node that actually answers SPD, without starting a takeover.
        port = await Task.Run(() => FindLiveSpreadtrumPort(), token);
        if (!string.IsNullOrWhiteSpace(port))
            return port;

        ILogSink sink = logOutput != null ? new DelegatedLogSink(logOutput) : NullLogSink.Instance;
        sink.LogWarning(SpdMode.SpdField("Port", "no BROM node - run Force BROM"));
        return null;
    }

    /// <summary>First 1782:xxxx port that answers an SPD CONNECT probe.</summary>
    private static string FindLiveSpreadtrumPort()
    {
        try
        {
            foreach (string candidate in System.IO.Ports.SerialPort.GetPortNames().Distinct())
            {
                if (!Brom.T509Native.UsbIds.IsSpreadtrum(candidate)) continue;
                if (Brom.T509Native.T509Engine.PortAnswers(candidate)) return candidate;
            }
        }
        catch { }
        return null;
    }

    public static async Task<bool> ExitForceAsync(Action<string, LogType>? logOutput, string? targetPort = null,
        CancellationToken token = default)
    {
        // Forced exit is the same reset; the frame comes from ExitBromAsync.
        try
        {
            return await ExitBromAsync(logOutput, targetPort, token);
        }
        catch (OperationCanceledException)
        {
            logOutput?.Invoke(SpdMode.SpdField("Result", "Cancelled - nothing written"), LogType.Warning);
            return false;
        }
        catch (Exception ex)
        {
            logOutput?.Invoke(SpdMode.SpdField("Result", "Failed - " + ex.Message), LogType.Error);
            OpLog.Exception("T509", "ExitBrom", ex, "exit-force");
            return false;
        }
    }

    public static async Task<bool> ExitBromAsync(Action<string, LogType>? logOutput, string? targetPort = null,
        CancellationToken token = default)
    {
        // A RESET frame has no handshake and no ACK: the only thing a software
        // tool can honestly report is that the write completed. The old text
        // claimed the tablet had rebooted even when the frame went nowhere.
        using var op = OpLog.Begin("T509", "ExitBrom", "exit-brom");

        logOutput?.Invoke("╔═════════════════════════════════════════════════════╗", LogType.Info);
        logOutput?.Invoke("║            T509 BOOTROM: EXIT BROM (REBOOT)          ║", LogType.Info);
        logOutput?.Invoke("╚═════════════════════════════════════════════════════╝", LogType.Info);

        string port = (!string.IsNullOrWhiteSpace(targetPort) && targetPort != "N/A")
            ? targetPort
            : (FindBromPort() ?? FindDiagPort());
        if (string.IsNullOrEmpty(port))
        {
            logOutput?.Invoke(" ✗ [FAILED] No BROM port detected to exit.", LogType.Error);
            op.Complete(false, "no port");
            return false;
        }

        op.Info("port=" + port);
        try
        {
            using var sp = new System.IO.Ports.SerialPort(port, 115200, System.IO.Ports.Parity.None, 8, System.IO.Ports.StopBits.One)
            {
                ReadTimeout = 2000,
                WriteTimeout = 2000,
                DtrEnable = true,
                RtsEnable = true
            };
            sp.Open();

            // The hand-written frame carried the checksum bytes in the wrong
            // order (FF FA instead of FA FF), so the device rejected it while
            // the UI still announced success. Frame it with the stack's
            // encoder, which the upload path already proves on this hardware.
            byte[] resetCmd = Brom.T509Native.T509Engine.BuildCommandFrame(BSL_CMD_NORMAL_RESET);
            op.Debug("reset frame=" + BitConverter.ToString(resetCmd));
            sp.Write(resetCmd, 0, resetCmd.Length);
            sp.BaseStream.Flush();

            await Task.Delay(700, token);
            op.Complete(true, "reset frame sent");
            logOutput?.Invoke("RESET frame sent to " + port + " (no ACK expected).", LogType.Info);
            return true;
        }
        catch (OperationCanceledException)
        {
            op.Complete(false, "cancelled");
            return false;
        }
        catch (Exception ex)
        {
            op.Exception(ex, "exit-brom");
            op.Complete(false, ex.GetType().Name);
            return false;
        }
    }

    /// <summary>True when the COM node still exists in the device registry.</summary>
    private static bool IsPortPresent(string port)
    {
        if (string.IsNullOrWhiteSpace(port)) return false;
        try
        {
            foreach (string name in System.IO.Ports.SerialPort.GetPortNames())
            {
                if (string.Equals(name, port, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        return false;
    }
}

public sealed class SpdOptions
{
    public int ConnectTimeoutMs { get; set; } = 30000;
    public int OperationTimeoutMs { get; set; } = 10000;
    public int AckTimeoutMs { get; set; } = 5000;
    public int StreamTimeoutMs { get; set; } = 300000;
    public int MaxHandshakeAttempts { get; set; } = 3;
}
