using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using Brom.Services;

namespace Brom.T509Native
{
    public static class T509FlashService
    {
        public const uint SPL_ADDR = 0x5500;
        public const uint FDL1_ADDR = 0x5500;
        public const uint STUB_ADDR = 0x3EE8;
        public const uint FDL2_ADDR = 0x9EFFFE00;

        internal sealed class LoadBlob
        {
            public uint Address;
            public byte[] Data;
        }

        // =====================================================================
        //  Fix Handshake  (in-process SPD engine, optional BROM re-entry)
        // =====================================================================
        /// <summary>
        /// Replays the captured BROM session (SPL + exploit stub) and then either
        /// lets the device continue booting or resets it straight back into BROM.
        /// </summary>
        public static bool FlashSplloaderOnly(ILogSink log, string port, CancellationToken token)
        {
            log ??= NullLogSink.Instance;
            var paths = PathManager.Instance;
            string bins = paths.GetT509BinsPath();
            string spl = Path.Combine(bins, "splloader.bin");
            if (!File.Exists(spl))
            {
                // Fall back to T509_Fix.bin from bins/t509 first, then
                // loaders/T509.
                string fixBin = paths.GetT509FixBinPath();
                string fixLoader = paths.GetT509FixPath();
                string fix = null;
                if (File.Exists(fixBin)) fix = fixBin;
                else if (File.Exists(fixLoader)) fix = fixLoader;

                if (fix != null)
                {
                    var blob = T509Engine.LoadFixSpl(paths, log);
                    string tmp = Path.Combine(Path.GetTempPath(), "splloader_fallback.bin");
                    File.WriteAllBytes(tmp, blob);
                    spl = tmp;
                    log.LogInfo($"Using T509_Fix.bin from: {fix}");
                }
                else
                {
                    log.LogError($"Missing splloader image: {spl}");
                    return false;
                }
            }
            var jobs = new List<(string Name, string Path)> { ("splloader", spl) };
            log.LogInfo("Engine: in-process SPD Client Core (flash splloader only).");
            var result = T509Engine.FlashPartitions(log, port, jobs, reboot: true, token);
            return result.Ok;
        }

        public static bool FixHandshake(ILogSink log, string port, bool rebootToBrom,
            CancellationToken token)
        {
            log ??= NullLogSink.Instance;
            var target = rebootToBrom ? T509BootTarget.Brom : T509BootTarget.Android;

            var result = T509Engine.FixHandshake(log, port, target, token);
            if (result.Ok)
                return true;

            // The engine already logged the concrete failure; only surface
            // cancellations here (the engine logs those as a warning).
            return false;
        }

        /// <summary>Legacy overload - handshake only, no reboot (used by the
        /// generic BROM boot/connect flow).</summary>
        public static bool FixHandshake(ILogSink log, string port, CancellationToken token)
            => FixHandshake(log, port, rebootToBrom: false, token);

        /// <summary>
        /// Upload-only BROM refresh: runs the download loader
        /// (<c>loaders/T509/fdl1-dl.bin</c> + CVE stub) from SRAM so the device
        /// drops USB and re-enumerates in download mode. Nothing is written to
        /// eMMC, so the tablet cannot be bricked by a bad image.
        /// </summary>
        public static bool RefreshSplToBrom(ILogSink log, string port, int bromWaitSeconds,
            CancellationToken token)
        {
            log ??= NullLogSink.Instance;
            return T509Engine.RefreshSplToBrom(log, port, bromWaitSeconds, token).Ok;
        }

        /// <summary>
        /// Same takeover, but returns the port the loader is actually on. A
        /// device that re-enumerates (new COM number) after the hijack would
        /// otherwise be read on the stale port and the table would fail right
        /// after a successful BOOT/CONNECT. Null when the takeover failed.
        /// </summary>
        public static string RefreshSplToBromPort(ILogSink log, string port, int bromWaitSeconds,
            CancellationToken token)
        {
            log ??= NullLogSink.Instance;
            T509RunResult result = T509Engine.RefreshSplToBrom(log, port, bromWaitSeconds, token);
            return result.Ok ? (string.IsNullOrWhiteSpace(result.Port) ? port : result.Port) : null;
        }

        /// <summary>
        /// BROM -> FDL1 takeover used by the BROM tab.
        /// Returns the port now in download mode, or throws with the engine's
        /// concrete reason. Nothing is written to eMMC.
        /// </summary>
        public static string EnterDownloadMode(ILogSink log, string port, CancellationToken token)
        {
            log ??= NullLogSink.Instance;
            return T509Engine.EnterDownloadMode(log, port, token);
        }

        /// <summary>
        /// Real partition table straight from the device (download mode). Used by
        /// the BROM tab so the grid is never a guessed layout.
        /// </summary>
        public static List<(string Name, ulong Size)> ReadPartitions(ILogSink log, string port,
            CancellationToken token)
        {
            log ??= NullLogSink.Instance;
            return T509Engine.ReadPartitionTable(log, port, token);
        }

