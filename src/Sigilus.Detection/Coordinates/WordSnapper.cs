using Sigilus.Core.Domain;

namespace Sigilus.Detection.Coordinates;

/// <summary>
/// Expande um retângulo para cobrir a "palavra completa" que toca o seed,
/// trabalhando em <b>nível de caractere</b> através de todos os runs da
/// página. Resolve dois problemas:
/// <list type="number">
///   <item>Texto nativo costuma vir como 1 único <see cref="TextRun"/>
///   com 1000+ chars — agrupar por run engole a página.</item>
///   <item>OCR fragmenta uma palavra em N runs — agrupar por run engole
///   só uma parte.</item>
/// </list>
/// O snapper acha um caractere "âncora" próximo ao seed e expande
/// horizontalmente até encontrar um separador (espaço) ou char fora da
/// linha do âncora.
/// </summary>
public sealed class WordSnapper
{
    private readonly List<Glyph> _glyphs;
    private readonly float _avgHeight;

    /// <summary>"Glifo" no nível de char: rect + caractere + se é separador.</summary>
    private readonly record struct Glyph(PdfRect Rect, char Ch);

    public WordSnapper(PageContext page)
    {
        _glyphs = new List<Glyph>(capacity: 4096);
        foreach (var run in page.Runs)
        {
            // Em runs OCR (palavra única, sem chars sintéticos por glifo), o
            // CharBounds tem 1 rect por char. Em runs nativos a quantidade
            // bate com Text.Length. Em ambos, iteramos par-a-par.
            var text = run.Text;
            var bounds = run.CharBounds;
            var n = Math.Min(text.Length, bounds.Count);
            for (var i = 0; i < n; i++)
            {
                var rect = bounds[i];
                if (rect.IsEmpty) continue;
                _glyphs.Add(new Glyph(rect, text[i]));
            }

            // Se o run tem um único Bounds e os CharBounds estão vazios,
            // trata o run inteiro como um glifo único (caso OCR sem subdivisão).
            if (n == 0 && !run.Bounds.IsEmpty && !string.IsNullOrEmpty(text))
                _glyphs.Add(new Glyph(run.Bounds, text[0]));
        }
        _avgHeight = _glyphs.Count == 0 ? 10f : _glyphs.Average(g => g.Rect.Height);
    }

    /// <summary>
    /// Devolve o retângulo da palavra/palavras contíguas que cobrem
    /// <paramref name="seed"/>. Limita expansão à <b>linha</b> do âncora
    /// e respeita separadores (espaço, tab).
    /// </summary>
    public PdfRect Snap(PdfRect seed)
    {
        if (seed.IsEmpty || _glyphs.Count == 0) return seed;

        // 1) Acha glifos que intersectam o seed.
        var inside = new List<int>();
        for (var i = 0; i < _glyphs.Count; i++)
        {
            if (Intersects(_glyphs[i].Rect, seed)) inside.Add(i);
        }
        if (inside.Count == 0) return seed;

        // 2) Âncora: glifo com maior área de interseção (mais "no centro").
        var anchorIdx = inside.OrderByDescending(i => IntersectArea(_glyphs[i].Rect, seed)).First();
        var anchor = _glyphs[anchorIdx];

        // 3) Coleta TODOS os glifos da MESMA LINHA do âncora, ordenados por X.
        var sameLine = new List<int>();
        for (var i = 0; i < _glyphs.Count; i++)
        {
            if (SameLine(_glyphs[i].Rect, anchor.Rect)) sameLine.Add(i);
        }
        sameLine.Sort((a, b) => _glyphs[a].Rect.X.CompareTo(_glyphs[b].Rect.X));

        // 4) Acha posição do âncora na linha ordenada.
        var anchorPos = sameLine.IndexOf(anchorIdx);
        if (anchorPos < 0) return seed;

        // 5) Define o RANGE [first, last] na linha que cobre todos os glifos
        //    que estavam DENTRO do seed (não só o âncora).
        var insideSet = new HashSet<int>(inside);
        var first = anchorPos;
        var last = anchorPos;
        for (var i = 0; i < sameLine.Count; i++)
        {
            if (insideSet.Contains(sameLine[i]))
            {
                if (i < first) first = i;
                if (i > last) last = i;
            }
        }

        // 6) Expande à esquerda do `first` SÓ se for "mesma palavra do mesmo tipo".
        //    Letras unem com letras; dígitos com dígitos; pontuação separa
        //    categorias (ex: "CNPJ:" não cola em "8825..."; "Tel." não cola
        //    em "(51)").
        while (first - 1 >= 0)
        {
            var cur = _glyphs[sameLine[first]];
            var prev = _glyphs[sameLine[first - 1]];
            if (!ContiguousHoriz(prev.Rect, cur.Rect)) break;
            if (!SameWordKind(prev.Ch, cur.Ch)) break;
            first--;
        }

        // 7) Idem à direita.
        while (last + 1 < sameLine.Count)
        {
            var cur = _glyphs[sameLine[last]];
            var next = _glyphs[sameLine[last + 1]];
            if (!ContiguousHoriz(cur.Rect, next.Rect)) break;
            if (!SameWordKind(cur.Ch, next.Ch)) break;
            last++;
        }

        // 8) União dos rects do range.
        var result = _glyphs[sameLine[first]].Rect;
        for (var i = first + 1; i <= last; i++)
            result = PdfRect.Union(result, _glyphs[sameLine[i]].Rect);
        return result;
    }

