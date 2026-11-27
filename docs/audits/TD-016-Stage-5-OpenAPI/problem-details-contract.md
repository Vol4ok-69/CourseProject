# ProblemDetails Contract Analysis (RFC 9457)

- **Runtime Behavior (`src/Backend/Api/Common/ApiProblemDetails.cs`, `ApiJwtEvents.cs`, `Program.cs:145-178`):**
  - Every error response sets `Content-Type: application/problem+json`.
  - Every error response includes `type`, `title`, `status`, `detail`, `instance`, plus extension fields `code` (`SCREAMING_SNAKE_CASE` from `ApiErrorCodes.cs`) and `traceId`.
  - HTTP `400 Bad Request` model-validation errors (`ConfigureInvalidModelStateResponse`, `:79-103`) return `ValidationProblemDetails` containing `errors` (`Dictionary<string, string[]>`), `code: "VALIDATION_FAILED"`, and `traceId`.
  - HTTP `429 Too Many Requests` (`Program.cs:153-176`) also sets the `Retry-After` response header.
- **OpenAPI (`swagger.json:2276-2302`):**
  - Defines only standard `ProblemDetails` (`type`, `title`, `status`, `detail`, `instance` + `additionalProperties: {}`).
  - Omits `code`, `traceId`, and `ValidationProblemDetails.errors` (400 responses reference `#/components/schemas/ProblemDetails`).
  - Declares `application/json` instead of `application/problem+json` on all error responses due to controller-level `[Produces("application/json")]`.
