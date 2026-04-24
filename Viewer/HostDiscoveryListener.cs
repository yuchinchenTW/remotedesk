using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using SimpleRemote.Shared;

namespace SimpleRemote.Viewer
{
    internal sealed class DiscoveredHostInfo
    {
        public string HostAddress;
        public int HostPort;
        public string MachineName;
        public string DisplayName;
        public DateTime LastSeenUtc;
    }

    internal sealed class HostDiscoveryListener : IDisposable
    {
        private readonly Action<DiscoveredHostInfo> _hostCallback;

        private UdpClient _udpClient;
        private Thread _thread;
        private volatile bool _running;

        public HostDiscoveryListener(Action<DiscoveredHostInfo> hostCallback)
        {
            _hostCallback = hostCallback;
        }

        public void Start()
        {
            if (_running)
            {
                return;
            }

            _udpClient = new UdpClient(AddressFamily.InterNetwork);
            _udpClient.ExclusiveAddressUse = false;
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryProtocol.DiscoveryPort));

            _running = true;
            _thread = new Thread(ListenLoop);
            _thread.IsBackground = true;
            _thread.Start();
        }

        public void Dispose()
        {
            _running = false;

            if (_udpClient != null)
            {
                try
                {
                    _udpClient.Close();
                }
                catch
                {
                }

                _udpClient = null;
            }

            if (_thread != null && _thread != Thread.CurrentThread)
            {
                _thread.Join(1000);
                _thread = null;
            }
        }

        private void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    IPEndPoint remoteEndpoint = null;
                    var data = _udpClient.Receive(ref remoteEndpoint);

                    DiscoveryAnnouncement announcement;
                    if (!DiscoveryProtocol.TryParseAnnouncement(data, data.Length, out announcement))
                    {
                        continue;
                    }

                    _hostCallback(new DiscoveredHostInfo
                    {
                        HostAddress = remoteEndpoint.Address.ToString(),
                        HostPort = announcement.HostPort,
                        MachineName = announcement.MachineName,
                        DisplayName = announcement.DisplayName,
                        LastSeenUtc = DateTime.UtcNow
                    });
                }
                catch (SocketException)
                {
                    if (!_running)
                    {
                        return;
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch
                {
                }
            }
        }
    }
}
