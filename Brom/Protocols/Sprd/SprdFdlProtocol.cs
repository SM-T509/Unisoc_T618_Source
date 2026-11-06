using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Brom.Models;
using Brom.Services;

namespace Brom.Protocols.Sprd;

public sealed class SprdFdlProtocol : IDisposable
{
    private readonly ILogSink _log;
    private readonly SerialPort _port;
    private readonly SprdFdlOptions _options;
    private readonly HdlcProtocol _hdlc;
    private readonly byte[] _readBuffer = new byte[64 * 1024];
    private bool _disposed;
    private int _dataChunkSize;
    private List<PartitionInfo> _cachedPartitions = new();

    public SprdFdlProtocol(ILogSink log, SerialPort port, SprdFdlOptions options = null)
    {
        _log = log ?? NullLogSink.Instance;
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _options = options ?? new SprdFdlOptions();
        _hdlc = new HdlcProtocol(msg => _log.LogDebug(msg));
        _hdlc.SkipRxCrcCheck = true;
        _hdlc.UseCrc16Mode = false;
        _hdlc.UseTranscode = false;
        _dataChunkSize = _options.MaxPacketSize > 0 ? Math.Min(_options.MaxPacketSize, 2112) : 2112;
        if (_dataChunkSize < 512) _dataChunkSize = 2112;
    }

    public async Task<bool> InitializeAsync(CancellationToken token = default)
    {
        _log.LogInfo("Initializing SPRD FDL protocol...");

        try { _port.DiscardInBuffer(); } catch { }

        // CONNECT
        var connectFrame = _hdlc.BuildCommand(0x00);
        await WriteFrameAsync(connectFrame, token);
        var resp = await ReadFrameAsync(3000, token);
        if (resp != null)
        {
            _log.LogDebug($"FDL init response: 0x{resp.Type:X2}");
        }

        _log.LogSuccess("FDL: protocol initialized");
        return true;
    }

    public async Task<List<PartitionInfo>> GetPartitionsAsync(CancellationToken token = default)
    {
        var partitions = new List<PartitionInfo>();

        // Method 1: READ_PARTITION command
        try
        {
            var frame = _hdlc.BuildCommand(0x2D);
            await WriteFrameAsync(frame, token);
            var response = await ReadFrameAsync(10000, token);
            if (response != null && response.Type == 0xBA && response.Payload != null)
            {
                partitions = ParsePartitionTable(response.Payload);
                _cachedPartitions = partitions;
                _log.LogSuccess($"Found {partitions.Count} partitions.");
                return partitions;
            }
            else if (response != null && response.Type == 0xFE)
            {
                _log.LogInfo("READ_PARTITION not supported, using traverse method...");
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug($"Read partition table error: {ex.Message}");
        }

        // Fallback: traverse common partitions
        partitions = await TraversePartitionsAsync(token);
        _cachedPartitions = partitions;
        _log.LogSuccess($"Found {partitions.Count} partitions.");
        return partitions;
    }

    private List<PartitionInfo> ParsePartitionTable(byte[] data)
    {
        var partitions = new List<PartitionInfo>();
        const int entrySize = 76;
        int count = data.Length / entrySize;
        for (int i = 0; i < count; i++)
        {
            int off = i * entrySize;
            string name = Encoding.Unicode.GetString(data, off, 72).TrimEnd('\0');
            if (string.IsNullOrWhiteSpace(name)) continue;
            uint size = BitConverter.ToUInt32(data, off + 72);
            partitions.Add(new PartitionInfo
            {
                Name = name,
                Lun = 0,
                LunDisplay = "0",
                StartLba = 0,
                SizeSectors = size / 512,
                SizeFormatted = FormatSize(size),
                SectorSize = 512
            });
        }
        return partitions;
    }

    private async Task<List<PartitionInfo>> TraversePartitionsAsync(CancellationToken token)
    {
        var partitions = new List<PartitionInfo>();
        string[] common = { "boot", "system", "userdata", "cache", "recovery", "misc", "vendor", "product", "dtbo", "vbmeta", "persist", "modem" };

        foreach (var name in common)
        {
            token.ThrowIfCancellationRequested();
            // Check existence by READ_START with size 0? Use erase? Instead use read start with 1 sector? We'll just add common names optimistically? 
            // Better: attempt READ_START with size 512; if ACK then partition exists.
            var payload = new byte[76];
            var nameBytes = Encoding.Unicode.GetBytes(name);
            Array.Copy(nameBytes, 0, payload, 0, Math.Min(nameBytes.Length, 72));
            // size 512
            BitConverter.GetBytes((uint)512).CopyTo(payload, 72);

            var frame = _hdlc.BuildFrame(0x10, payload); // READ_START
            await WriteFrameAsync(frame, token);
            var resp = await ReadFrameAsync(3000, token);
            if (resp != null && (resp.Type == 0x80 || resp.Type == 0x93))
            {
                partitions.Add(new PartitionInfo
                {
                    Name = name,
                    Lun = 0,
                    LunDisplay = "0",
                    StartLba = 0,
                    SizeSectors = 0,
                    SizeFormatted = "0",
                    SectorSize = 512
                });
            }
        }

        return partitions;
    }

    public async Task<bool> ReadPartitionAsync(string name, string outputPath, uint lun, CancellationToken token = default)
    {
        var part = FindPartition(name);
        if (part == null)
        {
            await GetPartitionsAsync(token);
            part = FindPartition(name);
        }
        if (part == null)
        {
            _log.LogError($"Partition not found: {name}");
            return false;
        }

        uint size = (uint)(part.SizeSectors * 512);
        if (size == 0)
        {
            _log.LogError($"Unknown size for partition: {name}");
            return false;
        }

        _log.LogInfo($"Reading partition: {name}");
        string fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? string.Empty);

        byte[] data = await ReadPartitionDataAsync(name, size, token);
        if (data == null)
        {
            _log.LogError($"Failed to read partition {name}");
            return false;
        }

        await File.WriteAllBytesAsync(fullPath, data, token);
        _log.LogSuccess($"Partition {name} read to {fullPath}");
        return true;
    }

