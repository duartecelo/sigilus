namespace Sigilus.Core.Domain;

public enum EntityType
{
    Cpf,
    Cnpj,
    Rg,
    Oab,
    Email,
    Phone,
    PersonName,
    Address,
    BankAccount,
    ProcessoCnj,
    Other,
}

public enum DetectionSource
{
    Regex,
    Ner,
    Manual,
}

public enum PageClassification
{
    NativeText,
    Scanned,
    Hybrid,
    Empty,
}
