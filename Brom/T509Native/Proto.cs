using System;
using System.Collections.Generic;
using System.IO.Ports;

namespace Brom.T509Native
{
    /// <summary>
    /// Minimal BROM BSL framing: HDLC flags + escape + CRC-16/CCITT (hqx).
    /// Only what this tool needs: echo probe, AT channel, RESET exit frame.
    /// </summary>
    internal static class Proto
    {
        public const byte FLAG = 0x7E;
        public const byte ESC = 0x7D;
        public const byte ESC_FLAG = 0x5E;
        public const byte ESC_ESC = 0x5D;

        public const int BROM_BAUD = 115200;
        public const int MODEM_BAUD = 19200;

        /// <summary>CRC-16/CCITT-FALSE over raw packet bytes.</summary>
        public static ushort Hqx(byte[] data)
        {
            int crc = 0xFFFF;
            foreach (byte b in data)
            {
                crc ^= b << 8;
                for (int i = 0; i < 8; i++)
                    crc = ((crc & 0x8000) != 0) ? (((crc << 1) ^ 0x1021) & 0xFFFF) : ((crc << 1) & 0xFFFF);
            }
            return (ushort)crc;
        }

        /// <summary>Build framed packet: FLAG + escaped(cmd BE16 + len BE16 + crc BE16) + FLAG.</summary>
        public static byte[] Frame(ushort cmd, byte[]? payload = null)
        {
            payload ??= Array.Empty<byte>();
            var body = new List<byte>();
            body.Add((byte)(cmd >> 8));
            body.Add((byte)(cmd & 0xFF));
            body.Add((byte)(payload.Length >> 8));
            body.Add((byte)(payload.Length & 0xFF));
            body.AddRange(payload);
            ushort crc = Hqx(body.ToArray());
            body.Add((byte)(crc >> 8));
            body.Add((byte)(crc & 0xFF));

            var out_ = new List<byte> { FLAG };
            foreach (byte b in body)
            {
                if (b == FLAG) { out_.Add(ESC); out_.Add(ESC_FLAG); }
                else if (b == ESC) { out_.Add(ESC); out_.Add(ESC_ESC); }
                else out_.Add(b);
            }
            out_.Add(FLAG);
            return out_.ToArray();
        }

        /// <summary>BSL_CMD_NORMAL_RESET (0x0005) frame = the clean session exit.</summary>
        public static byte[] ResetFrame() => Frame(0x0005);

        public static SerialPort Open(string port, int baud, int timeoutMs = 1000)
        {
            var sp = new SerialPort(port, baud)
            {
                DataBits = 8,
                Parity = Parity.None,
                StopBits = StopBits.One,
                Handshake = Handshake.None,
                ReadTimeout = timeoutMs,
                WriteTimeout = 2000,
                DtrEnable = false,
                RtsEnable = false,
            };
            sp.Open();
            sp.DiscardInBuffer();
            sp.DiscardOutBuffer();
            return sp;
        }

        /// <summary>
        /// BROM echo probe: a fresh BootROM echoes a bare 0x7E within ms.
        /// Safe on other ports (one byte, short timeout).
        /// </summary>
        public static bool IsBromEcho(string port)
        {
            // Hardware ID first: the SPRD diag node is always 1782:4D00 in BROM,
            // and unlike the echo probe it cannot be lost to a busy device.
            if (UsbIds.IsBrom(port)) return true;
            try
            {
                using var sp = Open(port, BROM_BAUD, 500);
                sp.DiscardInBuffer();
                sp.Write(new byte[] { FLAG }, 0, 1);
                int b = sp.ReadByte();
                return b == FLAG;
            }
            catch { return false; }
        }

        /// <summary>
        /// Real liveness test: a live BootROM answers BSL_CMD_CONNECT with a
        /// 0x7E. A ghosted COM port (stale enumeration, wedged endpoint) stays
        /// silent, so this is the final authority — the registry ID is only a hint.
        /// </summary>
        public static bool ProbeAlive(string port, int timeoutMs = 500)
        {
            try
            {
                using var sp = Open(port, BROM_BAUD, timeoutMs);
                sp.DiscardInBuffer();
                byte[] f = Frame(0x0000);          // BSL_CMD_CONNECT
                sp.Write(f, 0, f.Length);
                int b = sp.ReadByte();
                return b == FLAG;
            }
            catch { return false; }
        }

        /// <summary>AT ping: "AT\r" -> reply containing OK.</summary>
        public static bool IsAtModem(string port)
        {
            // Samsung VID 04E8 modem: trust the ID, skip the slow AT handshake.
            if (UsbIds.VidPidOf(port).StartsWith("04E8:")) return true;
            foreach (int baud in new[] { MODEM_BAUD, BROM_BAUD })
            {
                try
                {
                    using var sp = Open(port, baud, 1500);
                    try { sp.DtrEnable = false; System.Threading.Thread.Sleep(200); sp.DtrEnable = true; } catch { }
                    sp.DiscardInBuffer();
                    sp.Write("AT\r");
                    var buf = new byte[256];
                    var end = DateTime.UtcNow.AddSeconds(2);
                    var sb = new System.Text.StringBuilder();
                    while (DateTime.UtcNow < end)
                    {
                        try
                        {
                            int n = sp.Read(buf, 0, buf.Length);
                            if (n > 0) sb.Append(System.Text.Encoding.ASCII.GetString(buf, 0, n));
                            if (sb.ToString().Contains("OK")) return true;
                        }
                        catch (TimeoutException) { continue; }
                    }
                }
                catch { }
            }
            return false;
        }
    }
}