    private async Task<byte[]> ReadPartitionDataAsync(string partitionName, uint size, CancellationToken token)
    {
        // READ_START
        bool useMode64 = (size >> 32) != 0;
        int payloadSize = useMode64 ? 80 : 76;
        var payload = new byte[payloadSize];
        var nameBytes = Encoding.Unicode.GetBytes(partitionName);
        Array.Copy(nameBytes, 0, payload, 0, Math.Min(nameBytes.Length, 72));
        BitConverter.GetBytes((uint)(size & 0xFFFFFFFF)).CopyTo(payload, 72);
        if (useMode64) BitConverter.GetBytes((uint)(size >> 32)).CopyTo(payload, 76);

        var startFrame = _hdlc.BuildFrame(0x10, payload);
        await WriteFrameAsync(startFrame, token);
        if (!await WaitAckAsync(5000, token))
        {
            _log.LogError($"Read {partitionName} start failed");
            return null;
        }

        using var ms = new MemoryStream();
        ulong offset = 0;
        uint readChunkSize = (uint)_dataChunkSize;
        int consecutiveErrors = 0;
        while (offset < size)
        {
            token.ThrowIfCancellationRequested();
            uint nowReadSize = (uint)Math.Min(readChunkSize, size - offset);
            int midstPayloadSize = useMode64 ? 12 : 8;
            var midstPayload = new byte[midstPayloadSize];
            BitConverter.GetBytes(nowReadSize).CopyTo(midstPayload, 0);
            BitConverter.GetBytes((uint)(offset & 0xFFFFFFFF)).CopyTo(midstPayload, 4);
            if (useMode64) BitConverter.GetBytes((uint)(offset >> 32)).CopyTo(midstPayload, 8);

            byte[] chunkData = null;
            for (int retry = 0; retry <= 2; retry++)
            {
                if (retry > 0)
                {
                    await Task.Delay(200, token);
                    try { _port.DiscardInBuffer(); } catch { }
                }
                var midstFrame = _hdlc.BuildFrame(0x11, midstPayload);
                await WriteFrameAsync(midstFrame, token);
                var response = await ReadFrameAsync(15000, token);
                if (response != null)
                {
                    if (response.Type == 0x93 && response.Payload != null)
                    {
                        chunkData = response.Payload;
                        break;
                    }
                    else if (response.Type == 0x80)
                    {
                        break;
                    }
                }
            }

            if (chunkData == null)
            {
                consecutiveErrors++;
                if (consecutiveErrors >= 5) break;
                offset += nowReadSize;
                continue;
            }

            consecutiveErrors = 0;
            ms.Write(chunkData, 0, chunkData.Length);
            offset += (uint)chunkData.Length;
        }

        // READ_END
        var endFrame = _hdlc.BuildCommand(0x12);
        await WriteFrameAsync(endFrame, token);
        await WaitAckAsync(3000, token);

        return ms.ToArray();
    }

