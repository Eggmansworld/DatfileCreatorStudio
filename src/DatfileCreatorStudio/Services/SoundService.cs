using System.Runtime.InteropServices;
using System.Text;

namespace DatfileCreatorStudio.Services;

/// <summary>
/// Plays the end-of-run audio cue (.wav or .mp3) with no external packages,
/// using the built-in Windows MCI (winmm) — it handles both formats and plays
/// asynchronously. Everything is best-effort: a missing file never disturbs
/// a run.
/// </summary>
public static class SoundService
{
    /// <summary>Folder of bundled + user sound files, next to the executable.</summary>
    public static string SoundsDir => Path.Combine(AppContext.BaseDirectory, "sounds");

    /// <summary>Shipped default cue, used when no file is configured.</summary>
    public const string DefaultFileName = "datfile_generation_complete_default1.wav";

    private const string Alias = "dcsCompletionCue";

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
            // MCI picks the device from the file extension (waveaudio /
            // mpegvideo); the quoted form handles spaces in the path.
            if (MciSendString($"open \"{absolutePath}\" alias {Alias}", null, 0, IntPtr.Zero) == 0)
                MciSendString($"play {Alias}", null, 0, IntPtr.Zero);
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
            MciSendString($"close {Alias}", null, 0, IntPtr.Zero);
        }
        catch
        {
            // Nothing was playing — fine.
        }
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "mciSendStringW")]
    private static extern int MciSendString(string command, StringBuilder? buffer, int bufferSize, IntPtr callback);
}
