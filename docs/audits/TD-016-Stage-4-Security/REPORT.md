# Security Audit Report — CourseProject / NetScope (`run-1`)

- **Target:** `c:/Users/ivank/Desktop/Uceba/Projects/CourseProject`
- **Commit:** `a061bb8a388ef1848b64548d0a03178bb83162d7` (`main`)
- **Profile:** `deep`
- **Audit Mode / Execution Policy:** Static source-only audit (`source-only-no-os-sandbox`). Independent subagent verification was performed in Phase 3 and Phase 5; **dynamic execution was NOT performed** due to the absence of an OS-enforced empty-env + network-disabled + read-only sandbox on Windows.
- **Coverage Ledger:** 12 units (`3 covered`, `8 candidate`, `1 out_of_scope` for unimplemented CLI/Desktop/Telegram clients; `0 blocked`, `0 deferred`). *Note on ledger semantics:* per `validate-coverage-ledger.cjs`, `status: "candidate"` marks any unit with non-empty `result_fingerprints` (even when all linked fingerprints in `findings.json` are `rejected`, as for `NuGet Packages & Docker Base Images` and `GitHub PAT & Error Logging`), whereas `covered` requires `result_fingerprints: []`.
- **Findings Summary (`findings.json`, 24 total):**
  - `confirmed`: **0**
  - `needs_validation`: **13** (12 candidates requiring dynamic/infrastructure verification + 1 operational/lifecycle risk `ZW-005`)
  - `rejected`: **11** (9 refuted candidates + 2 supply-chain items `SUPPLY-01`/`SUPPLY-02` reclassified as hardening recommendations in `HARDENING.md`)

See `NEEDS-VALIDATION.md`, `HARDENING.md`, `FINDINGS-DETAIL.md`, and `VERIFICATION-RECORDS.json` for full details.
