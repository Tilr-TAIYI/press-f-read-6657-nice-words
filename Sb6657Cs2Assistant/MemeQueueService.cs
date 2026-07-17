using System.Text.RegularExpressions;

namespace Sb6657Cs2Assistant;

public sealed record PreparedMeme(string Id, string Text, string Tags, int? CandidateTotal, bool FromCache);

/// <summary>
/// Owns meme selection state. Network work and UI rendering stay separate so a
/// failed prefetch can never replace or repeat the item currently being used.
/// </summary>
public sealed class MemeQueueService
{
    private readonly MemeApiClient _api;
    private readonly HashSet<string> _usedIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reservedIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _tagTotals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<PreparedMeme> _cache = new();
    private IReadOnlyList<MemeTag> _tags = [];
    private HashSet<string> _selectedTags = new(StringComparer.OrdinalIgnoreCase);
    private string _prefix = string.Empty;
    private int _maxLength = 220;
    private string? _activeId;
    private string _filterKey = string.Empty;
    private int? _filteredTotal;
    private int _generation;
    private Task? _prefetchTask;
    private CancellationTokenSource? _prefetchCts;

    public MemeQueueService(MemeApiClient api) => _api = api;

    public event Action<PreparedMeme>? Prefetched;
    public event Action<Exception>? PrefetchFailed;

    public int RedrawnCount { get; private set; }

    public bool HasCachedItem => _cache.Count > 0;

    public bool Configure(
        IReadOnlyList<MemeTag> tags,
        IEnumerable<string> selectedTags,
        string prefix,
        int maxLength)
    {
        var selected = selectedTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var key = string.Join(',', selected.Order(StringComparer.OrdinalIgnoreCase));
        var changed = !string.Equals(_filterKey, key, StringComparison.Ordinal) ||
            !string.Equals(_prefix, prefix, StringComparison.Ordinal) ||
            _maxLength != maxLength;
        if (changed)
        {
            _filterKey = key;
            _prefix = prefix;
            _maxLength = maxLength;
            Reset();
        }

        _tags = tags;
        _selectedTags = selected;
        return changed;
    }

    public void Reset()
    {
        _generation++;
        _prefetchCts?.Cancel();
        _prefetchCts?.Dispose();
        _prefetchCts = null;
        _prefetchTask = null;
        _usedIds.Clear();
        _reservedIds.Clear();
        _tagTotals.Clear();
        _cache.Clear();
        _activeId = null;
        _filteredTotal = null;
    }

    public async Task<PreparedMeme?> GetNextAsync(CancellationToken token)
    {
        if (_cache.Count == 0 && _prefetchTask is { IsCompleted: false })
        {
            try { await _prefetchTask; }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }

        PreparedMeme? next = null;
        if (_cache.Count > 0)
        {
            next = _cache.Dequeue();
            _reservedIds.Remove(next.Id);
        }
        else
        {
            var meme = await GetUniqueMemeAsync(token);
            next = Prepare(meme, fromCache: false);
        }

        if (next is null) return null;
        _activeId = next.Id;
        StartPrefetch(token);
        return next;
    }

    public void MarkConsumed(string id)
    {
        _usedIds.Add(id);
        if (string.Equals(_activeId, id, StringComparison.OrdinalIgnoreCase)) _activeId = null;
    }

    private void StartPrefetch(CancellationToken token)
    {
        if ((_prefetchTask is { IsCompleted: false }) || _cache.Count > 0) return;
        var generation = _generation;
        _prefetchCts?.Dispose();
        _prefetchCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _prefetchTask = PrefetchAsync(generation, _prefetchCts.Token);
    }

    private async Task PrefetchAsync(int generation, CancellationToken token)
    {
        try
        {
            var meme = await GetUniqueMemeAsync(token);
            var prepared = Prepare(meme, fromCache: true);
            if (prepared is null || generation != _generation) return;
            _reservedIds.Add(prepared.Id);
            _cache.Enqueue(prepared);
            Prefetched?.Invoke(prepared);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PrefetchFailed?.Invoke(ex); }
        finally
        {
            if (generation == _generation)
            {
                _prefetchCts?.Dispose();
                _prefetchCts = null;
            }
        }
    }

    private PreparedMeme? Prepare(Meme? meme, bool fromCache)
    {
        if (meme is null) return null;
        var text = Sanitize(_prefix + meme.Barrage, _maxLength);
        return string.IsNullOrWhiteSpace(text)
            ? null
            : new PreparedMeme(meme.Id, text, meme.Tags, _filteredTotal, fromCache);
    }

    private async Task<Meme?> GetUniqueMemeAsync(CancellationToken token)
    {
        var selected = _selectedTags.Where(x => _tags.Any(tag => tag.DictValue.Equals(x, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (selected.Length == 0 || selected.Length == _tags.Count)
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var meme = await _api.GetRandomAsync(token);
                if (IsUsable(meme)) return meme;
                RedrawnCount++;
            }
            return null;
        }

        var available = new List<(string Tag, int Total)>();
        foreach (var tag in selected)
        {
            if (!_tagTotals.TryGetValue(tag, out var total))
            {
                var first = await _api.GetFilteredPageAsync(tag, 1, token);
                total = Math.Max(0, first.Total);
                _tagTotals[tag] = total;
            }
            if (total > 0) available.Add((tag, total));
        }
        if (available.Count == 0) throw new InvalidOperationException("所选标签均没有可用烂梗");

        _filteredTotal = (int)Math.Min(int.MaxValue, available.Sum(x => (long)x.Total));
        if (_usedIds.Count >= _filteredTotal) _usedIds.Clear();

        for (var attempt = 0; attempt < Math.Min(24, Math.Max(8, _filteredTotal.Value)); attempt++)
        {
            var chosen = ChooseWeighted(available);
            var page = Random.Shared.Next(1, chosen.Total + 1);
            var result = await _api.GetFilteredPageAsync(chosen.Tag, page, token);
            _tagTotals[chosen.Tag] = Math.Max(0, result.Total);
            if (IsUsable(result.Meme)) return result.Meme;
            RedrawnCount++;
        }
        return null;
    }

    private static (string Tag, int Total) ChooseWeighted(IReadOnlyList<(string Tag, int Total)> candidates)
    {
        var total = candidates.Sum(x => (long)x.Total);
        var ticket = Random.Shared.NextInt64(total);
        foreach (var candidate in candidates)
        {
            if (ticket < candidate.Total) return candidate;
            ticket -= candidate.Total;
        }
        return candidates[^1];
    }

    private bool IsUsable(Meme? meme) => meme is not null
        && !_usedIds.Contains(meme.Id)
        && !_reservedIds.Contains(meme.Id)
        && !string.Equals(_activeId, meme.Id, StringComparison.OrdinalIgnoreCase);

    public static string Sanitize(string value, int maxLength)
    {
        var clean = Regex.Replace(value ?? string.Empty, @"[\x00-\x1F\x7F]+", " ");
        clean = Regex.Replace(clean, @"\s+", " ").Trim();
        var limit = Math.Clamp(maxLength, 20, 500);
        if (clean.Length <= limit) return clean;
        var end = limit;
        if (end > 0 && char.IsHighSurrogate(clean[end - 1])) end--;
        return clean[..Math.Max(0, end)];
    }
}
