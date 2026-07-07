using System.Diagnostics;
using System.Net.NetworkInformation;

namespace DatfileCreator.Core;

/// <summary>
/// Thread-safe token-bucket rate limiter for I/O reads, ported from the
/// suite's _BandwidthThrottle. All worker threads share one instance; each
/// Consume(n) deducts n bytes from the bucket and sleeps until enough tokens
/// have refilled when the bucket runs dry, capping read throughput.
///
/// rateBytesPerSec &lt;= 0 means unlimited (Consume is a no-op).
/// </summary>
public sealed class BandwidthThrottle(double rateBytesPerSec)
{
    private readonly double _rate = rateBytesPerSec;
    private double _tokens = rateBytesPerSec; // start full
    private long _last = Stopwatch.GetTimestamp();
    private readonly Lock _lock = new();

    public double RateBytesPerSec => _rate;

    public void Consume(long nbytes, CancellationToken cancel)
    {
        if (_rate <= 0)
            return;

        double sleepSeconds = 0;
        lock (_lock)
        {
            long now = Stopwatch.GetTimestamp();
            double elapsed = Stopwatch.GetElapsedTime(_last, now).TotalSeconds;
            _last = now;
            // Refill — cap at one second's worth to prevent burst after idle
            _tokens = Math.Min(_rate, _tokens + elapsed * _rate);
            if (_tokens >= nbytes)
            {
                _tokens -= nbytes;
            }
            else
            {
                double deficit = nbytes - _tokens;
                _tokens = 0;
                sleepSeconds = deficit / _rate;
            }
        }

        // Sleep outside the lock in 50ms increments so cancel can interrupt
        if (sleepSeconds > 0)
        {
            long deadline = Stopwatch.GetTimestamp()
                          + (long)(sleepSeconds * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < deadline)
            {
                if (cancel.IsCancellationRequested)
                    return;
                double remaining = (deadline - Stopwatch.GetTimestamp())
                                   / (double)Stopwatch.Frequency;
                Thread.Sleep(TimeSpan.FromSeconds(Math.Min(0.05, Math.Max(0.0, remaining))));
            }
        }
    }
}

/// <summary>Network path detection and NIC speed lookup (psutil equivalents).</summary>
public static class NetworkInfo
{
    /// <summary>
    /// True if the path lives on a network share (UNC or mapped network
    /// drive). Always false for local drives (NVMe, SATA, USB, etc.).
    /// </summary>
    public static bool IsNetworkPath(string path)
    {
        try
        {
            string absPath = Path.GetFullPath(path);
            // UNC paths (\\server\share\...) are always network
            if (absPath.StartsWith(@"\\", StringComparison.Ordinal))
                return true;
            string? root = Path.GetPathRoot(absPath);
            if (string.IsNullOrEmpty(root))
                return false;
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// pct% of the fastest active NIC's speed in bytes/sec, or 0 when no
    /// speed can be determined. pct=0.85 leaves ~15% of NIC capacity free
    /// for other processes. Loopback and tunnel adapters are ignored (the
    /// physical link is what matters for SMB reads).
    /// </summary>
    public static double DetectNetCapBytesPerSec(double pct = 0.85)
    {
        try
        {
            long maxBitsPerSec = 0;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
                    or NetworkInterfaceType.Tunnel)
                    continue;
                if (nic.Speed > 0)
                    maxBitsPerSec = Math.Max(maxBitsPerSec, nic.Speed);
            }
            if (maxBitsPerSec > 0)
                return maxBitsPerSec / 8.0 * pct;
        }
        catch
        {
            // fall through to unlimited
        }
        return 0.0;
    }
}
