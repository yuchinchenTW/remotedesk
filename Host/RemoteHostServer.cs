using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;
using SimpleRemote.Shared;

namespace SimpleRemote.Host
{
    internal sealed class RemoteHostServer : IDisposable
    {
        private readonly Action<string> _statusCallback;
        private readonly Action<string> _clientCallback;
        private readonly DisplaySelectionState _displaySelection = new DisplaySelectionState();

        private TcpListener _listener;
        private Thread _acceptThread;
        private TcpClient _activeClient;
        private CancellationTokenSource _sessionTokenSource;
        private HostDiscoveryBroadcaster _discoveryBroadcaster;
        private volatile bool _running;
        private string _displayName;
        private string _password;
        private int _port;

        public RemoteHostServer(Action<string> statusCallback, Action<string> clientCallback)
        {
            _statusCallback = statusCallback;
            _clientCallback = clientCallback;
        }

        public bool IsRunning
        {
            get { return _running; }
        }

        public void Start(int port, string password, string displayName)
        {
            if (_running)
            {
                return;
            }

            _password = password ?? string.Empty;
            _port = port;
            _displayName = displayName ?? string.Empty;
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _discoveryBroadcaster = new HostDiscoveryBroadcaster();
            _discoveryBroadcaster.Start(port, _displayName);
            _running = true;
            _acceptThread = new Thread(AcceptLoop);
            _acceptThread.IsBackground = true;
            _acceptThread.Start();
            _statusCallback("Listening on port " + port + ".");
        }

        public void Stop()
        {
            _running = false;

            if (_sessionTokenSource != null)
            {
                _sessionTokenSource.Cancel();
            }

            if (_discoveryBroadcaster != null)
            {
                _discoveryBroadcaster.Dispose();
                _discoveryBroadcaster = null;
            }

            if (_activeClient != null)
            {
                try
                {
                    _activeClient.Close();
                }
                catch
                {
                }
            }

            if (_listener != null)
            {
                try
                {
                    _listener.Stop();
                }
                catch
                {
                }
            }

            _clientCallback("No viewer connected.");
            _statusCallback("Stopped.");
        }

        public void Dispose()
        {
            Stop();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client = null;

                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    if (!_running)
                    {
                        return;
                    }

                    _statusCallback("Socket error while listening. Retrying...");
                    Thread.Sleep(500);
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                try
                {
                    HandleClient(client);
                }
                catch (Exception ex)
                {
                    _statusCallback("Client session ended: " + ex.Message);
                }
                finally
                {
                    if (client != null)
                    {
                        try
                        {
                            client.Close();
                        }
                        catch
                        {
                        }
                    }

                    _activeClient = null;

                    if (_sessionTokenSource != null)
                    {
                        _sessionTokenSource.Dispose();
                        _sessionTokenSource = null;
                    }

                    _clientCallback("No viewer connected.");

                    if (_running)
                    {
                        _statusCallback("Listening on port " + _port + ".");
                    }
                }
            }
        }

        private void HandleClient(TcpClient client)
        {
            client.NoDelay = true;
            _activeClient = client;
            _clientCallback("Viewer connected from " + client.Client.RemoteEndPoint + ".");

            using (var stream = client.GetStream())
            {
                var writeSync = new object();
                var auth = Protocol.ReceiveMessage(stream);

                if (auth.Type != MessageType.AuthRequest)
                {
                    throw new InvalidDataException("Expected auth request.");
                }

                string providedPassword;

                using (var reader = Protocol.CreateReader(auth.Payload))
                {
                    providedPassword = reader.ReadString();
                }

                var isPasswordValid = string.Equals(providedPassword, _password, StringComparison.Ordinal);

                Protocol.SendMessage(stream, writeSync, MessageType.AuthResponse, delegate(BinaryWriter writer)
                {
                    writer.Write(isPasswordValid);
                    writer.Write(isPasswordValid ? "Connected." : "Password mismatch.");
                });

                if (!isPasswordValid)
                {
                    _statusCallback("Rejected viewer from " + client.Client.RemoteEndPoint + ".");
                    return;
                }

                _statusCallback("Streaming virtual desktop to " + client.Client.RemoteEndPoint + ".");

                _sessionTokenSource = new CancellationTokenSource();
                var senderThread = new Thread(delegate()
                {
                    try
                    {
                        using (var ffmpegStreamer = FfmpegVideoStreamer.TryCreate(30))
                        {
                            if (ffmpegStreamer != null)
                            {
                                var desktopBounds = Screen.PrimaryScreen.Bounds;
                                Protocol.SendMessage(stream, writeSync, MessageType.VideoConfig, delegate(BinaryWriter writer)
                                {
                                    writer.Write(desktopBounds.Width);
                                    writer.Write(desktopBounds.Height);
                                });
                                _statusCallback("Streaming H.264 video over ffmpeg.");
                                ffmpegStreamer.Stream(stream, writeSync, _sessionTokenSource.Token);
                                return;
                            }
                        }

                        _statusCallback(Screen.AllScreens.Length == 1
                            ? "ffmpeg unavailable, falling back to JPEG streaming."
                            : "Multi-monitor host detected, falling back to JPEG streaming.");
                        ScreenStreamer.StreamVirtualDesktop(stream, writeSync, _sessionTokenSource.Token, 20, _displaySelection.GetCaptureBounds);
                    }
                    catch (Exception ex)
                    {
                        _statusCallback("Video stream stopped: " + ex.Message);
                    }
                });

                senderThread.IsBackground = true;
                senderThread.Start();

                try
                {
                    ReceiveInputs(stream);
                }
                finally
                {
                    _sessionTokenSource.Cancel();
                    senderThread.Join(1000);
                }
            }
        }

