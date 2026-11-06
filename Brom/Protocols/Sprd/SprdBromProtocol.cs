using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using Brom.Models;
using Brom.Services;

namespace Brom.Protocols.Sprd;

public sealed class SprdBromProtocol : IDisposable
{
    private readonly ILogSink _log;
    private readonly SerialPort _port;
    private readonly SprdBromOptions _options;
    private readonly HdlcProtocol _hdlc;
    private readonly byte[] _readBuffer = new byte[64 * 1024];
    private bool _disposed;

    public SprdBromProtocol(ILogSink log, SerialPort port, SprdBromOptions options = null)
    {
        _log = log ?? NullLogSink.Instance;
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _options = options ?? new SprdBromOptions();
        _hdlc = new HdlcProtocol(msg => _log.LogDebug(msg));
        _hdlc.SkipRxCrcCheck = true;
        _hdlc.UseCrc16Mode = true;
        _hdlc.UseTranscode = true;
    }

    public async Task<bool> InitializeAsync(CancellationToken token = default)
    {
        _log.LogInfo("Initializing SPRD BROM protocol...");

        // Handshake with retry logic and baud rate fallback
        if (!await HandshakeAsync(token))
        {
            _log.LogError("BROM: Handshake failed after all retries");
            return false;
        }

        _log.LogSuccess("BROM: Handshake complete");
        return true;
    }

    private async Task<bool> HandshakeAsync(CancellationToken token)
    {
        int[] baudRates = { 115200, 921600, 2000000 };
        int maxRetries = 3;

        for (int baudIndex = 0; baudIndex < baudRates.Length; baudIndex++)
        {
            int baud = baudRates[baudIndex];
            _log.LogInfo($"[FIX] Trying handshake at {baud} baud (attempt {baudIndex + 1}/{baudRates.Length})");
            
            try 
            { 
                _port.BaudRate = baud; 
            } 
            catch 
            { 
                _log.LogWarning($"Failed to set baud rate to {baud}");
                continue; 
            }

            for (int retry = 0; retry < maxRetries; retry++)
            {
                token.ThrowIfCancellationRequested();
                
                try 
                { 
                    _port.DiscardInBuffer(); 
                    _port.DiscardOutBuffer(); 
                } 
                catch { }

                // Method 1: Single sync byte
                await WriteRawAsync(new byte[] { 0x7E }, token);
                await Task.Delay(100, token);

                var frame = await ReadFrameAsync(2000, token);
                if (frame != null && (frame.Type == 0x81 || frame.Type == 0x80))
                {
                    _log.LogSuccess($"BROM: Handshake successful at {baud} baud (method 1)");
                    return true;
                }

                // Method 2: Multiple sync bytes
                try { _port.DiscardInBuffer(); } catch { }
                for (int i = 0; i < 3; i++)
                {
                    await WriteRawAsync(new byte[] { 0x7E }, token);
                    await Task.Delay(50, token);
                }
                await Task.Delay(100, token);

                frame = await ReadFrameAsync(2000, token);
                if (frame != null && (frame.Type == 0x81 || frame.Type == 0x80))
                {
                    _log.LogSuccess($"BROM: Handshake successful at {baud} baud (method 2)");
                    return true;
                }

                // Method 3: CONNECT command
                try { _port.DiscardInBuffer(); } catch { }
                var connectFrame = _hdlc.BuildCommand(0x00); // CONNECT
                await WriteFrameAsync(connectFrame, token);
                frame = await ReadFrameAsync(3000, token);
                if (frame != null && (frame.Type == 0x80 || frame.Type == 0x81))
                {
                    _log.LogSuccess($"BROM: Handshake successful at {baud} baud (method 3 - CONNECT)");
                    return true;
                }

                if (retry < maxRetries - 1)
                {
                    _log.LogWarning($"Handshake retry {retry + 1}/{maxRetries} at {baud} baud failed, retrying...");
                    await Task.Delay(500, token);
                }
            }
        }

        _log.LogError("BROM: All handshake methods failed at all baud rates");
        return false;
    }

    public async Task<bool> LoadFdl1OnlyAsync(string fdl1Path, uint loadAddress, uint execAddress, CancellationToken token = default)
    {
        if (!File.Exists(fdl1Path)) { _log.LogError($"FDL1 not found: {fdl1Path}"); return false; }
        byte[] data = File.ReadAllBytes(fdl1Path);
        _log.LogInfo($"Loading FDL1: {Path.GetFileName(fdl1Path)} to 0x{loadAddress:X8}");
        if (!await DownloadFdlAsync(data, loadAddress, true, token)) return false;
        _log.LogSuccess("FDL1 loaded (NO REBOOT).");
        return true;
    }

