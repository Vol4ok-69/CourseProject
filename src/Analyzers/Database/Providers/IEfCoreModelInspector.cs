using Microsoft.EntityFrameworkCore.Metadata;

namespace DatabaseAnalyzer.Providers;

public interface IEfCoreModelInspector
{
    Task<IModel> InspectAsync(DatabaseProjectResolution resolution, CancellationToken cancellationToken = default);
}
