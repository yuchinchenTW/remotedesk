using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using SimpleRemote.Shared;

namespace SimpleRemote.Host
{
    internal interface IScreenCaptureBackend : IDisposable
    {
        bool TryCaptureFrame(out int desktopWidth, out int desktopHeight, out byte[] jpegBytes);
    }

    internal static class ScreenStreamer
    {
        private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(codec => codec.FormatID == ImageFormat.Jpeg.Guid);

        public static void StreamVirtualDesktop(NetworkStream stream, object writeSync, CancellationToken token, int fps)
        {
            using (var latestFrame = new LatestFrameStore())
            using (var capturer = CreateCaptureBackend())
            {
                Exception captureError = null;
                var captureThread = new Thread(delegate()
                {
                    try
                    {
                        var frameDelay = Math.Max(20, 1000 / Math.Max(1, fps));

                        while (!token.IsCancellationRequested)
                        {
                            int desktopWidth;
                            int desktopHeight;
                            byte[] jpeg;
                            if (capturer.TryCaptureFrame(out desktopWidth, out desktopHeight, out jpeg))
                            {
                                latestFrame.Update(desktopWidth, desktopHeight, jpeg);
                            }

                            if (token.WaitHandle.WaitOne(frameDelay))
                            {
                                return;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        captureError = ex;
                        latestFrame.Complete();
                    }
                });

                captureThread.IsBackground = true;
                captureThread.Start();

                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        FrameData frame;
                        if (!latestFrame.WaitAndTake(token, out frame))
                        {
                            break;
                        }

                        Protocol.SendMessage(stream, writeSync, MessageType.Frame, delegate(BinaryWriter writer)
                        {
                            writer.Write(frame.DesktopWidth);
                            writer.Write(frame.DesktopHeight);
                            writer.Write(frame.JpegBytes.Length);
                            writer.Write(frame.JpegBytes);
                        });
                    }
                }
                finally
                {
                    latestFrame.Complete();
                    captureThread.Join(1000);
                }

                if (captureError != null)
                {
                    throw captureError;
                }
            }
        }

        private static IScreenCaptureBackend CreateCaptureBackend()
        {
            if (Screen.AllScreens.Length == 1)
            {
                try
                {
                    return new DesktopDuplicationCapture(1600, 38L);
                }
                catch
                {
                }
            }

            return new GdiScreenCapture(1600, 38L);
        }

        private sealed class FrameData
        {
            public int DesktopWidth;
            public int DesktopHeight;
            public byte[] JpegBytes;
        }

        private sealed class LatestFrameStore : IDisposable
        {
            private readonly object _sync = new object();
            private readonly AutoResetEvent _available = new AutoResetEvent(false);
            private volatile bool _completed;
            private FrameData _pending;

            public void Update(int desktopWidth, int desktopHeight, byte[] jpegBytes)
            {
                if (_completed)
                {
                    return;
                }

                lock (_sync)
                {
                    _pending = new FrameData
                    {
                        DesktopWidth = desktopWidth,
                        DesktopHeight = desktopHeight,
                        JpegBytes = jpegBytes
                    };
                }

                _available.Set();
            }

            public bool WaitAndTake(CancellationToken token, out FrameData frame)
            {
                frame = null;

                while (!token.IsCancellationRequested)
                {
                    lock (_sync)
                    {
                        if (_pending != null)
                        {
                            frame = _pending;
                            _pending = null;
                            return true;
                        }

                        if (_completed)
                        {
                            return false;
                        }
                    }

                    var index = WaitHandle.WaitAny(new WaitHandle[] { _available, token.WaitHandle });
                    if (index == 1)
                    {
                        return false;
                    }
                }

                return false;
            }

            public void Complete()
            {
                _completed = true;
                _available.Set();
            }

            public void Dispose()
            {
                Complete();
                _available.Dispose();
            }
        }

        private sealed class GdiScreenCapture : IScreenCaptureBackend
        {
            private readonly long _jpegQuality;
            private readonly int _maxDimension;
            private readonly EncoderParameters _encoderParameters;

            private Rectangle _sourceBounds;
            private Bitmap _captureBitmap;
            private Graphics _captureGraphics;
            private Bitmap _scaledBitmap;
            private Graphics _scaledGraphics;
            private MemoryStream _jpegStream;

            public GdiScreenCapture(int maxDimension, long jpegQuality)
            {
                _maxDimension = maxDimension;
                _jpegQuality = jpegQuality;
                _encoderParameters = new EncoderParameters(1);
                _encoderParameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, jpegQuality);
            }

