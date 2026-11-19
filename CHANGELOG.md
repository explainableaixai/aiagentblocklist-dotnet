# Changelog

All notable changes to the `AiAgentBlocklist` NuGet package are listed here.

## 1.0.0

First release.

- `AiAgentBlocklistClient` for the subscriber download folder at `https://www.aiagentblocklist.com/download/<token>/`, with the token from the constructor or the `AIAGENTBLOCKLIST_TOKEN` environment variable and format checks before any request.
- `FileUrl`, `DownloadAsync`, `DownloadStringAsync` and `LoadBlocklistAsync`, all with `CancellationToken` support and a 30 second default timeout.
- `DownloadToAsync` with ETag sidecar files (`<path>.etag`), `If-None-Match` requests, weak and `-gzip` ETag handling, and atomic temp file plus rename writes.
- `SyncAsync` for all 19 feed files or a chosen subset, returning a name to updated map.
- `Blocklist` with `FromFile` and `FromJson`, lookup maps built once at load time, and `CheckDomain`, `CheckExtension`, `CheckProcess`, `CheckPackage`, `Check`, `Filter`, `Domains`, `AgentTypes` and `Kinds`.
- `Entry` record with the ten feed columns and `System.Text.Json` attributes.
- Exceptions: `AiAgentBlocklistException` with `NotFoundException` (404), `AccessEndedException` (403), `HttpStatusException` (other statuses) and `TokenException`.
- Optional `HttpClient` injection for `IHttpClientFactory`, custom proxies and tests.
- Examples: `FeedSyncWorker` (hosted `BackgroundService`), `OriginGuard` (ASP.NET Core middleware) and `IntuneExport` (CSV, Edge ID list and a PowerShell remediation script).
- Targets `net6.0`; no package dependencies.
