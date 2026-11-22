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

- [ ] Add `AttemptCount` (int, default 0), `MaxAttempts` (int, default 3), and `NextAttemptAt` (DateTime?, null) to `Analysis.cs`.
- [ ] Configure EF Core mappings in `AnalysisConfiguration.cs` with default values, check constraints (`CK_Analyses_AttemptCount`, `CK_Analyses_MaxAttempts`), and index `IX_Analyses_Status_NextAttemptAt_CreatedAt`.
- [ ] Generate EF Core migration via `dotnet ef migrations add AddAnalysisRetryAndPoisonPillQueue`.
- [ ] Verify migration `Up` and `Down` methods adhere strictly to 3NF and PascalCase quoting rules.
- [ ] Write integration test verifying migration application, rollback, default values, and check constraints against PostgreSQL 17 Testcontainers.
- [ ] Run migration test and verify it passes.

---

### Task 2: GitHub Rate Limit Parser and Safe Diagnostics (Q-05, Q-07)

**Files:**
- Create: `src/Backend/Application/Services/GitHubRateLimitParser.cs`
- Modify: `src/Backend/Application/Exceptions/GitHubIntegrationException.cs`
- Modify: `src/Backend/Infrastructure/GitHub/GitHubService.cs`
- Modify: `src/Backend/Infrastructure/GitHub/RepositorySourceService.cs`
- Test: `tests/Backend.Tests/Unit/GitHubRateLimitParserTests.cs`

- [ ] Update `GitHubIntegrationException` with `StatusCode`, `RateLimitCategory`, `RetryAfterDelay`, `ResetTimeUtc`, `ServerDateUtc`, `HasInvalidHeaders`, and `HeaderDiagnostics` (allow-list only).
- [ ] Implement `GitHubRateLimitParser` implementing RFC 9110 (delta-seconds from reception time, IMF-fixdate, RFC 850, asctime) and GitHub API rules (Primary limit via `x-ratelimit-reset`, Secondary limit via `retry-after` / 60s backoff).
- [ ] Implement strict allow-list filtering for `HeaderDiagnostics` excluding `Authorization`, cookies, and tokens.
- [ ] Update `GitHubService` and `RepositorySourceService` to parse response headers before `using (response)` disposes them.
- [ ] Write unit tests for `GitHubRateLimitParserTests` covering TS-01 through TS-13.
- [ ] Run unit tests and verify they pass.

---

### Task 3: Error Classification and Exponential Backoff Policy (Q-02, Q-05)

**Files:**
- Create: `src/Backend/Application/Services/AnalysisRetryPolicy.cs`
- Create: `src/Backend/Application/Interfaces/IAnalysisRetryPolicy.cs`
- Test: `tests/Backend.Tests/Unit/AnalysisRetryPolicyTests.cs`

- [ ] Define retry classification: transient network/GitHub 5xx/rate-limited errors are retryable; permanent errors (token missing, 404, bad repo url, archive limit exceeded, fatal crashes) are non-retryable.
- [ ] Implement exponential backoff formula $T = \text{BaseDelay} \times 2^{\text{attempt}-1} + \text{jitter}$ with cap `MaxRetryDelay` and pluggable deterministic jitter provider.
- [ ] Implement ceiling check: if required delay > `MaxRateLimitWait` (15 min) or `AttemptCount >= MaxAttempts`, transition to `Failed`.
- [ ] Write unit tests verifying transient vs permanent error classification, backoff calculation, ceiling enforcement, and deterministic jitter.
- [ ] Run unit tests and verify they pass.

---

### Task 4: Repository Queue Operations with Strong Ownership and PostgreSQL Time (Q-01, Q-03, Q-09)

**Files:**
- Modify: `src/Backend/Application/Interfaces/Repositories/IAnalysisRepository.cs`
- Modify: `src/Backend/Infrastructure/Repository/AnalysisRepository.cs`
- Test: `tests/Backend.Tests/Integration/PostgreSqlQueueOperationsTests.cs`

- [ ] Add `ScheduleRetryAsync` to `IAnalysisRepository` and update signatures of `CompleteAsync`, `FailAsync`, `ScheduleRetryAsync` to accept optional `IAnalysisLeaseGuard? guard = null`.
- [ ] Update `TryAcquireNextPendingOrStaleAsync` in `AnalysisRepository`:
  - Filter `NextAttemptAt <= clock_timestamp()`.
  - Atomically increment `AttemptCount = AttemptCount + 1` in acquire update.
  - Neutralize poison pills: stale task with `AttemptCount >= MaxAttempts` is atomically set to `Failed` and skipped.
  - Support `skippedCandidateIds` using strongly typed `NpgsqlParameter` (`NpgsqlDbType.Array | NpgsqlDbType.Integer`).
