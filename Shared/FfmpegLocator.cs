using System;
using System.IO;

namespace SimpleRemote.Shared
{
    internal static class FfmpegLocator
    {
        public static string TryResolve()
        {
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDirectory, "ffmpeg.exe"),
                Path.Combine(baseDirectory, "ffmpeg", "ffmpeg.exe"),
                Path.Combine(baseDirectory, "tools", "ffmpeg", "ffmpeg-8.1-essentials_build", "bin", "ffmpeg.exe"),
                Path.GetFullPath(Path.Combine(baseDirectory, "..", "tools", "ffmpeg", "ffmpeg-8.1-essentials_build", "bin", "ffmpeg.exe"))
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                }
            }

            return null;
        }
    }
}
