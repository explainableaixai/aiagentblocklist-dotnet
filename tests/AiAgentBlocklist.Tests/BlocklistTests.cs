using System;
using System.IO;
using System.Linq;
using AiAgentBlocklist;
using Xunit;

namespace AiAgentBlocklist.Tests
{
    /// <summary>Offline matching tests against the placeholder fixture.</summary>
    public class BlocklistTests
    {
        internal static string FixturePath => Path.Combine(AppContext.BaseDirectory, "aab_full.json");

        private static readonly Blocklist List = Blocklist.FromFile(FixturePath);

        [Fact]
        public void ReadsHeader()
        {
            Assert.Equal("AI Agent Blocklist", List.Product);
            Assert.Equal("test-1", List.Version);
            Assert.Equal(16, List.Entries.Count);
            Assert.Equal(3, List.Counts["domains"]);
            Assert.Equal(13, List.Counts["identifiers"]);
            Assert.Equal(10, List.Columns.Count);
            var first = List.Entries[0];
            Assert.Equal("Example Agent", first.ToolName);
            Assert.Equal("example-agent.ai", first.ToolDomain);
            Assert.Equal("domain", first.Kind);
            Assert.Equal("High", first.RiskGrade);
        }

        [Fact]
        public void FromJsonMatchesFromFile()
        {
            var fromText = Blocklist.FromJson(File.ReadAllText(FixturePath));
            Assert.Equal(List.Entries.Count, fromText.Entries.Count);
            Assert.Equal(List.Entries[5], fromText.Entries[5]);
        }

        [Fact]
        public void FromJsonRejectsBrokenText()
        {
            var ex = Assert.Throws<AiAgentBlocklistException>(() => Blocklist.FromJson("{\"entries\": [ "));
            Assert.Equal(0, ex.Status);
            Assert.Throws<AiAgentBlocklistException>(() => Blocklist.FromJson("{\"product\": \"x\"}"));
        }

        [Fact]
        public void CountsIgnoreNonNumbers()
        {
            var b = Blocklist.FromJson("{\"counts\":{\"domains\":2,\"note\":\"x\"},\"entries\":[]}");
            Assert.Single(b.Counts);
            Assert.Equal(2, b.Counts["domains"]);
        }

        [Theory]
        [InlineData("example-agent.ai", "example-agent.ai")]
        [InlineData("api.example-agent.ai", "example-agent.ai")]
        [InlineData("deep.api.example-agent.ai", "example-agent.ai")]
        [InlineData("sample-browser.app", "sample-browser.app")]
        [InlineData("cdn.demo-mcp-server.dev", "demo-mcp-server.dev")]
        public void DomainSuffixMatch(string host, string expected)
        {
            var hit = List.CheckDomain(host);
            Assert.NotNull(hit);
            Assert.Equal(expected, hit!.Ident);
            Assert.Equal("domain", hit.Kind);
        }

