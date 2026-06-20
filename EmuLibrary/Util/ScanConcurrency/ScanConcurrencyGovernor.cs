using Playnite.SDK;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EmuLibrary.Util.ScanConcurrency
{
    internal sealed class ScanConcurrencyGovernor : IScanConcurrency
    {
        internal const int NetworkPerEndpoint = 12;
        internal const int LocalPerEndpoint   = 4;

        // Global ceiling on concurrent endpoint I/O across every scan. Also the natural cap on cross-mapping
        // producer fan-out (EmuLibrary.MaxScanConcurrency) — beyond this, extra producers only block here.
        internal const int GlobalMax         = 16;

        private readonly SemaphoreSlim _global = new SemaphoreSlim(GlobalMax, GlobalMax);
        private readonly ConcurrentDictionary<string, Endpoint> _perEndpoint =
            new ConcurrentDictionary<string, Endpoint>();
        private readonly ILogger _logger;
        private int _globalInFlight;
        private int _globalPeak;

        // Per-endpoint state: the budget semaphore plus live/peak in-flight counters used only for the debug
        // logging that lets a real scan confirm the per-host and global caps hold (see EnterInFlight).
        private sealed class Endpoint
        {
            public readonly string Key;
            public readonly int Cap;
            public readonly bool IsNetwork;
            public readonly SemaphoreSlim Sem;
            public int InFlight;
            public int Peak;

            public Endpoint(string key, int cap, bool isNetwork)
            {
                Key = key;
                Cap = cap;
                IsNetwork = isNetwork;
                Sem = new SemaphoreSlim(cap, cap);
            }
        }

        public ScanConcurrencyGovernor(ILogger logger)
        {
            _logger = logger;
        }

        private Endpoint GetEndpoint(string endpointPath)
        {
            var ep = ScanEndpointKey.For(endpointPath);
            int cap = ep.IsNetwork ? NetworkPerEndpoint : LocalPerEndpoint;
            var endpoint = _perEndpoint.GetOrAdd(ep.Key, k => new Endpoint(k, cap, ep.IsNetwork));
            _logger?.Debug($"ScanConcurrency: '{endpointPath}' -> endpoint '{endpoint.Key}' " +
                           $"({(endpoint.IsNetwork ? "network" : "local")}, cap {endpoint.Cap})");
            return endpoint;
        }

        // Called while both permits are held (i.e. work is about to run), so the counters reflect actual
        // concurrent endpoint I/O. Logs at debug only when a new peak is reached, so a scan emits at most a
        // short climb to the caps — evidence that per-host stays <= cap and the total stays <= GlobalMax.
        private void EnterInFlight(Endpoint endpoint)
        {
            int epNow = Interlocked.Increment(ref endpoint.InFlight);
            if (TryBumpPeak(ref endpoint.Peak, epNow))
                _logger?.Debug($"ScanConcurrency: endpoint '{endpoint.Key}' " +
                               $"({(endpoint.IsNetwork ? "network" : "local")}) in-flight peak {epNow}/{endpoint.Cap}");

            int gNow = Interlocked.Increment(ref _globalInFlight);
            if (TryBumpPeak(ref _globalPeak, gNow))
                _logger?.Debug($"ScanConcurrency: global in-flight peak {gNow}/{GlobalMax}");
        }

        private void ExitInFlight(Endpoint endpoint)
        {
            Interlocked.Decrement(ref endpoint.InFlight);
            Interlocked.Decrement(ref _globalInFlight);
        }

        // CAS the peak up to `observed`; returns true iff this call raised it (so only the raiser logs).
        private static bool TryBumpPeak(ref int peak, int observed)
        {
            while (true)
            {
                int cur = Volatile.Read(ref peak);
                if (observed <= cur)
                    return false;
                if (Interlocked.CompareExchange(ref peak, observed, cur) == cur)
                    return true;
            }
        }

        // Acquire ENDPOINT first, then GLOBAL (consistent order => no deadlock; global is the single shared
        // last-acquired lock). Holding an endpoint permit while waiting on global only self-throttles that
        // endpoint, never starves others. Release in reverse.
        public T Run<T>(string endpointPath, Func<T> work, CancellationToken ct)
        {
            var endpoint = GetEndpoint(endpointPath);
            endpoint.Sem.Wait(ct);
            try
            {
                _global.Wait(ct);
                try
                {
                    EnterInFlight(endpoint);
                    try { return work(); }
                    finally { ExitInFlight(endpoint); }
                }
                finally { _global.Release(); }
            }
            finally { endpoint.Sem.Release(); }
        }

        public void Run(string endpointPath, Action work, CancellationToken ct)
        {
            Run<object>(endpointPath, () => { work(); return null; }, ct);
        }

        public IReadOnlyList<TResult> Map<TItem, TResult>(
            IReadOnlyCollection<TItem> items, string endpointPath, Func<TItem, TResult> worker, CancellationToken ct)
        {
            var endpoint = GetEndpoint(endpointPath);
            var results = new ConcurrentBag<TResult>();
            var tasks = new List<Task>(items.Count);

            // Acquire-before-launch: the producer blocks on Wait between launches, so in-flight tasks are bounded
            // by the live permits (<= min(endpoint cap, remaining global)) — we never spawn N blocked threads.
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                endpoint.Sem.Wait(ct);
                try { _global.Wait(ct); }
                catch { endpoint.Sem.Release(); throw; }

                var captured = item;
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        EnterInFlight(endpoint);
                        try { results.Add(worker(captured)); }
                        finally { ExitInFlight(endpoint); }
                    }
                    finally { _global.Release(); endpoint.Sem.Release(); }
                }));
            }

            try { Task.WaitAll(tasks.ToArray()); }
            catch (AggregateException ex)
            {
                var faults = ex.Flatten().InnerExceptions.Where(e => !(e is OperationCanceledException)).ToList();
                if (faults.Count > 0) _logger?.Error(new AggregateException(faults), "Parallel scan unit(s) failed.");
            }
            return results.ToArray();
        }
    }
}
