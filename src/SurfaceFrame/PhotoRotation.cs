namespace SurfaceFrame;

internal sealed class PhotoRotation
{
    private IReadOnlyList<PhotoItem> collection = [];
    private Queue<string> remaining = new();
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private Dictionary<string, PhotoItem> byKey = new(StringComparer.Ordinal);
    private string? last;
    private bool shuffle;

    internal void Update(IReadOnlyList<PhotoItem> items, bool randomOrder)
    {
        collection = items.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Key, StringComparer.Ordinal).ToArray();
        byKey = collection.ToDictionary(item => item.Key, StringComparer.Ordinal);
        seen.RemoveWhere(key => !byKey.ContainsKey(key));
        var pending = collection.Select(item => item.Key).Where(key => !seen.Contains(key)).ToArray();
        var queued = remaining.Where(byKey.ContainsKey).ToHashSet(StringComparer.Ordinal);
        bool changed = shuffle != randomOrder || queued.Count != remaining.Count || !queued.SetEquals(pending);
        shuffle = randomOrder;
        if (changed)
            SetQueue(pending, avoidImmediateRepeat: false);
    }

    internal PhotoItem? Next()
    {
        if (collection.Count == 0)
            return null;
        if (remaining.Count == 0)
        {
            seen.Clear();
            SetQueue(collection.Select(item => item.Key).ToArray(), avoidImmediateRepeat: true);
        }
        string key = remaining.Dequeue();
        seen.Add(key);
        last = key;
        return byKey[key];
    }

    private void SetQueue(string[] cycle, bool avoidImmediateRepeat)
    {
        if (shuffle)
        {
            Random.Shared.Shuffle(cycle.AsSpan());
            if (avoidImmediateRepeat && cycle.Length > 1 && cycle[0] == last)
                (cycle[0], cycle[1]) = (cycle[1], cycle[0]);
        }
        remaining = new Queue<string>(cycle);
    }
}
