using Sigilus.Core.Domain;

namespace Sigilus.Core.Pseudonymization;

/// <summary>
/// Pseudonimizador padrão PT-BR:
/// <list type="bullet">
///   <item>Nomes: Fulano/Beltrano/Ciclano/Deltrano... (masc) ou
///         Fulana/Beltrana/Ciclana/Deltrana... (fem).</item>
///   <item>CPF/CNPJ: valores falsos com checksum válido.</item>
///   <item>E-mail/telefone: valores plausíveis previsíveis.</item>
///   <item>Demais: token <c>[[TIPO_NNN]]</c>.</item>
/// </list>
/// </summary>
public sealed class BrazilianPseudonymizer : IPseudonymizer
{
    // Bases derivadas dos placeholders jurídicos clássicos + variações para 1º, 2º, 3º…
    private static readonly string[] MaleBases =
    {
        "Fulano", "Beltrano", "Ciclano", "Deltrano", "Fulgêncio",
        "Hermógenes", "Indalécio", "Joaquim", "Kléber", "Lourenço",
    };

    private static readonly string[] FemaleBases =
    {
        "Fulana", "Beltrana", "Ciclana", "Deltrana", "Fulgência",
        "Hermínia", "Indalécia", "Joaquina", "Klara", "Lourença",
    };

    public string Substitute(EntityType type, string original, PseudonymContext ctx)
    {
        return type switch
        {
            EntityType.PersonName => ctx.GetOrAdd(type, original, idx => PersonName(original, idx)),
            EntityType.Cpf => ctx.GetOrAdd(type, original, idx => FakeCpf(idx)),
            EntityType.Cnpj => ctx.GetOrAdd(type, original, idx => FakeCnpj(idx)),
            EntityType.Email => ctx.GetOrAdd(type, original, idx => $"pessoa{idx:000}@redacted.local"),
            EntityType.Phone => ctx.GetOrAdd(type, original, idx => $"(11) 9{idx:0000}-{idx:0000}"),
            EntityType.Rg => ctx.GetOrAdd(type, original, idx => $"00.000.{idx:000}-0"),
            EntityType.Oab => ctx.GetOrAdd(type, original, idx => $"OAB/SP {idx:000000}"),
            EntityType.ProcessoCnj => ctx.GetOrAdd(type, original, idx => $"0000{idx:000}-00.0000.0.00.0000"),
            EntityType.Address => ctx.GetOrAdd(type, original, idx => $"[ENDEREÇO_{idx:000}]"),
            EntityType.BankAccount => ctx.GetOrAdd(type, original, idx => $"Ag 0000 / C {idx:000000}-0"),
            _ => ctx.GetOrAdd(type, original, idx => $"[[{type.ToString().ToUpperInvariant()}_{idx:000}]]"),
        };
    }

    private static string PersonName(string original, int idx)
    {
        var gender = GenderInference.Infer(original);
        var bases = gender == Gender.Feminine ? FemaleBases : MaleBases;

        // Quociente decide a base; resto decide o sufixo (1º, 2º, 3º…).
        var baseIdx = (idx - 1) % bases.Length;
        var suffix = (idx - 1) / bases.Length;
        var name = bases[baseIdx];
        return suffix == 0 ? name : $"{name} {ToOrdinal(suffix + 1)}";
    }

    private static string ToOrdinal(int n) => $"{n}º";

    private static string FakeCpf(int idx)
    {
        // 9 dígitos a partir do índice repetido com offset.
        var seed = (idx * 1000003) % 1_000_000_000;
        Span<int> d = stackalloc int[11];
        for (var i = 8; i >= 0; i--) { d[i] = seed % 10; seed /= 10; }
        d[9] = ComputeCpfDv(d[..9]);
        d[10] = ComputeCpfDv(d[..10]);
        return $"{d[0]}{d[1]}{d[2]}.{d[3]}{d[4]}{d[5]}.{d[6]}{d[7]}{d[8]}-{d[9]}{d[10]}";
    }

    private static int ComputeCpfDv(ReadOnlySpan<int> digits)
    {
        var sum = 0;
        var weight = digits.Length + 1;
        foreach (var d in digits) sum += d * weight--;
        var mod = (sum * 10) % 11;
        return mod == 10 ? 0 : mod;
    }

    private static string FakeCnpj(int idx)
    {
        var seed = (idx * 1000003) % 100_000_000;
        Span<int> d = stackalloc int[14];
        for (var i = 7; i >= 0; i--) { d[i] = seed % 10; seed /= 10; }
        d[8] = 0; d[9] = 0; d[10] = 0; d[11] = 1;   // filial 0001
        d[12] = ComputeCnpjDv(d[..12], stackalloc int[] { 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2 });
        d[13] = ComputeCnpjDv(d[..13], stackalloc int[] { 6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2 });
        return $"{d[0]}{d[1]}.{d[2]}{d[3]}{d[4]}.{d[5]}{d[6]}{d[7]}/{d[8]}{d[9]}{d[10]}{d[11]}-{d[12]}{d[13]}";
    }

    private static int ComputeCnpjDv(ReadOnlySpan<int> digits, ReadOnlySpan<int> weights)
    {
        var sum = 0;
        for (var i = 0; i < digits.Length; i++) sum += digits[i] * weights[i];
        var mod = sum % 11;
        return mod < 2 ? 0 : 11 - mod;
    }
}
