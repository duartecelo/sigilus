using Sigilus.Core.Domain;

namespace Sigilus.Detection.Coordinates;

/// <summary>
/// Mapeia offsets de caractere no <c>PageContext.ConcatenatedText</c> para
/// retângulos em PDF user-space, usando os <c>CharBounds</c> de cada
/// <see cref="TextRun"/>. Quebra em múltiplos retângulos se o span cruzar
/// linhas (salto vertical &gt; altura de linha).
/// </summary>
public sealed class CharCoordIndex
{
    private readonly PdfRect[] _rects;
    private readonly int _length;

    public int Length => _length;

    public CharCoordIndex(PageContext page)
    {
        var totalChars = page.Runs.Sum(r => r.Text.Length);
        var separators = Math.Max(0, page.Runs.Count - 1);   // '\n' entre runs
        _length = totalChars + separators;
        _rects = new PdfRect[_length];

        var idx = 0;
        for (var ri = 0; ri < page.Runs.Count; ri++)
        {
            var run = page.Runs[ri];
            for (var ci = 0; ci < run.Text.Length; ci++)
            {
                _rects[idx++] = ci < run.CharBounds.Count
                    ? run.CharBounds[ci]
                    : default;
            }
            if (ri < page.Runs.Count - 1)
                _rects[idx++] = default;   // separador
        }
    }

    /// <summary>
    /// Retorna 1+ retângulos cobrindo o intervalo [start, start+length).
    /// Quebras de linha são detectadas comparando Y dos chars consecutivos.
    /// </summary>
    public IReadOnlyList<PdfRect> RectsFor(int start, int length)
    {
        if (length <= 0 || start < 0 || start >= _length) return Array.Empty<PdfRect>();
        var end = Math.Min(start + length, _length);

        var result = new List<PdfRect>();
        PdfRect line = default;
        float? lastY = null;
        float? lastH = null;

        for (var i = start; i < end; i++)
        {
            var r = _rects[i];
            if (r.IsEmpty) continue;

            var lineBreak = lastY is float ly && lastH is float lh
                && Math.Abs(r.Y - ly) > lh * 0.6f;

            if (lineBreak)
            {
                if (!line.IsEmpty) result.Add(line);
                line = r;
            }
            else
            {
                line = line.IsEmpty ? r : PdfRect.Union(line, r);
            }
            lastY = r.Y; lastH = r.Height;
        }
        if (!line.IsEmpty) result.Add(line);
        return result;
    }
}
