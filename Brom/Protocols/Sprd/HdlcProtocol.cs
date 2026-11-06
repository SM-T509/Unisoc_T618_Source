using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Brom.Protocols.Sprd;

public class HdlcProtocol
{
    public const byte HDLC_FLAG = 0x7E;
    public const byte HDLC_ESCAPE = 0x7D;
    public const byte HDLC_ESCAPE_XOR = 0x20;

    private readonly Action<string> _log;

    public bool SkipRxCrcCheck { get; set; } = true;
    public bool UseCrc16Mode { get; set; } = true;
    public bool UseTranscode { get; set; } = true;

    public HdlcProtocol(Action<string> log = null)
    {
        _log = log;
    }

    public void SetFdlMode() { UseCrc16Mode = false; _log?.Invoke("[HDLC] Switched to FDL mode (Checksum)"); }
    public void SetBromMode() { UseCrc16Mode = true; _log?.Invoke("[HDLC] Switched to BROM mode (CRC16)"); }
    public void DisableTranscode() { UseTranscode = false; _log?.Invoke("[HDLC] Transcoding disabled"); }
    public void EnableTranscode() { UseTranscode = true; _log?.Invoke("[HDLC] Transcoding enabled"); }

    public byte[] BuildFrame(byte type, byte[] payload)
    {
        if (payload == null) payload = Array.Empty<byte>();

        using var ms = new MemoryStream();
        ms.WriteByte(HDLC_FLAG);

        var data = new List<byte>();
        data.Add(0x00);
        data.Add(type);

        ushort length = (ushort)payload.Length;
        data.Add((byte)((length >> 8) & 0xFF));
        data.Add((byte)(length & 0xFF));

        if (payload.Length > 0) data.AddRange(payload);

        ushort checksum = UseCrc16Mode ? CalculateCRC16Ccitt(data.ToArray()) : CalculateSprdChecksum(data.ToArray());

        data.Add((byte)((checksum >> 8) & 0xFF));
        data.Add((byte)(checksum & 0xFF));

        foreach (byte b in data)
        {
            if (b == HDLC_FLAG || b == HDLC_ESCAPE)
            {
                ms.WriteByte(HDLC_ESCAPE);
                ms.WriteByte((byte)(b ^ HDLC_ESCAPE_XOR));
            }
            else
            {
                ms.WriteByte(b);
            }
        }

        ms.WriteByte(HDLC_FLAG);
        return ms.ToArray();
    }

    public byte[] BuildCommand(byte type) => BuildFrame(type, null);

    public bool TryParseFrame(byte[] frame, out HdlcFrame result, out HdlcParseError error)
    {
        result = null;
        error = HdlcParseError.None;

        if (frame == null || frame.Length < 7)
        {
            error = HdlcParseError.FrameTooShort;
            return false;
        }

        if (frame[0] != HDLC_FLAG || frame[frame.Length - 1] != HDLC_FLAG)
        {
            error = HdlcParseError.InvalidDelimiter;
            return false;
        }

        var data = new List<byte>();
        bool escaped = false;
        for (int i = 1; i < frame.Length - 1; i++)
        {
            if (escaped)
            {
                data.Add((byte)(frame[i] ^ HDLC_ESCAPE_XOR));
                escaped = false;
            }
            else if (frame[i] == HDLC_ESCAPE)
            {
                escaped = true;
            }
            else
            {
                data.Add(frame[i]);
            }
        }

        if (data.Count < 6)
        {
            error = HdlcParseError.FrameIncomplete;
            return false;
        }

        byte type = data[1];
        ushort length = (ushort)((data[2] << 8) | data[3]);

        if (data.Count < 4 + length + 2)
        {
            error = HdlcParseError.PayloadMismatch;
            return false;
        }

        byte[] payload = new byte[length];
        for (int i = 0; i < length; i++) payload[i] = data[4 + i];

        int crcOffset = 4 + length;
        ushort receivedCrc = (ushort)((data[crcOffset] << 8) | data[crcOffset + 1]);

        if (!SkipRxCrcCheck)
        {
            byte[] crcData = data.GetRange(0, crcOffset).ToArray();
            ushort calculated = UseCrc16Mode ? CalculateCRC16Ccitt(crcData) : CalculateSprdChecksum(crcData);
            if (receivedCrc != calculated)
            {
                ushort alternative = UseCrc16Mode ? CalculateSprdChecksum(crcData) : CalculateCRC16Ccitt(crcData);
                if (receivedCrc == alternative)
                {
                    UseCrc16Mode = !UseCrc16Mode;
                }
                else
                {
                    error = HdlcParseError.CrcMismatch;
                    return false;
                }
            }
        }

        result = new HdlcFrame { Type = type, Length = length, Payload = payload, Crc = receivedCrc };
        return true;
    }

    public HdlcFrame ParseFrame(byte[] frame)
    {
        if (!TryParseFrame(frame, out var result, out var error))
            throw new InvalidDataException(error.ToString());
        return result;
    }

    public bool TryExtractFrame(byte[] buffer, int length, out byte[] frame, out int consumed)
    {
        frame = null;
        consumed = 0;

        int startIndex = -1;
        for (int i = 0; i < length; i++)
        {
            if (buffer[i] == HDLC_FLAG) { startIndex = i; break; }
        }
        if (startIndex < 0) return false;

        for (int i = startIndex + 1; i < length; i++)
        {
            if (buffer[i] == HDLC_FLAG)
            {
                int frameLength = i - startIndex + 1;
                frame = new byte[frameLength];
                Array.Copy(buffer, startIndex, frame, 0, frameLength);
                consumed = i + 1;
                return true;
            }
        }
        return false;
    }

    public ushort CalculateCRC16Ccitt(byte[] data)
    {
        uint crc = 0;
        foreach (byte b in data)
        {
            crc ^= (uint)(b << 8);
            for (int i = 0; i < 8; i++)
            {
                if ((crc & 0x8000) != 0) crc = (crc << 1) ^ 0x11021;
                else crc <<= 1;
            }
        }
        return (ushort)(crc & 0xFFFF);
    }

    public ushort CalculateSprdChecksum(byte[] data)
    {
        uint ctr = 0;
        int len = data.Length;
        int i = 0;
        while (len > 1)
        {
            ctr += (uint)(data[i] | (data[i + 1] << 8));
            i += 2;
            len -= 2;
        }
        if (len > 0) ctr += data[i];
        ctr = (ctr >> 16) + (ctr & 0xFFFF);
        ctr = ~(ctr + (ctr >> 16)) & 0xFFFF;
        return (ushort)((ctr >> 8) | ((ctr & 0xFF) << 8));
    }
}

public class HdlcFrame
{
    public byte Type { get; set; }
    public ushort Length { get; set; }
    public byte[] Payload { get; set; }
    public ushort Crc { get; set; }
}

public enum HdlcParseError
{
    None, FrameTooShort, InvalidDelimiter, FrameIncomplete, PayloadMismatch, CrcMismatch
}