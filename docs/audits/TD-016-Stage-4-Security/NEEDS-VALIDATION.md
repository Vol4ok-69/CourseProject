# Needs Validation & Operational Risks — CourseProject / NetScope (`run-1`)

All 13 records below are source-verified (`findings.json`), without assigned severity, because dynamic execution was not performed (`source-only-no-os-sandbox`).

## A. Candidates Requiring Dynamic or Infrastructure Validation (`12`)
1. **`auth-jwt-no-revocation-stale-claims`** — Stateless 1h HS256 JWTs lack `jti`/token-version; role demotion, user deletion, or password change does not invalidate active tokens (`JwtTokenService.cs:24`, `ApiJwtEvents.cs:39`, `UserService.cs:100`).
2. **`CAND-SE-002`** — `PUT /api/auth/me/username`, `PUT /api/auth/me/github`, `POST .../github/sync`, and `POST .../analyses` lack `[EnableRateLimiting]` and `Program.cs:145-196` has no `GlobalLimiter`.
3. **`DOCKER-01`** — `docker-compose.yml:68,71-72` mounts `/var/run/docker.sock` with `group_add: ["${DOCKER_GID:-0}"]`.
4. **`DOCKER-02`** — `DatabaseContainerRunner.cs:133-137,244-255` sets `0777` on `outputDirectory` and reads `output.json` after non-atomic `FileInfo` checks.
5. **`HUNTER-W2-001`** — `ConnectionStringPasswordRegex` (`Sec001HardcodedSecretsRule.cs:20-22`, `Sec002PlaintextConfigSecretsRule.cs:17-19`) uses adjacent lazy wildcards `=.*?;.*?` without `matchTimeout` or `RegexOptions.NonBacktracking` (potential pathological regex runtime risk; catastrophic backtracking not dynamically proven).
6. **`HUNTER-W2-002`** — `CSharpAnalyzer.cs:52`, `SqlAnalyzer.cs:58`, `SecurityAnalyzer.cs:64,92` lack analyzer-level file-size caps and file-level `ReparsePoint` checks (upstream extraction caps single files at 50 MiB / 250 MiB total and skips Unix symlinks `0xA000`; actual OOM/resource impact at 50 MiB remains unverified dynamically).
7. **`HUNTER-W2-003`** — `GET /health` (`Program.cs:313`) is unauthenticated and unthrottled, calling `DatabaseHealthCheck.CanConnectAsync` (`DatabaseHealthCheck.cs:25`) per request.
8. **`HUNTER-W2-004`** — `GitHubSyncService.SyncAsync` (`:105-202`) has no application-level page/commit cap, accumulates DAG state in memory, and rebuilds `allExpandedParents` over `vExpanded` on every page (actual resource impact not dynamically reproduced).
9. **`QUEUE-01`** — `TryAcquireNextPendingOrStaleAsync` (`AnalysisRepository.cs:190`) caps skipped candidates at 50; `AnalysisBackgroundService` polls sequentially.
10. **`QUEUE-03`** — `AnalysisService.EnqueueAsync` (`:32-74`) and `AnalysesController.Create` (`:30-74`) lack per-user or global queue depth caps.
11. **`ZW-001`** — `GitHubRepositoryReference.Parse` (`:7-33`) does not validate scheme or characters itself; known MVC paths use `CreateProjectDto`/`UpdateProjectDto` (`https://`), though `[^/\s]+` permits percent-encoded chars (`%2F`/`%2E%2E`) and any DTO bypass via another path is not established.
12. **`ZW-003`** — `RepositorySourceService.ExtractArchiveInternalCoreAsync` (`:431-502`) bypasses `MaxFileCount` (10,000) on directory entries (`item.IsDirectory`); archive size is bounded at 50 MiB upstream, but actual memory/inode impact requires dynamic verification.

## B. Operational / Lifecycle Risk (`1`)
13. **`ZW-005`** — `RepositorySourceService.CleanupInternalAsync` (`:604-605`) returns early if `!Directory.Exists(fullTargetDir)`, skipping immediate cleanup of `containerExchangeDir` (`:637-657`); mitigated by periodic `AnalysisCleanupService` background cleanup.
