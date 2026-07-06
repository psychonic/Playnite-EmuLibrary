using System;

namespace EmuLibrary.Installs
{
    // Estimates install throughput from a stream of cumulative bytes-done samples. Each Sample(done) returns a
    // smoothed bytes/sec rate (exponential moving average, so a momentary stall or burst doesn't whipsaw the
    // ETA). It's fed from the throttled handle (~a few samples/sec), which is plenty. A phase boundary resets
    // it (Reset), and a non-monotonic sample (done went backwards — a new phase reusing the same handle)
    // re-seeds without emitting a bogus spike. The clock is injectable for tests.
    internal sealed class ThroughputEstimator
    {
        // Weight of the newest instantaneous rate in the moving average. Higher = more responsive, noisier.
        private const double Alpha = 0.4;

        private readonly Func<DateTime> _now;
        private readonly object _lock = new object();
        private long _lastDone = -1;
        private DateTime _lastAt;
        private double _bytesPerSec;
        private bool _hasRate;

        public ThroughputEstimator(Func<DateTime> now = null)
        {
            _now = now ?? (() => DateTime.UtcNow);
        }

        // Forget accumulated rate. Call at a phase boundary so the next phase measures fresh.
        public void Reset()
        {
            lock (_lock)
            {
                _lastDone = -1;
                _hasRate = false;
                _bytesPerSec = 0;
            }
        }

        // Feed a cumulative bytes-done value; returns the smoothed rate in bytes/sec, or null until enough
        // data exists to establish one.
        public double? Sample(long done)
        {
            lock (_lock)
            {
                var now = _now();

                // First sample, or a reset/new phase (done rewound) — re-seed the baseline, keep any prior rate.
                if (_lastDone < 0 || done < _lastDone)
                {
                    _lastDone = done;
                    _lastAt = now;
                    return _hasRate ? _bytesPerSec : (double?)null;
                }

                var deltaBytes = done - _lastDone;
                var deltaSec = (now - _lastAt).TotalSeconds;
                _lastDone = done;
                _lastAt = now;

                // Zero-time or zero-byte tick carries no rate information; keep the current estimate.
                if (deltaSec <= 0 || deltaBytes <= 0)
                    return _hasRate ? _bytesPerSec : (double?)null;

                var instant = deltaBytes / deltaSec;
                _bytesPerSec = _hasRate ? (Alpha * instant) + ((1 - Alpha) * _bytesPerSec) : instant;
                _hasRate = true;
                return _bytesPerSec;
            }
        }
    }
}
