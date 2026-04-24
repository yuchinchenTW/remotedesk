using System;
using System.IO;
using System.Text;

namespace SimpleRemote.Shared
{
    internal sealed class DiscoveryAnnouncement
    {
        public string MachineName;
        public string DisplayName;
        public int HostPort;
    }

    internal static class DiscoveryProtocol
    {
        public const int DiscoveryPort = 5902;
        public const int BroadcastIntervalMs = 2000;
        public const int HostTimeoutMs = 7000;

        private const string Signature = "SRDISC1";

        public static byte[] CreateAnnouncement(string machineName, string displayName, int hostPort)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Signature);
                writer.Write(machineName ?? string.Empty);
                writer.Write(displayName ?? string.Empty);
                writer.Write(hostPort);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static bool TryParseAnnouncement(byte[] data, int length, out DiscoveryAnnouncement announcement)
        {
            announcement = null;

            try
            {
                using (var stream = new MemoryStream(data, 0, length, false))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    var signature = reader.ReadString();
                    if (!string.Equals(signature, Signature, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    announcement = new DiscoveryAnnouncement
                    {
                        MachineName = reader.ReadString(),
                        DisplayName = reader.ReadString(),
                        HostPort = reader.ReadInt32()
                    };

                    return announcement.HostPort > 0 && announcement.HostPort <= 65535;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
