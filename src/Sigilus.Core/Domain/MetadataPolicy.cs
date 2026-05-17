namespace Sigilus.Core.Domain;

public sealed record MetadataPolicy(
    bool ClearInfoDict,
    bool ClearXmp,
    bool ClearProducer,
    bool StripStructureTree)
{
    public static MetadataPolicy Default { get; } = new(
        ClearInfoDict: true,
        ClearXmp: true,
        ClearProducer: true,
        StripStructureTree: false);
}
