using Sigilus.Core.Domain;

namespace Sigilus.Core.Abstractions;

public interface IPageClassifier
{
    PageClassification Classify(Stream pdf, int pageIndex);
}
