using System.IO;
using System.Text.Json;
using FlipDrop.Core;

namespace FlipDrop.App;

/// <summary>One received file remembered for the "Received" page. No secrets (no tokens, no bytes).</summary>
public sealed class HistoryEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public FileKind Kind { get; set; }
    public long Size { get; set; }
    public DateTimeOffset Time { get; set; }
    public string Device { get; set; } = "";
    public bool IsRisky { get; set; }
}

/// <summary>
/// Plain-JSON history of the last 100 received files, persisted to <c>data\history.json</c>.
/// Nothing sensitive is stored, so it is not DPAPI-protected.
/// </summary>
public sealed class HistoryStore
{
    private const int MaxItems = 100;
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Lock _lock = new();
    private List<HistoryEntry> _items = [];

    public event Action? Changed;

    public HistoryStore(string path)
    {
        _path = path;
        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
                _items = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(_path)) ?? [];
        }
        catch { _items = []; }
    }

    private void SaveLocked()
    {
        try
        {
            AppPaths.EnsureDataDir();
            File.WriteAllText(_path, JsonSerializer.Serialize(_items, Opts));
        }
        catch { /* non-fatal */ }
    }

    /// <summary>Most-recent-first snapshot.</summary>
    public IReadOnlyList<HistoryEntry> Items
    {
        get { lock (_lock) return _items.AsEnumerable().Reverse().ToList(); }
    }

    public void Add(HistoryEntry entry)
    {
        lock (_lock)
        {
            _items.Add(entry);
            if (_items.Count > MaxItems)
                _items.RemoveRange(0, _items.Count - MaxItems);
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
            SaveLocked();
        }
        Changed?.Invoke();
    }
}
