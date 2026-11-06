using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using Brom.Services;

namespace Brom.Core;

public sealed class UsbTransport : IDisposable
{
    private readonly ILogSink _log;
    private readonly SerialPort _port;
    private readonly byte[] _readBuffer = new byte[64 * 1024];
    private readonly object _lock = new();
    private bool _disposed;

    public string PortName => _port.PortName;
    public int BaudRate => _port.BaudRate;
    public bool IsOpen => _port.IsOpen;
    public SerialPort UnderlyingPort => _port;

    public event Action<byte[]> DataReceived;

    public UsbTransport(ILogSink log, string portName, int baudRate = 115200,
        int readTimeoutMs = 5000, int writeTimeoutMs = 5000,
        bool dtrEnable = false, bool rtsEnable = false)
    {
        _log = log ?? NullLogSink.Instance;
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = readTimeoutMs,
            WriteTimeout = writeTimeoutMs,
            DtrEnable = dtrEnable,
            RtsEnable = rtsEnable,
            Handshake = Handshake.None,
            Encoding = System.Text.Encoding.ASCII,
            ReceivedBytesThreshold = 1,
            // Windows defaults are 4 KB, which throttles a 700 KB loader upload to
            // one buffer refill per few packets. 1 MB each way removes that stall.
            ReadBufferSize = 1024 * 1024,
            WriteBufferSize = 1024 * 1024
        };
    }

    public void Open()
    {
        lock (_lock)
        {
            if (_port.IsOpen)
                return;
            _port.Open();
            _log.LogDebug($"Transport opened: {_port.PortName}@{_port.BaudRate}");
        }
    }

    public void Close()
    {
        lock (_lock)
        {
            try
            {
                if (_port.IsOpen)
                    _port.Close();
            }
            catch { }
            _log.LogDebug($"Transport closed: {_port.PortName}");
        }
    }

    public Task<bool> WriteAsync(byte[] data, CancellationToken token = default)
    {
        if (data == null || data.Length == 0)
            return Task.FromResult(true);

        lock (_lock)
        {
            try
            {
                if (!_port.IsOpen)
                    return Task.FromResult(false);
                _port.BaseStream.Write(data, 0, data.Length);
                _port.BaseStream.Flush();
                _log.LogDebug($"Transport write: {data.Length} bytes to {_port.PortName}");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _log.LogError($"Transport write error on {_port.PortName}: {ex.Message}");
                return Task.FromResult(false);
            }
        }
    }

    public Task<bool> WriteByteAsync(byte value, CancellationToken token = default)
    {
        lock (_lock)
        {
            try
            {
                if (!_port.IsOpen)
                    return Task.FromResult(false);
                _port.BaseStream.WriteByte(value);
                _port.BaseStream.Flush();
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _log.LogError($"Transport write byte error: {ex.Message}");
                return Task.FromResult(false);
            }
        }
    }

    public Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token = default)
    {
        lock (_lock)
        {
            try
            {
                if (!_port.IsOpen)
                    return Task.FromResult(0);
                int read = _port.BaseStream.Read(buffer, offset, count);
                if (read > 0)
                {
                    DataReceived?.Invoke(buffer.AsSpan(offset, read).ToArray());
                }
                return Task.FromResult(read);
            }
            catch (Exception ex)
            {
                _log.LogError($"Transport read error: {ex.Message}");
                return Task.FromResult(0);
            }
        }
    }

    public async Task<byte[]> ReadExactAsync(int count, CancellationToken token = default)
    {
        byte[] buffer = new byte[count];
        int totalRead = 0;
        while (totalRead < count)
        {
            token.ThrowIfCancellationRequested();
            int read = await ReadAsync(buffer, totalRead, count - totalRead, token);
            if (read == 0)
                throw new EndOfStreamException($"Transport disconnected: expected {count} bytes, read {totalRead}");
            totalRead += read;
        }
        return buffer;
    }

    public async Task<byte[]> ReadResponseAsync(int expectedLength, int timeoutMs, CancellationToken token = default)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        using var ms = new MemoryStream();

        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();

            int available = _port.BytesToRead;
            if (available > 0)
            {
                int toRead = Math.Min(available, _readBuffer.Length);
                int read = await ReadAsync(_readBuffer, 0, toRead, token);
                if (read > 0)
                    ms.Write(_readBuffer, 0, read);

                if (ms.Length >= expectedLength)
                    return ms.ToArray();
            }

            await Task.Delay(10, token);
        }

        return ms.Length > 0 ? ms.ToArray() : null;
    }

    public async Task<bool> WaitForByteAsync(byte expected, int timeoutMs, CancellationToken token = default)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (_port.BytesToRead > 0)
            {
                int b = _port.ReadByte();
                if (b == expected)
                    return true;
            }
            await Task.Delay(10, token);
        }
        return false;
    }

    public void DiscardBuffers()
    {
        lock (_lock)
        {
            try
            {
                if (_port.IsOpen)
                {
                    _port.DiscardInBuffer();
                    _port.DiscardOutBuffer();
                }
            }
            catch { }
        }
    }

    public void SetBaudRate(int baudRate)
    {
        lock (_lock)
        {
            try
            {
                _port.BaudRate = baudRate;
            }
            catch { }
        }
    }

    public void SetControlLines(bool dtr, bool rts)
    {
        lock (_lock)
        {
            try
            {
                _port.DtrEnable = dtr;
                _port.RtsEnable = rts;
            }
            catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Close();
        try { _port.Dispose(); } catch { }
    }
}
