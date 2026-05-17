namespace Sigilus.Detection.Validation;

/// <summary>
/// Filtra detecções que correspondem a entidades públicas, geográficas ou
/// genéricas que <b>não</b> devem ser pseudonimizadas. Inclui:
/// <list type="bullet">
///   <item>27 estados + DF (siglas e nomes completos)</item>
///   <item>Capitais e cidades grandes do Brasil</item>
///   <item>Órgãos públicos (Ministério Público, TJ, OAB, INSS, Receita Federal, etc.)</item>
///   <item>Termos genéricos jurídicos ("Estado", "União", "Município")</item>
/// </list>
/// A comparação é case/acento-insensível e normaliza espaços/quebras.
/// </summary>
public static class PublicEntityFilter
{
    /// <summary>True se o texto detectado é uma entidade pública/geográfica que NÃO deve ser pseudonimizada.</summary>
    public static bool IsPublic(string detectedText)
    {
        if (string.IsNullOrWhiteSpace(detectedText)) return false;
        var key = Normalize(detectedText);
        if (key.Length == 0) return false;

        // Match exato em qualquer das listas (cidades, estados, órgãos canônicos).
        if (Exact.Contains(key)) return true;

        // Match exato em "isolados" sutis (fragmentos comuns de OCR/tokenizer).
        if (SmallExact.Contains(key)) return true;

        // Match parcial: se contém o termo público em qualquer lugar,
        // por exemplo "Estado do Rio Grande do Sul" → contém "RIO GRANDE DO SUL".
        foreach (var pub in ContainsAnchors)
            if (key.Contains(pub)) return true;

        // "MUNICIPIO DE X", "PREFEITURA DE X", "GOVERNO DE/DO X" → órgão público.
        foreach (var prefix in PublicPrefixes)
            if (key.StartsWith(prefix)) return true;

        // Substring reversa: detecção fragmentada por OCR pode ser parte de
        // uma cidade conhecida. "TO ALEGRE" → substring de "PORTO ALEGRE".
        // Só aceita se o fragmento tiver pelo menos 5 chars (evita matches
        // genéricos como "RIO" dentro de qualquer nome).
        if (key.Length >= 5)
        {
            foreach (var city in BigCities)
                if (city.Contains(key) && city.Length - key.Length <= city.Length / 2) return true;
        }

        return false;
    }

    // Fragmentos curtos que tipicamente aparecem como ruído de NER em texto
    // jurídico (ex: tokenizer cortou "MINISTÉRIO PÚBLICO" no meio). Match EXATO
    // (não substring) para evitar afetar nomes próprios.
    private static readonly HashSet<string> SmallExact = new(StringComparer.Ordinal)
    {
        "O PUBLICO", "A PUBLICA", "PUBLICO", "PUBLICA",
        "PROMO", "TORIA", "MINIST", "MINISTERIO",
        "TRIBUNAL", "FAZENDA", "UNIAO", "REPUBLICA",
        "GOVERNO", "ESTADO", "MUNICIPIO", "PREFEITURA",
        "ESTAD", "MUNIC", "REPUB",
        "O ESTADO", "A UNIAO", "O MUNICIPIO", "O GOVERNO",
    };

