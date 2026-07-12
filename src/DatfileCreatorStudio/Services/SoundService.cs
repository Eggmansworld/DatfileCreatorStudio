using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DatfileCreatorStudio.Services;

/// <summary>
/// Plays the end-of-run audio cue (.wav or .mp3) with no external packages.
/// Windows uses the built-in MCI (winmm), which handles both formats and
/// plays asynchronously; Linux/macOS spawn the first available system player.
/// Everything is best-effort — a missing file or player never disturbs a run.
/// </summary>
public static class SoundService
{
    /// <summary>Folder of bundled + user sound files, next to the executable.</summary>
    public static string SoundsDir => Path.Combine(AppContext.BaseDirectory, "sounds");

    /// <summary>Shipped default cue, used when no file is configured.</summary>
    public const string DefaultFileName = "datfile_generation_complete_default1.wav";

    private const string Alias = "dcsCompletionCue";
    private static Process? _unixPlayer;

    /// <summary>
    /// Turn the configured value into an absolute path: empty → bundled
    /// default; relative → relative to the exe folder. Returns null when the
    /// file doesn't exist.
    /// </summary>
    public static string? Resolve(string configured)
    {
        string path = configured.Trim();
        if (path.Length == 0)
            path = Path.Combine(SoundsDir, DefaultFileName);
        else if (!Path.IsPathRooted(path))
            path = Path.Combine(AppContext.BaseDirectory, path);
        return File.Exists(path) ? Path.GetFullPath(path) : null;
    }

    /// <summary>
    /// Store paths inside the app folder as relative (with '/' separators) so
    /// the config stays valid when the portable folder is moved.
    /// </summary>
    public static string ToPortablePath(string path)
    {
        path = path.Trim();
        if (path.Length == 0 || !Path.IsPathRooted(path))
            return path.Replace('\\', '/');
        string rel = Path.GetRelativePath(AppContext.BaseDirectory, path);
        bool outside = rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel);
        return (outside ? path : rel).Replace('\\', '/');
    }

    /// <summary>Fire-and-forget playback; stops any previous cue first.</summary>
    public static void Play(string absolutePath)
    {
        try
        {
            Stop();
            if (OperatingSystem.IsWindows())
                PlayWindows(absolutePath);
            else
                PlayUnix(absolutePath);
        }
        catch
        {
            // An audio cue must never break anything.
        }
    }

    public static void Stop()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                MciSendString($"close {Alias}", null, 0, IntPtr.Zero);
            else if (_unixPlayer is { HasExited: false } p)
                p.Kill();
        }
        catch
        {
            // Nothing was playing (or the player already exited) — fine.
        }
    }

    // ── Windows: MCI plays .wav and .mp3 asynchronously ──────────────────

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "mciSendStringW")]
    private static extern int MciSendString(string command, StringBuilder? buffer, int bufferSize, IntPtr callback);

    private static void PlayWindows(string path)
    {
        // MCI picks the device from the file extension (waveaudio/mpegvideo);
        // the quoted form handles spaces in the path.
        if (MciSendString($"open \"{path}\" alias {Alias}", null, 0, IntPtr.Zero) == 0)
            MciSendString($"play {Alias}", null, 0, IntPtr.Zero);
    }

    // ── Linux/macOS: first available system player wins ──────────────────

    private static void PlayUnix(string path)
    {
        bool isMp3 = path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
        (string Cmd, string[] Args)[] candidates = OperatingSystem.IsMacOS()
            ? [("afplay", [path])]
            : isMp3
                ? [("mpg123", ["-q", path]),
                   ("ffplay", ["-nodisp", "-autoexit", "-loglevel", "quiet", path]),
                   ("paplay", [path])]
                : [("paplay", [path]),
                   ("aplay", ["-q", path]),
                   ("ffplay", ["-nodisp", "-autoexit", "-loglevel", "quiet", path])];

        foreach (var (cmd, args) in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo(cmd)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (string a in args)
                    psi.ArgumentList.Add(a);
                var p = Process.Start(psi);
                if (p is null)
                    continue;
                p.EnableRaisingEvents = true;
                p.Exited += (_, _) => p.Dispose();
                _unixPlayer = p;
                return;
            }
            catch
            {
                // Player not installed — try the next candidate.
            }
        }
    }
}
