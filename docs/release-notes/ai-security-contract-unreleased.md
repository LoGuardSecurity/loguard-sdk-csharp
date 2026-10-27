# AI telemetry and error privacy — unreleased

## Changes

- Transport errors no longer include arbitrary HTTP response bodies or `detail` values in their messages. HTTP status classification and SDK exception types are retained. Network failures use bounded generic messages instead of underlying connection text.
- Added AI-event wire/signature and error-privacy regression tests, integration guidance and a dedicated CI workflow. Tests use synthetic data with mocks or loopback endpoints.

## Migration

If application code parses exception text or expects response/plan details there, switch to the SDK's exception types and application-owned error context. Never log credentials or full event objects. Manual AI telemetry uses the existing Event API; this release does not add automatic LLM capture.

The generic Event API preserves caller metadata. Exclude system/developer messages and credentials before invoking it. Memory queues do not inherit the server cache TTL. Do not introduce persistent AI-content spools or job payloads.

## Validation and release scope

The existing offline/unit/loopback suites and added regressions passed on the maintainer's Mac before this documentation-only finalization. CI actions are pinned to commit SHA; the multi-version CI matrix has not yet run for this branch.

These changes do not establish production end-to-end delivery or detector accuracy. SDK → ingest → temporary cache → detector → finding, notification isolation and persistent-storage checks remain separate release gates. Server processing remains off during preparation.

Package versions and tags are intentionally unchanged. Choose the release version after reviewing compatibility impact and CI results. No registry publication is performed by the workflow.
