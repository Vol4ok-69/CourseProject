using Microsoft.EntityFrameworkCore.Metadata;

namespace DatabaseAnalyzer.Providers;

public sealed class EfCoreModelInspector : IEfCoreModelInspector
{
    public Task<IModel> InspectAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}