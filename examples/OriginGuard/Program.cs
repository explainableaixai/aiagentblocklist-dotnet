// ASP.NET Core middleware that refuses requests sent from listed AI agent pages or extensions.
//
//   dotnet run --project examples/OriginGuard -- /path/to/aab_full.json
//   curl -H "Origin: chrome-extension://abcdefghijklmnopabcdefghijklmnop" http://localhost:5000/api/records
//
// It reads the Origin and Referer headers. A chrome-extension://, extension:// or moz-extension://
// origin is checked as an extension ID; an https:// origin is checked as a domain (suffix match).
// The list is loaded from a local copy; pair it with the FeedSyncWorker example to keep it fresh.

using System;
using System.Threading.Tasks;
using AiAgentBlocklist;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OriginGuard
{
    public sealed class AgentOriginMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly Blocklist _list;
        private readonly ILogger<AgentOriginMiddleware> _log;

        public AgentOriginMiddleware(RequestDelegate next, Blocklist list, ILogger<AgentOriginMiddleware> log)
        {
            _next = next;
            _list = list;
            _log = log;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var hit = Inspect(context.Request.Headers["Origin"].ToString())
                      ?? Inspect(context.Request.Headers["Referer"].ToString());
            if (hit != null)
            {
                _log.LogWarning("Refused {Path}: {Kind} {Ident} ({AgentType})", context.Request.Path, hit.Kind, hit.Ident, hit.AgentType);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Requests from AI agents are not allowed on this service.");
                return;
            }
            await _next(context);
        }

        private Entry? Inspect(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "null") return null;
            var scheme = value.IndexOf("://", StringComparison.Ordinal);
            if (scheme > 0 && value.Substring(0, scheme).EndsWith("extension", StringComparison.OrdinalIgnoreCase))
            {
                var rest = value.Substring(scheme + 3);
                var slash = rest.IndexOf('/');
                return _list.CheckExtension(slash >= 0 ? rest.Substring(0, slash) : rest);
            }
            return _list.CheckDomain(value);
        }
    }

    public static class Program
    {
        public static void Main(string[] args)
        {
            var path = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "aab_full.json";
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddSingleton(Blocklist.FromFile(path));

            var app = builder.Build();
            app.UseMiddleware<AgentOriginMiddleware>();
            app.MapGet("/api/records", () => Results.Json(new[] { new { id = 1, title = "internal record" } }));
            app.Run();
        }
    }
}