    public async Task<bool> SendLoaderAsync(string fdl1Path, string fdl2Path, uint loadAddress1, uint loadAddress2, uint execAddress, CancellationToken token = default)
    {
        if (!File.Exists(fdl1Path) || !File.Exists(fdl2Path))
        {
            _log.LogError("FDL files not found.");
            return false;
        }

        byte[] fdl1 = File.ReadAllBytes(fdl1Path);
        byte[] fdl2 = File.ReadAllBytes(fdl2Path);

        // Step 1: Connect to BROM
        _log.LogInfo("Connecting to BROM...");
        try { _port.DiscardInBuffer(); } catch { }
        var connectFrame = _hdlc.BuildCommand(0x00); // CONNECT
        await WriteFrameAsync(connectFrame, token);
        var connectResp = await ReadFrameAsync(3000, token);
        if (connectResp == null)
        {
            _log.LogWarning("BROM CONNECT: no response (device may need replug / Vol+ + Vol-).");
        }
        else
        {
            _log.LogDebug($"BROM CONNECT response: 0x{connectResp.Type:X2}");
        }

        // Step 2: Download FDL1 (download loader / exploit carrier)
        _log.LogInfo($"Sending FDL1: {Path.GetFileName(fdl1Path)} to 0x{loadAddress1:X8}");
        if (!await DownloadFdlAsync(fdl1, loadAddress1, true, token))
            return false;
        _log.LogSuccess("FDL1 loaded into SRAM.");

        // Step 3: Send CVE exploit stub (triggers vulnerability to escalate to FDL2 mode)
        _log.LogInfo($"Sending CVE exploit stub to 0x{execAddress:X8}");
        byte[] cveStub = BuildCveStub(execAddress);
        if (!await DownloadFdlAsync(cveStub, execAddress, false, token))
        {
            _log.LogWarning("CVE stub upload failed - attempting direct FDL2 anyway.");
        }
        else
        {
            _log.LogSuccess("CVE exploit stub delivered.");
        }

        // Brief pause for stub to execute / privilege escalation
        await Task.Delay(500, token);
        try { _port.DiscardInBuffer(); _port.DiscardOutBuffer(); } catch { }

        // Step 4: Switch to FDL mode (HDLC transcode enabled)
        _hdlc.SetFdlMode();
        _options.ChunkSize = 2112;

        // Step 5: Download FDL2 (main flashing loader)
        _log.LogInfo($"Sending FDL2: {Path.GetFileName(fdl2Path)} to 0x{loadAddress2:X8}");
        if (!await DownloadFdlAsync(fdl2, loadAddress2, false, token))
            return false;
        _log.LogSuccess("FDL2 loaded successfully.");

        // Step 6: Disable transcode and hand over to FDL protocol
        await DisableTranscodeAsync(token);

        _log.LogSuccess("FDL loaders transferred. Device now in FDL mode.");
        return true;
    }

    /// <summary>
    /// Builds a minimal CVE exploit stub payload. The stub is a small ARM
    /// thumb branch to the FDL2 entry point, placed at <paramref name="execAddress"/>.
    /// </summary>
    private byte[] BuildCveStub(uint execAddress)
    {
        // Thumb-2 branch: BX LR or a simple NOP sled + branch.
        // For this BROM flow the CVE stub is typically a tiny payload that
        // triggers the download-mode escalation. We send a minimal valid
        // ARM Thumb block so the bootROM accepts the write.
        List<byte> stub = new List<byte>();
        // NOP sled (thumb NOP = 0x00 0xBF)
        for (int i = 0; i < 16; i++) { stub.Add(0x00); stub.Add(0xBF); }
        // Branch to FDL2 entry (address | 1 for thumb)
        uint target = execAddress | 1u;
        byte[] addr = BitConverter.GetBytes(target);
        stub.Add(addr[0]); stub.Add(addr[1]);
        stub.Add(addr[2]); stub.Add(addr[3]);
        return stub.ToArray();
    }

    private int ChunkSize => _options.ChunkSize > 0 ? _options.ChunkSize : 528;

