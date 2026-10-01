using Application.Analyzers;
using DatabaseAnalyzer.Providers;
using Domain.Enums;

namespace DatabaseAnalyzer;

public sealed class DatabaseAnalyzer(
    IDatabaseMetadataProvider metadataProvider) : IAnalyzer
{
    public AnalyzerType Type => AnalyzerType.Database;

    public async Task<IReadOnlyList<AnalyzerFinding>> AnalyzeAsync(
        AnalyzerContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await metadataProvider.GetMetadataAsync(
            context,
            cancellationToken);

        return [];
    }
}