using System;
using System.Collections.Generic;
using System.Threading;

namespace EmuLibrary.Util.ScanConcurrency
{
    // Global, endpoint-keyed I/O concurrency budget shared by every scan. Bounds per-host and total
    // concurrent reads so cross-mapping fan-out and intra-mapping fan-out collapse onto one budget.
    internal interface IScanConcurrency
    {
        // Run one unit of endpoint I/O. Blocks until an endpoint+global permit is free, runs `work` on the
        // CALLING thread, releases. For single-threaded scanners (SingleFile/MultiFile) and coarse phases.
        T Run<T>(string endpointPath, Func<T> work, CancellationToken ct);
        void Run(string endpointPath, Action work, CancellationToken ct);

        // Run `worker` over `items` with bounded parallelism, each item a unit gated by the SAME endpoint+global
        // permits. For per-file scanners (Yuzu/PS3). `worker` must be thread-safe and own its try/catch; results
        // are returned in unspecified order. Honors `ct` (OperationCanceledException).
        IReadOnlyList<TResult> Map<TItem, TResult>(
            IReadOnlyCollection<TItem> items, string endpointPath, Func<TItem, TResult> worker,
            CancellationToken ct);
    }
}
