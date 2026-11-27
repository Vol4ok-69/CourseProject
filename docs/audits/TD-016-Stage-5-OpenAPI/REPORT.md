# TD-016 Stage 5 Report Summary (`run-1`)

- **Commit:** `a061bb8a388ef1848b64548d0a03178bb83162d7` (`main`)
- **OpenAPI Source:** `swagger.json` (OpenAPI `3.0.4`, Swashbuckle `10.2.3`)
- **Operations Checked:** 27 (`26` MVC actions + `1` `GET /health`)
- **DTOs & Enums Checked:** 19 public DTOs, 3 active enums (+1 missing `HistoryClassification`)
- **Findings (`13` total in `findings.json`):**
  - `confirmed_defect` (`medium`): **5** (`TD016-S5-001`..`TD016-S5-005`; downgraded from `high` because 0 clients are implemented today)
  - `client_generation_risk` (`medium`): **1** (`TD016-S5-006`)
  - `contract_ambiguity` (`medium`): **2** (`TD016-S5-007`, `TD016-S5-008`)
  - `documentation_gap`: **4** (`3` `low`: `TD016-S5-009`..`011`; `1` unrated info: `TD016-S5-012`)
  - `evolution_risk`: **1** (`TD016-S5-013`)

See `endpoint-contract-matrix.md`, `dto-schema-matrix.md`, `problem-details-contract.md`, `client-generation-assessment.md`, `findings.json`, and `verification-records.json`.
