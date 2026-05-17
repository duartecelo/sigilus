namespace Sigilus.Detection.Validation;

public static class BrazilianIdValidators
{
    public static bool IsValidCpf(ReadOnlySpan<char> input)
    {
        Span<int> d = stackalloc int[11];
        var n = 0;
        foreach (var c in input)
        {
            if (char.IsDigit(c)) { if (n == 11) return false; d[n++] = c - '0'; }
        }
        if (n != 11) return false;
        if (AllSame(d)) return false;

        var s1 = 0;
        for (var i = 0; i < 9; i++) s1 += d[i] * (10 - i);
        var v1 = (s1 * 10) % 11; if (v1 == 10) v1 = 0;
        if (v1 != d[9]) return false;

        var s2 = 0;
        for (var i = 0; i < 10; i++) s2 += d[i] * (11 - i);
        var v2 = (s2 * 10) % 11; if (v2 == 10) v2 = 0;
        return v2 == d[10];
    }

    public static bool IsValidCnpj(ReadOnlySpan<char> input)
    {
        Span<int> d = stackalloc int[14];
        var n = 0;
        foreach (var c in input)
        {
            if (char.IsDigit(c)) { if (n == 14) return false; d[n++] = c - '0'; }
        }
        if (n != 14) return false;
        if (AllSame(d)) return false;

        ReadOnlySpan<int> w1 = stackalloc int[] { 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2 };
        ReadOnlySpan<int> w2 = stackalloc int[] { 6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2 };

        var s1 = 0;
        for (var i = 0; i < 12; i++) s1 += d[i] * w1[i];
        var v1 = s1 % 11; v1 = v1 < 2 ? 0 : 11 - v1;
        if (v1 != d[12]) return false;

        var s2 = 0;
        for (var i = 0; i < 13; i++) s2 += d[i] * w2[i];
        var v2 = s2 % 11; v2 = v2 < 2 ? 0 : 11 - v2;
        return v2 == d[13];
    }

    private static bool AllSame(ReadOnlySpan<int> d)
    {
        for (var i = 1; i < d.Length; i++) if (d[i] != d[0]) return false;
        return true;
    }
}
