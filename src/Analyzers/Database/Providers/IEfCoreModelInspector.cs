using Microsoft.EntityFrameworkCore.Metadata;

namespace DatabaseAnalyzer.Providers;

public interface IEfCoreModelInspector
{
    Task<IModel> InspectAsync(string projectPath, CancellationToken cancellationToken = default);
}
