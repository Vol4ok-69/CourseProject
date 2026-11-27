# Architecture Summary — CourseProject / NetScope (Stage 4 Security Audit)

## 1. Product, Principals, and Protected Resources
- **Product:** NetScope — modular .NET 10 ASP.NET Core Web API static analysis platform for GitHub repositories (`CSharpAnalyzer`, `DatabaseAnalyzer`, `SqlAnalyzer`, `SecurityAnalyzer`) with PostgreSQL 17 persistence/queueing and a Docker-isolated `Database.Runner`. CLI, Desktop, and Telegram Bot clients are not yet implemented in this repository.
- **Principals:**
  - Unauthenticated HTTP client (`Register`, `Login`, `/health`).
  - Authenticated `User` (`RoleId = 2`, seeded in `DatabaseInitializer.cs:98`): manages own username/password/GitHub PAT, owns `Project`s, triggers GitHub sync and analyses.
  - `Admin` (`RoleId = 1`, seeded in `DatabaseInitializer.cs:97`): all `User` capabilities plus cross-user project/commit/analysis access (`?all=true`) and `AdminUsersController` (`[Authorize(Roles = "Admin")]`).
  - Internal workloads: `AnalysisBackgroundService`, `AnalysisCleanupBackgroundService`, and isolated `Database.Runner` container (`uid/gid 10001`).
- **Protected Resources:** `Users` (BCrypt `PasswordHash`, AES-256-GCM `EncryptedGitHubToken`), `Projects`, `Commits`, `Analyses`, `AnalysisResults`, workspace directories (`{Storage:AnalysisPath}/{AnalysisId}/{ExecutionToken}`), and container exchange volumes (`/tmp/netscope-database-analyzer/{AnalysisId}_{ExecutionToken}`).

## 2. Tech Stack & Execution Boundaries
- **Stack:** .NET 10, ASP.NET Core Web API, EF Core `10.0.12`, Npgsql `10.0.3`, PostgreSQL 17, `System.IdentityModel.Tokens.Jwt` `8.23.0`, `BCrypt.Net-Next` `4.2.0`, `Microsoft.CodeAnalysis.CSharp` `5.0.0`, Docker CLI `28.5.2-cli`.
- **Execution Policy:** `source-only-no-os-sandbox`. Because Windows host lacks an OS-enforced empty-env + network-disabled + read-only target/toolchain sandbox, target-controlled builds/tests/fixtures are not executed during this audit; existing Stage 0 test results (`1533/1533` passing) are referenced as prior evidence only.

## 3. Entry Surfaces & Data Flows
1. `HTTP -> Controllers -> Application Services -> EF Core / Npgsql -> PostgreSQL`
2. `HTTP (/api/auth/me/github, /api/projects/{id}/github/sync) -> AesGcmTokenEncryptionService -> GitHubService (https://api.github.com/)`
3. `GitHub zipball -> RepositorySourceService (quota/ZipSlip/symlink/NFC checks) -> workspace/{analysisId}/{executionToken}/repository -> Analyzers`
4. `DatabaseContainerRunner -> DockerCliProcessAdapter (docker run --network none --read-only --cap-drop ALL --user 10001:10001) -> Database.Runner -> output.json -> Backend`
5. `PostgreSQL queue (FOR UPDATE SKIP LOCKED) -> AnalysisBackgroundService -> PostgreSqlAnalysisLeaseGuard -> AnalysisCleanupBackgroundService`

## 4. Trust Boundaries & Controls
- **HTTP Auth/Authz:** JWT HMAC-SHA256 (`Program.cs:198-221`), `[Authorize]`, `project.OwnerId == userId || User.IsInRole("Admin")`, `pg_advisory_xact_lock('admin_role_protection')`.
- **HTTP Edge:** `ApiCorsPolicies` strict origin parser (`ApiCorsPolicies.cs:55-266`), `TokenBucketRateLimiter` (`AuthPolicy`, `Program.cs:145-196`), `ApiProblemDetails` 5xx masking (`ApiProblemDetails.cs:109-233`).
- **Crypto:** AES-256-GCM with 12-byte CSPRNG nonce (`AesGcmTokenEncryptionService.cs:11-122`), `SanitizeErrorMessage` PAT/path redaction (`AnalysisService.cs:1471+`).
- **Archive & Workspace:** 50 MiB zip / 250 MiB extracted / 10,000 files quotas, `ExecutionToken` isolation, `pg_try_advisory_lock` + `WorkspaceFileLockProvider` (`AnalysisCleanupService.cs`).
- **Docker Runner:** `--network none`, `--read-only`, `--cap-drop ALL`, `--security-opt no-new-privileges`, `--user 10001:10001`, `--cpus 1.0`, `--memory 512m`, `--pids-limit 64`, 60s timeout (`DockerCliProcessAdapter.cs:156-222`).

## 5. Selected Companions & Excluded Surfaces
- **Selected:** `WEB-PROTOCOL-AND-AUTH.md`, `CLOUD-AND-DEPLOYMENT.md`, `DATA-ISOLATION-AND-LIFECYCLE.md`, `RESOURCE-EXHAUSTION-AND-AVAILABILITY.md`, `SUPPLY-CHAIN-AND-RELEASE.md`, `PROTOCOLS-RPC-AND-MESSAGING.md`.
- **Excluded (`out_of_scope`):** Unimplemented clients (`src/Cli`, `src/Desktop`, `src/Bot`), `.github/workflows/*` (absent), `AI-AND-LLM.md`, `MEMORY-SAFETY-AND-BINARY.md`, `CLIENT-SIDE.md`, `DESKTOP-MOBILE-AND-LOCAL-IPC.md`.
