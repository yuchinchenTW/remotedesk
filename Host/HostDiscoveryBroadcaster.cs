using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using SimpleRemote.Shared;

namespace SimpleRemote.Host
{
    internal sealed class HostDiscoveryBroadcaster : IDisposable
    {
        private readonly string _machineName = Environment.MachineName;

        private Thread _thread;
        private volatile bool _running;
        private string _displayName;
        private int _hostPort;

        public void Start(int hostPort, string displayName)
        {
            if (_running)
            {
                return;
            }

            _hostPort = hostPort;
            _displayName = string.IsNullOrWhiteSpace(displayName) ? _machineName : displayName.Trim();
            _running = true;
            _thread = new Thread(BroadcastLoop);
            _thread.IsBackground = true;
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;

            if (_thread != null && _thread != Thread.CurrentThread)
            {
                _thread.Join(1000);
                _thread = null;
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private void BroadcastLoop()
        {
            using (var udp = new UdpClient())
            {
                udp.EnableBroadcast = true;

                while (_running)
                {
                    var payload = DiscoveryProtocol.CreateAnnouncement(_machineName, _displayName, _hostPort);
                    foreach (var endpoint in GetBroadcastEndpoints())
                    {
                        try
                        {
                            udp.Send(payload, payload.Length, endpoint);
                        }
                        catch
                        {
                        }
                    }

                    Thread.Sleep(DiscoveryProtocol.BroadcastIntervalMs);
                }
            }
        }

        private static IEnumerable<IPEndPoint> GetBroadcastEndpoints()
        {
            var endpoints = new List<IPEndPoint>();

            try
            {
                var adapters = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback);

                foreach (var adapter in adapters)
                {
                    foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask == null)
                        {
                            continue;
                        }

                        endpoints.Add(new IPEndPoint(GetBroadcastAddress(unicast.Address, unicast.IPv4Mask), DiscoveryProtocol.DiscoveryPort));
                    }
                }
            }
            catch
            {
            }

            endpoints.Add(new IPEndPoint(IPAddress.Broadcast, DiscoveryProtocol.DiscoveryPort));
            return endpoints
                .GroupBy(endpoint => endpoint.Address.ToString() + ":" + endpoint.Port)
                .Select(group => group.First());
        }

        private static IPAddress GetBroadcastAddress(IPAddress address, IPAddress subnetMask)
        {
            var addressBytes = address.GetAddressBytes();
            var maskBytes = subnetMask.GetAddressBytes();
            var broadcastBytes = new byte[addressBytes.Length];

            for (var i = 0; i < addressBytes.Length; i++)
            {
                broadcastBytes[i] = (byte)(addressBytes[i] | (maskBytes[i] ^ 255));
            }

            return new IPAddress(broadcastBytes);
        }
    }
}
