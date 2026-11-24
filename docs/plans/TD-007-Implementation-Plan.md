# TD-007 Implementation Plan: Retry, Backoff, Poison Pill and Durable PostgreSQL Queue

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement resilient background queue task execution in PostgreSQL, including atomic attempt counting, poison pill neutralization, exponential backoff with jitter, RFC 9110 & GitHub rate limit handling, strict lease ownership guarantees in write operations (Q-03), and recovery from ambiguous COMMIT results (Q-04).

**Architecture:** Extend `Domain.Entities.Analysis` and EF Core 10 mappings with `AttemptCount`, `MaxAttempts`, `NextAttemptAt` and check constraints. Implement `GitHubRateLimitParser` with safe header diagnostics (allow-list). Implement `AnalysisRetryPolicy` with transient error classification and exponential backoff. Upgrade `AnalysisRepository` to use PostgreSQL `clock_timestamp()`, atomic attempt increment, advisory lock PID matching (`objsubid = 2`), and lease expiration checking directly in write operations (`CompleteAsync`, `FailAsync`, `ScheduleRetryAsync`). Upgrade `AnalysisService` to recover safely if COMMIT was persisted during an ambiguous network failure, preventing result duplication and orphan workspaces.

**Tech Stack:** .NET 10.0, C# 13, EF Core 10.0.12, Npgsql 10.0.3, PostgreSQL 17, Testcontainers 4.16.0, xUnit.

**Spec:** [TD-007-Architecture-Verification.md](TD-007-Architecture-Verification.md) and [TD-007-Technical-Investigation-Review.md](TD-007-Technical-Investigation-Review.md).

## Global Constraints

- Strictly adhere to `docs/Правила-базы-данных.txt`: PostgreSQL 17, 3NF, PascalCase identifiers in double quotes.
- Do NOT modify public API / DTOs (`AnalysisDto`, `IAnalyzer`).
- Preserve TD-002 invariants (`IAnalysisLeaseGuard`, `ExecutionToken`, Lease Renewal, terminal states).
- Do NOT implement TD-003 (leave `DEFERRED`).
- No Git commit, push, reset, clean, or stash.
- Evidence before assertions: every task must be verified with tests.

## Review Focus

1. **Stale Worker Write Prevention (Q-03):** Worker A losing connection or lease cannot mutate status or save results even if Worker B hasn't acquired the row yet.
2. **Ambiguous Commit ACK Recovery (Q-04):** Network drops during commit ACK; service verifies task state, avoids duplicate results and cleans workspace safely.
3. **Poison Pill CrashLoop Neutralization (Q-02):** Stale task that crashed `MaxAttempts` times is atomically transitioned to `Failed` during acquisition without re-running analyzers.
4. **Rate Limit Classification (Q-05):** Primary vs Secondary limits correctly parsed according to RFC 9110 and GitHub documentation without blind fallback to 60s.
5. **No Secret Leaks (Q-07):** Only allow-listed rate limit headers are captured in diagnostics; tokens and credentials are never stored.

---

### Task 1: Domain Entities and Database Schema Migration

**Files:**
- Modify: `src/Backend/Domain/Entities/Analysis.cs`
- Modify: `src/Backend/Infrastructure/Data/Configurations/AnalysisConfiguration.cs`
- Create: `src/Backend/Infrastructure/Data/Migrations/<timestamp>_AddAnalysisRetryAndPoisonPillQueue.cs`
- Modify: `src/Backend/Infrastructure/Data/Migrations/AppDbContextModelSnapshot.cs`
- Test: `tests/Backend.Tests/Integration/PostgreSqlAnalysisMigrationTests.cs`

- [x] Add `AttemptCount` (int, default 0), `MaxAttempts` (int, default 3), and `NextAttemptAt` (DateTime?, null) to `Analysis.cs`.
- [x] Configure EF Core mappings in `AnalysisConfiguration.cs` with default values, check constraints (`CK_Analyses_AttemptCount`, `CK_Analyses_MaxAttempts`), and index `IX_Analyses_Status_NextAttemptAt_CreatedAt`.
- [x] Generate EF Core migration via `dotnet ef migrations add AddAnalysisRetryAndPoisonPillQueue`.
- [x] Verify migration `Up` and `Down` methods adhere strictly to 3NF and PascalCase quoting rules.
- [x] Write integration test verifying migration application, rollback, default values, and check constraints against PostgreSQL 17 Testcontainers.
- [x] Run migration test and verify it passes.

---

### Task 2: GitHub Rate Limit Parser and Safe Diagnostics (Q-05, Q-07)

