using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AiAgentBlocklist;
using Xunit;

namespace AiAgentBlocklist.Tests
{
    /// <summary>A fake transport: records each request and answers from a callback.</summary>
    internal sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _answer;
        public readonly List<HttpRequestMessage> Requests = new List<HttpRequestMessage>();

        public FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> answer) => _answer = answer;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(_answer(request, Requests.Count));
        }

        public static HttpResponseMessage Ok(string body, string? etag = null)
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
            if (etag != null) r.Headers.TryAddWithoutValidation("ETag", etag);
            return r;
        }

        public static HttpResponseMessage Status(HttpStatusCode code) =>
            new HttpResponseMessage(code) { Content = new StringContent("") };
    }

    public class ClientTests
    {
        private const string Token = "AbCdEfGhIjKlMnOpQrStUvWxYz012345";

        private static string NewTempDir()
        {
            // Created without clean-up on purpose: the tests never delete files.
            var dir = Path.Combine(Path.GetTempPath(), "aab-dotnet-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static AiAgentBlocklistClient NewClient(FakeHandler handler, AiAgentBlocklistOptions? options = null) =>
            new AiAgentBlocklistClient(Token, options ?? new AiAgentBlocklistOptions { BaseUrl = "https://feed.test/" },
                new HttpClient(handler));

        [Theory]
        [InlineData("short")]
        [InlineData("AbCdEfGhIjKlMnOpQrStUvWxYz01234")]
        [InlineData("AbCdEfGhIjKlMnOpQrStUvWxYz0123456")]
        [InlineData("AbCdEfGhIjKlMnOpQrStUvWxYz01234-")]
        [InlineData("AbCdEfGhIjKlMnOp/rStUvWxYz012345")]
        public void RejectsMalformedTokens(string token)
        {
            var ex = Assert.Throws<TokenException>(() => new AiAgentBlocklistClient(token));
            Assert.DoesNotContain(token, ex.Message);
            Assert.IsAssignableFrom<AiAgentBlocklistException>(ex);
        }

        [Fact]
        public void TokenFromEnvironment()
        {
            var old = Environment.GetEnvironmentVariable(AiAgentBlocklistClient.TokenEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(AiAgentBlocklistClient.TokenEnvironmentVariable, " " + Token + " ");
                using var client = new AiAgentBlocklistClient();
                Assert.Equal("https://www.aiagentblocklist.com/download/" + Token + "/domains.txt", client.FileUrl("domains.txt"));

                Environment.SetEnvironmentVariable(AiAgentBlocklistClient.TokenEnvironmentVariable, null);
                Assert.Throws<TokenException>(() => new AiAgentBlocklistClient());
                Assert.Throws<TokenException>(() => new AiAgentBlocklistClient(""));
            }
            finally
            {
                Environment.SetEnvironmentVariable(AiAgentBlocklistClient.TokenEnvironmentVariable, old);
            }
        }

        [Theory]
        [InlineData("aab_full.csv")]
        [InlineData("domains.txt")]
        [InlineData("proxy.pac")]
        [InlineData("dns_rpz.zone")]
        [InlineData("chrome_edge_extension_blocklist.reg")]
        [InlineData("chrome_edge_extensions.mobileconfig")]
        [InlineData("README.txt")]
        public void AcceptsFeedFileNames(string name)
        {
            using var client = NewClient(new FakeHandler((r, n) => FakeHandler.Ok("")));
            Assert.Equal("https://feed.test/download/" + Token + "/" + name, client.FileUrl(name));
        }

        [Theory]
        [InlineData("../etc/passwd")]
        [InlineData("aab_full.exe")]
        [InlineData("_x.txt")]
        [InlineData("domains.txt/../x")]
        [InlineData("my-list.txt")]
        [InlineData("domains")]
        [InlineData("")]
        public void RejectsBadFileNames(string name)
        {
            using var client = NewClient(new FakeHandler((r, n) => FakeHandler.Ok("")));
            Assert.Throws<ArgumentException>(() => client.FileUrl(name));
            Assert.False(AiAgentBlocklistClient.IsValidFileName(name));
        }

        [Fact]
        public async Task DownloadSendsUserAgentAndReturnsBody()
        {
            var handler = new FakeHandler((r, n) => FakeHandler.Ok("example-agent.ai\n"));
            using var client = NewClient(handler);
            var text = await client.DownloadStringAsync("domains.txt");
            Assert.Equal("example-agent.ai\n", text);
            var req = handler.Requests.Single();
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Equal("https://feed.test/download/" + Token + "/domains.txt", req.RequestUri!.ToString());
            Assert.Equal("aiagentblocklist-dotnet/1.0.0 (+https://www.aiagentblocklist.com)", string.Join(" ", req.Headers.GetValues("User-Agent")));
        }

        [Fact]
        public async Task CustomUserAgent()
        {
            var handler = new FakeHandler((r, n) => FakeHandler.Ok("x"));
            using var client = NewClient(handler, new AiAgentBlocklistOptions { BaseUrl = "https://feed.test", UserAgent = "my-sync/2.0" });
            await client.DownloadAsync("counts.json");
            Assert.Equal("my-sync/2.0", string.Join(" ", handler.Requests[0].Headers.GetValues("User-Agent")));
        }

        [Fact]
        public async Task StatusCodesMapToErrors()
        {
            var codes = new Dictionary<string, HttpStatusCode>
            {
                ["domains.txt"] = HttpStatusCode.NotFound,
                ["hosts.txt"] = HttpStatusCode.Forbidden,
                ["proxy.pac"] = HttpStatusCode.InternalServerError,
                ["counts.json"] = (HttpStatusCode)429,
            };
            var handler = new FakeHandler((r, n) => FakeHandler.Status(codes[r.RequestUri!.Segments.Last()]));
            using var client = NewClient(handler);

            var nf = await Assert.ThrowsAsync<NotFoundException>(() => client.DownloadAsync("domains.txt"));
            Assert.Equal(404, nf.Status);
            Assert.DoesNotContain(Token, nf.Message);

            var ended = await Assert.ThrowsAsync<AccessEndedException>(() => client.DownloadAsync("hosts.txt"));
            Assert.Equal(403, ended.Status);

            var other = await Assert.ThrowsAsync<HttpStatusException>(() => client.DownloadAsync("proxy.pac"));
            Assert.Equal(500, other.Status);

            var limited = await Assert.ThrowsAsync<HttpStatusException>(() => client.DownloadAsync("counts.json"));
            Assert.Equal(429, limited.Status);
            Assert.IsAssignableFrom<AiAgentBlocklistException>(limited);
        }

        [Fact]
        public async Task NetworkErrorsAreWrapped()
        {
            var handler = new FakeHandler((r, n) => throw new HttpRequestException("connection refused"));
            using var client = NewClient(handler);
            var ex = await Assert.ThrowsAsync<AiAgentBlocklistException>(() => client.DownloadAsync("domains.txt"));
            Assert.Equal(0, ex.Status);
            Assert.IsType<HttpRequestException>(ex.InnerException);
        }

        [Fact]
        public void BareETagForms()
        {
            Assert.Equal("v1", AiAgentBlocklistClient.BareETag("W/\"v1-gzip\""));
            Assert.Equal("v1", AiAgentBlocklistClient.BareETag("\"v1\""));
            Assert.Equal("v1", AiAgentBlocklistClient.BareETag("w/\"v1\""));
            Assert.Equal("v1", AiAgentBlocklistClient.BareETag(" v1-gzip "));
            Assert.Equal("", AiAgentBlocklistClient.BareETag(null));
        }

        [Fact]
        public async Task DownloadToUsesETagSidecar()
        {
            var dir = NewTempDir();
            var path = Path.Combine(dir, "nested", "domains.txt");
            var handler = new FakeHandler((r, n) => n switch
            {
                1 => FakeHandler.Ok("one\n", "W/\"v1-gzip\""),
                2 => FakeHandler.Status(HttpStatusCode.NotModified),
                3 => FakeHandler.Ok("one again\n", "\"v1\""),
                _ => FakeHandler.Ok("two\n", "\"v2\""),
            });
            using var client = NewClient(handler);

            // 1: first download writes the file and the sidecar, no conditional header.
            Assert.True(await client.DownloadToAsync("domains.txt", path));
            Assert.False(handler.Requests[0].Headers.Contains("If-None-Match"));
            Assert.Equal("one\n", File.ReadAllText(path));
            Assert.Equal("W/\"v1-gzip\"", File.ReadAllText(path + ".etag"));

            // 2: 304 leaves everything as it was.
            Assert.False(await client.DownloadToAsync("domains.txt", path));
            Assert.Equal("W/\"v1-gzip\"", string.Join(",", handler.Requests[1].Headers.GetValues("If-None-Match")));
            Assert.Equal("one\n", File.ReadAllText(path));

            // 3: a 200 with the same bare ETag counts as unchanged.
            Assert.False(await client.DownloadToAsync("domains.txt", path));
            Assert.Equal("one\n", File.ReadAllText(path));

            // 4: a new ETag replaces the file and the sidecar.
            Assert.True(await client.DownloadToAsync("domains.txt", path));
            Assert.Equal("two\n", File.ReadAllText(path));
            Assert.Equal("\"v2\"", File.ReadAllText(path + ".etag"));

            // No temp files are left next to the target.
            Assert.Equal(new[] { "domains.txt", "domains.txt.etag" },
                Directory.GetFiles(Path.Combine(dir, "nested")).Select(Path.GetFileName).OrderBy(s => s, StringComparer.Ordinal));
        }

        [Fact]
        public async Task NoConditionalHeaderWhenTargetIsMissing()
        {
            var dir = NewTempDir();
            var path = Path.Combine(dir, "hosts.txt");
            File.WriteAllText(path + ".etag", "\"old\"");
            var handler = new FakeHandler((r, n) => FakeHandler.Ok("0.0.0.0 example-agent.ai\n"));
            using var client = NewClient(handler);

            Assert.True(await client.DownloadToAsync("hosts.txt", path));
            Assert.False(handler.Requests[0].Headers.Contains("If-None-Match"));
            Assert.Equal("", File.ReadAllText(path + ".etag"));

            // An empty sidecar means "no ETag known": the next request is unconditional too.
            Assert.True(await client.DownloadToAsync("hosts.txt", path));
            Assert.False(handler.Requests[1].Headers.Contains("If-None-Match"));
        }

        [Fact]
        public async Task SyncReturnsUpdateMap()
        {
            var dir = NewTempDir();
            var handler = new FakeHandler((r, n) =>
            {
                if (r.Headers.Contains("If-None-Match")) return FakeHandler.Status(HttpStatusCode.NotModified);
                return FakeHandler.Ok("body of " + r.RequestUri!.Segments.Last(), "\"" + r.RequestUri.Segments.Last() + "\"");
            });
            using var client = NewClient(handler);

            var first = await client.SyncAsync(dir, new[] { "domains.txt", "proxy.pac" });
            Assert.Equal(new[] { "domains.txt", "proxy.pac" }, first.Keys);
            Assert.True(first.Values.All(v => v));
            Assert.Equal("body of proxy.pac", File.ReadAllText(Path.Combine(dir, "proxy.pac")));

            var second = await client.SyncAsync(dir, new[] { "domains.txt", "proxy.pac" });
            Assert.True(second.Values.All(v => !v));
        }

        [Fact]
        public async Task SyncDefaultsToAllFiles()
        {
            var dir = NewTempDir();
            var handler = new FakeHandler((r, n) => FakeHandler.Ok("x", "\"t\""));
            using var client = NewClient(handler);
            var result = await client.SyncAsync(dir);
            Assert.Equal(19, result.Count);
            Assert.Equal(AiAgentBlocklistClient.AllFiles, result.Keys);
            Assert.Equal(19, handler.Requests.Count);
        }

        [Fact]
        public async Task SyncStopsOnAccessEnded()
        {
            var dir = NewTempDir();
            var handler = new FakeHandler((r, n) => FakeHandler.Status(HttpStatusCode.Forbidden));
            using var client = NewClient(handler);
            await Assert.ThrowsAsync<AccessEndedException>(() => client.SyncAsync(dir, new[] { "domains.txt", "hosts.txt" }));
            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task SyncValidatesNamesBeforeAnyRequest()
        {
            var handler = new FakeHandler((r, n) => FakeHandler.Ok("x"));
            using var client = NewClient(handler);
            await Assert.ThrowsAsync<ArgumentException>(() => client.SyncAsync(NewTempDir(), new[] { "domains.txt", "../x.txt" }));
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task LoadBlocklistOverHttp()
        {
            var json = File.ReadAllText(BlocklistTests.FixturePath);
            var handler = new FakeHandler((r, n) => FakeHandler.Ok(json, "\"j1\""));
            using var client = NewClient(handler);
            var list = await client.LoadBlocklistAsync();
            Assert.EndsWith("/aab_full.json", handler.Requests[0].RequestUri!.AbsolutePath);
            Assert.Equal(16, list.Entries.Count);
            Assert.Equal("example-agent.ai", list.CheckDomain("https://x.example-agent.ai/")?.Ident);
        }

        [Fact]
        public async Task CallerCancellationIsNotWrapped()
        {
            var handler = new FakeHandler((r, n) => FakeHandler.Ok("x"));
            using var client = NewClient(handler);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadAsync("domains.txt", cts.Token));
        }
    }
}
