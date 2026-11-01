# LoGuard Security SDK for .NET

Official .NET 10 SDK for LoGuard Security monitoring.

## Installation

```bash
dotnet add package LoGuardSecurity.Sdk
```

Requires .NET 10.

## Quick start

```csharp
using LoGuard.Sdk;

await using var client = new LoGuardClient(
    apiKey: Environment.GetEnvironmentVariable("LOGUARD_API_KEY")!,
    baseUrl: "https://loguard.org",
    env: "production");

// Track a failed login attempt
await client.EventAsync(new EventInput(
    Type: "login_failed",
    Ip: "203.0.113.10",
    Path: "/api/login",
    StatusCode: 401));
```

## Automatic HTTP field capture

```csharp
app.UseLoGuard(client, new LoGuardMiddlewareOptions {
    SecurityCapture = true,
    MaxBodyBytes = 32768,
    MaxJsonDepth = 8,
});
```

Register before endpoints/body readers. Capture is opt-in and selects all
statuses as `http_request`; 401 remains `login_failed`. Existing registrations
retain `http_error` and status selection. `TrackAll` selects all statuses without
enabling field capture. Options are copied during setup.

The middleware observes only downstream Stream reads, never pre-reading/draining
a body, and restores the original stream. Only fully observed JSON
objects/arrays, `application/*+json` and URL-encoded bodies are parsed, including
form-labeled JSON. Pre-consumed/rewound streams, already initialized BodyReader
features and unread bodies can produce `body_unavailable`. Multipart/files and
response bodies are omitted. Exceptions propagate unchanged; uncommitted
failures can report 500 without changing the response.

Secret-like names are removed recursively before queue admission. Defaults:
32 KiB raw body/query, depth 8, 64 inspected fields, 4096 UTF-8 bytes per key/value.
Body limits must be 1..1048576 bytes and depth 1..32; invalid configuration fails
at setup. Form bracket keys remain literal, duplicate names use the last
inspected value. Empty results are omitted; omissions use `body_*` /
`query_too_large` metadata. Header capture is explicitly allowlisted (64 names,
512 UTF-8 bytes per value), with credential headers blocked even if configured.
Field-name filtering is not PII detection and does not inspect allowed values
for embedded secrets. Review your data policy before enabling capture.

Forwarded chains walk right to left through exact trusted IP strings, stopping
at the first untrusted hop. CIDR entries are not supported by this integration.
Invalid/oversized chains fall back to the direct peer.

### HTTP delivery and lifecycle

One lazy worker per client drains a 2000-event waiting queue in batches up to 50.
Overflow drops the newest event. Exhausted retries/process termination can lose
events; no durable spool is added. Direct EventAsync behavior is unchanged.

Stop accepting application requests before
`await client.StopHttpDeliveryAsync(token)`. Cancellation aborts remaining HTTP
delivery. `await client.DisposeAsync()` allows 30 seconds before cancellation.
Wire this into the host lifecycle: middleware registration does not own the
client. A drained queue means attempts completed, not guaranteed ingest acceptance.

Run `dotnet test tests/LoGuard.Tests/LoGuard.Tests.csproj --nologo` for regression
checks, including the shared conformance vectors in `contracts/`.


### Shared HTTP capture acceptance

`contracts/http-capture-v1.json` defines 39 shared body-policy cases, executed
by this SDK's regression suite. They cover nested secret fields, encoded form
names, duplicates, media-type normalization, malformed JSON, empty bodies,
UTF-8 truncation, field budgets and depth/size boundaries. Native tests cover
framework stream handling and delivery separately. Passing these vectors does
not establish staging delivery or identical behavior for every possible input.
Filtering field names does not identify all personal or sensitive values.
