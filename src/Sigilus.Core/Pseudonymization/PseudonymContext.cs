using System.Collections.Concurrent;
using Sigilus.Core.Domain;

namespace Sigilus.Core.Pseudonymization;

/// <summary>
/// Mantém consistência de substituição por documento: dois CPFs iguais
/// recebem o mesmo pseudo-CPF; dois "João da Silva" recebem o mesmo
/// "Fulano da Silva". Thread-safe.
/// </summary>
public sealed class PseudonymContext
{
    private readonly ConcurrentDictionary<(EntityType, string), string> _cache = new();
    private readonly ConcurrentDictionary<EntityType, int> _counters = new();

    /// <summary>
    /// Cacheia a substituição para <c>(type, normalizedKey)</c>. Se já
    /// existir, devolve a anterior. Caso contrário invoca
    /// <paramref name="factory"/>, que recebe o índice 1-based (1, 2, 3…)
    /// já incrementado para esse tipo.
    /// </summary>
    public string GetOrAdd(EntityType type, string original, Func<int, string> factory)
    {
        var key = (type, Normalize(original));
        if (_cache.TryGetValue(key, out var existing)) return existing;

        var nextIdx = _counters.AddOrUpdate(type, 1, (_, v) => v + 1);
        var value = factory(nextIdx);

        if (!_cache.TryAdd(key, value))
        {
            // Outro thread venceu — devolve o que ficou.
            _counters.AddOrUpdate(type, 0, (_, v) => Math.Max(0, v - 1));
            return _cache[key];
        }
        return value;
    }

    private static string Normalize(string s)
        => string.Concat(s.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
}
