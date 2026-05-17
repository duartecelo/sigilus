namespace Sigilus.Core.Domain;

/// <summary>
/// Retângulo no espaço de usuário do PDF: origem na <b>base-esquerda</b>, unidade em pontos (1/72").
/// Esta é a representação canônica em toda a fronteira pública do Sigilus.
/// </summary>
public readonly record struct PdfRect(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Top => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static PdfRect Union(PdfRect a, PdfRect b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        var r = Math.Max(a.Right, b.Right);
        var t = Math.Max(a.Top, b.Top);
        return new PdfRect(x, y, r - x, t - y);
    }
}