    /// <summary>
    /// Normaliza removendo acentos, lowercase, colapsando whitespace,
    /// removendo pontuação de borda. "Estado  do\nRio Grande" → "ESTADO DO RIO GRANDE".
    /// </summary>
    public static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        var lastWasSpace = true;
        foreach (var ch in s.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch)
                == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToUpperInvariant(ch));
                lastWasSpace = false;
            }
            else if (char.IsWhiteSpace(ch) || ch == '\n')
            {
                if (!lastWasSpace) { sb.Append(' '); lastWasSpace = true; }
            }
            // descarta pontuação
        }
        return sb.ToString().Trim();
    }

    // ---- Estados + DF ----
    private static readonly string[] StatesFull =
    {
        "ACRE","ALAGOAS","AMAPA","AMAZONAS","BAHIA","CEARA","DISTRITO FEDERAL","ESPIRITO SANTO",
        "GOIAS","MARANHAO","MATO GROSSO","MATO GROSSO DO SUL","MINAS GERAIS","PARA","PARAIBA",
        "PARANA","PERNAMBUCO","PIAUI","RIO DE JANEIRO","RIO GRANDE DO NORTE","RIO GRANDE DO SUL",
        "RONDONIA","RORAIMA","SANTA CATARINA","SAO PAULO","SERGIPE","TOCANTINS",
    };
    private static readonly string[] StatesShort =
    {
        "AC","AL","AP","AM","BA","CE","DF","ES","GO","MA","MT","MS","MG","PA","PB","PR","PE","PI",
        "RJ","RN","RS","RO","RR","SC","SP","SE","TO",
    };

    // ---- Capitais + cidades grandes (>200k hab. - amostra representativa) ----
    private static readonly string[] BigCities =
    {
        // capitais
        "RIO BRANCO","MACEIO","MACAPA","MANAUS","SALVADOR","FORTALEZA","BRASILIA","VITORIA",
        "GOIANIA","SAO LUIS","CUIABA","CAMPO GRANDE","BELO HORIZONTE","BELEM","JOAO PESSOA",
        "CURITIBA","RECIFE","TERESINA","NATAL","PORTO ALEGRE","PORTO VELHO","BOA VISTA",
        "FLORIANOPOLIS","SAO PAULO","ARACAJU","PALMAS",
        // RS (foco do PDF de teste)
        "NOVO HAMBURGO","CANOAS","CAXIAS DO SUL","PELOTAS","SANTA MARIA","GRAVATAI","VIAMAO",
        "SAO LEOPOLDO","RIO GRANDE","ALVORADA","PASSO FUNDO","SAPUCAIA DO SUL","CACHOEIRINHA",
        "BAGE","ESTEIO","BENTO GONCALVES","ERECHIM","GUAIBA","CACHOEIRA DO SUL","SANTA CRUZ DO SUL",
        // SP grandes
        "GUARULHOS","CAMPINAS","SAO BERNARDO DO CAMPO","SANTO ANDRE","OSASCO","RIBEIRAO PRETO",
        "SOROCABA","SANTOS","SAO JOSE DOS CAMPOS","MAUA","SAO JOSE DO RIO PRETO","DIADEMA",
        "JUNDIAI","CARAPICUIBA","PIRACICABA","BAURU","ITAQUAQUECETUBA","FRANCA","SAO VICENTE",
        "PRAIA GRANDE","GUARUJA","TABOAO DA SERRA","LIMEIRA","SUMARE","SAO CARLOS",
        // outras grandes
        "DUQUE DE CAXIAS","NOVA IGUACU","SAO GONCALO","NITEROI","CAMPOS DOS GOYTACAZES",
        "PETROPOLIS","VOLTA REDONDA","UBERLANDIA","CONTAGEM","JUIZ DE FORA","BETIM","MONTES CLAROS",
        "RIBEIRAO DAS NEVES","UBERABA","GOVERNADOR VALADARES","IPATINGA","SETE LAGOAS","DIVINOPOLIS",
        "JABOATAO DOS GUARARAPES","OLINDA","CARUARU","PETROLINA",
        "FEIRA DE SANTANA","VITORIA DA CONQUISTA","CAMACARI","JEQUIE","ILHEUS","ITABUNA",
        "ANAPOLIS","APARECIDA DE GOIANIA","RIO VERDE",
        "LONDRINA","MARINGA","PONTA GROSSA","CASCAVEL","SAO JOSE DOS PINHAIS","FOZ DO IGUACU",
        "JOINVILLE","BLUMENAU","SAO JOSE","CRICIUMA","CHAPECO","ITAJAI",
        "ANANINDEUA","SANTAREM","MARABA","PARAUAPEBAS",
        "SERRA","CARIACICA","VILA VELHA",
        "CAMPINA GRANDE","SOBRAL","JUAZEIRO DO NORTE","CAUCAIA","MARACANAU",
        "OUTRO LADO","UNIAO","REPUBLICA","ESTADO","MUNICIPIO","FAZENDA PUBLICA",
    };

    // ---- Órgãos públicos brasileiros (siglas e nomes) ----
    private static readonly string[] PublicOrgs =
    {
        "MINISTERIO PUBLICO","MP","MPF","MPT","MPE","MPM","MPRS","MPSP","MPRJ","MPMG","MPPR",
        "TRIBUNAL DE JUSTICA","TJ","TJRS","TJSP","TJRJ","TJMG","TJPR","TJSC","TJBA","TJPE","TJDF",
        "TRIBUNAL REGIONAL FEDERAL","TRF","TRF1","TRF2","TRF3","TRF4","TRF5","TRF6",
        "TRIBUNAL REGIONAL DO TRABALHO","TRT","SUPERIOR TRIBUNAL DE JUSTICA","STJ",
        "SUPREMO TRIBUNAL FEDERAL","STF","TRIBUNAL SUPERIOR ELEITORAL","TSE","TRIBUNAL REGIONAL ELEITORAL","TRE",
        "TRIBUNAL SUPERIOR DO TRABALHO","TST","SUPERIOR TRIBUNAL MILITAR","STM",
        "ORDEM DOS ADVOGADOS DO BRASIL","OAB","DEFENSORIA PUBLICA","DPE","DPU",
        "POLICIA FEDERAL","PF","POLICIA CIVIL","POLICIA MILITAR","PM","POLICIA RODOVIARIA FEDERAL","PRF",
        "RECEITA FEDERAL","RFB","INSS","INSTITUTO NACIONAL DO SEGURO SOCIAL",
        "CAIXA ECONOMICA FEDERAL","CEF","BANCO DO BRASIL","BB","BANCO CENTRAL","BACEN","BCB",
        "PROCURADORIA","PGE","PGM","PGFN","AGU","ADVOCACIA GERAL DA UNIAO","CGU",
        "TRIBUNAL DE CONTAS","TCU","TCE","TCM","CONTROLADORIA",
        "UNIAO FEDERAL","UNIAO","FAZENDA NACIONAL","FAZENDA PUBLICA",
        "GOVERNO FEDERAL","GOVERNO DO ESTADO","GOVERNO ESTADUAL","GOVERNO MUNICIPAL",
        "PRESIDENCIA DA REPUBLICA","CAMARA DOS DEPUTADOS","SENADO FEDERAL","CONGRESSO NACIONAL",
        "ASSEMBLEIA LEGISLATIVA","CAMARA MUNICIPAL","CAMARA DE VEREADORES",
        "PROMOTORIA","PROMOTORIA DE JUSTICA","PROCURADORIA DA REPUBLICA",
        "VARA","VARA CIVEL","VARA CRIMINAL","VARA DA FAZENDA PUBLICA","JUIZADO","JUIZADO ESPECIAL",
        "SECRETARIA","MINISTERIO","DEPARTAMENTO","SUPERINTENDENCIA","DELEGACIA",
        "ANATEL","ANAC","ANEEL","ANS","ANVISA","ANTT","ANTAQ","ANP","ANCINE","ANA","ANM",
        "IBAMA","ICMBIO","INMETRO","INPI","INMET","CADE","CVM","CNJ","CNMP",
    };

    // Conjunto rápido para lookup O(1).
    private static readonly HashSet<string> Exact;

    // Termos âncora que, se aparecem em qualquer lugar do texto detectado, marcam como público.
    // Útil para casos como "ESTADO DO RIO GRANDE DO SUL PRAZO" (texto sujo de OCR).
    private static readonly string[] ContainsAnchors =
    {
        // Órgãos
        "MINISTERIO PUBLICO","MINISTERIO","TRIBUNAL DE JUSTICA","TRIBUNAL REGIONAL","SUPREMO TRIBUNAL",
        "RECEITA FEDERAL","POLICIA FEDERAL","POLICIA CIVIL","POLICIA MILITAR",
        "GOVERNO DO ESTADO","GOVERNO FEDERAL","GOVERNO MUNICIPAL",
        "DEFENSORIA PUBLICA","ORDEM DOS ADVOGADOS",
        "PROMOTORIA","PROCURADORIA",
        "UNIAO FEDERAL","FAZENDA PUBLICA","FAZENDA NACIONAL",
        "ASSEMBLEIA LEGISLATIVA","CAMARA MUNICIPAL","CAMARA DOS DEPUTADOS","SENADO FEDERAL",
        // Geografia
        "RIO GRANDE DO SUL","RIO GRANDE DO NORTE","MATO GROSSO DO SUL","MATO GROSSO",
        "MINAS GERAIS","SAO PAULO","RIO DE JANEIRO","SANTA CATARINA","ESPIRITO SANTO",
        "DISTRITO FEDERAL","PARA","BAHIA","PERNAMBUCO","CEARA","PARANA",
        // Termos genéricos jurídicos
        "INQUERITO CIVIL","INQUERITO POLICIAL","PROCESSO ADMINISTRATIVO","FORO DE",
        "VARA CIVEL","VARA CRIMINAL","JUIZADO ESPECIAL",
        "JUSTICA CIVEL","JUSTICA CRIMINAL","JUSTICA FEDERAL","JUSTICA DO TRABALHO",
        // Substrings de cidades importantes (para casos OCR-fragmentados,
        // ex: "TO ALEGRE", "io de Novo Hamburgo").
        "PORTO ALEGRE","NOVO HAMBURGO","RIO DE JANEIRO","SAO PAULO","BELO HORIZONTE",
        "BRASILIA","CURITIBA","SALVADOR","FORTALEZA","RECIFE","MANAUS","BELEM",
        "VITORIA","FLORIANOPOLIS","NATAL","JOAO PESSOA","TERESINA","ARACAJU","CUIABA",
        "MACEIO","CAMPO GRANDE","BOA VISTA","PORTO VELHO","RIO BRANCO","MACAPA","PALMAS",
        "GOIANIA","SAO LUIS",
    };

    private static readonly string[] PublicPrefixes =
    {
        "MUNICIPIO DE","MUNICIPIO DO","MUNICIPIO DA",
        "ESTADO DE","ESTADO DO","ESTADO DA",
        "PREFEITURA DE","PREFEITURA DO","PREFEITURA DA","PREFEITURA MUNICIPAL",
        "GOVERNO DE","GOVERNO DO","GOVERNO DA",
        "ASSEMBLEIA LEGISLATIVA DO","ASSEMBLEIA LEGISLATIVA DE",
        "TRIBUNAL DE JUSTICA",
        "MINISTERIO PUBLICO",
    };

    static PublicEntityFilter()
    {
        Exact = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in StatesFull) Exact.Add(s);
        foreach (var s in StatesShort) Exact.Add(s);
        foreach (var s in BigCities) Exact.Add(s);
        foreach (var s in PublicOrgs) Exact.Add(s);
        // Variações compostas comuns
        foreach (var st in StatesFull)
        {
            Exact.Add("ESTADO DE " + st);
            Exact.Add("ESTADO DO " + st);
            Exact.Add("ESTADO DA " + st);
            Exact.Add("GOVERNO DO " + st);
            Exact.Add("GOVERNO DA " + st);
            Exact.Add("GOVERNO DE " + st);
        }
        foreach (var c in BigCities)
        {
            Exact.Add("MUNICIPIO DE " + c);
            Exact.Add("MUNICIPIO DO " + c);
            Exact.Add("MUNICIPIO DA " + c);
            Exact.Add("PREFEITURA DE " + c);
            Exact.Add("PREFEITURA DO " + c);
            Exact.Add("PREFEITURA DA " + c);
            Exact.Add("PREFEITURA MUNICIPAL DE " + c);
        }
    }
}
