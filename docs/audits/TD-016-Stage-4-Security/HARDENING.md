# Hardening Recommendations — CourseProject / NetScope (`run-1`)

Hardening items below are defense-in-depth improvements rather than confirmed vulnerabilities with a demonstrated trust-boundary violation.

1. **`SUPPLY-01` (Reclassified from `needs_validation`):** Add `packages.lock.json` (`RestorePackagesWithLockFile`) and `dotnet restore --locked-mode` in `infrastructure/docker/Dockerfile` (`:19`) and `infrastructure/docker/Dockerfile.database-analyzer` (`:7`).
2. **`SUPPLY-02` (Reclassified from `needs_validation`):** Pin Docker base images (`postgres:17`, `docker:28.5.2-cli`, `mcr.microsoft.com/dotnet/*:10.0`) and `netscope-database-analyzer:latest` by immutable `@sha256:` digests in `docker-compose.yml` and `infrastructure/docker/Dockerfile*`.
3. **JWT `ClockSkew` & `ValidAlgorithms`:** Explicitly set `ClockSkew = TimeSpan.Zero` (or minimal skew) and `ValidAlgorithms = [SecurityAlgorithms.HmacSha256]` in `src/Backend/Api/Program.cs:209-218`.
4. **JWT Signing Key Startup Check:** Validate minimum 32-byte length for `Jwt:Key` at startup in `src/Backend/Api/Program.cs:198-218` (matching `Security:TokenEncryptionKey` at `:96-100`).
5. **Host Header Validation:** Restrict `AllowedHosts` in `src/Backend/Api/appsettings.json` instead of `"*"` for non-containerized deployments.
6. **Legacy Unwired Types in `DatabaseAnalyzer.dll`:** Move `EfCoreModelInspector` (`src/Analyzers/Database/Providers/EfCoreModelInspector.cs`) and `LocalProcessAdapter` (`src/Analyzers/Database/Process/LocalProcessAdapter.cs`) out of production assemblies or behind `#if DEBUG` / test-only boundaries.
7. **`.locks/` Cleanup & `ValidImageRegex`:** Periodically prune stale `.locks/{analysisId}.lock` files (`WorkspaceFileLockProvider.cs`) and disallow leading `-` in `ValidImageRegex` (`DockerCliProcessAdapter.cs:415`).
8. **AES-GCM Key Lifecycle & AAD:** Bind `User.Id` + purpose as AAD in `AesGcmTokenEncryptionService.cs` and zero `_key` on dispose.
