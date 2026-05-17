using System.Text.RegularExpressions;
using Sigilus.Core.Domain;
using Sigilus.Detection.Validation;

namespace Sigilus.Detection;

/// <summary>
/// Conjunto padrão de regras para PDFs jurídicos brasileiros. Cada regex usa
/// <c>RegexOptions.Compiled</c> e timeout de 200ms para evitar ReDoS.
/// </summary>
public static class BrazilianRegexRules
{
    private static readonly RegexOptions Opts =
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);

    public static IReadOnlyList<RegexEntityDetector.Rule> Default { get; } = new RegexEntityDetector.Rule[]
    {
        // ---- IDs estritos (validados por checksum) ----
        new(EntityType.Cpf,
            new Regex(@"\b\d{3}\.\d{3}\.\d{3}-\d{2}\b", Opts, Timeout),
            0.99f, s => BrazilianIdValidators.IsValidCpf(s)),

        new(EntityType.Cnpj,
            new Regex(@"\b\d{2}\.\d{3}\.\d{3}/\d{4}-\d{2}\b", Opts, Timeout),
            0.99f, s => BrazilianIdValidators.IsValidCnpj(s)),

        // ---- IDs OCR-tolerantes (dígitos contínuos, validados) ----
        // Aceitam 14 ou 11 dígitos colados (OCR perde pontuação).
        // Boundary mais permissiva: não-dígito (ou início/fim) em volta.
        new(EntityType.Cnpj,
            new Regex(@"(?<![\d.\-/])\d{14}(?![\d.\-/])", Opts, Timeout),
            0.95f, s => BrazilianIdValidators.IsValidCnpj(s)),

        new(EntityType.Cpf,
            new Regex(@"(?<![\d.\-/])\d{11}(?![\d.\-/])", Opts, Timeout),
            0.93f, s => BrazilianIdValidators.IsValidCpf(s)),

        // ---- Processos e identificadores jurídicos ----
        new(EntityType.ProcessoCnj,
            new Regex(@"\b\d{7}-\d{2}\.\d{4}\.\d\.\d{2}\.\d{4}\b", Opts, Timeout),
            0.98f),

        new(EntityType.Oab,
            new Regex(@"\bOAB[/\s\-]?[A-Z]{2}[/\s\-]?\d{1,6}\b", Opts, Timeout),
            0.92f),

        new(EntityType.Rg,
            new Regex(@"\b\d{1,2}\.\d{3}\.\d{3}-[\dXx]\b", Opts, Timeout),
            0.85f),

        // ---- Contato ----
        new(EntityType.Email,
            new Regex(@"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b", Opts, Timeout),
            0.97f),

        // Phone exige parêntese OU traço/espaço pra evitar casar com sequências
        // grandes de dígitos (CNPJ/CPF cruamente OCR'ados).
        new(EntityType.Phone,
            new Regex(@"\(\d{2}\)\s?9?\d{4}[\-\s]?\d{4}\b", Opts, Timeout),
            0.85f),
        new(EntityType.Phone,
            new Regex(@"\b\d{2}\s9?\d{4}[\-\s]\d{4}\b", Opts, Timeout),
            0.82f),

        // ---- Endereços brasileiros ----
        // "Rua/Av./Tv./Travessa/Praça/Rod./Estrada/Alameda <Nome>, <número>"
        // [ \t] em vez de \s para nunca atravessar \n — evita match gigante
        // que engole várias linhas em PDFs OCR.
        new(EntityType.Address,
            new Regex(@"\b(?:Rua|R\.|Av(?:\.|enida)?|Tv(?:\.|essa)?|Travessa|Pra[çc]a|Rod(?:\.|ovia)?|Estrada|Estr\.|Alameda|Al\.)[ \t]+[A-ZÁÉÍÓÚÂÊÔÃÕÇa-záéíóúâêôãõç\.][A-ZÁÉÍÓÚÂÊÔÃÕÇa-záéíóúâêôãõç\.\- \t]{1,80}?,[ \t]*(?:n[º°o]?[ \t]*)?\d{1,6}\b", Opts, Timeout),
            0.90f),

        // Variante OCR: cada palavra está separada por \n no ConcatenatedText.
        // Aceita até 8 quebras entre tokens, com no máximo 3 espaços/tabs por gap.
        new(EntityType.Address,
            new Regex(@"\b(?:Rua|R\.|Av(?:\.|enida)?|Tv(?:\.|essa)?|Travessa|Pra[çc]a|Rod(?:\.|ovia)?|Estrada|Estr\.|Alameda|Al\.)(?:[ \t]*\n){1,2}(?:[A-ZÁÉÍÓÚÂÊÔÃÕÇa-záéíóúâêôãõç\.\-]+(?:[ \t]*\n){1,2}){1,7}\d{1,6}\b", Opts, Timeout),
            0.85f),

        // CEP isolado — usa lookbehind para o prefixo "CEP" não entrar no
        // span (não queremos pseudonimizar a palavra "CEP", só os dígitos).
        new(EntityType.Address,
            new Regex(@"(?<=\bCEP[\s:]*)\d{5}-?\d{3}\b", Opts, Timeout),
            0.93f),
        // Variante: 8 dígitos no formato 99999-999 sem prefixo CEP.
        new(EntityType.Address,
            new Regex(@"\b\d{5}-\d{3}\b", Opts, Timeout),
            0.85f),

        // ---- Instituições privadas ----
        // Apenas tipos de instituição tipicamente PRIVADAS. "Município", "Estado",
        // "Centro" (de Apoio Operacional etc.) saíram daqui porque costumam ser
        // públicas; a IA local pega esses casos quando relevante. O
        // PublicEntityFilterDetector descarta qualquer match que combine com
        // a lista de entidades públicas conhecidas (Ministério Público, Estado
        // do RS, Município de NH etc.).
        // Em texto NATIVO (não-OCR) "Escola Senador Alberto Pasqualini" cabe
        // numa linha só — [ \t]+ não atravessa \n, evita regex gigante.
        // Em OCR cada palavra vira run separado e \n entre elas, então essa
        // regra não casa — o LLM cobre esse caso (que é onde regex falharia
        // de qualquer jeito por causa do tokenizing).
        new(EntityType.Other,
            new Regex(@"\b(?:Escola|Col[ée]gio|Faculdade|Universidade|Hospital|Cl[íi]nica|Hotel)(?:[ \t]+(?:do|da|de|dos|das))?(?:[ \t]+[A-ZÁÉÍÓÚÂÊÔÃÕÇ][A-Za-záéíóúâêôãõç\.]+){1,5}\b",
                Opts, Timeout),
            0.78f),
    };
}