**Files:**
- Create: `src/Backend/Application/Services/GitHubRateLimitParser.cs`
- Modify: `src/Backend/Application/Exceptions/GitHubIntegrationException.cs`
- Modify: `src/Backend/Infrastructure/GitHub/GitHubService.cs`
- Modify: `src/Backend/Infrastructure/GitHub/RepositorySourceService.cs`
- Test: `tests/Backend.Tests/Unit/GitHubRateLimitParserTests.cs`

- [x] Update `GitHubIntegrationException` with `StatusCode`, `RateLimitCategory`, `RetryAfterDelay`, `ResetTimeUtc`, `ServerDateUtc`, `HasInvalidHeaders`, and `HeaderDiagnostics` (allow-list only).
- [x] Implement `GitHubRateLimitParser` implementing RFC 9110 (delta-seconds from reception time, IMF-fixdate, RFC 850, asctime) and GitHub API rules (Primary limit via `x-ratelimit-reset`, Secondary limit via `retry-after` / 60s backoff).
- [x] Implement strict allow-list filtering for `HeaderDiagnostics` excluding `Authorization`, cookies, and tokens.
- [x] Update `GitHubService` and `RepositorySourceService` to parse response headers before `using (response)` disposes them.
- [x] Write unit tests for `GitHubRateLimitParserTests` covering TS-01 through TS-13.
- [x] Run unit tests and verify they pass.

---

### Task 3: Error Classification and Exponential Backoff Policy (Q-02, Q-05)

**Files:**
- Create: `src/Backend/Application/Services/AnalysisRetryPolicy.cs`
- Create: `src/Backend/Application/Interfaces/IAnalysisRetryPolicy.cs`
- Test: `tests/Backend.Tests/Unit/AnalysisRetryPolicyTests.cs`

- [x] Define retry classification: transient network/GitHub 5xx/rate-limited errors are retryable; permanent errors (token missing, 404, bad repo url, archive limit exceeded, fatal crashes) are non-retryable.
- [x] Implement exponential backoff formula $T = \text{BaseDelay} \times 2^{\text{attempt}-1} + \text{jitter}$ with cap `MaxRetryDelay` and pluggable deterministic jitter provider.
- [x] Implement ceiling check: if required delay > `MaxRateLimitWait` (15 min) or `AttemptCount >= MaxAttempts`, transition to `Failed`.
- [x] Write unit tests verifying transient vs permanent error classification, backoff calculation, ceiling enforcement, and deterministic jitter.
- [x] Run unit tests and verify they pass.

---

### Task 4: Repository Queue Operations with Strong Ownership and PostgreSQL Time (Q-01, Q-03, Q-09)

**Files:**
- Modify: `src/Backend/Application/Interfaces/Repositories/IAnalysisRepository.cs`
- Modify: `src/Backend/Infrastructure/Repository/AnalysisRepository.cs`
- Test: `tests/Backend.Tests/Integration/PostgreSqlQueueOperationsTests.cs`

- [x] Add `ScheduleRetryAsync` to `IAnalysisRepository` and update signatures of `CompleteAsync`, `FailAsync`, `ScheduleRetryAsync`: eliminate all guard-less overloads; `IAnalysisLeaseGuard guard` is non-nullable and strictly required.
- [x] Eliminate all SQL branches on PostgreSQL without advisory lock checks.
- [x] Execute protected write operations (`CompleteAsync`, `FailAsync`, `ScheduleRetryAsync`) directly through `guard.Connection` (`NpgsqlConnection`) using a dedicated `AppDbContext` instance (`UseNpgsql(guardConnection)`), locking atomically with `pid = pg_backend_pid()` and eliminating any TOCTOU window.
- [x] Update `TryAcquireNextPendingOrStaleAsync` in `AnalysisRepository`:
  - Filter `NextAttemptAt <= clock_timestamp()`.
  - Atomically increment `AttemptCount = AttemptCount + 1` in acquire update.
  - Neutralize poison pills: stale task with `AttemptCount >= MaxAttempts` is atomically set to `Failed` and skipped.
  - Support `skippedCandidateIds` using strongly typed `NpgsqlParameter` (`NpgsqlDbType.Array | NpgsqlDbType.Integer`).
- [x] Update `RenewLeaseAsync`, `CompleteAsync`, `FailAsync`, and implement `ScheduleRetryAsync`:
  - Use `clock_timestamp()` for timestamps.
  - Include strict lease expiration condition: `"LeaseExpiration" >= clock_timestamp()` (rejecting `NULL` lease for write operations as required by Q-03-A).
  - Mandatory advisory lock verification on the guard connection:
    `AND EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND classid = hashtext('netscope_analysis_workspace') AND objid = @id AND objsubid = 2 AND pid = pg_backend_pid() AND mode = 'ShareLock' AND granted = true)`.
