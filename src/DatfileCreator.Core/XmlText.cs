using System.Text;
using System.Text.RegularExpressions;

namespace DatfileCreator.Core;

/// <summary>
/// Text handling for dat output: CP437 control-glyph translation, XML escaping,
/// and rom/file name sanitising. Ported 1:1 from the Python suite so the XML
/// output is byte-identical.
/// </summary>
public static partial class XmlText
{
    // Python's cp437 codec (and .NET's IBM437) decodes ZIP-internal filename
    // bytes 0x01-0x1F as Unicode control characters U+0001-U+001F, which are
    // illegal in XML 1.0. RomVault's own CP437 table (CodePage437.cs) maps
    // those same bytes to graphical Unicode symbols — all valid XML. We
    // translate using RomVault's exact table so dat output matches RomVault's
    // own interpretation. U+0009/000A/000D (tab/LF/CR) are legal XML and left
    // as-is. U+0000 and U+FFFE/FFFF are stripped (unrepresentable in XML 1.0).
    private static readonly Dictionary<char, char> Cp437CtrlToGlyph = new()
    {
        ['\x01'] = '☺', // ☺
        ['\x02'] = '☻', // ☻
        ['\x03'] = '♥', // ♥
        ['\x04'] = '♦', // ♦
        ['\x05'] = '♣', // ♣
        ['\x06'] = '♠', // ♠
        ['\x07'] = '•', // •
        ['\x08'] = '◘', // ◘
        ['\x0b'] = '♂', // ♂
        ['\x0c'] = '♀', // ♀
        ['\x0e'] = '♫', // ♫
        ['\x0f'] = '☼', // ☼
        ['\x10'] = '►', // ►
        ['\x11'] = '◄', // ◄
        ['\x12'] = '↕', // ↕
        ['\x13'] = '‼', // ‼
        ['\x14'] = '¶', // ¶
        ['\x15'] = '§', // §
        ['\x16'] = '▬', // ▬
        ['\x17'] = '↨', // ↨
        ['\x18'] = '↑', // ↑
        ['\x19'] = '↓', // ↓
        ['\x1a'] = '→', // →
        ['\x1b'] = '←', // ←
        ['\x1c'] = '∟', // ∟
        ['\x1d'] = '↔', // ↔
        ['\x1e'] = '▲', // ▲
        ['\x1f'] = '▼', // ▼
    };

    /// <summary>
    /// Translate CP437 control chars to their RomVault graphical equivalents,
    /// then strip the handful of code points that have no valid XML 1.0 form.
    /// </summary>
    public static string FixXmlChars(string s)
    {
        if (s.Length == 0)
            return s;

        StringBuilder? sb = null;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (Cp437CtrlToGlyph.TryGetValue(c, out char glyph))
            {
                sb ??= new StringBuilder(s, 0, i, s.Length);
                sb.Append(glyph);
            }
            else if (c is '\x00' or (char)0xFFFE or (char)0xFFFF)
            {
                sb ??= new StringBuilder(s, 0, i, s.Length);
                // stripped
            }
            else
            {
                sb?.Append(c);
            }
        }
        return sb?.ToString() ?? s;
    }

    // Windows forbids these characters in filenames. ZIP archives allow any
    // byte sequence in internal paths; each offending char becomes '_' so the
    // dat is always safe to materialise on NTFS. '/' is intentionally excluded:
    // it is the path-component separator inside ZIP internal paths.
    [GeneratedRegex("[\\\\:*?\"<>|]")]
    private static partial Regex WinForbiddenRegex();

    /// <summary>
    /// Replace Windows-forbidden filename characters with '_'. Preserves '/'
    /// as the ZIP path-component separator. Strips trailing spaces from
    /// directory components only; trailing spaces on the final filename
    /// component are left untouched (legal in ZIP and in the dat).
    /// </summary>
    public static string SanitizeRomName(string name)
    {
        name = WinForbiddenRegex().Replace(name, "_");
        string[] parts = name.Split('/');
        if (parts.Length > 1)
        {
            for (int i = 0; i < parts.Length - 1; i++)
                parts[i] = parts[i].TrimEnd(' ');
        }
        return string.Join('/', parts);
    }

    /// <summary>
    /// Escape like Python's xml.sax.saxutils.escape: &amp;, &lt;, &gt; always.
    /// </summary>
    private static string EscapeBase(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Escape for XML attribute values, fixing CP437/XML char issues first.</summary>
    public static string Xa(string value) =>
        EscapeBase(FixXmlChars(value)).Replace("\"", "&quot;").Replace("'", "&apos;");

    /// <summary>Escape for XML element content, fixing CP437/XML char issues first.</summary>
    public static string Xe(string value) =>
        EscapeBase(FixXmlChars(value));

    [GeneratedRegex("[<>:\"/\\\\|?*]")]
    private static partial Regex FilenameIllegalRegex();

    /// <summary>Strip characters illegal in Windows filenames.</summary>
    public static string SafeFilename(string s) => FilenameIllegalRegex().Replace(s, "_");
}
