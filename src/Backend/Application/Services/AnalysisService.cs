using Application.Analyzers;
using Application.Interfaces.Repositories;
using Domain.Entities;
using Domain.Enums;

namespace Application.Services;

public class AnalysisService(
    IAnalysisRepository analysisRepository,
    ICommitRepository commitRepository,
    IEnumerable<IAnalyzer> analyzers)
{
    public async Task<Analysis> AnalyzeAsync(
        int commitId,
        AnalyzerContext context,
        CancellationToken cancellationToken = default)
    {
        var commit = await commitRepository.GetByIdAsync(commitId, cancellationToken) ?? throw new InvalidOperationException("Коммит не найден.");

        var analysis = new Analysis
        {
            CommitId = commitId,
            Status = AnalysisStatus.Running,
            StartedAt = DateTime.UtcNow
        };

        await analysisRepository.AddAsync(analysis, cancellationToken);

        try
        {
            foreach (var analyzer in analyzers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var findings = await analyzer.AnalyzeAsync(context, cancellationToken);

                foreach (var finding in findings)
                {
                    analysis.Results.Add(new AnalysisResult
                    {
                        FilePath = finding.FilePath,
                        LineNumber = finding.LineNumber,
                        Rule = finding.Rule,
                        Message = finding.Message,
                        Severity = finding.Severity,
                        Recommendation = finding.Recommendation,
                        AnalyzerType = analyzer.Type
                    });
                }
            }

            analysis.Status = AnalysisStatus.Completed;
            analysis.CompletedAt = DateTime.UtcNow;

            await analysisRepository.UpdateAsync(analysis, cancellationToken);

            return analysis;
        }
        catch
        {
            analysis.Status = AnalysisStatus.Failed;
            analysis.CompletedAt = DateTime.UtcNow;

            await analysisRepository.UpdateAsync(analysis, cancellationToken);

            throw;
        }
    }
}