- [ ] Update `RenewLeaseAsync`, `CompleteAsync`, `FailAsync`, and implement `ScheduleRetryAsync`:
  - Use `clock_timestamp()` for timestamps.
  - Include condition: `"LeaseExpiration" >= clock_timestamp()`.
  - When `guard` is provided, include advisory lock verification:
    `AND (@guardPid IS NULL OR EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND classid = hashtext('netscope_analysis_workspace') AND objid = @id AND objsubid = 2 AND pid = @guardPid AND granted = true))`.
- [ ] Write integration tests verifying:
  - Atomic increment of `AttemptCount`.
  - Poison pill transition to `Failed` without worker pick-up.
  - Write rejection (`CompleteAsync`/`FailAsync`/`ScheduleRetryAsync`) when lease expired or guard connection closed.
- [ ] Run integration tests and verify they pass.

---

### Task 5: AnalysisService Pipeline Integration and Recovery from Ambiguous Commit (Q-03, Q-04)

**Files:**
- Modify: `src/Backend/Application/Services/AnalysisService.cs`
- Modify: `src/Backend/Api/BackgroundServices/AnalysisBackgroundService.cs`
- Test: `tests/Backend.Tests/Unit/AnalysisServiceRetryQueueTests.cs`

- [ ] In `ExecuteAnalysisAsync`, pass `guard` into `CompleteAsync`, `FailAsync`, and `ScheduleRetryAsync`.
- [ ] In `ExecuteAnalysisAsync`, catch exceptions during `CompleteAsync` and check `GetByIdAsync`: if `Status == Completed` and `ExecutionToken == executionToken`, recognize completion as successful and preserve `ownershipConfirmed = true`.
- [ ] Integrate `IAnalysisRetryPolicy` in error handling: upon retryable error and within attempt limits, call `ScheduleRetryAsync` with calculated delay and mark `ownershipConfirmed = true` so workspace is cleaned for current token.
- [ ] If error is non-retryable or attempts exhausted, call `FailAsync`.
- [ ] Update `AnalysisBackgroundService` to log retry scheduling.
- [ ] Write unit tests for `AnalysisServiceRetryQueueTests` verifying retry scheduling, poison pill failure, and ambiguous commit recovery.
- [ ] Run unit tests and verify they pass.

---

### Task 6: Commit ACK Loss Fault-Injection Integration Test (Q-04)

**Files:**
- Create: `tests/Backend.Tests/Integration/CommitAckLossInterceptor.cs`
- Create: `tests/Backend.Tests/Integration/PostgreSqlCommitAckLossIntegrationTests.cs`

- [ ] Create `CommitAckLossInterceptor` inheriting `DbTransactionInterceptor` that intercepts `TransactionCommittedAsync`: after PostgreSQL has committed the transaction, simulates connection drop / throws `NpgsqlException` / `IOException`.
- [ ] Write `PostgreSqlCommitAckLossIntegrationTests` against real Testcontainers PostgreSQL 17:
  - Worker executes `ExecuteAnalysisAsync` with interceptor active.
  - Commit succeeds in PostgreSQL, but interceptor throws network error during ACK reception.
  - `AnalysisService` verifies actual database state via `GetByIdAsync`.
  - Confirm `ExecuteAnalysisAsync` returns `true`.
  - Confirm task status is `Completed` with results present in database.
  - Confirm repeated `CompleteAsync` does NOT duplicate results.
  - Confirm workspace cleanup executed safely.
- [ ] Run fault-injection test and verify it passes.

---

### Task 7: Full Test Suite Verification

**Files:**
- Test: All tests in `tests/Backend.Tests`

- [ ] Run `dotnet test tests/Backend.Tests/Backend.Tests.csproj` (excluding known deferred TD-003 CSharpAnalyzer tests).
- [ ] Verify 100% pass rate across all unit and PostgreSQL integration tests.
- [ ] Verify `dotnet ef migrations has-pending-model-changes`.

---

### Task 8: Documentation Synchronization

**Files:**
- Modify: `docs/plans/TD-007-Architecture-Verification.md`
- Modify: `docs/plans/TD-007-Technical-Investigation-Review.md`
- Modify: `docs/plans/Technical-Debt-Plan.md`

- [ ] Synchronize `TD-007-Architecture-Verification.md` and `TD-007-Technical-Investigation-Review.md` with implemented solutions for Q-01..Q-11.
- [ ] Update `Technical-Debt-Plan.md`: update TD-007 status to `VERIFIED` with test metrics and summary of implemented components.
- [ ] Verify TD-002 remains `VERIFIED` and TD-003 remains `DEFERRED`.
