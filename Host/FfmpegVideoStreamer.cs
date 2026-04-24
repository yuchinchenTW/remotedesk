using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using SimpleRemote.Shared;

namespace SimpleRemote.Host
{
    internal sealed class FfmpegVideoStreamer : IDisposable
    {
        private const int ChunkSize = 64 * 1024;
        private const int MaxDimension = 3840;

        private readonly string _ffmpegPath;
        private readonly int _fps;
        private readonly StringBuilder _stderrTail = new StringBuilder();

        private Process _process;
        private Thread _stderrThread;

        private FfmpegVideoStreamer(string ffmpegPath, int fps)
        {
            _ffmpegPath = ffmpegPath;
            _fps = fps;
        }

        public static bool IsSupported()
        {
            return Screen.AllScreens.Length == 1 && !string.IsNullOrEmpty(FfmpegLocator.TryResolve());
        }

        public static FfmpegVideoStreamer TryCreate(int fps)
        {
            if (Screen.AllScreens.Length != 1)
            {
                return null;
            }

            var ffmpegPath = FfmpegLocator.TryResolve();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                return null;
            }

            var streamer = new FfmpegVideoStreamer(ffmpegPath, fps);
            try
            {
                streamer.Start();
                return streamer;
            }
            catch
            {
                streamer.Dispose();
                return null;
            }
        }

        public void Stream(NetworkStream stream, object writeSync, CancellationToken token)
        {
            var output = _process.StandardOutput.BaseStream;
            var buffer = new byte[ChunkSize];

            while (!token.IsCancellationRequested)
            {
                var read = output.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    throw new InvalidOperationException(BuildFailureMessage("ffmpeg video stream stopped unexpectedly."));
                }

                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);

                Protocol.SendMessage(stream, writeSync, MessageType.VideoChunk, delegate(BinaryWriter writer)
                {
                    writer.Write(chunk);
                });
            }
        }

        public void Dispose()
        {
            if (_process != null)
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill();
                    }
                }
                catch
                {
                }
            }

            if (_stderrThread != null && _stderrThread != Thread.CurrentThread)
            {
                _stderrThread.Join(500);
                _stderrThread = null;
            }

            if (_process != null)
            {
                _process.Dispose();
                _process = null;
            }
        }

        private void Start()
        {
            var args = BuildArguments();
            var startInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _process = Process.Start(startInfo);
            if (_process == null)
            {
                throw new InvalidOperationException("Failed to start ffmpeg.");
            }

            _stderrThread = new Thread(ReadStandardError);
            _stderrThread.IsBackground = true;
            _stderrThread.Start();

            Thread.Sleep(400);
            if (_process.HasExited)
            {
                throw new InvalidOperationException(BuildFailureMessage("ffmpeg exited during startup."));
            }
        }

        private string BuildArguments()
        {
            var primaryScreen = Screen.PrimaryScreen.Bounds;
            var scaledSize = ScaleToFit(primaryScreen.Width, primaryScreen.Height, MaxDimension);
            var filter = scaledSize.Width == primaryScreen.Width && scaledSize.Height == primaryScreen.Height
                ? "hwdownload,format=bgra,format=bgr0"
                : "hwdownload,format=bgra,scale=" + scaledSize.Width + ":" + scaledSize.Height + ":flags=lanczos,format=bgr0";

            return string.Join(" ", new[]
            {
                "-hide_banner",
                "-loglevel", "error",
                "-f", "lavfi",
                "-i", Quote("ddagrab=output_idx=0:framerate=" + _fps + ":draw_mouse=1"),
                "-an",
                "-sn",
                "-vf", Quote(filter),
                "-c:v", "libx264rgb",
                "-preset", "veryfast",
                "-crf", "18",
                "-g", Math.Max(10, _fps).ToString(),
                "-keyint_min", Math.Max(10, _fps).ToString(),
                "-sc_threshold", "0",
                "-bf", "0",
                "-f", "mpegts",
                "pipe:1"
            });
        }

        private void ReadStandardError()
        {
            try
            {
                while (_process != null && !_process.HasExited)
                {
                    var line = _process.StandardError.ReadLine();
                    if (line == null)
                    {
                        return;
                    }

                    lock (_stderrTail)
                    {
                        if (_stderrTail.Length > 2048)
                        {
                            _stderrTail.Remove(0, _stderrTail.Length - 2048);
                        }

                        _stderrTail.AppendLine(line);
                    }
                }
            }
            catch
            {
            }
        }

        private string BuildFailureMessage(string prefix)
        {
            lock (_stderrTail)
            {
                var detail = _stderrTail.ToString().Trim();
                return string.IsNullOrEmpty(detail) ? prefix : prefix + " " + detail;
            }
        }

        private static System.Drawing.Size ScaleToFit(int width, int height, int maxDimension)
        {
            if (width <= maxDimension && height <= maxDimension)
            {
                return new System.Drawing.Size(MakeEven(width), MakeEven(height));
            }

            var scale = Math.Min((double)maxDimension / width, (double)maxDimension / height);
            var scaledWidth = Math.Max(2, MakeEven((int)Math.Round(width * scale)));
            var scaledHeight = Math.Max(2, MakeEven((int)Math.Round(height * scale)));
            return new System.Drawing.Size(scaledWidth, scaledHeight);
        }

        private static int MakeEven(int value)
        {
            return (value & 1) == 0 ? value : value - 1;
        }

        private static string Quote(string value)
        {
            return "\"" + value + "\"";
        }
    }
}