            public bool TryCaptureFrame(out int desktopWidth, out int desktopHeight, out byte[] jpegBytes)
            {
                var bounds = SystemInformation.VirtualScreen;
                EnsureBuffers(bounds);

                desktopWidth = bounds.Width;
                desktopHeight = bounds.Height;

                CaptureDesktop(bounds);

                var imageToEncode = _scaledBitmap ?? _captureBitmap;
                _jpegStream.SetLength(0);

                if (JpegCodec != null)
                {
                    imageToEncode.Save(_jpegStream, JpegCodec, _encoderParameters);
                }
                else
                {
                    imageToEncode.Save(_jpegStream, ImageFormat.Jpeg);
                }

                jpegBytes = _jpegStream.ToArray();
                return true;
            }

            public void Dispose()
            {
                if (_encoderParameters != null)
                {
                    _encoderParameters.Dispose();
                }

                if (_scaledGraphics != null)
                {
                    _scaledGraphics.Dispose();
                }

                if (_scaledBitmap != null)
                {
                    _scaledBitmap.Dispose();
                }

                if (_captureGraphics != null)
                {
                    _captureGraphics.Dispose();
                }

                if (_captureBitmap != null)
                {
                    _captureBitmap.Dispose();
                }

                if (_jpegStream != null)
                {
                    _jpegStream.Dispose();
                }
            }

            private void EnsureBuffers(Rectangle bounds)
            {
                if (_captureBitmap != null && bounds == _sourceBounds)
                {
                    return;
                }

                DisposeBuffers();
                _sourceBounds = bounds;

                _captureBitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
                _captureGraphics = Graphics.FromImage(_captureBitmap);

                var scale = Math.Min(1.0, Math.Min((double)_maxDimension / bounds.Width, (double)_maxDimension / bounds.Height));
                if (scale < 0.999)
                {
                    var scaledWidth = Math.Max(1, (int)Math.Round(bounds.Width * scale));
                    var scaledHeight = Math.Max(1, (int)Math.Round(bounds.Height * scale));
                    _scaledBitmap = new Bitmap(scaledWidth, scaledHeight, PixelFormat.Format24bppRgb);
                    _scaledGraphics = Graphics.FromImage(_scaledBitmap);
                    _scaledGraphics.CompositingMode = CompositingMode.SourceCopy;
                    _scaledGraphics.CompositingQuality = CompositingQuality.HighSpeed;
                    _scaledGraphics.InterpolationMode = InterpolationMode.Low;
                    _scaledGraphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                    _scaledGraphics.SmoothingMode = SmoothingMode.None;
                }

                _jpegStream = new MemoryStream(bounds.Width * bounds.Height / 4);
            }

            private void DisposeBuffers()
            {
                if (_scaledGraphics != null)
                {
                    _scaledGraphics.Dispose();
                    _scaledGraphics = null;
                }

                if (_scaledBitmap != null)
                {
                    _scaledBitmap.Dispose();
                    _scaledBitmap = null;
                }

                if (_captureGraphics != null)
                {
                    _captureGraphics.Dispose();
                    _captureGraphics = null;
                }

                if (_captureBitmap != null)
                {
                    _captureBitmap.Dispose();
                    _captureBitmap = null;
                }

                if (_jpegStream != null)
                {
                    _jpegStream.Dispose();
                    _jpegStream = null;
                }
            }

            private void CaptureDesktop(Rectangle bounds)
            {
                var screenDc = GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Failed to access the screen device context.");
                }

                var targetDc = IntPtr.Zero;

                try
                {
                    targetDc = _captureGraphics.GetHdc();
                    if (!BitBlt(targetDc, 0, 0, bounds.Width, bounds.Height, screenDc, bounds.Left, bounds.Top, CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt))
                    {
                        throw new InvalidOperationException("BitBlt screen capture failed.");
                    }
                }
                finally
                {
                    if (targetDc != IntPtr.Zero)
                    {
                        _captureGraphics.ReleaseHdc(targetDc);
                    }

                    ReleaseDC(IntPtr.Zero, screenDc);
                }

                if (_scaledGraphics != null)
                {
                    _scaledGraphics.DrawImage(_captureBitmap, new Rectangle(Point.Empty, _scaledBitmap.Size));
                }
            }

            [DllImport("user32.dll")]
            private static extern IntPtr GetDC(IntPtr hWnd);

            [DllImport("user32.dll")]
            private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

            [DllImport("gdi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool BitBlt(
                IntPtr hdcDest,
                int nXDest,
                int nYDest,
                int nWidth,
                int nHeight,
                IntPtr hdcSrc,
                int nXSrc,
                int nYSrc,
                CopyPixelOperation dwRop);
        }
    }
}
