using System;
using System.Threading;

namespace EmuLibrary.Installs
{
    // Adapts the crypto/extract primitives' INCREMENTAL byte reports (each Report is the count of bytes just
    // processed in one chunk) into the install handle's cumulative SetBytes(done, total). The 3DS decrypt path
    // and the PS3 PKG extractor report per-chunk byte counts through this so a single phase shows a real
    // percentage bar against a total known up front. Thread-safe accumulation (the sequence runs on a worker
    // thread and chunks may report from it). Complements CopyProgressReporter, which forwards the copy path's
    // already-cumulative (done, total) pairs.
    internal sealed class ByteProgressReporter : IProgress<long>
    {
        private readonly IInstallProgressHandle _handle;
        private readonly long _total;
        private long _done;

        public ByteProgressReporter(IInstallProgressHandle handle, long total)
        {
            _handle = handle;
            _total = total;
        }

        public void Report(long bytesJustProcessed)
        {
            if (bytesJustProcessed <= 0)
                return;
            long done = Interlocked.Add(ref _done, bytesJustProcessed);
            _handle.SetBytes(done, _total);
        }
    }
}
