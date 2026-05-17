using System.Text.RegularExpressions;
using Sigilus.Core.Domain;

namespace Sigilus.Detection.Validation;

/// <summary>
/// Filtra "alucinações" comuns do LLM: trechos que ele extrai como entidade
/// mas que são, na verdade, datas, números de processo, leis,
/// cargos genéricos, status burocráticos. Cada regra retorna <c>true</c>
/// se o texto DEVE SER DESCARTADO (= não pseudonimizar).
///
/// <para>Atua APENAS em detecções vindas do LLM (<c>EntityType.PersonName /
/// Address / Other</c>). Detecções estruturadas (CPF/CNPJ/Email/etc) passam
/// sempre.</para>
/// </summary>
public static class LlmHallucinationFilter
{
    private static readonly Regex Date = new(
        @"^\s*\d{1,2}[/.\-]\d{1,2}[/.\-]\d{2,4}\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex YearOnly = new(
        @"^\s*(?:19|20)\d{2}\s*$",
        RegexOptions.Compiled);

    private static readonly Regex Time = new(
        @"^\s*\d{1,2}:\d{2}(:\d{2})?\s*$",
        RegexOptions.Compiled);

    private static readonly Regex Cep = new(
        @"^\s*\d{5}-?\d{3}\s*$",
        RegexOptions.Compiled);

    private static readonly Regex MoneyOrNumber = new(
        @"^\s*R?\$?\s*[\d.,]+\s*$",
        RegexOptions.Compiled);

    // Processo CNJ moderno: 0000000-00.0000.0.00.0000
    private static readonly Regex ProcessoCnj = new(
        @"^\s*\d{7}-\d{2}\.\d{4}\.\d\.\d{2}\.\d{4}\s*$",
        RegexOptions.Compiled);

    // Processo antigo: 1.11.0009530-3 ou 001/1.11.0001234-5
    private static readonly Regex ProcessoLegado = new(
        @"^\s*\d{1,3}[./]\d{1,3}[./]\d{4,}-\d\s*$",
        RegexOptions.Compiled);

    // OAB-like: 12345/RS, 123456/UF
    private static readonly Regex OabNumeric = new(
        @"^\s*\d{3,7}\s*/\s*[A-Z]{2}\s*$",
        RegexOptions.Compiled);

    // Inquérito/protocolo: 00815.001.632/2021, DI.00815.02020/2016
    private static readonly Regex InqueritoCodigo = new(
        @"^\s*[A-Z]{0,4}\.?\s*\d{3,5}[./]\d{3,5}[./]\d{3,5}\s*$",
        RegexOptions.Compiled);

    // Lei: Lei nº 8.625/1993, Lei 7.347/85
    private static readonly Regex Lei = new(
        @"^\s*(?:Lei|LEI|Art\.?|Artigo|Decreto|DECRETO|S[uú]mula)\b",
        RegexOptions.Compiled);

    // URL
    private static readonly Regex Url = new(
        @"^\s*(?:https?://|www\.)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Texto todo em MAIÚSCULAS sem nome próprio (status burocrático):
    // "AGUARDA AUDIÊNCIA", "PROCESSO DISTRIBUÍDO", "CONCLUSÃO AO JUIZ".
    // Heurística: 100% caixa alta + cada palavra >= 3 chars + termos típicos.
    private static readonly HashSet<string> StatusKeywords = new(StringComparer.Ordinal)
    {
        "AGUARDA","AGUARDANDO","CONCLUSAO","CONCLUSÃO","DISTRIBUIDO","DISTRIBUÍDO",
        "JUNTADA","JUNTADO","DESPACHO","SENTENÇA","SENTENCA","AUTOS","CARTÓRIO","CARTORIO",
        "AUDIÊNCIA","AUDIENCIA","JULGAMENTO","SUSPENSAO","SUSPENSÃO","RECEBIDOS","RETORNADOS",
        "DECISÃO","DECISAO","INTIMAÇÃO","INTIMACAO","CITAÇÃO","CITACAO","NOTIFICAÇÃO","NOTIFICACAO",
        "PROCESSO","PROTOCOLO","VISTA","CARGA","ADVOGADO","PETIÇÃO","PETICAO","OFÍCIO","OFICIO",
        "MANDADO","CERTIDÃO","CERTIDAO","JUIZ","JUÍZ","PROMOTOR","RELATOR","DECURSO","PRAZO",
    };

    // Cargos / títulos / honoríficos sozinhos (sem nome próprio acompanhando).
    private static readonly HashSet<string> CargoTitulos = new(StringComparer.OrdinalIgnoreCase)
    {
        "Promotor","Promotora","Promotor de Justiça","Promotora de Justiça",
        "Promotor(a)","Promotor(a) de Justiça",
        "Juiz","Juíza","Juiz de Direito","Juíza de Direito",
        "Desembargador","Desembargadora","Ministro","Ministra",
        "Diretor","Diretora","Presidente","Vice-Presidente","Secretário","Secretária",
        "Assistente","Assistente Financeiro","Assistente Administrativo",
        "Procurador","Procuradora","Defensor","Defensora",
        "Advogado","Advogada","Delegado","Delegada","Escrivão","Escrivã",
        "Excelentíssimo","Excelentíssima","Excelentissimo","Excelentissima",
        "Senhor","Senhora","Sr.","Sra.","Dr.","Dra.",
    };

    /// <summary>Retorna true se a detecção deve ser DESCARTADA.</summary>
    public static bool IsLikelyHallucination(EntityType type, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var t = text.Trim();

        // Tipos estruturados (CPF/CNPJ/...) passam — quem chegou aqui já foi validado por checksum.
        if (type is EntityType.Cpf or EntityType.Cnpj or EntityType.Email
                  or EntityType.Phone or EntityType.Rg or EntityType.Oab
                  or EntityType.ProcessoCnj or EntityType.BankAccount)
            return false;

        if (Date.IsMatch(t)) return true;
        if (YearOnly.IsMatch(t)) return true;
        if (Time.IsMatch(t)) return true;
        if (Cep.IsMatch(t)) return true;
        if (MoneyOrNumber.IsMatch(t)) return true;
        if (ProcessoCnj.IsMatch(t)) return true;
        if (ProcessoLegado.IsMatch(t)) return true;
        if (OabNumeric.IsMatch(t)) return true;
        if (InqueritoCodigo.IsMatch(t)) return true;
        if (Lei.IsMatch(t)) return true;
        if (Url.IsMatch(t)) return true;

        // Cargo / título sozinho.
        var compact = t.Trim().TrimEnd(',', '.', ';', ':');
        if (CargoTitulos.Contains(compact)) return true;

        // Status burocrático: tudo em MAIÚSCULAS (acentos contam) + sem dígitos
        // + pelo menos uma palavra reconhecida como status.
        if (IsAllCaps(t))
        {
            foreach (var w in t.Split(new[] { ' ', '\t', '\n', ',', '.', ';', ':' },
                                       StringSplitOptions.RemoveEmptyEntries))
            {
                if (StatusKeywords.Contains(w)) return true;
            }
        }

        return false;
    }

    private static bool IsAllCaps(string s)
    {
        var hasLetter = false;
        foreach (var c in s)
        {
            if (char.IsLetter(c))
            {
                hasLetter = true;
                if (!char.IsUpper(c)) return false;
            }
        }
        return hasLetter;
    }
}