    private static bool Intersects(PdfRect a, PdfRect b)
        => a.Right > b.X && b.Right > a.X && a.Top > b.Y && b.Top > a.Y;

    private static float IntersectArea(PdfRect a, PdfRect b)
    {
        var ix = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X));
        var iy = Math.Max(0, Math.Min(a.Top, b.Top) - Math.Max(a.Y, b.Y));
        return ix * iy;
    }

    private static bool SameLine(PdfRect a, PdfRect b)
    {
        // Mesma linha = baseline parecida (Y), tolerância = 40% da menor altura.
        var refH = Math.Min(a.Height, b.Height);
        return Math.Abs(a.Y - b.Y) < refH * 0.4f
            && Math.Abs(a.Top - b.Top) < refH * 0.6f;
    }

    private static bool ContiguousHoriz(PdfRect left, PdfRect right)
    {
        // Gap aceitável = até meia altura. Acima disso é provavelmente
        // separador entre palavras OU um chunk que não pertence a esta palavra.
        var gap = right.X - left.Right;
        var refH = Math.Min(left.Height, right.Height);
        return gap >= -1f && gap <= refH * 0.45f;
    }

    /// <summary>
    /// True se <paramref name="left"/> e <paramref name="right"/> são do
    /// mesmo "tipo semântico", ou seja, fazem parte da mesma palavra/token:
    /// letra↔letra (com acento/ç), dígito↔dígito, dígito↔separador-numérico,
    /// letra↔hífen/apóstrofo. Pontuação genérica (`:`, `;`, `(`, `)`, `,`,
    /// `—`) quebra → snap não atravessa, preservando o span original.
    /// Whitespace sempre quebra.
    /// </summary>
    private static bool SameWordKind(char a, char b)
    {
        if (char.IsWhiteSpace(a) || char.IsWhiteSpace(b)) return false;

        var aDigit = char.IsDigit(a);
        var bDigit = char.IsDigit(b);
        var aLetter = char.IsLetter(a);
        var bLetter = char.IsLetter(b);

        // Conectores numéricos: . - / dentro de números (CPF/CNPJ/CEP/telefone).
        bool aNumSep = a is '.' or '-' or '/' or ',';
        bool bNumSep = b is '.' or '-' or '/' or ',';

        // Conectores de palavra: . - ' dentro de "Dr." "B.B.B." "O'Hara".
        bool aWordSep = a is '.' or '-' or '\'' or '’';
        bool bWordSep = b is '.' or '-' or '\'' or '’';

        if (aDigit && bDigit) return true;
        if (aLetter && bLetter) return true;
        if (aDigit && bNumSep) return true;
        if (aNumSep && bDigit) return true;
        if (aLetter && bWordSep) return true;
        if (aWordSep && bLetter) return true;

        return false;
    }
}
