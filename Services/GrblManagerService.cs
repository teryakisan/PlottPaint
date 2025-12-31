using NVSPlotter.Properties;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NVSPlotter.Services
{
    /// <summary>
    /// Machine state information from GRBL.
    /// </summary>
    public sealed class MachineState
    {
        public double BedX { get; set; }
        public double BedY { get; set; }
        public bool BedFromGrbl { get; set; }
        public int HomingDirMask { get; set; }
        public bool HomeAtMaxX { get; set; }
        public bool HomeAtMaxY { get; set; }
        public bool IsHomed { get; set; }
    }

    /// <summary>
    /// Event args for connection state changes.
    /// </summary>
    public sealed class ConnectionStateChangedEventArgs : EventArgs
    {
        public bool IsConnected { get; }
        public string? PortName { get; }
        public int BaudRate { get; }

        public ConnectionStateChangedEventArgs(bool isConnected, string? portName = null, int baudRate = 0)
        {
            IsConnected = isConnected;
            PortName = portName;
            BaudRate = baudRate;
        }
    }

    /// <summary>
    /// Manages GRBL connection, machine state, and serial communication.
    /// </summary>
    public sealed class GrblManagerService : IDisposable
    {
        private GrblConnection? _connection;
        private CancellationTokenSource? _sendCts;
        private readonly Action<string> _log;
        private bool _disposed;

        // Machine state
        private double _bedX;
        private double _bedY;
        private bool _bedFromGrbl;
        private int _homingDirMask;
        private bool _homeAtMaxX;
        private bool _homeAtMaxY;
        private bool _isHomed;

        /// <summary>
        /// Raised when connection state changes.
        /// </summary>
        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

        /// <summary>
        /// Raised when machine state (bed size, homing) changes.
        /// </summary>
        public event EventHandler? MachineStateChanged;

        /// <summary>
        /// Gets whether the connection is open.
        /// </summary>
        public bool IsConnected => _connection?.IsOpen == true;

        /// <summary>
        /// Gets the connected port name, or null if not connected.
        /// </summary>
        public string? PortName => _connection?.PortName;

        /// <summary>
        /// Gets the connected baud rate, or 0 if not connected.
        /// </summary>
        public int BaudRate => _connection?.BaudRate ?? 0;

        /// <summary>
        /// Gets the current machine state.
        /// </summary>
        public MachineState State => new()
        {
            BedX = _bedX,
            BedY = _bedY,
            BedFromGrbl = _bedFromGrbl,
            HomingDirMask = _homingDirMask,
            HomeAtMaxX = _homeAtMaxX,
            HomeAtMaxY = _homeAtMaxY,
            IsHomed = _isHomed
        };

        /// <summary>
        /// Gets the bed X dimension in mm.
        /// </summary>
        public double BedX => _bedX;

        /// <summary>
        /// Gets the bed Y dimension in mm.
        /// </summary>
        public double BedY => _bedY;

        /// <summary>
        /// Gets whether bed size was loaded from GRBL settings.
        /// </summary>
        public bool BedFromGrbl => _bedFromGrbl;

        /// <summary>
        /// Gets whether X homes at max position.
        /// </summary>
        public bool HomeAtMaxX => _homeAtMaxX;

        /// <summary>
        /// Gets whether Y homes at max position.
        /// </summary>
        public bool HomeAtMaxY => _homeAtMaxY;

        /// <summary>
        /// Gets whether the machine has been homed.
        /// </summary>
        public bool IsHomed => _isHomed;

        /// <summary>
        /// Initializes the GRBL manager service.
        /// </summary>
        /// <param name="log">Logging callback</param>
        public GrblManagerService(Action<string> log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));

            // Initialize from settings
            _bedX = Settings.Default.bedX;
            _bedY = Settings.Default.bedY;
        }

        /// <summary>
        /// Gets available serial ports.
        /// </summary>
        public static List<string> GetAvailablePorts()
        {
            return SerialPort.GetPortNames().OrderBy(p => p).ToList();
        }

        /// <summary>
        /// Connects to a GRBL device.
        /// </summary>
        /// <param name="portName">Serial port name</param>
        /// <param name="baudRate">Baud rate</param>
        /// <param name="autoHome">Whether to auto-home after connecting</param>
        public async Task<bool> ConnectAsync(string portName, int baudRate, bool autoHome = false)
        {
            if (_connection?.IsOpen == true)
            {
                await DisconnectAsync();
            }

            try
            {
                _connection?.Dispose();
                _connection = new GrblConnection(portName, baudRate, _log);

                await _connection.OpenAsync();

                // Load settings from GRBL
                await LoadGrblSettingsAsync();

                // Auto-home if requested
                if (autoHome)
                {
                    await HomeAsync();
                }

                OnConnectionStateChanged(true);
                return true;
            }
            catch (Exception ex)
            {
                _log($"Connection failed: {ex.Message}");
                await DisconnectAsync();
                return false;
            }
        }

        /// <summary>
        /// Disconnects from the GRBL device.
        /// </summary>
        public async Task DisconnectAsync()
        {
            CancelSend();

            if (_connection != null)
            {
                try
                {
                    await _connection.CloseAsync();
                }
                catch (Exception ex)
                {
                    _log($"Error while closing port: {ex.Message}");
                }
                finally
                {
                    _connection.Dispose();
                    _connection = null;
                }

                _log("Disconnected.");
            }

            _isHomed = false;
            OnConnectionStateChanged(false);
        }

        /// <summary>
        /// Loads GRBL settings ($$ command) to get bed size and homing configuration.
        /// </summary>
        public async Task LoadGrblSettingsAsync()
        {
            if (_connection == null) return;

            try
            {
                var lines = await _connection.SendAndCollectAsync("$$", TimeSpan.FromSeconds(3));

                double bedX = _bedX;
                double bedY = _bedY;
                int homingMask = _homingDirMask;

                foreach (var line in lines)
                {
                    if (line.StartsWith("$130=") && double.TryParse(line[5..], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
                    {
                        bedX = x;
                        _bedFromGrbl = true;
                    }
                    else if (line.StartsWith("$131=") && double.TryParse(line[5..], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                    {
                        bedY = y;
                        _bedFromGrbl = true;
                    }
                    else if (line.StartsWith("$23=") && int.TryParse(line[4..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mask))
                    {
                        homingMask = mask;
                    }
                }

                _bedX = bedX;
                _bedY = bedY;
                _homingDirMask = homingMask;
                _homeAtMaxX = (_homingDirMask & 0x01) != 0;
                _homeAtMaxY = (_homingDirMask & 0x02) != 0;

                _log($"Read $$: $130={_bedX:0.###}, $131={_bedY:0.###}, $23={_homingDirMask}");
                OnMachineStateChanged();
            }
            catch (Exception ex)
            {
                _log($"Failed to query $$: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends the homing command ($H).
        /// </summary>
        public async Task<bool> HomeAsync()
        {
            if (!EnsureConnected()) return false;

            try
            {
                await _connection!.SendLineWaitOkAsync("$H", TimeSpan.FromSeconds(30), _sendCts?.Token ?? default);
                _isHomed = true;
                _log("Homing complete.");
                OnMachineStateChanged();
                return true;
            }
            catch (Exception ex)
            {
                _log($"Homing failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sends the unlock command ($X).
        /// </summary>
        public async Task<bool> UnlockAsync()
        {
            if (!EnsureConnected()) return false;

            try
            {
                await _connection!.SendLineWaitOkAsync("$X", TimeSpan.FromSeconds(5), _sendCts?.Token ?? default);
                _log("Alarm unlocked.");
                return true;
            }
            catch (Exception ex)
            {
                _log($"Unlock failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sends a soft reset (Ctrl+X).
        /// </summary>
        public async Task<bool> SoftResetAsync()
        {
            if (!EnsureConnected()) return false;

            try
            {
                await _connection!.SoftResetAsync();
                _isHomed = false;
                OnMachineStateChanged();
                return true;
            }
            catch (Exception ex)
            {
                _log($"Soft reset failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sends a jog command.
        /// </summary>
        /// <param name="axis">Axis to jog (X, Y, or Z)</param>
        /// <param name="distance">Distance to jog (positive or negative)</param>
        /// <param name="feedRate">Feed rate in mm/min</param>
        public async Task<bool> JogAsync(string axis, double distance, double feedRate = 1000)
        {
            if (!EnsureConnected()) return false;

            var cmd = $"$J=G91 {axis}{distance:0.###} F{feedRate:0}";

            try
            {
                await _connection!.SendLineWaitOkAsync(cmd, TimeSpan.FromSeconds(10));
                return true;
            }
            catch (Exception ex)
            {
                _log($"Jog failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sends G-code lines to the machine.
        /// </summary>
        /// <param name="gcode">G-code string (may contain multiple lines)</param>
        /// <param name="progress">Progress callback (line index, total lines)</param>
        public async Task<bool> SendGcodeAsync(string gcode, Action<int, int>? progress = null)
        {
            if (!EnsureConnected()) return false;

            if (!_isHomed)
            {
                _log("Warning: Machine not homed. Please home before sending G-code.");
                return false;
            }

            // Cancel any previous send
            CancelSend();

            _sendCts = new CancellationTokenSource();
            var token = _sendCts.Token;

            var lines = gcode
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith(';'))
                .ToList();

            try
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    progress?.Invoke(i, lines.Count);

                    await _connection!.SendLineWaitOkAsync(lines[i], TimeSpan.FromSeconds(120), token);
                }

                progress?.Invoke(lines.Count, lines.Count);
                _log($"G-code complete: {lines.Count} lines sent.");
                return true;
            }
            catch (OperationCanceledException)
            {
                _log("G-code send cancelled.");
                return false;
            }
            catch (Exception ex)
            {
                _log($"G-code send failed: {ex.Message}");
                return false;
            }
            finally
            {
                _sendCts?.Dispose();
                _sendCts = null;
            }
        }

        /// <summary>
        /// Cancels the current send operation and performs a soft reset.
        /// </summary>
        public async Task StopAsync()
        {
            if (!EnsureConnected()) return;

            try
            {
                CancelSend();
                await _connection!.SoftResetAsync();
                _isHomed = false;
                OnMachineStateChanged();
            }
            catch (Exception ex)
            {
                _log($"Stop failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Cancels any ongoing send operation.
        /// </summary>
        public void CancelSend()
        {
            _sendCts?.Cancel();
            _sendCts?.Dispose();
            _sendCts = null;
        }

        /// <summary>
        /// Checks if connected and logs a message if not.
        /// </summary>
        public bool EnsureConnected()
        {
            if (_connection?.IsOpen == true)
            {
                return true;
            }

            _log("Not connected to GRBL.");
            return false;
        }

        private void OnConnectionStateChanged(bool isConnected)
        {
            ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(
                isConnected,
                _connection?.PortName,
                _connection?.BaudRate ?? 0));
        }

        private void OnMachineStateChanged()
        {
            MachineStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            CancelSend();
            _connection?.Dispose();
            _connection = null;
        }
    }
}