    public async Task<bool> WritePartitionAsync(string name, string filePath, uint lun, CancellationToken token = default)
    {
        if (!File.Exists(filePath)) { _log.LogError($"File not found: {filePath}"); return false; }

        byte[] data = File.ReadAllBytes(filePath);
        var part = FindPartition(name);

        _log.LogInfo($"Writing partition: {name} from {Path.GetFileName(filePath)}");

        // START_DATA
        bool useMode64 = ((ulong)data.Length >> 32) != 0;
        int payloadSize = useMode64 ? 80 : 76;
        var startPayload = new byte[payloadSize];
        var nameBytes = Encoding.Unicode.GetBytes(name);
        Array.Copy(nameBytes, 0, startPayload, 0, Math.Min(nameBytes.Length, 72));
        BitConverter.GetBytes((uint)(((ulong)data.Length) & 0xFFFFFFFF)).CopyTo(startPayload, 72);
        if (useMode64) BitConverter.GetBytes((uint)(((ulong)data.Length) >> 32)).CopyTo(startPayload, 76);

        var startFrame = _hdlc.BuildFrame(0x01, startPayload);
        await WriteFrameAsync(startFrame, token);
        if (!await WaitAckAsync(5000, token))
        {
            _log.LogError($"Partition {name} START failed");
            return false;
        }

        // chunks
        int totalChunks = (data.Length + _dataChunkSize - 1) / _dataChunkSize;
        int failedChunks = 0;
        for (int i = 0; i < totalChunks; i++)
        {
            token.ThrowIfCancellationRequested();
            int offset = i * _dataChunkSize;
            int length = Math.Min(_dataChunkSize, data.Length - offset);
            var chunk = new byte[length];
            Array.Copy(data, offset, chunk, 0, length);

            var midstFrame = _hdlc.BuildFrame(0x02, chunk);
            await WriteFrameAsync(midstFrame, token);

            bool ack = await WaitAckAsync(10000, token);
            if (!ack)
            {
                failedChunks++;
                if (failedChunks >= 3) { _log.LogError($"Too many failures writing {name}"); return false; }
                continue;
            }
            failedChunks = 0;
        }

        // END_DATA
        var endFrame = _hdlc.BuildCommand(0x03);
        await WriteFrameAsync(endFrame, token);
        if (!await WaitAckAsync(10000, token))
        {
            _log.LogError($"Partition {name} END failed");
            return false;
        }

        _log.LogSuccess($"Partition {name} written successfully.");
        return true;
    }

    public async Task<bool> ErasePartitionAsync(string name, uint lun, CancellationToken token = default)
    {
        var payload = new byte[72];
        var nameBytes = Encoding.Unicode.GetBytes(name);
        Array.Copy(nameBytes, 0, payload, 0, Math.Min(nameBytes.Length, 72));

        var frame = _hdlc.BuildFrame(0x0A, payload);
        await WriteFrameAsync(frame, token);
        if (!await WaitAckAsync(60000, token))
        {
            _log.LogError($"Partition {name} erase failed");
            return false;
        }
        _log.LogSuccess($"Partition {name} erased.");
        return true;
    }

    public async Task<bool> ResetAsync(CancellationToken token = default)
    {
        var frame = _hdlc.BuildCommand(0x05);
        await WriteFrameAsync(frame, token);
        await WaitAckAsync(3000, token);
        _log.LogInfo("Device reset.");
        return true;
    }

    private PartitionInfo FindPartition(string name)
    {
        foreach (var p in _cachedPartitions)
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    private async Task<bool> WaitAckAsync(int timeoutMs, CancellationToken token)
    {
        var frame = await ReadFrameAsync(timeoutMs, token);
        if (frame == null) return false;
        return frame.Type == 0x80 || frame.Type == 0x81 || frame.Type == 0x96 || frame.Type == 0x93;
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

    private static string FormatSize(uint bytes)
    {
        if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F0} KB";
        return $"{bytes} B";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}

public sealed class SprdFdlOptions
{
    public int ResponseTimeoutMs { get; set; } = 10000;
    public int FileReceiveTimeoutMs { get; set; } = 300000;
    public int BaudRate { get; set; } = 115200;
    public int MaxPacketSize { get; set; } = 2112;
    public int AckTimeoutMs { get; set; } = 5000;
}