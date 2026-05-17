using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Sigilus.Core.Abstractions;
using Sigilus.Core.Domain;
using Sigilus.Detection.Coordinates;

namespace Sigilus.Detection;

/// <summary>
/// Detector regex genérico parametrizado por padrão + tipo + validador opcional.
/// </summary>
public sealed class RegexEntityDetector : IEntityDetector
{
    public sealed record Rule(
        EntityType Type,
        Regex Pattern,
        float Confidence,
        Func<string, bool>? Validate = null);

    private readonly IReadOnlyList<Rule> _rules;

    public RegexEntityDetector(IReadOnlyList<Rule> rules) => _rules = rules;

    public async IAsyncEnumerable<DetectedEntity> DetectAsync(
        PageContext page,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        var index = new CharCoordIndex(page);
        var text = page.ConcatenatedText;

        foreach (var rule in _rules)
        {
            foreach (Match m in rule.Pattern.Matches(text))
            {
                ct.ThrowIfCancellationRequested();
                if (rule.Validate is not null && !rule.Validate(m.Value)) continue;

                var rects = index.RectsFor(m.Index, m.Length);
                foreach (var rect in rects)
                {
                    if (rect.IsEmpty) continue;
                    yield return new DetectedEntity(
                        Type: rule.Type,
                        MatchedText: m.Value,
                        Confidence: rule.Confidence,
                        Bounds: rect,
                        PageIndex: page.PageIndex,
                        Source: DetectionSource.Regex,
                        CharStart: m.Index,
                        CharLength: m.Length);
                }
            }
        }
    }
}
