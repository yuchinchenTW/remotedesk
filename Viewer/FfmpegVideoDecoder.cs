using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using SimpleRemote.Shared;

namespace SimpleRemote.Viewer
{
    internal sealed class FfmpegVideoDecoder : IDisposable
    {
        private readonly Action<Bitmap> _frameCallback;
        private readonly Action<string> _statusCallback;
        private readonly StringBuilder _stderrTail = new StringBuilder();

        private Process _process;
        private Thread _stderrThread;
        private Thread _frameThread;
        private volatile bool _running;

        public FfmpegVideoDecoder(Action<Bitmap> frameCallback, Action<string> statusCallback)
        {
            _frameCallback = frameCallback;
            _statusCallback = statusCallback;
            Start();
        }

        public void WriteChunk(byte[] chunk)
        {
            if (!_running || chunk == null || chunk.Length == 0)
            {
                return;
            }

            try
            {
                var stdin = _process.StandardInput.BaseStream;
                stdin.Write(chunk, 0, chunk.Length);
                stdin.Flush();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(BuildFailureMessage("ffmpeg decoder write failed: " + ex.Message));
            }
        }

        public void Dispose()
        {
            _running = false;

            if (_process != null)
            {
                try
                {
                    _process.StandardInput.Close();
                }
                catch
                {
                }

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

            if (_frameThread != null && _frameThread != Thread.CurrentThread)
            {
                _frameThread.Join(500);
                _frameThread = null;
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
            var ffmpegPath = FfmpegLocator.TryResolve();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                throw new InvalidOperationException("ffmpeg.exe was not found next to the viewer.");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-hide_banner -loglevel error -fflags nobuffer -flags low_delay -probesize 32 -analyzeduration 0 -f mpegts -i pipe:0 -an -sn -c:v bmp -f image2pipe pipe:1",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _process = Process.Start(startInfo);
            if (_process == null)
            {
                throw new InvalidOperationException("Failed to start ffmpeg decoder.");
            }

            _running = true;

            _stderrThread = new Thread(ReadStandardError);
            _stderrThread.IsBackground = true;
            _stderrThread.Start();

            _frameThread = new Thread(ReadFrames);
            _frameThread.IsBackground = true;
            _frameThread.Start();
        }

        private void ReadFrames()
        {
            try
            {
                var stream = _process.StandardOutput.BaseStream;

                while (_running)
                {
                    var header = ReadExact(stream, 14);
                    if (header == null)
                    {
                        return;
                    }

                    if (header[0] != 'B' || header[1] != 'M')
                    {
                        throw new InvalidDataException("Unexpected decoder frame format.");
                    }

                    var imageLength = BitConverter.ToInt32(header, 2);
                    if (imageLength < 54 || imageLength > 64 * 1024 * 1024)
                    {
                        throw new InvalidDataException("Invalid decoded frame length.");
                    }

                    var imageBytes = new byte[imageLength];
                    Buffer.BlockCopy(header, 0, imageBytes, 0, header.Length);

                    var remaining = ReadExact(stream, imageLength - header.Length);
                    if (remaining == null)
                    {
                        return;
                    }

                    Buffer.BlockCopy(remaining, 0, imageBytes, header.Length, remaining.Length);

                    using (var imageStream = new MemoryStream(imageBytes))
                    using (var decoded = new Bitmap(imageStream))
                    {
                        _frameCallback(new Bitmap(decoded));
                    }
                }
            }
            catch (Exception ex)
            {
                if (_running)
                {
                    _statusCallback(BuildFailureMessage("ffmpeg decoder stopped: " + ex.Message));
                }
            }
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

        private static byte[] ReadExact(Stream stream, int length)
        {
            var buffer = new byte[length];
            var offset = 0;

            while (offset < length)
            {
                var read = stream.Read(buffer, offset, length - offset);
                if (read <= 0)
                {
                    return null;
                }

                offset += read;
            }

            return buffer;
        }
    }
}