        [Theory]
        [InlineData("notexample-agent.ai")]
        [InlineData("example-agent.ai.other.test")]
        [InlineData("agent.ai")]
        [InlineData("ai")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void DomainNegatives(string? host)
        {
            Assert.Null(List.CheckDomain(host));
        }

        [Theory]
        [InlineData("HTTPS://user:pw@API.Example-Agent.AI:8443/path?q=1#frag")]
        [InlineData("http://example-agent.ai")]
        [InlineData("example-agent.ai:443")]
        [InlineData("example-agent.ai.")]
        [InlineData("EXAMPLE-AGENT.AI/some/path")]
        [InlineData("//example-agent.ai/x")]
        [InlineData("  wss://chat.example-agent.ai/socket  ")]
        [InlineData("example-agent.ai?x=1")]
        [InlineData("example-agent.ai#top")]
        public void UrlNormalisation(string input)
        {
            Assert.Equal("example-agent.ai", List.CheckDomain(input)?.Ident);
        }

        [Fact]
        public void NormalizeHostCases()
        {
            Assert.Equal("api.example-agent.ai", Blocklist.NormalizeHost("https://a:b@API.Example-Agent.AI.:8080/p?q#f"));
            Assert.Equal("::1", Blocklist.NormalizeHost("http://[::1]:8080/"));
            Assert.Equal("", Blocklist.NormalizeHost(null));
        }

        [Fact]
        public void NonDomainKindsDoNotCountForDomains()
        {
            Assert.Null(List.CheckDomain("com.example.agentbrowser"));
            Assert.Null(List.CheckDomain("demo-agent"));
            Assert.DoesNotContain("com.example.agentbrowser", List.Domains());
        }

        [Theory]
        [InlineData("abcdefghijklmnopabcdefghijklmnop", "chrome_ext")]
        [InlineData("ABCDEFGHIJKLMNOPABCDEFGHIJKLMNOP", "chrome_ext")]
        [InlineData("ponmlkjihgfedcbaponmlkjihgfedcba", "edge_ext")]
        [InlineData("Agent@Example-Agent.AI", "firefox_ext")]
        [InlineData("exampleagent.agent-helper", "vscode_ext")]
        [InlineData(" com.example.agent.jetbrains ", "jetbrains_plugin")]
        public void ExtensionMatch(string id, string kind)
        {
            var hit = List.CheckExtension(id);
            Assert.NotNull(hit);
            Assert.Equal(kind, hit!.Kind);
        }

        [Fact]
        public void ExtensionNegatives()
        {
            Assert.Null(List.CheckExtension("pppppppppppppppppppppppppppppppp"));
            Assert.Null(List.CheckExtension("example-agent.ai"));
            Assert.Null(List.CheckExtension("com.example.agentbrowser"));
            Assert.Null(List.CheckExtension(null));
        }

        [Theory]
        [InlineData("SampleAgent.exe", "SampleAgent.exe")]
        [InlineData("sampleagent", "SampleAgent.exe")]
        [InlineData("SAMPLEAGENT.EXE", "SampleAgent.exe")]
        [InlineData(@"C:\Program Files\Example\SampleAgent.exe", "SampleAgent.exe")]
        [InlineData("demo-agent", "demo-agent")]
        [InlineData("DEMO-AGENT.EXE", "demo-agent")]
        [InlineData("/usr/local/bin/demo-agent", "demo-agent")]
        [InlineData("sample browser", "Sample Browser")]
        public void ProcessMatch(string name, string ident)
        {
            Assert.Equal(ident, List.CheckProcess(name)?.Ident);
        }

        [Fact]
        public void ProcessNegatives()
        {
            Assert.Null(List.CheckProcess("sampleagent2.exe"));
            Assert.Null(List.CheckProcess(".exe"));
            Assert.Null(List.CheckProcess(""));
            Assert.Null(List.CheckProcess("demo-mcp-server"));
        }

        [Fact]
        public void NpmPackages()
        {
            Assert.Equal("demo-mcp-server", List.CheckPackage("npm", "demo-mcp-server")?.Ident);
            Assert.Equal("demo-mcp-server", List.CheckPackage("NPM", "Demo-MCP-Server")?.Ident);
            Assert.Equal("@example/agent-cli", List.CheckPackage("npm", "@Example/Agent-CLI")?.Ident);
            Assert.Null(List.CheckPackage("npm", "demo_mcp_server"));
        }

        [Theory]
        [InlineData("demo-mcp-server")]
        [InlineData("Demo_MCP.Server")]
        [InlineData("DEMO__mcp..server")]
        [InlineData("demo.mcp_server")]
        public void PypiNormalisation(string name)
        {
            var hit = List.CheckPackage("pypi", name);
            Assert.NotNull(hit);
            Assert.Equal("pypi", hit!.Kind);
        }

        [Fact]
        public void PypiHelperAndEcosystems()
        {
            Assert.Equal("demo-mcp-server", Blocklist.NormalizePypi("DEMO__mcp..server"));
            Assert.Null(List.CheckPackage("pypi", "@example/agent-cli"));
            Assert.Throws<ArgumentException>(() => List.CheckPackage("gem", "demo"));
        }

        [Fact]
        public void CheckTriesDomainThenIdent()
        {
            Assert.Equal("domain", List.Check("https://api.sample-browser.app/login")?.Kind);
            Assert.Equal("mac_bundle_id", List.Check("com.example.agentbrowser")?.Kind);
            Assert.Equal("mac_bundle_id", List.Check("COM.EXAMPLE.AGENTBROWSER")?.Kind);
            Assert.Equal("winget", List.Check("ExampleCorp.AgentDesktop")?.Kind);
            Assert.Equal("domain", List.Check("agent@example-agent.ai")?.Kind);
            Assert.Null(List.Check("nothing-listed.test"));
            Assert.Null(List.Check(null));
        }

        [Fact]
        public void FilterByFields()
        {
            Assert.Equal(3, List.Filter(kind: "domain").Count);
            Assert.Equal(3, List.Filter(agentType: "mcp server").Count);
            Assert.Equal(3, List.Filter(riskGrade: "High").Count);
            Assert.Single(List.Filter(agentType: "AI browser", kind: "domain"));
            Assert.Single(List.Filter(riskGrade: "Not graded"));
            Assert.Equal(16, List.Filter().Count);
            Assert.Empty(List.Filter(kind: "brew_cask"));
            var ordered = List.Filter(kind: "npm");
            Assert.Equal(new[] { "demo-mcp-server", "@example/agent-cli" }, ordered.Select(e => e.Ident));
        }

        [Fact]
        public void DistinctValues()
        {
            Assert.Equal(new[] { "example-agent.ai", "sample-browser.app", "demo-mcp-server.dev" }, List.Domains());
            Assert.Equal(new[] { "AI browser", "Autonomous agent", "Browser agent", "Coding agent", "MCP server", "Personal AI assistant" },
                List.AgentTypes());
            Assert.Equal(13, List.Kinds().Count);
            Assert.Equal("chrome_ext", List.Kinds()[0]);
            Assert.Contains("process_win", List.Kinds());
        }

        [Fact]
        public void FirstEntryWinsForDuplicates()
        {
            var json = "{\"entries\":[" +
                       "{\"kind\":\"domain\",\"ident\":\"dup.example\",\"tool_name\":\"First\"}," +
                       "{\"kind\":\"domain\",\"ident\":\"DUP.example.\",\"tool_name\":\"Second\"}]}";
            var b = Blocklist.FromJson(json);
            Assert.Equal("First", b.CheckDomain("x.dup.example")?.ToolName);
            Assert.Single(b.Domains());
        }
    }
}
