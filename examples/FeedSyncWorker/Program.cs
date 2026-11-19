// A hosted worker that keeps a local mirror of the AI Agent Blocklist feed fresh.
//
//   export AIAGENTBLOCKLIST_TOKEN=<your 32-character token>
//   dotnet run --project examples/FeedSyncWorker -- --Feed:Directory=/var/lib/aiagentblocklist --Feed:IntervalHours=6
//
// Every cycle sends conditional GET requests, so unchanged files cost one 304 answer each.
// When aab_full.json changes, the worker swaps the in-memory Blocklist that other services read.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiAgentBlocklist;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FeedSyncWorker
{
    /// <summary>Thread-safe holder for the current list; register it as a singleton.</summary>
    public sealed class BlocklistHolder
    {
        private Blocklist? _current;

        public Blocklist? Current => Volatile.Read(ref _current);

        public void Swap(Blocklist next) => Volatile.Write(ref _current, next);
    }

    public sealed class FeedSyncService : BackgroundService
    {
        private readonly AiAgentBlocklistClient _client;
        private readonly BlocklistHolder _holder;
        private readonly ILogger<FeedSyncService> _log;
        private readonly string _directory;
        private readonly TimeSpan _interval;
        private readonly string[] _files;

        public FeedSyncService(AiAgentBlocklistClient client, BlocklistHolder holder, IConfiguration config,
            ILogger<FeedSyncService> log)
        {
            _client = client;
            _holder = holder;
            _log = log;
            _directory = config["Feed:Directory"] ?? Path.Combine(Path.GetTempPath(), "aiagentblocklist");
            _interval = TimeSpan.FromHours(double.TryParse(config["Feed:IntervalHours"], out var h) && h > 0 ? h : 6);
            var files = config["Feed:Files"];
            _files = string.IsNullOrWhiteSpace(files)
                ? AiAgentBlocklistClient.AllFiles.ToArray()
                : files.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!_files.Contains("aab_full.json")) _files = _files.Append("aab_full.json").ToArray();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var jsonPath = Path.Combine(_directory, "aab_full.json");
            if (File.Exists(jsonPath)) _holder.Swap(Blocklist.FromFile(jsonPath));

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var result = await _client.SyncAsync(_directory, _files, stoppingToken);
                    var changed = result.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
                    _log.LogInformation("Feed sync done: {Changed} of {Total} files changed", changed.Count, result.Count);

                    if (changed.Contains("aab_full.json") || _holder.Current == null)
                    {
                        var list = Blocklist.FromFile(jsonPath);
                        _holder.Swap(list);
                        _log.LogInformation("Loaded list version {Version} with {Count} entries", list.Version, list.Entries.Count);
                    }
                }
                catch (AccessEndedException)
                {
                    _log.LogError("The subscription has ended or is paused; keeping the last copy on disk");
                }
                catch (NotFoundException)
                {
                    _log.LogError("Unknown token or file name; check AIAGENTBLOCKLIST_TOKEN");
                }
                catch (AiAgentBlocklistException ex)
                {
                    _log.LogWarning(ex, "Feed sync failed with status {Status}; retrying next cycle", ex.Status);
                }

                try
                {
                    await Task.Delay(_interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    public static class Program
    {
        public static async Task Main(string[] args)
        {
            using var host = Host.CreateDefaultBuilder(args)
                .ConfigureServices(services =>
                {
                    services.AddSingleton(_ => new AiAgentBlocklistClient());
                    services.AddSingleton<BlocklistHolder>();
                    services.AddHostedService<FeedSyncService>();
                })
                .Build();
            await host.RunAsync();
        }
    }
}
