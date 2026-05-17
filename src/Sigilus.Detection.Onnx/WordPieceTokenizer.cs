using System.Text;

namespace Sigilus.Detection.Onnx;

/// <summary>
/// Tokenizador BERT WordPiece minimalista para PT-BR. Lê <c>vocab.txt</c>
/// (1 token por linha), faz lowercase opcional, separação básica por
/// pontuação/espaço, e mapeia para IDs. Devolve offsets (charStart, charLen)
/// no texto original para permitir mapear spans BIO de volta para retângulos.
/// </summary>
public sealed class WordPieceTokenizer
{
    public sealed record Encoding(int[] Ids, int[] Mask, (int CharStart, int CharLen)[] Offsets);

    private readonly Dictionary<string, int> _vocab;
    private readonly int _unkId;
    private readonly int _clsId;
    private readonly int _sepId;
    private readonly int _padId;
    private readonly int _maxLen;
    private readonly bool _lowercase;
    private const string Prefix = "##";

    public WordPieceTokenizer(string vocabPath, int maxLen = 512, bool lowercase = true)
    {
        _vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        var i = 0;
        foreach (var line in File.ReadLines(vocabPath))
            _vocab[line.Trim()] = i++;
        _unkId = _vocab.GetValueOrDefault("[UNK]", 100);
        _clsId = _vocab.GetValueOrDefault("[CLS]", 101);
        _sepId = _vocab.GetValueOrDefault("[SEP]", 102);
        _padId = _vocab.GetValueOrDefault("[PAD]", 0);
        _maxLen = maxLen;
        _lowercase = lowercase;
    }

    public Encoding Encode(string text)
    {
        var ids = new List<int> { _clsId };
        var offsets = new List<(int, int)> { (0, 0) };

        foreach (var (word, wordStart) in BasicTokenize(text))
        {
            var src = _lowercase ? word.ToLowerInvariant() : word;
            WordPieceEncode(src, wordStart, ids, offsets);
            if (ids.Count >= _maxLen - 1) break;
        }

        ids.Add(_sepId);
        offsets.Add((0, 0));

        while (ids.Count < _maxLen) { ids.Add(_padId); offsets.Add((0, 0)); }
        var mask = ids.Select(x => x == _padId ? 0 : 1).ToArray();

        return new Encoding(ids.ToArray(), mask, offsets.ToArray());
    }

    private void WordPieceEncode(string word, int wordStart, List<int> ids, List<(int, int)> offsets)
    {
        var start = 0;
        var len = word.Length;
        var pieces = new List<(int id, int charStart, int charLen)>();
        while (start < len)
        {
            var end = len;
            var matchedId = -1;
            var matched = 0;
            while (start < end)
            {
                var sub = (start > 0 ? Prefix : string.Empty) + word[start..end];
                if (_vocab.TryGetValue(sub, out var id))
                {
                    matchedId = id;
                    matched = end - start;
                    break;
                }
                end--;
            }
            if (matchedId < 0)
            {
                pieces.Clear();
                pieces.Add((_unkId, 0, len));
                break;
            }
            pieces.Add((matchedId, start, matched));
            start += matched;
        }
        foreach (var (id, cs, cl) in pieces)
        {
            ids.Add(id);
            offsets.Add((wordStart + cs, cl));
        }
    }

    private static IEnumerable<(string Word, int Start)> BasicTokenize(string text)
    {
        var sb = new StringBuilder();
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            var atEnd = i == text.Length;
            var ch = atEnd ? ' ' : text[i];
            var isSep = atEnd || char.IsWhiteSpace(ch) || char.IsPunctuation(ch);
            if (isSep)
            {
                if (sb.Length > 0)
                {
                    yield return (sb.ToString(), start);
                    sb.Clear();
                }
                if (!atEnd && char.IsPunctuation(ch))
                    yield return (ch.ToString(), i);
                start = i + 1;
            }
            else
            {
                if (sb.Length == 0) start = i;
                sb.Append(ch);
            }
        }
    }
}
