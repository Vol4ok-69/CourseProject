namespace DatabaseAnalyzer.Process;

public interface IContainerProcessAdapter
{
    Task<ProcessExecutionResult> RunAsync(
        ContainerRunOptions options,
        CancellationToken cancellationToken = default);
}
