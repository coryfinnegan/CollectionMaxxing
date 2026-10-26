---
name: aspire-telemetry
description: Query CollectionMaxxing server logs and traces through the Aspire dashboard telemetry API. Use when diagnosing API behavior from live evidence: starting the standalone dashboard, exporting OTLP from the API, aspire otel logs/traces/spans queries, trace-id correlation, time-window search, prod dashboard limits. Installed by aspire agent init (#375); the generic Aspire workflow lives in the aspire-monitoring skill.
---

# CollectionMaxxing Aspire Telemetry

Live server evidence for bug diagnosis. Telemetry is in-memory only: query
while the app runs, export what matters.

## What `aspire agent init` installed (#375)

- Six Aspire workflow skills in `.agents/skills/` and `.claude/skills/`
  (both trees must stay identical): `aspire`, `aspire-init`,
  `aspire-orchestration`, `aspire-monitoring`, `aspire-deployment`,
  `aspireify`. Companions (`playwright-cli`, `dotnet-inspect`) skipped.
- `aspire.config.json` at the repo root, pinning the AppHost path so CLI
  commands need no `--apphost`.
- No MCP server config: non-interactive init skips MCP. To opt in later,
  run `aspire agent mcp --dashboard-url <login-url>` (foreground process).

`aspire agent init` also writes `~/.agents/skills/`; delete that copy, this
repo keeps skills in the two mirrored trees only. Never run init with
`--skill-locations all`: it would write `.github/skills/`.

## Start here: standalone dashboard (verified path)

The AppHost path (`aspire start`) is blocked on this PC: DCP needs Docker
CLI >= 25 and the PC has 24.0.7, so the postgres resource never starts.
Until the owner upgrades Docker Desktop, use a standalone dashboard plus
the API directly. Prod owns ports 5480 (API) and 18888 (dashboard); the
ports below avoid everything on the PC.

```powershell
# 1. Dashboard in the background; note the login URL it prints.
aspire dashboard run --frontend-url http://localhost:18890 `
  --otlp-grpc-url http://localhost:4317 --otlp-http-url http://localhost:4318 `
  --non-interactive --nologo
```

```powershell
# 2. API with OTLP export, on a scratch port (never 5480: prod is there).
# Default exporter speaks gRPC: point at :4317, not :4318 (HTTP gives silence).
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_HTTP_PORTS = "5499"
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"
dotnet run --project src/CollectionMaxxing.Api --no-launch-profile
```

```powershell
# 3. Make requests, then query (pass the full login URL; the CLI
# normalizes it). /health/* is filtered from traces by design; the
# anonymous /openapi/v1.json is a good probe for a real trace.
Invoke-WebRequest http://localhost:5499/openapi/v1.json -UseBasicParsing
$dash = "http://localhost:18890/login?t=TOKEN"
aspire otel logs --dashboard-url $dash -n 20 --format Json --non-interactive --nologo
aspire otel traces --dashboard-url $dash -n 20 --format Json --non-interactive --nologo
```

Query patterns that work (CLI 13.4.6, all verified live):

- `aspire otel logs --dashboard-url $dash --trace-id <full-id>`:
  logs for one request. Get the id from a trace first.
- `aspire otel logs --dashboard-url $dash --search "severity:error"`.
- `aspire otel logs --dashboard-url $dash --search "Job worker"`.
- `aspire otel traces --dashboard-url $dash --search "status:error"`.
- `aspire otel spans <resource>`, `aspire export -o out.zip` for a bundle.

Standalone dashboards have no AppHost resource model: resource names show
as `unknown_service:*`, so prefer `--search` over the resource argument.

## Time windows (CLI 13.4.6 caveat)

The documented `timestamp:` search qualifier (`--search
"timestamp:>=2026-10-26T14:00:00"`) returns zero rows in every form
against this dashboard — text and severity filters on the same data work,
so treat server-side time filtering as broken until the CLI is upgraded.
Workaround, verified:

1. `aspire export --dashboard-url $dash -o /tmp/exp.zip` (OTLP JSON with
   `timeUnixNano` on every record).
2. Filter client-side, e.g. keep records with
   `start <= timeUnixNano <= end`, then pull `--trace-id` for the hits.
3. Traces from `aspire otel traces --format Json` carry `timestamp` and
   can be windowed directly; log entries from `aspire otel logs` carry no
   timestamp field, so window via traces or the export.

## PowerShell output gotcha

`aspire ... > file` writes UTF-16; parse with
`python -c "json.loads(open(f,'rb').read().decode('utf-16'))"` or redirect
with `| Out-File -Encoding utf8`. Piping CLI JSON straight into another
program is unreliable; always go through a file.

## Cleanup

Kill only processes you started (dashboard `aspire-managed.exe`, your API
`dotnet`), verify the ports are free, and delete any `secrets/keyring/`
the API run created (dev keys; gitignored but leave no trace).

## Prod telemetry (read-only; the dashboard is lead-owned)

- Prod dashboard: `http://127.0.0.1:18888`, image 9.0.0. It predates the
  telemetry API, so the CLI cannot query it ("does not have the telemetry
  API enabled"). A human can still read recent entries in the browser UI;
  agents cannot. Upgrading that image + enabling the API is a prod hosting
  change for the lead, not this loop.
- Prod API exports OTLP to the dashboard (`OTEL_EXPORTER_OTLP_ENDPOINT`
  in `deploy/docker-compose.yml`), so once the dashboard is upgraded the
  same `aspire otel ... --dashboard-url` queries work against prod.
- Console history: `docker logs <container> --since/--until` only reaches
  the current container; a redeploy discards the old container's logs.
- Deploy state: `C:\CollectionMaxxing\logs\deployer.log` records every
  swap (`deployed <sha>; smoke test passed`) and refusal; image SHAs are
  git SHAs, so `git merge-base --is-ancestor` proves what code ran when.
- Cover correlation: the release row's `provider_payloads` jsonb holds
  `coverStatus`/`coverFailure`/`coverUrl`; `refreshed_at == created_at`
  means the backfill never touched it (backfill always bumps
  `refreshed_at`). Cover files live at
  `<CoversRoot>/<blob_path>` on the `collectionmaxxing_files` volume;
  inspect read-only via a throwaway
  `docker run --rm -v collectionmaxxing_files:/data/files:ro` (the API
  image has no shell).
