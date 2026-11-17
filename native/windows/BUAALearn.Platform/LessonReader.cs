using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BUAALearn.Platform;

public sealed record TranscriptRow(string Id, double Start, double End, string Text);
public enum ReaderTab { Timeline, Transcript, Slides, Materials }
public sealed record ReadingPosition(ReaderTab Tab, string Search, string? Anchor, double Offset, string Snapshot);

/// Offline reading belongs to a fixed account. Analysis citations are always
/// resolved against that analysis' evidence, even after a material refresh.
public sealed class LessonReader
{
    private readonly AccountStore store;
    public string LessonId { get; }
    public string Owner => store.Owner;
    public LessonReader(AccountStore store, string lessonId)
    {
        ArgumentNullException.ThrowIfNull(store);
        var lesson = store.Get("lesson", lessonId);
        if (lesson.ValueKind != JsonValueKind.Object) throw new ArgumentException("未找到这节课。", nameof(lessonId));
        this.store = store; LessonId = lessonId;
    }
    public JsonElement Materials => store.Get("materials", LessonId);
    public JsonElement Analysis => store.Get("analysis", LessonId);
    public IReadOnlyList<TranscriptRow> Transcript => Parse(Materials, false);
    public IReadOnlyList<TranscriptRow> Evidence => Parse(Analysis, true);
    public IReadOnlyList<TranscriptRow> Search(string query, bool savedEvidence = false)
    {
        ArgumentNullException.ThrowIfNull(query);
        var rows = savedEvidence ? Evidence : Transcript;
        if (query.Length == 0) return rows;
        return rows.Where(x => CultureInfo.InvariantCulture.CompareInfo.IndexOf(x.Text, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth) >= 0).ToArray();
    }
    public IReadOnlyList<TranscriptRow> SourceRange(string startId, string endId) => Range(Evidence, startId, endId);
    public IReadOnlyList<TranscriptRow> Source(JsonElement finding) => SourceFrom(Analysis, finding);
    internal static IReadOnlyList<TranscriptRow> SourceFrom(JsonElement document, JsonElement finding)
    {
        if (finding.ValueKind != JsonValueKind.Object) return [];
        if (finding.TryGetProperty("source_range", out var range) && range.ValueKind == JsonValueKind.Object &&
            range.TryGetProperty("start_id", out var start) && range.TryGetProperty("end_id", out var end) && start.ValueKind == JsonValueKind.String && end.ValueKind == JsonValueKind.String)
            return Range(Parse(document, true), start.GetString()!, end.GetString()!);
        if (!finding.TryGetProperty("evidence_ids", out var refs) || refs.ValueKind != JsonValueKind.Array) return [];
        var ids = refs.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
        var rows = Parse(document, true); var selected = rows.Where(x => ids.Contains(x.Id)).ToArray();
        // Any invalid citation invalidates the whole range; never silently repair it.
        return selected.Length == ids.Count && selected.Length > 0 ? Range(rows, selected[0].Id, selected[^1].Id) : [];
    }
    private static IReadOnlyList<TranscriptRow> Range(IReadOnlyList<TranscriptRow> rows, string start, string end)
    {
        var first = -1; var last = -1;
        for (var i = 0; i < rows.Count; i++) { if (rows[i].Id == start) first = i; if (rows[i].Id == end) last = i; }
        return first >= 0 && last >= first ? rows.Skip(first).Take(last - first + 1).ToArray() : [];
    }
    internal static IReadOnlyList<TranscriptRow> Parse(JsonElement document, bool evidence)
    {
        if (document.ValueKind != JsonValueKind.Object || !document.TryGetProperty(evidence ? "evidence" : "segments", out var raw)) return [];
        var rows = new List<TranscriptRow>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? key, JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("start", out var s) || s.ValueKind != JsonValueKind.Number || !s.TryGetDouble(out var start) ||
                !value.TryGetProperty("end", out var e) || e.ValueKind != JsonValueKind.Number || !e.TryGetDouble(out var end) || !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end < start ||
                !value.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String) return;
            var id = key ?? (value.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null);
            if (string.IsNullOrEmpty(id) || !seen.Add(id)) return;
            rows.Add(new(id, start, end, t.GetString()!));
        }
        if (evidence && raw.ValueKind == JsonValueKind.Object) foreach (var property in raw.EnumerateObject()) Add(property.Name, property.Value);
        else if (!evidence && raw.ValueKind == JsonValueKind.Array) foreach (var value in raw.EnumerateArray()) Add(null, value);
        return rows.OrderBy(x => x.Start).ThenBy(x => x.End).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
    }
    private string StateId(ReaderTab tab) => "reading:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(LessonId))) + ":" + tab;
    private string Snapshot(ReaderTab tab)
    {
        var data = tab == ReaderTab.Timeline ? Analysis : Materials;
        if (data.ValueKind == JsonValueKind.Null) return "";
        // Hash the complete snapshot as imported/older analyses may lack a digest.
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(data.GetRawText())));
    }
    public void SavePosition(ReaderTab tab, string search, string? anchor, double offset)
    {
        if (!Enum.IsDefined(tab)) throw new ArgumentOutOfRangeException(nameof(tab));
        ArgumentNullException.ThrowIfNull(search);
        if (search.Length > 4096 || anchor?.Length > 512 || !double.IsFinite(offset) || offset < 0) throw new ArgumentException("阅读位置无效。");
        store.PutBatch([
            new("meta", StateId(tab), new ReadingPosition(tab, search, anchor, offset, Snapshot(tab))),
            new("meta", "reader:last:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(LessonId))), new { tab = (int)tab })
        ]);
    }
    public ReadingPosition RestorePosition(ReaderTab tab)
    {
        if (!Enum.IsDefined(tab)) throw new ArgumentOutOfRangeException(nameof(tab));
        var snapshot = Snapshot(tab); var raw = store.Get("meta", StateId(tab));
        ReadingPosition? saved;
        try { saved = raw.ValueKind == JsonValueKind.Object ? raw.Deserialize<ReadingPosition>() : null; }
        catch (JsonException) { saved = null; }
        if (saved == null || saved.Tab != tab || saved.Search == null || saved.Search.Length > 4096 || saved.Anchor?.Length > 512 || !double.IsFinite(saved.Offset) || saved.Offset < 0)
            return new(tab, "", null, 0, snapshot);
        if (saved.Snapshot != snapshot) return saved with { Anchor = null, Offset = 0, Snapshot = snapshot };
        if (tab is ReaderTab.Transcript or ReaderTab.Timeline && saved.Anchor != null && !(tab == ReaderTab.Timeline ? Evidence : Transcript).Any(x => x.Id == saved.Anchor))
            return saved with { Anchor = null, Offset = 0 };
        return saved;
    }
    public ReaderTab LastTab
    {
        get
        {
            var raw = store.Get("meta", "reader:last:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(LessonId))));
            return raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("tab", out var tab) && tab.ValueKind == JsonValueKind.Number && tab.TryGetInt32(out var value) && Enum.IsDefined((ReaderTab)value) ? (ReaderTab)value : ReaderTab.Timeline;
        }
    }
}