        // -----------------------------------------------------------------
        //  Partition I/O (BROM). All four require the device to be in
        //  download mode already; they never trigger a takeover themselves.
        // -----------------------------------------------------------------
        public static int ReadPartitionsToFolder(ILogSink log, string port,
            IReadOnlyList<string> names, string folder, CancellationToken token)
        {
            log ??= NullLogSink.Instance;
            return T509Engine.ReadPartitionsToFolder(log, port, names, folder, token);
        }

        public static bool WritePartitionFromFile(ILogSink log, string port,
            string name, string file, CancellationToken token)
        {
            log ??= NullLogSink.Instance;
            return T509Engine.WritePartitionFromFile(log, port, name, file, token);
        }

        public static int ErasePartitions(ILogSink log, string port,
            IReadOnlyList<string> names, CancellationToken token)
        {
            log ??= NullLogSink.Instance;
            return T509Engine.ErasePartitions(log, port, names, token);
        }

        public static int DumpAllPartitions(ILogSink log, string port, string folder,
            CancellationToken token)
        {
            log ??= NullLogSink.Instance;
            return T509Engine.DumpAllPartitions(log, port, folder, token);
        }

        // =====================================================================
        //  FRP + MDM  (in-process, no external exe)
        // =====================================================================
        /// <summary>
        /// Writes misc + persistent + splloader (last) in one verified FDL2
        /// session. Runs the vendored SPRDClientCore stack in-process.
        ///
        /// This is the MDM + FRP set. sec_efs (device identity) is intentionally
        /// NOT touched here, and no backup/dump is taken — only the three
        /// partitions are written with read-back verification.
        /// </summary>
        public static bool FlashFrpMdm(ILogSink log, string port, bool allowSecEfs,
            CancellationToken token)
        {
            log ??= NullLogSink.Instance;
            return T509Engine.FlashFrpMdm(log, port, allowSecEfs, token).Ok;
        }

        /// <summary>Generic verified write set (Format / FRP), all in-process.</summary>
        public static bool FlashPartitions(ILogSink log, string port,
            IReadOnlyList<(string Name, string Path)> jobs, bool reboot,
            CancellationToken token)
        {
            log ??= NullLogSink.Instance;
            return T509Engine.FlashPartitions(log, port, jobs, reboot, token).Ok;
        }


        public static string FindBromPort(int seconds)
        {
            var end = DateTime.UtcNow.AddSeconds(Math.Max(1, seconds));
            do
            {
                foreach (string port in SerialPort.GetPortNames().Distinct())
                {
                    try
                    {
                        if (UsbIds.IsBrom(port))
                            return port;
                    }
                    catch { }
                }
                Thread.Sleep(500);
            }
            while (DateTime.UtcNow < end);

            return null;
        }

        // =====================================================================
        //  T509_Fix.bin parser (HDLC-framed capture: START/MIDST/END)
        // =====================================================================
        internal static List<LoadBlob> ParseFixBin(string path)
        {
            byte[] raw = File.ReadAllBytes(path);
            var blobs = new List<LoadBlob>();

            uint address = 0;
            int expected = -1;
            MemoryStream current = null;

            void Complete()
            {
                if (current != null && expected >= 0)
                {
                    byte[] data = current.ToArray();
                    if (data.Length > 0)
                        blobs.Add(new LoadBlob { Address = address, Data = data });
                }
                current = null;
                expected = -1;
            }

            int i = 0;
            while (i < raw.Length - 1)
            {
                if (raw[i] != 0x7E) { i++; continue; }
                int j = i + 1;
                while (j < raw.Length && raw[j] != 0x7E) j++;
                if (j >= raw.Length) break;

                var frame = new List<byte>();
                bool esc = false;
                for (int k = i + 1; k < j; k++)
                {
                    byte b = raw[k];
                    if (esc) { frame.Add((byte)(b ^ 0x20)); esc = false; }
                    else if (b == 0x7D) { esc = true; }
                    else { frame.Add(b); }
                }

                if (frame.Count >= 4)
                {
                    ushort cmd = (ushort)((frame[0] << 8) | frame[1]);
                    ushort len = (ushort)((frame[2] << 8) | frame[3]);
                    if (frame.Count >= 4 + len + 2)
                    {
                        if (cmd == 0x0001)
                        {
                            Complete();
                            if (len >= 8)
                            {
                                address = (uint)((frame[4] << 24) | (frame[5] << 16) | (frame[6] << 8) | frame[7]);
                                expected = (frame[8] << 24) | (frame[9] << 16) | (frame[10] << 8) | frame[11];
                                current = new MemoryStream();
                            }
                        }
                        else if (cmd == 0x0002)
                        {
                            if (current != null && expected > 0)
                            {
                                int chunkLen = Math.Min(len, expected - (int)current.Length);
                                if (chunkLen > 0)
                                    current.Write(frame.ToArray(), 4, chunkLen);
                                if (current.Length >= expected)
                                    Complete();
                            }
                        }
                        else if (cmd == 0x0003)
                        {
                            Complete();
                        }
                    }
                }

                i = j;
            }
            Complete();
            return blobs;
        }
    }
}
