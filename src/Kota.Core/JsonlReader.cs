using System.Globalization;
using System.Text.Json;

namespace Kota.Core;

public delegate void LineHandler(ReadOnlySpan<byte> line);

/// <summary>Reads JSON Lines files incrementally without loading them into memory.</summary>
public static class JsonlReader
{
    /// <summary>
    /// Calls <paramref name="onLine"/> for every complete line after <paramref name="offset"/> and
    /// returns the offset just past the last complete line, so the next call can resume there.
    /// The file may be written to by another process while we read it.
    /// </summary>
    public static long ReadFrom(string path, long offset, LineHandler onLine)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        if (offset > fs.Length) offset = 0; // file was truncated or replaced
        fs.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[1 << 20];
        using var carry = new MemoryStream();
        long consumed = offset;
        long chunkStart = offset;
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            int lineStart = 0;
            for (int i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                if (carry.Length > 0)
                {
                    carry.Write(buffer, lineStart, i - lineStart);
                    onLine(TrimCr(carry.GetBuffer().AsSpan(0, (int)carry.Length)));
                    carry.SetLength(0);
                }
                else
                {
                    onLine(TrimCr(buffer.AsSpan(lineStart, i - lineStart)));
                }
                lineStart = i + 1;
                consumed = chunkStart + i + 1;
            }
            if (lineStart < read) carry.Write(buffer, lineStart, read - lineStart);
            chunkStart += read;
        }
        return consumed;
    }

    static ReadOnlySpan<byte> TrimCr(ReadOnlySpan<byte> s) => s.Length > 0 && s[^1] == (byte)'\r' ? s[..^1] : s;

    internal static JsonDocument? TryParse(ReadOnlySpan<byte> line)
    {
        try
        {
            var reader = new Utf8JsonReader(line);
            return JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    internal static long Long(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt64(out var n)
            ? n
            : 0;

    internal static double? Double(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;

    internal static JsonElement? Obj(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v
            : null;

    internal static DateTimeOffset? Time(this JsonElement e, string name) =>
        e.Str(name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : null;
}