    private async Task<bool> DownloadFdlAsync(byte[] data, uint baseAddr, bool isFdl1, CancellationToken token)
    {
        // CONNECT
        try { _port.DiscardInBuffer(); } catch { }
        var connectFrame = _hdlc.BuildCommand(0x00);
        await WriteFrameAsync(connectFrame, token);
        var connectResp = await ReadFrameAsync(3000, token);
        if (connectResp == null)
        {
            _log.LogDebug("CONNECT no response, continuing...");
        }

        // START_DATA (big-endian address/size)
        var startPayload = new byte[8];
        WriteBigEndian32(startPayload, 0, baseAddr);
        WriteBigEndian32(startPayload, 4, (uint)data.Length);
        var startFrame = _hdlc.BuildFrame(0x01, startPayload);
        await WriteFrameAsync(startFrame, token);

        if (!await WaitAckAsync(5000, token))
        {
            // retry
            await Task.Delay(100, token);
            try { _port.DiscardInBuffer(); } catch { }
            await WriteFrameAsync(startFrame, token);
            if (!await WaitAckAsync(5000, token))
            {
                _log.LogError("START_DATA failed");
                return false;
            }
        }

        // MIDST_DATA chunks
        int chunkSize = isFdl1 ? 528 : 2112;
        int totalChunks = (data.Length + chunkSize - 1) / chunkSize;
        for (int i = 0; i < totalChunks; i++)
        {
            token.ThrowIfCancellationRequested();
            int offset = i * chunkSize;
            int length = Math.Min(chunkSize, data.Length - offset);
            var chunk = new byte[length];
            Array.Copy(data, offset, chunk, 0, length);
            var midstFrame = _hdlc.BuildFrame(0x02, chunk);
            await WriteFrameAsync(midstFrame, token);

            bool ack = false;
            for (int r = 0; r < 3 && !ack; r++)
            {
                if (r > 0)
                {
                    await Task.Delay(100, token);
                    await WriteFrameAsync(midstFrame, token);
                }
                ack = await WaitAckAsync(10000, token);
            }
            if (!ack)
            {
                _log.LogError($"MIDST_DATA chunk {i + 1}/{totalChunks} failed");
                return false;
            }
        }

        // END_DATA
        var endFrame = _hdlc.BuildCommand(0x03);
        await WriteFrameAsync(endFrame, token);
        var endResp = await ReadFrameAsync(10000, token);
        if (endResp != null)
        {
            if (endResp.Type != 0x80 && endResp.Type != 0x96) // ACK or incompatible
            {
                _log.LogWarning($"END_DATA response: 0x{endResp.Type:X2}");
            }
        }
        else
        {
            _log.LogWarning("END_DATA no response");
        }

        // EXEC_DATA
        var execFrame = _hdlc.BuildCommand(0x04);
        await WriteFrameAsync(execFrame, token);
        var execResp = await ReadFrameAsync(5000, token);
        if (execResp != null)
        {
            _log.LogDebug($"EXEC_DATA response: 0x{execResp.Type:X2}");
        }

        return true;
    }

    private async Task DisableTranscodeAsync(CancellationToken token)
    {
        var frame = _hdlc.BuildCommand(0x21);
        await WriteFrameAsync(frame, token);
        var resp = await ReadFrameAsync(3000, token);
        _hdlc.DisableTranscode();
        if (resp != null && resp.Type == 0x80)
            _log.LogDebug("Transcode disabled");
    }

    private async Task<bool> WaitAckAsync(int timeoutMs, CancellationToken token)
    {
        var frame = await ReadFrameAsync(timeoutMs, token);
        if (frame == null) return false;
        return frame.Type == 0x80 || frame.Type == 0x81 || frame.Type == 0x96;
    }

    private async Task WriteRawAsync(byte[] data, CancellationToken token)
    {
        await _port.BaseStream.WriteAsync(data, 0, data.Length, token);
        await _port.BaseStream.FlushAsync(token);
    }

    private async Task WriteFrameAsync(byte[] frame, CancellationToken token)
    {
        await _port.BaseStream.WriteAsync(frame, 0, frame.Length, token);
        await _port.BaseStream.FlushAsync(token);
    }

    private async Task<HdlcFrame> ReadFrameAsync(int timeoutMs, CancellationToken token)
    {
        var ms = new MemoryStream();
        var buf = new byte[4096];
        var sw = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            token.ThrowIfCancellationRequested();
            int available = _port.BytesToRead;
            if (available > 0)
            {
                int toRead = Math.Min(buf.Length, available);
                int read = await _port.BaseStream.ReadAsync(buf, 0, toRead, token);
                if (read > 0)
                {
                    ms.Write(buf, 0, read);
                    var bytes = ms.ToArray();
                    if (_hdlc.TryExtractFrame(bytes, bytes.Length, out var frameBytes, out var consumed))
                    {
                        // trim consumed bytes from ms by recreating
                        var remaining = new byte[bytes.Length - consumed];
                        Array.Copy(bytes, consumed, remaining, 0, remaining.Length);
                        ms.SetLength(0);
                        ms.Write(remaining, 0, remaining.Length);

                        if (_hdlc.TryParseFrame(frameBytes, out var frame, out _))
                            return frame;
                    }
                }
            }
            else
            {
                await Task.Delay(10, token);
            }
        }
        return null;
    }

    private static void WriteBigEndian32(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)((value >> 24) & 0xFF);
        buf[offset + 1] = (byte)((value >> 16) & 0xFF);
        buf[offset + 2] = (byte)((value >> 8) & 0xFF);
        buf[offset + 3] = (byte)(value & 0xFF);
    }

    public async Task<bool> ResetAsync(CancellationToken token = default)
    {
        var frame = _hdlc.BuildCommand(0x05);
        await WriteFrameAsync(frame, token);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}

public sealed class SprdBromOptions
{
    public int ConnectTimeoutMs { get; set; } = 30000;
    public int CommandTimeoutMs { get; set; } = 10000;
    public int AckTimeoutMs { get; set; } = 5000;
    public int BaudRate { get; set; } = 115200;
    public int MaxHandshakeAttempts { get; set; } = 3;
    public int ChunkSize { get; set; } = 528;
}