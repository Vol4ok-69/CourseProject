# Findings Detail — CourseProject / NetScope (`run-1`)

## 1. Confirmed Vulnerabilities (`0`)
None. Under `source-only-no-os-sandbox`, dynamic reproduction was not performed; source-verified candidates remain in `NEEDS-VALIDATION.md` without severity.

## 2. Needs Dynamic / Infrastructure Validation (`12`) & Operational Risks (`1`)
See `NEEDS-VALIDATION.md` for all 13 `needs_validation` records.

## 3. Hardening Recommendations
See `HARDENING.md` (includes `SUPPLY-01` and `SUPPLY-02`, reclassified from `needs_validation`).

## 4. Rejected Candidates (`11`)
1. `auth-jwt-key-length-unvalidated` — `Jwt:Key` is deployment-supplied and `System.IdentityModel.Tokens.Jwt` 8.23.0 enforces HMAC key length at runtime.
2. `CAND-SE-001` — `UseForwardedHeaders` defaults `KnownProxies` to loopback only (`127.0.0.1`, `::1`), preventing external `X-Forwarded-For` spoofing.
3. `CAND-SE-003` — `DatabaseAnalyzer` only propagates standard .NET `OperationCanceledException` with no secrets/paths.
4. `CAND-SE-004` — `EncryptedGitHubToken` is strictly server-side; swapping ciphertext requires direct DB write access.
5. `CAND-SE-005` — `SanitizeErrorMessage` strips stack traces, redacts PATs/paths/IPs, and truncates to 500 chars; regex edge cases are defense-in-depth only.
6. `DOCKER-03` — `Program.cs:59-63` requires `Docker:ExchangeVolume` at startup, making the bind-mount fallback unreachable in the API runtime.
7. `QUEUE-02` — `Status = 1` (`Running`) is always written with a non-null `LeaseExpiration`.
8. `SUPPLY-01` — Reclassified as hardening recommendation (`HARDENING.md`); missing lockfiles alone do not prove a boundary violation without a compromised NuGet feed.
9. `SUPPLY-02` — Reclassified as hardening recommendation (`HARDENING.md`); mutable Docker tags alone do not prove a boundary violation without a compromised registry/tag attack path.
10. `ZW-002` — `Commit.CommitHash` is strictly validated by `CreateCommitDto` (`\A[0-9a-fA-F]{7,64}\z`) or sourced from `https://api.github.com/`.
11. `ZW-004` — `Link` headers are parsed only from trusted `https://api.github.com/` HTTPS responses.
