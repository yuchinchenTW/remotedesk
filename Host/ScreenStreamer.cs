using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;
using SimpleRemote.Shared;

namespace SimpleRemote.Host
{
    internal static class ScreenStreamer
    {
        private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(codec => codec.FormatID == ImageFormat.Jpeg.Guid);

        public static void StreamVirtualDesktop(NetworkStream stream, object writeSync, CancellationToken token, int fps)
        {
            var frameDelay = Math.Max(40, 1000 / Math.Max(1, fps));

            while (!token.IsCancellationRequested)
            {
                var bounds = SystemInformation.VirtualScreen;
                var jpeg = Capture(bounds);

                Protocol.SendMessage(stream, writeSync, MessageType.Frame, delegate(BinaryWriter writer)
                {
                    writer.Write(bounds.Width);
                    writer.Write(bounds.Height);
                    writer.Write(jpeg.Length);
                    writer.Write(jpeg);
                });

                if (token.WaitHandle.WaitOne(frameDelay))
                {
                    return;
                }
            }
        }

        private static byte[] Capture(Rectangle bounds)
        {
            using (var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var stream = new MemoryStream())
            {
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);

                if (JpegCodec != null)
                {
                    var quality = new EncoderParameters(1);
                    quality.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 45L);
                    bitmap.Save(stream, JpegCodec, quality);
                }
                else
                {
                    bitmap.Save(stream, ImageFormat.Jpeg);
                }

                return stream.ToArray();
            }
        }
    }
}
