using System.Text;
using iText.Kernel.Geom;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using Sigilus.Core.Domain;

namespace Sigilus.Pdf.Extraction;

/// <summary>
/// Coletor de texto + retângulo por caractere. Não delega à
/// <see cref="LocationTextExtractionStrategy"/> porque essa classe não expõe
/// publicamente os char rects sincronizados com o texto resultante. Em vez
/// disso replicamos a lógica essencial: agrupar render infos, ordenar por
/// linha e inserir espaços entre chunks separados (heurística de "word
/// boundary" — gap horizontal &gt; ~ largura de meio espaço).
/// </summary>
internal sealed class CharCoordExtractionStrategy : IEventListener
{
    private readonly List<Chunk> _chunks = new();

    private string? _text;
    private PdfRect[]? _rects;

    public ICollection<EventType> GetSupportedEvents() => new[] { EventType.RENDER_TEXT };

    public void EventOccurred(IEventData data, EventType type)
    {
        if (type != EventType.RENDER_TEXT) return;
        var render = (TextRenderInfo)data;
        foreach (var ci in render.GetCharacterRenderInfos())
        {
            var ascent = ci.GetAscentLine().GetBoundingRectangle();
            var descent = ci.GetDescentLine().GetBoundingRectangle();
            var rect = new PdfRect(
                descent.GetX(),
                descent.GetY(),
                ascent.GetWidth(),
                ascent.GetY() + ascent.GetHeight() - descent.GetY());

            var text = ci.GetText();
            if (string.IsNullOrEmpty(text)) continue;

            _chunks.Add(new Chunk(
                Text: text,
                Rect: rect,
                StartX: ci.GetBaseline().GetStartPoint().Get(0),
                EndX: ci.GetBaseline().GetEndPoint().Get(0),
                BaselineY: ci.GetBaseline().GetStartPoint().Get(1),
                CharSpaceWidth: ci.GetSingleSpaceWidth()));
        }
    }

    public string Text => _text ??= Materialize().text;

    public IReadOnlyList<PdfRect> CharRects => _rects ??= Materialize().rects;

    private (string text, PdfRect[] rects) Materialize()
    {
        if (_chunks.Count == 0) return (string.Empty, Array.Empty<PdfRect>());

        // Agrupa por linha (baseline Y arredondada à precisão de meio glyph).
        var lines = _chunks
            .GroupBy(c => Math.Round(c.BaselineY, 1))
            .OrderByDescending(g => g.Key)   // top-down: PDF tem Y crescendo pra cima
            .Select(g => g.OrderBy(c => c.StartX).ToList())
            .ToList();

        var sb = new StringBuilder();
        var rects = new List<PdfRect>();

        for (var li = 0; li < lines.Count; li++)
        {
            var line = lines[li];
            for (var i = 0; i < line.Count; i++)
            {
                var c = line[i];
                if (i > 0)
                {
                    var prev = line[i - 1];
                    var gap = c.StartX - prev.EndX;
                    var spaceWidth = Math.Max(prev.CharSpaceWidth, c.CharSpaceWidth);
                    if (spaceWidth > 0 && gap > spaceWidth / 2f && !c.Text.StartsWith(' ') && !prev.Text.EndsWith(' '))
                    {
                        sb.Append(' ');
                        var midX = prev.Rect.Right;
                        var midW = Math.Max(0, c.Rect.X - prev.Rect.Right);
                        rects.Add(new PdfRect(midX, c.Rect.Y, midW, c.Rect.Height));
                    }
                }
                sb.Append(c.Text);
                for (var k = 0; k < c.Text.Length; k++)
                    rects.Add(c.Rect);
            }
            if (li < lines.Count - 1)
            {
                sb.Append('\n');
                rects.Add(default);
            }
        }

        return (sb.ToString(), rects.ToArray());
    }

    private readonly record struct Chunk(
        string Text,
        PdfRect Rect,
        float StartX,
        float EndX,
        float BaselineY,
        float CharSpaceWidth);
}
