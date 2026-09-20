using Application.Analyzers;
using Application.Interfaces;
using Application.Interfaces.Repositories;
using Domain.Entities;
using Domain.Enums;

namespace Application.Services;

public class AnalysisService(
    IAnalysisRepository analysisRepository,
    ICommitRepository commitRepository,
    IRepositorySourceService repositorySourceService,
    IEnumerable<IAnalyzer> analyzers)
{
    public async Task<Analysis> AnalyzeAsync(
        int commitId,
        CancellationToken cancellationToken = default)
    {
        var commit = await commitRepository.GetByIdAsync(commitId, cancellationToken)
            ?? throw new InvalidOperationException("Коммит не найден.");

        var analysis = new Analysis
        {
            CommitId = commitId,
            Status = AnalysisStatus.Running,
            StartedAt = DateTime.UtcNow
        };

        await analysisRepository.AddAsync(analysis, cancellationToken);

        try
        {
            var repositoryPath = await repositorySourceService.DownloadAndExtractAsync(
                commit.Project.RepoUrl,
                commit.CommitHash,
                analysis.Id,
                cancellationToken);

            var context = new AnalyzerContext
            {
                RepositoryPath = repositoryPath,
                CommitHash = commit.CommitHash
            };

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

            await analysisRepository.UpdateAsync(analysis, CancellationToken.None);

            throw;
        }
        finally
        {
            await repositorySourceService.CleanupAsync(analysis.Id, CancellationToken.None);
        }
    }
    public async Task<Analysis?> GetByIdAsync(int analysisId, CancellationToken cancellationToken = default)
    {
        return await analysisRepository.GetByIdAsync(analysisId, cancellationToken);
    }

    public async Task<IReadOnlyList<Analysis>> GetByCommitIdAsync(
        int commitId,
        CancellationToken cancellationToken = default)
    {
        return await analysisRepository.GetByCommitIdAsync(commitId, cancellationToken);
    }
}