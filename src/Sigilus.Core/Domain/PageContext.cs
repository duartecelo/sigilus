namespace Sigilus.Core.Domain;

public sealed record PageContext(
    int PageIndex,
    PageClassification Classification,
    string ConcatenatedText,
    IReadOnlyList<TextRun> Runs,
    IReadOnlyList<PageElement> Elements,
    float WidthPts,
    float HeightPts,
    int RotationDegrees);