- [x] Write integration tests verifying:
  - Atomic increment of `AttemptCount`.
  - Poison pill transition to `Failed` without worker pick-up.
  - Write rejection (`CompleteAsync`/`FailAsync`/`ScheduleRetryAsync`/`RenewLeaseAsync`) when lease expired, lease is `NULL`, or guard connection severed before write.
  - Cleaner acquiring exclusive lock after severed connection, confirming stale worker cannot write.
  - Mismatched `ExecutionToken` rejected.
  - Null or unacquired guard rejected.
  - Database state remains untouched on ownership loss.
- [x] Run integration tests (`PostgreSqlQueueOperationsTests`) and verify all 21 pass.

---

### Task 5: AnalysisService Pipeline Integration and Recovery from Ambiguous Commit (Q-03, Q-04)

**Files:**
- Modify: `src/Backend/Application/Services/AnalysisService.cs`
- Modify: `src/Backend/Api/BackgroundServices/AnalysisBackgroundService.cs`
- Test: `tests/Backend.Tests/Unit/AnalysisServiceRetryQueueTests.cs`

- [x] In `ExecuteAnalysisAsync`, pass `guard` directly into `CompleteAsync`, `FailAsync`, and `ScheduleRetryAsync` without conditional guard-less fallbacks.
- [x] In `ExecuteAnalysisAsync`, catch exceptions during `CompleteAsync` and inspect state via `GetAnalysisWithProcessIdAsync` (distinct physical connection from guard):
  - Strict consistency check `AreResultsConsistent(actualResults, expectedResults)`: count, rule, message, severity, filepath, linenumber, analyzer type, non-null, and deduplication.
  - Foreign token rejection: if `current.ExecutionToken != executionToken`, reject completion and retain state.
  - Incomplete results rejection: if `Status == Completed` but results inconsistent, mark as ambiguous commit (`isAmbiguousCommit = true`) and retain workspace.
  - Ambiguous COMMIT protection: if state check fails (`checkEx`) or results inconsistent, suppress automatic `FailAsync`, `ScheduleRetryAsync`, and `CleanupAsync`; log `AggregateException(commitEx, checkEx)`; retain workspace directory for manual inspection.
- [x] Separate PID semantics: capture `guardProcessId` (from guard connection) and `recoveryProcessId` (from independent inspection connection).
- [x] Integrate `IAnalysisRetryPolicy` in error handling: upon retryable error and within attempt limits, call `ScheduleRetryAsync` with calculated delay and mark `ownershipConfirmed = true` so workspace is cleaned for current token.
- [x] If error is non-retryable or attempts exhausted, call `FailAsync`.
- [x] Update `AnalysisBackgroundService` to log retry scheduling.
- [x] Write unit tests for `AnalysisServiceRetryQueueTests` verifying retry scheduling, poison pill failure, and ambiguous commit recovery.
- [x] Run unit tests and verify they pass.

---

### Task 6: Commit ACK Loss Fault-Injection Integration Test and Wire Protocol Parser (Q-04)

**Files:**
- Create: `tests/Backend.Tests/Integration/PostgreSqlLoopbackFaultProxy.cs`
- Create: `tests/Backend.Tests/Integration/PostgreSqlProtocolParser.cs`
- Create: `tests/Backend.Tests/Integration/PostgreSqlCommitAckLossIntegrationTests.cs`
- Create: `tests/Backend.Tests/Unit/PostgreSqlProtocolParserTests.cs`

- [x] Implement `PostgreSqlProtocolParser` with strict framing and format validation:
  - Unified `TryReadNullTerminatedString` helper with `scoped ref int offset`.
  - Structural validation for Query (`'Q'`), Parse (`'P'`), and CommandComplete (`'C'`) messages.
  - Strict length checking: reject malformed messages with extra trailing bytes or missing NUL terminators.
  - Integer overflow protection in framing calculation (`1L + msgLen <= int.MaxValue`).
  - Unit tests in `PostgreSqlProtocolParserTests`: 40 tests covering Query, Parse, CommandComplete, and framing (100% pass; Parse is validated structurally but not treated as commit command execution per Option A).
- [x] Fix5.1 targeted corrections:
  - F51-01: Parse semantics corrected to Option A (Simple Query Protocol `'Q'` only executes COMMIT).
  - F51-02: Isolated DB recovery check in `AnalysisService.ExecuteAnalysisAsync`, ensuring `commitEx` is properly re-thrown on `Running` status to outer retry/fail handler.
  - F51-04: Explicit `GetAnalysisWithProcessIdAsync` in `IAnalysisRepository` without default fallback.
  - F51-05: `AreResultsConsistent` includes `Recommendation`, bag/multiset matching, 19 unit tests in `AnalysisResultsConsistencyTests`.
  - F51-03: Ambiguous commit lifecycle verification tests in `AnalysisServiceRetryQueueTests`.
