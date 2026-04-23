using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NVSPlotter.Services;

public sealed class GrblConnection : IDisposable
{
    private readonly SerialPort _port;
    private readonly Action<string> _log;

    private readonly object _rxLock = new();
    private readonly StringBuilder _rx = new(8192);
    private readonly Queue<string> _lines = new();
    private readonly SemaphoreSlim _lineAvailable = new(0, int.MaxValue);

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public string PortName => _port.PortName;
    public int BaudRate => _port.BaudRate;
    public bool IsOpen => _port.IsOpen;

    public GrblConnection(string portName, int baudRate, Action<string> log)
    {
        _log = log;
        _port = new SerialPort(portName, baudRate)
        {
            NewLine = "\n",
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = 5000,
            WriteTimeout = 5000
        };
        _port.DataReceived += Port_DataReceived;
    }

    public async Task OpenAsync()
    {
        _port.Open();
        _port.Write("\r\n\r\n");
        await Task.Delay(200);
        try { _port.DiscardInBuffer(); } catch { }
        _log($"Opened {_port.PortName} @ {_port.BaudRate}");
    }

    public Task CloseAsync()
    {
        try
        {
            if (_port.IsOpen) _port.Close();
        }
        catch { }
        return Task.CompletedTask;
    }

    public async Task SoftResetAsync()
    {
        if (!_port.IsOpen) return;
        _port.Write([(char)0x18], 0, 1);
        await Task.Delay(200);
        _log("Sent Ctrl+X (soft reset).");
    }

    public async Task SendLineWaitOkAsync(string line, TimeSpan timeout, CancellationToken ct = default)
    {
        _ = await SendAndCollectAsync(line, timeout, ct);
    }

    public async Task<List<string>> SendAndCollectAsync(string line, TimeSpan timeout, CancellationToken ct = default)
    {
        line = line.Trim();
        if (line.Length == 0) return [];

        var collected = new List<string>();

        await _sendLock.WaitAsync(ct);
        try
        {
            _log($"> {line}");
            _port.Write(line + "\n");

            var deadline = DateTime.UtcNow + timeout;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"Timeout waiting for ok after: {line}");

                var resp = await ReadLineAsync(ct);
                if (resp == null) continue;

                resp = resp.Trim();
                if (resp.Length == 0) continue;

                _log($"< {resp}");

                if (resp.Equals("ok", StringComparison.OrdinalIgnoreCase))
                    return collected;

                if (resp.StartsWith("error", StringComparison.OrdinalIgnoreCase) ||
                    resp.StartsWith("alarm", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("GRBL: " + resp);

                collected.Add(resp);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        lock (_rxLock)
        {
            if (_lines.Count > 0)
                return _lines.Dequeue();
        }

        if (!await _lineAvailable.WaitAsync(TimeSpan.FromSeconds(5), ct))
            return null;

        lock (_rxLock)
        {
            return _lines.Count > 0 ? _lines.Dequeue() : null;
        }
    }

    private void Port_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            var s = _port.ReadExisting();
            if (string.IsNullOrEmpty(s)) return;

            int enqueued = 0;
            lock (_rxLock)
            {
                _rx.Append(s);

                while (true)
                {
                    var str = _rx.ToString();
                    var idx = str.IndexOf('\n');
                    if (idx < 0) break;

                    var line = str[..idx].Trim('\r');
                    _lines.Enqueue(line);
                    enqueued++;

                    _rx.Clear();
                    _rx.Append(str[(idx + 1)..]);
                }
            }

            if (enqueued > 0) _lineAvailable.Release(enqueued);
        }
        catch
        {
            // ignore read errors during close/reset
        }
    }

    public void Dispose()
    {
        try { _port.DataReceived -= Port_DataReceived; } catch { }
        try { if (_port.IsOpen) _port.Close(); } catch { }
        _port.Dispose();
        _sendLock.Dispose();
        _lineAvailable.Dispose();
    }
}
