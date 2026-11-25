using Application.DTOs;
using Application.Enums;
using Application.Interfaces.Repositories;
using Application.Services;
using Domain.Entities;
using Moq;
using Xunit;

namespace Backend.Tests.Unit;

public sealed class GitHubHistoryClassifierTests
{
    private const int ProjectId = 1;

    [Fact]
    public async Task ScenarioA_CommonAncestorBehindVerifiedBoundary_ReturnsNonFastForwardHistory()
    {
        // БД: A0 (корень) -> B0 (verified boundary), prevHead (verified) -> A0
        var commits = new Dictionary<string, Commit>(StringComparer.OrdinalIgnoreCase)
        {
            ["A0"] = VerifiedCommit("A0"),
            ["B0"] = VerifiedCommit("B0", "A0"),
            ["prevHead"] = VerifiedCommit("prevHead", "A0")
        };

        var repo = CreateCommitRepoMock(commits);

        var result = await GitHubHistoryClassifier.ClassifyAsync(
            ProjectId,
            SameRepoSnapshot("prevHead"),
            SameRepoInfo("newHead"),
            headSha: "newHead",
            fetchedHashes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "newHead" },
            verifiedBoundaries: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "B0" },
            repo.Object);

        Assert.Equal(HistoryClassification.NonFastForwardHistory, result);
    }

    [Fact]
    public async Task ScenarioB_DirectCommonAncestorInFetchedOrBoundary_ReturnsNonFastForwardHistory()
    {
        // БД: A0 (verified), prevHead -> A0; новая ветка дошла до A0 как до verifiedBoundary
        var commits = new Dictionary<string, Commit>(StringComparer.OrdinalIgnoreCase)
        {
            ["A0"] = VerifiedCommit("A0"),
            ["prevHead"] = VerifiedCommit("prevHead", "A0")
        };

        var repo = CreateCommitRepoMock(commits);

        var result = await GitHubHistoryClassifier.ClassifyAsync(
            ProjectId,
            SameRepoSnapshot("prevHead"),
            SameRepoInfo("newHead"),
            headSha: "newHead",
            fetchedHashes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "newHead" },
            verifiedBoundaries: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A0" },
            repo.Object);

        Assert.Equal(HistoryClassification.NonFastForwardHistory, result);
    }

    [Fact]
    public async Task ScenarioC_TrulyIndependentHistories_ReturnsIndependentHistory()
    {
        var commits = new Dictionary<string, Commit>(StringComparer.OrdinalIgnoreCase)
        {
            ["A0"] = VerifiedCommit("A0"),
            ["B0"] = VerifiedCommit("B0", "A0"),
            ["oldRoot"] = VerifiedCommit("oldRoot"),
            ["prevHead"] = VerifiedCommit("prevHead", "oldRoot")
        };

        var repo = CreateCommitRepoMock(commits);

        var result = await GitHubHistoryClassifier.ClassifyAsync(
            ProjectId,
            SameRepoSnapshot("prevHead"),
            SameRepoInfo("newHead"),
            headSha: "newHead",
            fetchedHashes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "newHead" },
            verifiedBoundaries: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "B0" },
            repo.Object);

        Assert.Equal(HistoryClassification.IndependentHistory, result);
    }

    [Fact]
    public async Task ScenarioD_MergeVerifiedBoundaryWithMultipleParents_FindsCommonAncestorOnSecondParent()
    {
        // B0 — merge-коммит с родителями ["otherRoot", "A0"]
        var commits = new Dictionary<string, Commit>(StringComparer.OrdinalIgnoreCase)
        {
            ["otherRoot"] = VerifiedCommit("otherRoot"),
            ["A0"] = VerifiedCommit("A0"),
            ["B0"] = VerifiedCommit("B0", "otherRoot", "A0"),
            ["prevHead"] = VerifiedCommit("prevHead", "A0")
        };

        var repo = CreateCommitRepoMock(commits);

        var result = await GitHubHistoryClassifier.ClassifyAsync(
            ProjectId,
            SameRepoSnapshot("prevHead"),
            SameRepoInfo("newHead"),
            headSha: "newHead",
            fetchedHashes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "newHead" },
            verifiedBoundaries: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "B0" },
            repo.Object);

        Assert.Equal(HistoryClassification.NonFastForwardHistory, result);
    }

    [Fact]
    public async Task ScenarioE_CyclicOrRepeatedAncestorReferences_TerminatesWithoutInfiniteLoop()
    {
        // Цикл: B0 -> A0 -> B0, и независимый prevHead -> oldRoot
        var commits = new Dictionary<string, Commit>(StringComparer.OrdinalIgnoreCase)
        {
            ["A0"] = VerifiedCommit("A0", "B0"),
            ["B0"] = VerifiedCommit("B0", "A0"),
            ["oldRoot"] = VerifiedCommit("oldRoot"),
            ["prevHead"] = VerifiedCommit("prevHead", "oldRoot")
        };

        var repo = CreateCommitRepoMock(commits);

        var result = await GitHubHistoryClassifier.ClassifyAsync(
            ProjectId,
            SameRepoSnapshot("prevHead"),
            SameRepoInfo("newHead"),
            headSha: "newHead",
            fetchedHashes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "newHead" },
            verifiedBoundaries: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "B0" },
            repo.Object);

        Assert.Equal(HistoryClassification.IndependentHistory, result);
        repo.Verify(x => x.GetByHashAsync(ProjectId, "B0", It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(x => x.GetByHashAsync(ProjectId, "A0", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ScenarioF_TraversalLimitReached_ReturnsInsufficientEvidenceInsteadOfIndependentHistory()
    {
        var commits = new Dictionary<string, Commit>(StringComparer.OrdinalIgnoreCase)
        {
            ["A0"] = VerifiedCommit("A0"),
            ["A1"] = VerifiedCommit("A1", "A0"),
            ["B0"] = VerifiedCommit("B0", "A1"),
            ["prevHead"] = VerifiedCommit("prevHead", "A0")
        };

        var repo = CreateCommitRepoMock(commits);

        var result = await GitHubHistoryClassifier.ClassifyAsync(
            ProjectId,
            SameRepoSnapshot("prevHead"),
            SameRepoInfo("newHead"),
            headSha: "newHead",
            fetchedHashes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "newHead" },
            verifiedBoundaries: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "B0" },
            repo.Object,
            CancellationToken.None,
            maxTraversalNodes: 2);

        Assert.Equal(HistoryClassification.InsufficientEvidence, result);
    }


    [Fact]
    public async Task ScenarioG_BoundaryParentAlreadyInFetchedHashes_StillTraversesItsParentsToFindCommonAncestor()
    {
        // X уже есть в fetchedHashes, но его родители (A0) не исследованы; B0 -> X -> A0
        var commits = new Dictionary<string, Commit>(StringComparer.OrdinalIgnoreCase)
        {
            ["A0"] = VerifiedCommit("A0"),
            ["X"] = VerifiedCommit("X", "A0"),
            ["B0"] = VerifiedCommit("B0", "X"),
            ["prevHead"] = VerifiedCommit("prevHead", "A0")
        };

        var repo = CreateCommitRepoMock(commits);

        var result = await GitHubHistoryClassifier.ClassifyAsync(
            ProjectId,
            SameRepoSnapshot("prevHead"),
            SameRepoInfo("newHead"),
            headSha: "newHead",
            fetchedHashes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "newHead", "X" },
            verifiedBoundaries: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "B0" },
            repo.Object);

        Assert.Equal(HistoryClassification.NonFastForwardHistory, result);
    }

    [Fact]
    public async Task ScenarioH_MissingOrUnverifiedNodeInPrevHistory_ReturnsInsufficientEvidence()
    {
        var commits = new Dictionary<string, Commit>(StringComparer.OrdinalIgnoreCase)
        {
            ["B0"] = VerifiedCommit("B0"),
            ["unverifiedNode"] = new Commit
            {
                ProjectId = ProjectId,
                CommitHash = "unverifiedNode",
                ParentHashes = [],
                IsVerified = false,
                CommitDate = DateTime.UtcNow
            },
            ["prevHead"] = VerifiedCommit("prevHead", "unverifiedNode")
        };

        var repo = CreateCommitRepoMock(commits);

        var result = await GitHubHistoryClassifier.ClassifyAsync(
            ProjectId,
            SameRepoSnapshot("prevHead"),
            SameRepoInfo("newHead"),
            headSha: "newHead",
            fetchedHashes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "newHead" },
            verifiedBoundaries: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "B0" },
            repo.Object);

        Assert.Equal(HistoryClassification.InsufficientEvidence, result);
    }


    [Fact]
    public async Task ScenarioI_UnifiedTraversal_QueriesEachNodeAtMostOnceAcrossAllStages()
    {
        var commits = new Dictionary<string, Commit>(StringComparer.OrdinalIgnoreCase)
        {
            ["A0"] = VerifiedCommit("A0"),
            ["B0"] = VerifiedCommit("B0", "A0"),
            ["prevHead"] = VerifiedCommit("prevHead", "oldRoot"),
            ["oldRoot"] = VerifiedCommit("oldRoot")
        };

        var repo = CreateCommitRepoMock(commits);

        var result = await GitHubHistoryClassifier.ClassifyAsync(
            ProjectId,
            SameRepoSnapshot("prevHead"),
            SameRepoInfo("newHead"),
            headSha: "newHead",
            fetchedHashes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "newHead" },
            verifiedBoundaries: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "B0", "b0" },
            repo.Object);

        Assert.Equal(HistoryClassification.IndependentHistory, result);
        repo.Verify(x => x.GetByHashAsync(ProjectId, "B0", It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(x => x.GetByHashAsync(ProjectId, "A0", It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(x => x.GetByHashAsync(ProjectId, "prevHead", It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(x => x.GetByHashAsync(ProjectId, "oldRoot", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ScenarioJ_SharedNodeAcrossStages_CountedOnlyOnceTowardLimit()
    {
        // Общий отсутствующий узел X: запрашивается и при обходе B0 -> X, и при обходе prevHead -> X
        var commits = new Dictionary<string, Commit>(StringComparer.OrdinalIgnoreCase)
        {
            ["B0"] = VerifiedCommit("B0", "X"),
            ["prevHead"] = VerifiedCommit("prevHead", "X")
        };

        var repo = CreateCommitRepoMock(commits);

        var result = await GitHubHistoryClassifier.ClassifyAsync(
            ProjectId,
            SameRepoSnapshot("prevHead"),
            SameRepoInfo("newHead"),
            headSha: "newHead",
            fetchedHashes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "newHead" },
            verifiedBoundaries: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "B0" },
            repo.Object,
            CancellationToken.None,
            maxTraversalNodes: 3);

        Assert.Equal(HistoryClassification.InsufficientEvidence, result);
        repo.Verify(x => x.GetByHashAsync(ProjectId, "X", It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(x => x.GetByHashAsync(ProjectId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task ScenarioK_DuplicateHashesWithDifferentCasing_DoNotIncreaseUniqueNodeCount()
    {
        var commits = new Dictionary<string, Commit>(StringComparer.OrdinalIgnoreCase)
        {
            ["A0"] = VerifiedCommit("A0"),
            ["B0"] = VerifiedCommit("B0", "A0", "a0"),
            ["oldRoot"] = VerifiedCommit("oldRoot"),
            ["prevHead"] = VerifiedCommit("prevHead", "oldRoot")
        };

        var repo = CreateCommitRepoMock(commits);

        var result = await GitHubHistoryClassifier.ClassifyAsync(
            ProjectId,
            SameRepoSnapshot("prevHead"),
            SameRepoInfo("newHead"),
            headSha: "newHead",
            fetchedHashes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "newHead" },
            verifiedBoundaries: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "B0", "b0" },
            repo.Object,
            CancellationToken.None,
            maxTraversalNodes: 4);

        Assert.Equal(HistoryClassification.IndependentHistory, result);
        repo.Verify(x => x.GetByHashAsync(ProjectId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(4));
    }

    [Fact]
    public async Task ScenarioL_ExactBoundaryAtMaxTraversalNodes_SucceedsWhileExceedingReturnsInsufficientEvidence()
    {
        // Уникальные узлы до нахождения общего предка A0: B0, A0, prevHead (итого 3)
        var commits = new Dictionary<string, Commit>(StringComparer.OrdinalIgnoreCase)
        {
            ["A0"] = VerifiedCommit("A0"),
            ["B0"] = VerifiedCommit("B0", "A0"),
            ["prevHead"] = VerifiedCommit("prevHead", "A0")
        };

        var repo = CreateCommitRepoMock(commits);

        var exactResult = await GitHubHistoryClassifier.ClassifyAsync(
            ProjectId,
            SameRepoSnapshot("prevHead"),
            SameRepoInfo("newHead"),
            headSha: "newHead",
            fetchedHashes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "newHead" },
            verifiedBoundaries: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "B0" },
            repo.Object,
            CancellationToken.None,
            maxTraversalNodes: 3);

        var exceededResult = await GitHubHistoryClassifier.ClassifyAsync(
            ProjectId,
            SameRepoSnapshot("prevHead"),
            SameRepoInfo("newHead"),
            headSha: "newHead",
            fetchedHashes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "newHead" },
            verifiedBoundaries: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "B0" },
            repo.Object,
            CancellationToken.None,
            maxTraversalNodes: 2);

        Assert.Equal(HistoryClassification.NonFastForwardHistory, exactResult);
        Assert.Equal(HistoryClassification.InsufficientEvidence, exceededResult);
    }

    [Fact]
    public async Task ScenarioM_MergeBoundaryFirstParentMissingSecondIsPrevHead_ReturnsFastForward()
    {
        var commits = new Dictionary<string, Commit>(StringComparer.OrdinalIgnoreCase)
        {
            ["prevHead"] = VerifiedCommit("prevHead"),
            ["B0"] = VerifiedCommit("B0", "missingParent", "prevHead")
        };

        var repo = CreateCommitRepoMock(commits);

        var result = await GitHubHistoryClassifier.ClassifyAsync(
            ProjectId,
            SameRepoSnapshot("prevHead"),
            SameRepoInfo("newHead"),
            headSha: "newHead",
            fetchedHashes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "newHead" },
            verifiedBoundaries: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "B0" },
            repo.Object);

        Assert.Equal(HistoryClassification.FastForward, result);
    }

    [Fact]
    public async Task ScenarioN_MergeBoundaryOneParentMissingOtherDoesNotReachPrevHead_ReturnsInsufficientEvidence()
    {
        var commits = new Dictionary<string, Commit>(StringComparer.OrdinalIgnoreCase)
        {
            ["otherRoot"] = VerifiedCommit("otherRoot"),
            ["prevHead"] = VerifiedCommit("prevHead"),
            ["B0"] = VerifiedCommit("B0", "missingParent", "otherRoot")
        };

        var repo = CreateCommitRepoMock(commits);

        var result = await GitHubHistoryClassifier.ClassifyAsync(
            ProjectId,
            SameRepoSnapshot("prevHead"),
            SameRepoInfo("newHead"),
            headSha: "newHead",
            fetchedHashes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "newHead" },
            verifiedBoundaries: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "B0" },
            repo.Object);

        Assert.Equal(HistoryClassification.InsufficientEvidence, result);
    }


    [Fact]
    public async Task ScenarioO_ProvenVerifiedCommonAncestorWithMissingGrandparent_ReturnsNonFastForwardHistory()
    {
        var commits = new Dictionary<string, Commit>(StringComparer.OrdinalIgnoreCase)
        {
            ["A0"] = VerifiedCommit("A0", "missingGrandparent"),
            ["B0"] = VerifiedCommit("B0", "A0"),
            ["prevHead"] = VerifiedCommit("prevHead", "A0")
        };

        var repo = CreateCommitRepoMock(commits);

        var result = await GitHubHistoryClassifier.ClassifyAsync(
            ProjectId,
            SameRepoSnapshot("prevHead"),
            SameRepoInfo("newHead"),
            headSha: "newHead",
            fetchedHashes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "newHead" },
            verifiedBoundaries: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "B0" },
            repo.Object);

        Assert.Equal(HistoryClassification.NonFastForwardHistory, result);
    }

    [Fact]
    public async Task ScenarioP_NoCommonAncestorAndIncompleteBranch_ReturnsInsufficientEvidenceInsteadOfIndependentHistory()
    {
        var commits = new Dictionary<string, Commit>(StringComparer.OrdinalIgnoreCase)
        {
            ["B0"] = VerifiedCommit("B0", "missingParent"),
            ["oldRoot"] = VerifiedCommit("oldRoot"),
            ["prevHead"] = VerifiedCommit("prevHead", "oldRoot")
        };

        var repo = CreateCommitRepoMock(commits);

        var result = await GitHubHistoryClassifier.ClassifyAsync(
            ProjectId,
            SameRepoSnapshot("prevHead"),
            SameRepoInfo("newHead"),
            headSha: "newHead",
            fetchedHashes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "newHead" },
            verifiedBoundaries: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "B0" },
            repo.Object);

        Assert.Equal(HistoryClassification.InsufficientEvidence, result);
    }


    [Fact]
    public async Task ScenarioQ_LimitReachedWhilePrevHeadQueued_ReturnsFastForwardWithoutExceedingLimit()
    {
        var commits = new Dictionary<string, Commit>(StringComparer.OrdinalIgnoreCase)
        {
            ["prevHead"] = VerifiedCommit("prevHead"),
            ["B0"] = VerifiedCommit("B0", "missingParent", "prevHead")
        };

        var repo = CreateCommitRepoMock(commits);

        var result = await GitHubHistoryClassifier.ClassifyAsync(
            ProjectId,
            SameRepoSnapshot("prevHead"),
            SameRepoInfo("newHead"),
            headSha: "newHead",
            fetchedHashes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "newHead" },
            verifiedBoundaries: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "B0" },
            repo.Object,
            CancellationToken.None,
            maxTraversalNodes: 1);

        Assert.Equal(HistoryClassification.FastForward, result);
        repo.Verify(x => x.GetByHashAsync(ProjectId, "B0", It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(x => x.GetByHashAsync(ProjectId, "missingParent", It.IsAny<CancellationToken>()), Times.Never);
        repo.Verify(x => x.GetByHashAsync(ProjectId, "prevHead", It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Commit VerifiedCommit(string hash, params string[] parents) => new()
    {
        ProjectId = ProjectId,
        CommitHash = hash,
        ParentHashes = parents,
        IsVerified = true,
        CommitDate = DateTime.UtcNow
    };

    private static ProjectCheckpointSnapshotDto SameRepoSnapshot(string prevHead) =>
        new(prevHead, "main", "123", "github.com");

    private static GitHubRepositoryInfoDto SameRepoInfo(string headSha) =>
        new("github.com", "123", "main", headSha);

    private static Mock<ICommitRepository> CreateCommitRepoMock(IReadOnlyDictionary<string, Commit> commits)
    {
        var mock = new Mock<ICommitRepository>();
        mock.Setup(x => x.GetByHashAsync(ProjectId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, string hash, CancellationToken _) =>
                commits.TryGetValue(hash, out var commit) ? commit : null);
        mock.Setup(x => x.GetHashesVerificationStatusAsync(ProjectId, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, IReadOnlyCollection<string> hashes, CancellationToken _) =>
                hashes.Where(commits.ContainsKey).ToDictionary(h => h, h => commits[h].IsVerified, StringComparer.OrdinalIgnoreCase));
        return mock;
    }
}
