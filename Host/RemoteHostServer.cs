using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using SimpleRemote.Shared;

namespace SimpleRemote.Host
{
    internal sealed class RemoteHostServer : IDisposable
    {
        private readonly Action<string> _statusCallback;
        private readonly Action<string> _clientCallback;

        private TcpListener _listener;
        private Thread _acceptThread;
        private TcpClient _activeClient;
        private CancellationTokenSource _sessionTokenSource;
        private volatile bool _running;
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

        public void Start(int port, string password)
        {
            if (_running)
            {
                return;
            }

            _password = password ?? string.Empty;
            _port = port;
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
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
                        ScreenStreamer.StreamVirtualDesktop(stream, writeSync, _sessionTokenSource.Token, 8);
                    }
                    catch
                    {
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
                            InputInjector.MouseMove(reader.ReadInt32(), reader.ReadInt32());
                            break;

                        case InputCommandType.MouseDown:
                            InputInjector.MouseMove(reader.ReadInt32(), reader.ReadInt32());
                            InputInjector.MouseButton((MouseButtonCode)reader.ReadByte(), true);
                            break;

                        case InputCommandType.MouseUp:
                            InputInjector.MouseMove(reader.ReadInt32(), reader.ReadInt32());
                            InputInjector.MouseButton((MouseButtonCode)reader.ReadByte(), false);
                            break;

                        case InputCommandType.MouseWheel:
                            InputInjector.MouseMove(reader.ReadInt32(), reader.ReadInt32());
                            InputInjector.MouseWheel(reader.ReadInt32());
                            break;

                        case InputCommandType.KeyDown:
                            InputInjector.Key(reader.ReadInt32(), true);
                            break;

                        case InputCommandType.KeyUp:
                            InputInjector.Key(reader.ReadInt32(), false);
                            break;
                    }
                }
            }
        }
    }
}