        private void ReceiveInputs(NetworkStream stream)
        {
            while (_running)
            {
                var message = Protocol.ReceiveMessage(stream);

                if (message.Type != MessageType.Input)
                {
                    continue;
                }

                using (var reader = Protocol.CreateReader(message.Payload))
                {
                    var command = (InputCommandType)reader.ReadByte();

                    switch (command)
                    {
                        case InputCommandType.MouseMove:
                            MoveMouseRelativeToSelection(reader.ReadInt32(), reader.ReadInt32());
                            break;

                        case InputCommandType.MouseDown:
                            MoveMouseRelativeToSelection(reader.ReadInt32(), reader.ReadInt32());
                            InputInjector.MouseButton((MouseButtonCode)reader.ReadByte(), true);
                            break;

                        case InputCommandType.MouseUp:
                            MoveMouseRelativeToSelection(reader.ReadInt32(), reader.ReadInt32());
                            InputInjector.MouseButton((MouseButtonCode)reader.ReadByte(), false);
                            break;

                        case InputCommandType.MouseWheel:
                            MoveMouseRelativeToSelection(reader.ReadInt32(), reader.ReadInt32());
                            InputInjector.MouseWheel(reader.ReadInt32());
                            break;

                        case InputCommandType.KeyDown:
                            InputInjector.Key(reader.ReadInt32(), true);
                            break;

                        case InputCommandType.KeyUp:
                            InputInjector.Key(reader.ReadInt32(), false);
                            break;

                        case InputCommandType.PasteText:
                            InputInjector.PasteText(reader.ReadString());
                            break;

                        case InputCommandType.SetDisplaySelection:
                            var selection = reader.ReadInt32();
                            _displaySelection.SetSelection(selection);
                            _statusCallback("Display selection changed to " + _displaySelection.GetLabel() + ".");
                            break;
                    }
                }
            }
        }

        private void MoveMouseRelativeToSelection(int x, int y)
        {
            var virtualPoint = _displaySelection.TranslateToVirtualDesktopCoordinates(x, y);
            InputInjector.MouseMove(virtualPoint.X, virtualPoint.Y);
        }
    }

    internal sealed class DisplaySelectionState
    {
        private int _selectedScreenIndex = -1;

        public Rectangle GetCaptureBounds()
        {
            var selection = GetEffectiveSelection();
            if (selection < 0)
            {
                return SystemInformation.VirtualScreen;
            }

            var screens = Screen.AllScreens;
            return screens[selection].Bounds;
        }

        public Point TranslateToVirtualDesktopCoordinates(int x, int y)
        {
            var virtualBounds = SystemInformation.VirtualScreen;
            var captureBounds = GetCaptureBounds();
            return new Point(
                x + captureBounds.Left - virtualBounds.Left,
                y + captureBounds.Top - virtualBounds.Top);
        }

        public void SetSelection(int selection)
        {
            _selectedScreenIndex = selection;
        }

        public string GetLabel()
        {
            var selection = GetEffectiveSelection();
            return selection < 0 ? "All screens" : "Screen " + (selection + 1);
        }

        private int GetEffectiveSelection()
        {
            var selection = _selectedScreenIndex;
            var screens = Screen.AllScreens;
            if (selection < 0 || selection >= screens.Length)
            {
                return -1;
            }

            return selection;
        }
    }
}
