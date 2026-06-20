using Playnite.SDK;
using System;
using System.IO;
using System.Net;

namespace EmuLibrary.Util.Metadata
{
    // Keeps an on-disk cached copy of a remote file under the plugin's data dir, refreshing only when the
    // cached copy is older than maxAge. Designed to fail soft: any error while refreshing falls back to the
    // existing (stale) cached copy, and it only returns null when there is nothing cached at all. Shared by
    // TitleDb and GameTdb so the daily-refresh + graceful-degradation policy lives in exactly one place.
    internal static class CachedRemoteFile
    {
        // Ensures localPath is present and fresh, invoking writeFreshCopy(tempPath) to produce a new copy when
        // a refresh is needed (the delegate downloads/extracts into the given temp path). Returns localPath
        // when usable (fresh, or stale-but-present after a failed refresh), otherwise null.
        public static string Ensure(string localPath, TimeSpan maxAge, Action<string> writeFreshCopy, ILogger logger)
        {
            try
            {
                var fi = new FileInfo(localPath);
                if (fi.Exists && DateTime.UtcNow - fi.LastWriteTimeUtc < maxAge)
                    return localPath;

                Directory.CreateDirectory(Path.GetDirectoryName(localPath));
                var tmp = localPath + ".tmp";
                try
                {
                    if (File.Exists(tmp))
                        File.Delete(tmp);

                    writeFreshCopy(tmp);

                    if (File.Exists(localPath))
                        File.Delete(localPath);
                    File.Move(tmp, localPath);
                    return localPath;
                }
                catch (Exception ex)
                {
                    logger?.Warn(ex, $"[Metadata] Failed to refresh \"{localPath}\"; falling back to cached copy if present.");
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort cleanup */ }
                    return fi.Exists ? localPath : null;
                }
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, $"[Metadata] Cache access failed for \"{localPath}\".");
                return File.Exists(localPath) ? localPath : null;
            }
        }

        // Downloads url to destPath. Enables TLS 1.2 (the net462 default may omit it, which breaks GitHub) and
        // applies a generous timeout so a stalled connection can't hang a library scan indefinitely.
        public static void Download(string url, string destPath, ILogger logger)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var client = new TimedWebClient(TimeSpan.FromMinutes(5)))
            {
                client.Headers[HttpRequestHeader.UserAgent] = "EmuLibrary";
                client.DownloadFile(url, destPath);
            }
        }

        private sealed class TimedWebClient : WebClient
        {
            private readonly int _timeoutMs;
            public TimedWebClient(TimeSpan timeout) { _timeoutMs = (int)timeout.TotalMilliseconds; }
            protected override WebRequest GetWebRequest(Uri address)
            {
                var request = base.GetWebRequest(address);
                if (request != null)
                    request.Timeout = _timeoutMs;
                return request;
            }
        }
    }
}
