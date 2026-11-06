using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace Brom.T509Native
{
    /// <summary>
    /// Reads real USB VID/PID from the device registry so BROM detection never
    /// depends on a flaky serial echo probe.
    ///   BROM  = VID 1782 / PID 4D00  (SPRD U2S Diag)
    ///   Modem = VID 04E8 (Samsung)
    /// Source: HKLM\SYSTEM\CurrentControlSet\Enum\USB\<VID_x&PID_y>\...\Device Parameters\PortName
    /// </summary>
    internal static class UsbIds
    {
        private const string UsbRoot = @"SYSTEM\CurrentControlSet\Enum\USB";

        private static Dictionary<string, string>? _cache;
        private static DateTime _cacheAt;

        private static Dictionary<string, string> Scan()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using RegistryKey? root = Registry.LocalMachine.OpenSubKey(UsbRoot);
                    if (root == null) return map;

                    foreach (string devId in root.GetSubKeyNames())
                    {
                        int amp = devId.IndexOf('&');
                        int pidAt = devId.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
                        if (amp < 0 || pidAt < 0) continue;
                        string vid = devId.Substring(4, amp - 4);
                        string pid = devId.Substring(pidAt + 4, 4);
                        if (vid.Length != 4 || pid.Length != 4) continue;
                        string id = vid + ":" + pid;

                        using RegistryKey? devKey = root.OpenSubKey(devId);
                        if (devKey == null) continue;
                        foreach (string inst in devKey.GetSubKeyNames())
                        {
                            using RegistryKey? dp = devKey.OpenSubKey($"{inst}\\Device Parameters");
                            if (dp == null) continue;
                            object? port = dp.GetValue("PortName");
                            if (port is string com && com.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                                map[com] = id;
                        }
                    }
                }
                catch { /* fall back to the echo probe */ }
                return map;
            }

            // Linux: walk /sys/class/tty to recover VID:PID for each serial node.
            try
            {
                const string ttyRoot = "/sys/class/tty";
                if (Directory.Exists(ttyRoot))
                {
                    foreach (string ttyDir in Directory.GetDirectories(ttyRoot))
                    {
                        string name = Path.GetFileName(ttyDir);
                        string cur = ttyDir;
                        string vid = string.Empty;
                        string pid = string.Empty;
                        for (int i = 0; i < 8 && !string.IsNullOrEmpty(cur); i++)
                        {
                            string vp = Path.Combine(cur, "idVendor");
                            string pp = Path.Combine(cur, "idProduct");
                            if (File.Exists(vp)) vid = File.ReadAllText(vp).Trim();
                            if (File.Exists(pp)) pid = File.ReadAllText(pp).Trim();
                            if (vid.Length == 4 && pid.Length == 4) break;
                            var parent = Directory.GetParent(cur);
                            cur = parent?.FullName;
                        }
                        if (vid.Length == 4 && pid.Length == 4)
                        {
                            string id = vid.ToUpperInvariant() + ":" + pid.ToUpperInvariant();
                            map[name] = id;
                            map["/dev/" + name] = id;
                        }
                    }
                }
            }
            catch { /* fall back to the echo probe */ }
            return map;
        }

        /// <summary>Cached USB VID:PID for a COM port, or "" if not a USB serial device.</summary>
        public static string VidPidOf(string comPort)
        {
            if (_cache == null || (DateTime.UtcNow - _cacheAt).TotalSeconds > 2)
            {
                _cache = Scan();
                _cacheAt = DateTime.UtcNow;
            }
            return _cache.TryGetValue(comPort, out string? id) ? id ?? "" : "";
        }

        /// <summary>True when the port enumerates as the Spreadtrum BROM diag node.</summary>
        public static bool IsBrom(string comPort) => VidPidOf(comPort) == "1782:4D00";

        /// <summary>
        /// True when the port is any Spreadtrum (VID 1782) serial node.
        /// </summary>
        public static bool IsSpreadtrum(string comPort)
            => VidPidOf(comPort).StartsWith("1782:", StringComparison.Ordinal);

        /// <summary>All USB COM devices the registry knows about, for diagnostics.</summary>
        public static List<string> Dump()
        {
            var lines = new List<string>();
            foreach (var kv in Scan()) lines.Add($"{kv.Key}  USB {kv.Value}");
            return lines;
        }
    }
}