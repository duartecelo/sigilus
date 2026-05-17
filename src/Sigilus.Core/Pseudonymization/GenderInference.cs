namespace Sigilus.Core.Pseudonymization;

public enum Gender { Masculine, Feminine, Unknown }

/// <summary>
/// Inferência de gênero por sufixo + lista curta de exceções. Bom o
/// suficiente para PT-BR jurídico — onde "Maria" e "João" dominam.
/// </summary>
public static class GenderInference
{
    private static readonly HashSet<string> MaleEndingInA = new(StringComparer.OrdinalIgnoreCase)
    {
        "Joshua", "Akira", "Costa", "Silva", "Nicola", "Andrea", "Luca",
        "Iva", "Issa",
    };

    private static readonly HashSet<string> FemaleNotEndingInA = new(StringComparer.OrdinalIgnoreCase)
    {
        "Beatriz", "Inês", "Ines", "Carmen", "Luz", "Esther", "Ruth",
        "Cíntia", "Iris", "Íris", "Mércia", "Dalva", "Mercedes",
    };

    public static Gender Infer(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return Gender.Unknown;

        // Texto OCR vem com \n entre palavras. Quebramos em qualquer whitespace
        // (espaço, tab, \n) e pegamos o primeiro token não-vazio como "primeiro
        // nome" — heurística do gênero. Sem isso, "Cristiano\nAraújo\nda\nSilva"
        // vira um único "nome" terminando em 'a' (Silva) → falsa Feminine.
        var first = fullName
            .Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()
            ?? string.Empty;
        first = first.Trim().TrimEnd(',', '.', ';', ':');
        if (first.Length == 0) return Gender.Unknown;

        if (FemaleNotEndingInA.Contains(first)) return Gender.Feminine;
        if (MaleEndingInA.Contains(first)) return Gender.Masculine;

        var last = char.ToLowerInvariant(first[^1]);
        return last switch
        {
            'a' => Gender.Feminine,
            'e' => Gender.Feminine,   // Adelaide, Beatrice — minoria mas conservador
            _ => Gender.Masculine,
        };
    }
}
