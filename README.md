# AiAgentBlocklist for .NET

Most Windows shops enforce policy in three places: Intune, Group Policy and their own ASP.NET Core services. This NuGet package lets C# code feed all three from one source, the [AI Agent Blocklist](https://www.aiagentblocklist.com/ai-agents/block-ai-agents.php). It pulls your subscriber files, keeps a local mirror current, and answers lookups from memory, so no request ever waits on our server.

[![NuGet](https://img.shields.io/nuget/v/AiAgentBlocklist.svg)](https://www.nuget.org/packages/AiAgentBlocklist)

Right now the download covers **2,600+ AI agent domains and 5,300+ device identifiers**. The identifiers span Chrome, Edge and Firefox extension IDs, macOS bundle IDs, winget and Microsoft Store IDs, process names, npm and PyPI packages, Homebrew casks and formulae, and editor plugin IDs for VS Code, Open VSX and JetBrains. We rebuild it every day under the same file links, so a mirror that syncs on a timer stays current without any reconfiguration.

## Contents

- [At a glance](#at-a-glance)
- [Install](#install)
- [Five minute start](#five-minute-start)
- [Why Windows teams add this](#why-windows-teams-add-this)
- [Worked example 1: a hosted BackgroundService](#worked-example-1-a-hosted-backgroundservice)
- [Worked example 2: ASP.NET Core middleware](#worked-example-2-aspnet-core-middleware)
- [Worked example 3: Intune and PowerShell export](#worked-example-3-intune-and-powershell-export)
- [Worked example 4: a nightly check of installed processes](#worked-example-4-a-nightly-check-of-installed-processes)
- [API surface](#api-surface)
- [How matching behaves](#how-matching-behaves)
- [Feed files you can mirror](#feed-files-you-can-mirror)
- [Exceptions and failure modes](#exceptions-and-failure-modes)
- [Dependency injection and HttpClient](#dependency-injection-and-httpclient)
- [FAQ](#faq)
- [Links](#links)
- [Related](#related)

## At a glance

| Item | Value |
|---|---|
| Package | `AiAgentBlocklist` on NuGet |
| Target | `net6.0`, runs on .NET 6, 7, 8 and later |
| Dependencies | none beyond the shared framework (`System.Text.Json`, `System.Net.Http`) |
| Network model | conditional GET for files, then local lookups |
| Auth | a private 32 character download token |
| Thread safety | a loaded `Blocklist` is immutable and safe to share across threads |
| License | MIT |

## Install

From a terminal in your project folder:

```bash
dotnet add package AiAgentBlocklist
```

Or in the project file, if you pin versions centrally:

```xml
<ItemGroup>
  <PackageReference Include="AiAgentBlocklist" Version="1.0.0" />
</ItemGroup>
```

Your subscription comes with a private download link. The 32 letters and digits after `/download/` are the token, and the client wants exactly that value. Plans and terms are linked from the Links section at the end of this page.

Keep the token out of source control. The constructor reads `AIAGENTBLOCKLIST_TOKEN` from the environment when you pass nothing, which fits user secrets in development and a Key Vault reference or a service environment variable in production.

## Five minute start

```csharp
using AiAgentBlocklist;

using var client = new AiAgentBlocklistClient(); // token from AIAGENTBLOCKLIST_TOKEN

// 1. Mirror two files into a folder; only changed files are rewritten.
var changed = await client.SyncAsync(@"C:\ProgramData\AiAgentBlocklist",
    new[] { "aab_full.json", "edge_extension_blocklist.json" });

// 2. Load the full list from the local copy and ask questions.
var list = Blocklist.FromFile(@"C:\ProgramData\AiAgentBlocklist\aab_full.json");

Entry? hit = list.CheckDomain("https://app.example-agent.ai/session?id=7");
if (hit != null)
    Console.WriteLine($"{hit.ToolName}: {hit.AgentType}, grade {hit.RiskGrade}");

Console.WriteLine(list.CheckExtension("abcdefghijklmnopabcdefghijklmnop")?.Kind); // chrome_ext
Console.WriteLine(list.CheckProcess(@"C:\Tools\SampleAgent.exe")?.Name);
Console.WriteLine(list.CheckPackage("pypi", "Demo_MCP.Server")?.Ident);
```

All names above are placeholders. They mirror the shape of real rows without pointing at any real product.

## Why Windows teams add this

An AI agent does not log in as itself. It runs inside a browser profile, an editor or a terminal that already holds the user's cookies, mailbox session and file shares. Whatever the person can open, the agent can open, and it can act on what it reads without a human checking each step.

That is the core of the problem for an endpoint team. AI browsers, agent extensions, coding agents and Model Context Protocol servers arrive through the same channels as ordinary software: a store click, a winget command, a package install. Nobody files a request, so nothing reaches the review queue.

Public guidance treats this as a governance task rather than a blocking reflex. The [risk framework that NIST publishes for AI](https://www.nist.gov/itl/ai-risk-management-framework) starts with knowing which AI systems are in use, and [CISA's AI resources](https://www.cisa.gov/ai) stress the same inventory step. A machine readable list is the practical way to do that at scale, and our page on [agent identity and delegated permissions](https://www.aiagentblocklist.com/ai-agents/ai-agent-identity-and-permissions.php) explains why borrowed sessions matter.

This client does not decide your policy. It gives you the facts per identifier, including agent type and a risk grade, and leaves the allow or block call to you. Many teams start in report mode, review a week of hits, then switch selected grades to enforcement.

## Worked example 1: a hosted BackgroundService

The usual home for the feed in a .NET estate is a worker that runs next to an existing service. The worker syncs on a timer, and when `aab_full.json` changes it swaps the parsed list that request handlers read.

```csharp
using AiAgentBlocklist;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed class BlocklistHolder
{
    private Blocklist? _current;
    public Blocklist? Current => Volatile.Read(ref _current);
    public void Swap(Blocklist next) => Volatile.Write(ref _current, next);
}

public sealed class FeedSyncService : BackgroundService
{
    private const string Dir = "/var/lib/aiagentblocklist";
    private readonly AiAgentBlocklistClient _client;
    private readonly BlocklistHolder _holder;
    private readonly ILogger<FeedSyncService> _log;

    public FeedSyncService(AiAgentBlocklistClient client, BlocklistHolder holder, ILogger<FeedSyncService> log)
        => (_client, _holder, _log) = (client, holder, log);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var json = Path.Combine(Dir, "aab_full.json");
        if (File.Exists(json)) _holder.Swap(Blocklist.FromFile(json));   // warm start from disk

        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        do
        {
            try
            {
                var result = await _client.SyncAsync(Dir, new[] { "aab_full.json", "domains.txt" }, stop);
                if (result["aab_full.json"] || _holder.Current is null)
                    _holder.Swap(Blocklist.FromFile(json));
            }
            catch (AccessEndedException)
            {
                _log.LogError("Subscription ended or paused; serving the last good copy");
            }
            catch (AiAgentBlocklistException ex)
            {
                _log.LogWarning(ex, "Sync failed (status {Status}); next attempt on the next tick", ex.Status);
            }
        } while (await timer.WaitForNextTickAsync(stop));
    }
}
```

Register it in `Program.cs`:

```csharp
builder.Services.AddSingleton(_ => new AiAgentBlocklistClient());
builder.Services.AddSingleton<BlocklistHolder>();
builder.Services.AddHostedService<FeedSyncService>();
```

The worker loads the copy on disk before the first sync, so a restart during an outage still serves a list. The swap is a single reference write, which means readers never see a half built index.

A failed cycle only logs. The previous list is still correct enough to use until the next tick succeeds.

The repository ships a runnable version in `examples/FeedSyncWorker`. Run it with `dotnet run --project examples/FeedSyncWorker -- --Feed:Directory=./feed --Feed:IntervalHours=6`.

## Worked example 2: ASP.NET Core middleware

Internal APIs see a new kind of caller now: an agent extension or an AI browser page that calls your service with the user's cookies attached. The `Origin` and `Referer` headers usually reveal it, either as a `chrome-extension://<id>` origin or as a page on an agent domain.

```csharp
public sealed class AgentOriginMiddleware
{
    private readonly RequestDelegate _next;
    private readonly BlocklistHolder _holder;

    public AgentOriginMiddleware(RequestDelegate next, BlocklistHolder holder)
        => (_next, _holder) = (next, holder);

    public async Task InvokeAsync(HttpContext ctx)
    {
        var list = _holder.Current;
        var origin = ctx.Request.Headers.Origin.ToString();
        Entry? hit = null;

        if (list != null && origin.Length > 0)
        {
            hit = origin.Contains("extension://", StringComparison.OrdinalIgnoreCase)
                ? list.CheckExtension(new Uri(origin).Host)
                : list.CheckDomain(origin);
        }
        hit ??= list?.CheckDomain(ctx.Request.Headers.Referer.ToString());

        if (hit is { RiskGrade: "High" or "Severe" })
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsync("This API does not accept requests from AI agents.");
            return;
        }
        if (hit != null) ctx.Items["ai-agent"] = hit;   // let later handlers log or step up auth
        await _next(ctx);
    }
}

// Program.cs
app.UseMiddleware<AgentOriginMiddleware>();
```

`CheckDomain` accepts the raw header value. It strips the scheme, any user info, the port, the path, the query and the fragment before it matches, so you do not need to parse the URL yourself.

Grade based enforcement is a pattern we see often: refuse the top grades, tag the rest, and review the tags. A runnable variant with a fixed file path sits in `examples/OriginGuard`, and you can poke it with `curl -H "Origin: chrome-extension://abcdefghijklmnopabcdefghijklmnop"`.

## Worked example 3: Intune and PowerShell export

Endpoint teams often want artifacts, not code. The feed already includes ready policy files such as `edge_extension_blocklist.json` and `chrome_edge_extension_blocklist.reg`, and the client can also cut custom exports from the JSON when you need a subset.

```csharp
var list = Blocklist.FromFile("aab_full.json");
string[] keep = { "Elevated", "High", "Severe" };

var edgeIds = list.Filter(kind: "edge_ext")
                  .Where(e => keep.Contains(e.RiskGrade))
                  .Select(e => e.Ident)
                  .Distinct();

// One ID per line: paste into the Intune settings catalog list for Edge.
File.WriteAllLines("edge_extension_ids.txt", edgeIds);

// A CSV that a remediation script or a reporting workbook can read.
var rows = list.Filter(kind: "winget").Concat(list.Filter(kind: "process_win"))
               .Select(e => $"{e.Kind},{e.Ident},{e.AgentType},{e.RiskGrade}");
File.WriteAllLines("windows_agents.csv", rows.Prepend("kind,ident,agent_type,risk_grade"));
```

On the PowerShell side, the CSV feeds a detection script directly:

```powershell
$agents = Import-Csv .\windows_agents.csv | Where-Object kind -eq 'process_win'
$names  = $agents.ident | ForEach-Object { $_ -replace '\.exe$','' }
$found  = Get-Process | Where-Object { $names -contains $_.ProcessName }
if ($found) { Write-Output "AI agent process running: $($found.ProcessName -join ', ')"; exit 1 }
exit 0
```

`examples/IntuneExport` goes one step further and writes `Set-ExtensionBlocklist.ps1`, which fills the Chrome and Edge `ExtensionInstallBlocklist` registry keys. Microsoft documents that policy in the [Edge browser policy reference](https://learn.microsoft.com/en-us/deployedge/microsoft-edge-policies#extensioninstallblocklist). For the console side of Intune, our [Intune setup walkthrough](https://www.aiagentblocklist.com/network/intune-block-ai.php) shows where each file goes, and the [Edge extension policy notes](https://www.aiagentblocklist.com/extensions/edge-extension-blocklist.php) cover the browser settings themselves.

If your rule is the reverse, block everything except approved extensions, the list still helps: it tells reviewers which requests to look at twice. The guide to [running an extension allowlist](https://www.aiagentblocklist.com/extensions/chrome-extension-allowlist.php) describes that setup.

## Worked example 4: a nightly check of installed processes

Some teams only want a report. This snippet runs as a scheduled task, compares running processes with the list and writes one line per finding to the Windows event log or a file.

```csharp
var list = Blocklist.FromFile(@"C:\ProgramData\AiAgentBlocklist\aab_full.json");

foreach (var p in System.Diagnostics.Process.GetProcesses())
{
    var hit = list.CheckProcess(p.ProcessName);           // "SampleAgent" matches "SampleAgent.exe"
    if (hit == null) continue;
    Console.WriteLine($"{Environment.MachineName}\t{p.ProcessName}\t{hit.ToolName}\t{hit.AgentType}\t{hit.RiskGrade}");
}
```

Process matching ignores case and treats `name` and `name.exe` as the same, so `Process.ProcessName` (which has no extension) still finds Windows rows. A full path also works, because the client keeps only the file name.

Coding agents and local MCP servers deserve a second pass, since they often start from a package manager. Combine `CheckPackage("npm", ...)` and `CheckPackage("pypi", ...)` with a walk over global package folders, and see our note on [switching off unapproved MCP servers](https://www.aiagentblocklist.com/mcp/disable-mcp-servers.php) for the editor settings involved.

## API surface

| Member | Returns | Notes |
|---|---|---|
| `new AiAgentBlocklistClient(token?, options?, httpClient?)` | client | token from the argument or `AIAGENTBLOCKLIST_TOKEN`; checked at once |
| `FileUrl(name)` | `string` | full URL; throws `ArgumentException` for names outside the feed pattern |
| `DownloadAsync(name, ct)` | `Task<byte[]>` | one file, raw bytes |
| `DownloadStringAsync(name, ct)` | `Task<string>` | one file as UTF-8 text |
| `DownloadToAsync(name, path, ct)` | `Task<bool>` | conditional GET; `true` when the file was replaced |
| `SyncAsync(dir, names?, ct)` | `Task<IReadOnlyDictionary<string,bool>>` | all 19 files when `names` is null |
| `LoadBlocklistAsync(ct)` | `Task<Blocklist>` | downloads and parses `aab_full.json` |
| `Blocklist.FromFile(path)` / `FromJson(text)` | `Blocklist` | offline loading |
| `Version`, `Counts`, `Entries`, `Columns` | properties | header data and every row |
| `CheckDomain(hostOrUrl)` | `Entry?` | label boundary suffix match, domain rows only |
| `CheckExtension(id)` | `Entry?` | browser and editor extension IDs, any case |
| `CheckProcess(name)` | `Entry?` | Windows, macOS and command line names |
| `CheckPackage("npm" or "pypi", name)` | `Entry?` | npm lower case, PyPI normalised |
| `Check(identifier)` | `Entry?` | domain first, then any identifier |
| `Filter(agentType, kind, riskGrade)` | `IReadOnlyList<Entry>` | null means any; case insensitive |
| `Domains()`, `AgentTypes()`, `Kinds()` | `IReadOnlyList<string>` | distinct values |

`Entry` is an immutable record with `ToolName`, `ToolDomain`, `Category`, `Subcategory`, `AgentType`, `Kind`, `Ident`, `Name`, `Platform` and `RiskGrade`. Each property carries a `JsonPropertyName` attribute, so `JsonSerializer` writes the same snake case columns the feed uses.

## How matching behaves

Every index is a dictionary built once when the list loads. A check is a handful of hash lookups, which keeps per request middleware cheap even on busy services.

- **Domains.** Input may be a bare host or a full URL; after normalising, the client tries the host, then each parent on a dot boundary. `api.example-agent.ai` therefore finds `example-agent.ai`, while `notexample-agent.ai` finds nothing. Rows of other kinds never answer a domain query.
- **Extensions.** Exact match without regard to case across `chrome_ext`, `edge_ext`, `firefox_ext`, `vscode_ext`, `openvsx_ext` and `jetbrains_plugin`.
- **Processes.** Case insensitive across `process_win`, `process_mac` and `process_cli`, with the `.exe` suffix optional in both directions.
- **Packages.** npm names compare in lower case. PyPI names follow PEP 503 normalisation: `Demo_MCP.Server` collapses to `demo-mcp-server` before the lookup.
- **Any identifier.** `Check` runs the domain logic first, then an exact identifier lookup, then a case insensitive one. That makes it handy for mixed input such as a column of mixed bundle IDs and hosts.

Duplicate identifiers can occur, for example one extension listed under two tools; in that case the earliest row wins. Call `Filter` when you want every row for a value.

## Feed files you can mirror

`SyncAsync` without a name list fetches every file below. Most deployments only need two or three.

| File | Typical consumer |
|---|---|
| `aab_full.json` | this client, dashboards, custom exports |
| `aab_full.csv` | spreadsheets, SIEM lookup tables |
| `domains.txt` | DNS filters (block subdomains too) |
| `firewall_edl.txt` | firewall external dynamic lists |
| `hosts.txt` | a [hosts file](https://en.wikipedia.org/wiki/Hosts_%28file%29) on kiosks or lab machines |
| `proxy.pac` | proxy auto config for managed browsers |
| `dns_rpz.zone` | DNS servers with response policy zones |
| `edge_extension_blocklist.json` | the Edge `ExtensionInstallBlocklist` policy |
| `chrome_extension_blocklist.json` | the same policy for Chrome |
| `chrome_edge_extension_blocklist.reg` | both browsers through a registry import |
| `windows_package_ids.txt` | package IDs for both Windows installers (winget and Store), ready for Intune |
| `process_names.txt` | EDR rules and detection scripts |
| `npm_packages.txt`, `pypi_packages.txt` | package proxies and build checks |
| `counts.json`, `README.txt` | running totals by kind, agent type and risk grade, plus a short guide to every file |

The remaining files cover Firefox add-on IDs, macOS bundle IDs and a macOS configuration profile. `AiAgentBlocklistClient.AllFiles` lists all 19 names in a stable order.

## Exceptions and failure modes

Every error derives from `AiAgentBlocklistException`, which carries `Status` (the HTTP code, or 0 when no answer arrived) and a message. The token never appears in a message; the file name does.

| Exception | When | Sensible reaction |
|---|---|---|
| `TokenException` | token missing or not 32 letters and digits | fix configuration; thrown by the constructor |
| `NotFoundException` (404) | unknown token or unknown file name | check the token you copied |
| `AccessEndedException` (403) | subscription ended or paused | keep the last copy, alert an admin |
| `HttpStatusException` | any other non success status | retry on the next cycle |
| `AiAgentBlocklistException` (Status 0) | DNS, TLS, timeout, disk or JSON trouble | look at `InnerException` |

```csharp
try
{
    await client.DownloadToAsync("domains.txt", @"D:\feeds\domains.txt", stoppingToken);
}
catch (NotFoundException)        { /* token typo or retired file name */ }
catch (AccessEndedException)     { /* renew; the old file stays usable */ }
catch (AiAgentBlocklistException ex) when (ex.Status == 0)
{
    logger.LogWarning(ex.InnerException, "Network or disk problem");
}
```

Cancellation through your own `CancellationToken` surfaces as `OperationCanceledException`, as .NET code expects. Only the internal timeout (30 seconds by default) is converted into the base exception, which keeps shutdown paths clean in hosted services.

`DownloadToAsync` writes into a temporary file beside the target and renames it into place. A reader such as a DNS server never sees a partial file, and a failed download leaves the previous copy untouched. The ETag sits in a sidecar file named `<target>.etag`.

## Dependency injection and HttpClient

The third constructor argument accepts any `HttpClient`, which lets you plug the client into `IHttpClientFactory`, add Polly policies, or route through a corporate proxy with a custom handler.

```csharp
builder.Services.AddHttpClient("aab")
       .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
       {
           Proxy = new WebProxy("http://proxy.corp.example:3128"),
           AutomaticDecompression = DecompressionMethods.All,
       });

builder.Services.AddSingleton(sp => new AiAgentBlocklistClient(
    token: builder.Configuration["AiAgentBlocklist:Token"],
    options: new AiAgentBlocklistOptions { Timeout = TimeSpan.FromSeconds(60) },
    httpClient: sp.GetRequiredService<IHttpClientFactory>().CreateClient("aab")));
```

When you pass your own `HttpClient`, the library never disposes it. Without one, the client creates a handler that honours the standard proxy environment variables and gzip.

`AiAgentBlocklistOptions` has three settings: `BaseUrl`, `Timeout` and `UserAgent`. The default agent string is `aiagentblocklist-dotnet/1.0.0` with the product URL in parentheses; change it if your egress rules key on it.

For unit tests in your own code, hand the client an `HttpClient` built on a stub `HttpMessageHandler`. Our test suite in `tests/AiAgentBlocklist.Tests` does exactly that for the ETag sequence and status mapping.

## FAQ

### Does every lookup call your server?

No. Lookups run against the in memory `Blocklist`. The network is only touched when you call a download or sync method, and an unchanged file costs a single `304 Not Modified` answer.

### How often should the worker sync?

We publish a fresh build every day. Syncing every few hours keeps you close behind the latest build at almost no cost, thanks to conditional requests.

### Can I keep using the list offline, for example on an air gapped network?

Yes. Sync on a connected machine, copy `aab_full.json` across, and load it with `Blocklist.FromFile`. Nothing in the matching code needs a connection.

### What does the risk grade mean for my policy?

Grades run from Low through Moderate, Elevated and High to Severe, with "Not graded" for rows that carry no grade yet. Treat the grade as one input to your own review, next to who uses the tool and why. Our [risk assessment guide](https://www.aiagentblocklist.com/guides/ai-agent-risk-assessment.php) shows one way to map grades to actions.

### Why did the client throw before sending anything?

The constructor and `FileUrl` validate input first. A token must be exactly 32 letters and digits, and a file name must match the feed naming pattern, which also blocks path tricks such as `../`.

### Is the package usable from PowerShell 7 directly?

Yes, PowerShell 7 runs on .NET, so `Add-Type -Path` on the package DLL works. Most admins find the exported CSV or the ready `.reg` file simpler, though.

### Who do we contact about the feed or the client?

Use our [message page for questions and bug reports](https://www.aiagentblocklist.com/contact.php), or email info@alpha-quantum.com. Both reach the people who maintain this package.

## Links

- Source code: [explainableaixai/aiagentblocklist-dotnet on GitHub](https://github.com/explainableaixai/aiagentblocklist-dotnet)
- Package page: [AiAgentBlocklist on NuGet](https://www.nuget.org/packages/AiAgentBlocklist)
- Plans and subscription terms: [what a feed subscription includes](https://www.aiagentblocklist.com/pricing.php)
- Background reading: [ENISA publications on AI and cybersecurity](https://www.enisa.europa.eu/), plus the NIST and CISA material cited in the section on why Windows teams add this
- Changes: `CHANGELOG.md` in this repository. The code ships under the MIT license (`LICENSE`).

Product names are trademarks of their respective owners.

## Related

If you also manage AI tools in general, see the [AI tools blocklist](https://www.aitoolsblocklist.com). Mobile device admins may want the [app blocklist for MDM](https://www.appblocklist.com). For broad category filtering there is the [web filtering database](https://www.webfilteringdatabase.com).