- [x] Fix5.2 final acceptance corrections:
  - F52-01: Added Scenario H (ambiguous commit does not allow next worker cycle to re-acquire completed task) and Scenario I (unexecuted commit allows safe re-acquisition after lease expiry) to `PostgreSqlCommitAckLossIntegrationTests` (10/10 scenarios pass).
  - F52-01: Added `AnalysisBackgroundService_WhenExecutionReturnsFalse_DoesNotMutateAnalysisState` to `AnalysisBackgroundServiceTests` (4/4 pass).
  - F52-02: Refined XML doc comment for `GetAnalysisWithProcessIdAsync` in `IAnalysisRepository.cs` for Npgsql single-connection backend PID reading.
  - F52-03: Corrected documentation unit test count to match actual 103/103 tests in `fix51_targeted_unit.trx`.
  - F52-04: Formed complete source archive in `dumps/td007_fix5_2_review_package/`.
- [x] Implement `PostgreSqlLoopbackFaultProxy` as an in-process TCP forwarder/proxy using robust binary parsing of PostgreSQL wire protocol 3.0:
  - Detects `COMMIT` command in client-to-server stream (`CommitDetected`).
  - Identifies `CommandComplete` (`'C'`) message with tag `COMMIT` in server-to-client stream (`CommitAckIntercepted`).
  - Drops the ACK packet without forwarding to client and aborts client TCP socket via `LingerState(true, 0); Close()` (`ClientSocketAborted`).
  - Implements `RejectNewConnectionsAfterDrop` to simulate database unreachability during recovery check.
- [x] Write `PostgreSqlCommitAckLossIntegrationTests` against real Testcontainers PostgreSQL 17 covering Scenarios A-I:
  - Scenario A: Real network/socket-level drop of COMMIT ACK via wire protocol proxy (recovers to Completed, validates distinct guard PID vs recovery PID, verifies results consistency, cleans workspace).
  - Scenario B: Network drop BEFORE commit processed by PostgreSQL (`DropOnClientCommit = true`, does NOT mark completed, NO blind cleanup, workspace retained).
  - Scenario C: Recovery state check failure due to database unreachability (`RejectNewConnectionsAfterDrop = true`, recognizes ambiguous commit, suppresses `FailAsync`/`ScheduleRetryAsync`, retains workspace).
  - Scenario D: Inconsistent/corrupted results on recovery (rejects completion, marks commit ambiguous, retains workspace).
  - Scenario E: Foreign `ExecutionToken` on recovery (rejects completion, does not touch other worker's task, retains workspace).
  - Scenario F: Idempotent repeated recovery on already completed analysis (no duplicate results inserted).
  - Scenario G: Post-commit callback exception via `CommitAckFaultInjectionInterceptor` (programmatic callback exception, distinct from real TCP RST).
  - Scenario H: End-to-end ambiguous commit lifecycle where PG committed, next worker cycle does not re-acquire completed task.
  - Scenario I: Safe re-acquisition of uncommitted task after lease expiry.
  - Normal execution through proxy without faults succeeds seamlessly.
- [x] Run fault-injection tests and verify all 10 pass.

---

### Task 7: Full Test Suite Verification

**Files:**
- Test: All tests in `tests/Backend.Tests`

- [x] Run `dotnet test tests/Backend.Tests/Backend.Tests.csproj` (excluding known deferred TD-003 CSharpAnalyzer tests).
- [x] Verify 100% pass rate across all unit and PostgreSQL integration tests.
- [x] Verify `dotnet ef migrations has-pending-model-changes`.

---

### Task 8: Documentation Synchronization

**Files:**
- Modify: `docs/plans/TD-007-Architecture-Verification.md`
- Modify: `docs/plans/TD-007-Technical-Investigation-Review.md`
- Modify: `docs/plans/Technical-Debt-Plan.md`

- [x] Synchronize `TD-007-Architecture-Verification.md` and `TD-007-Technical-Investigation-Review.md` with implemented solutions for Q-01..Q-11.
- [x] Update `Technical-Debt-Plan.md`: update TD-007 status to `IMPLEMENTATION_COMPLETE` and `PENDING_VERIFICATION` with test metrics and summary of implemented components.
- [x] Verify TD-002 remains `VERIFIED` and TD-003 remains `DEFERRED`.
- [ ] Final independent verification (`PENDING_VERIFICATION`).
