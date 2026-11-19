using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiAgentBlocklist
{
    /// <summary>
    /// The parsed AI Agent Blocklist (<c>aab_full.json</c>) with lookup maps built once at load time.
    /// All checks are dictionary lookups; nothing scans the entry list per call.
    /// </summary>
    public sealed class Blocklist
    {
        private static readonly HashSet<string> ExtensionKinds = new HashSet<string>(StringComparer.Ordinal)
        {
            "chrome_ext", "edge_ext", "firefox_ext", "vscode_ext", "openvsx_ext", "jetbrains_plugin",
        };

        private static readonly HashSet<string> ProcessKinds = new HashSet<string>(StringComparer.Ordinal)
        {
            "process_cli", "process_mac", "process_win",
        };

        private static readonly Regex PypiSeparators = new Regex("[-_.]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly Dictionary<string, Entry> _domains = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<string, Entry> _extensions = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<string, Entry> _processes = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<string, Entry> _npm = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<string, Entry> _pypi = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<string, Entry> _identExact = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<string, Entry> _identLower = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly List<string> _domainList = new List<string>();
        private readonly List<string> _agentTypes;
        private readonly List<string> _kinds;

        /// <summary>Product name from the feed header ("AI Agent Blocklist").</summary>
        public string Product { get; }

        /// <summary>Feed version string (changes with every daily rebuild).</summary>
        public string Version { get; }

        /// <summary>Counts from the feed header (domains, identifiers, entries and per-kind totals).</summary>
        public IReadOnlyDictionary<string, long> Counts { get; }

        /// <summary>Column names from the feed header.</summary>
        public IReadOnlyList<string> Columns { get; }

        /// <summary>Every entry in file order.</summary>
        public IReadOnlyList<Entry> Entries { get; }

        /// <summary>Builds a blocklist from already parsed parts and indexes it.</summary>
        public Blocklist(string product, string version, IReadOnlyDictionary<string, long> counts,
            IReadOnlyList<string> columns, IReadOnlyList<Entry> entries)
        {
            Product = product ?? "";
            Version = version ?? "";
            Counts = counts ?? new Dictionary<string, long>();
            Columns = columns ?? Array.Empty<string>();
            Entries = entries ?? Array.Empty<Entry>();

            var agentTypes = new HashSet<string>(StringComparer.Ordinal);
            var kinds = new HashSet<string>(StringComparer.Ordinal);
            var seenDomains = new HashSet<string>(StringComparer.Ordinal);

            foreach (var e in Entries)
            {
                if (e.AgentType.Length > 0) agentTypes.Add(e.AgentType);
                if (e.Kind.Length > 0) kinds.Add(e.Kind);

                var ident = e.Ident.Trim();
                if (ident.Length == 0) continue;
                var lower = ident.ToLowerInvariant();

                TryAdd(_identExact, ident, e);
                TryAdd(_identLower, lower, e);

                if (e.Kind == "domain")
                {
                    var key = lower.TrimEnd('.');
                    if (key.Length > 0)
                    {
                        TryAdd(_domains, key, e);
                        if (seenDomains.Add(key)) _domainList.Add(key);
                    }
                }
                else if (ExtensionKinds.Contains(e.Kind))
                {
                    TryAdd(_extensions, lower, e);
                }
                else if (ProcessKinds.Contains(e.Kind))
                {
                    TryAdd(_processes, lower, e);
                    var bare = StripExe(lower);
                    if (bare.Length > 0 && bare != lower) TryAdd(_processes, bare, e);
                }
                else if (e.Kind == "npm")
                {
                    TryAdd(_npm, lower, e);
                }
                else if (e.Kind == "pypi")
                {
                    TryAdd(_pypi, NormalizePypi(ident), e);
                }
            }

            _agentTypes = agentTypes.OrderBy(s => s, StringComparer.Ordinal).ToList();
            _kinds = kinds.OrderBy(s => s, StringComparer.Ordinal).ToList();
        }

        /// <summary>Parses the JSON text of <c>aab_full.json</c>.</summary>
        /// <exception cref="AiAgentBlocklistException">The text is not valid feed JSON.</exception>
        public static Blocklist FromJson(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            try
            {
                using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
                return FromElement(doc.RootElement);
            }
            catch (JsonException ex)
            {
                throw new AiAgentBlocklistException(0, "aab_full.json could not be parsed: " + ex.Message, ex);
            }
        }

        /// <summary>Parses UTF-8 bytes of <c>aab_full.json</c>.</summary>
        /// <exception cref="AiAgentBlocklistException">The bytes are not valid feed JSON.</exception>
        public static Blocklist FromJson(byte[] utf8Json)
        {
            if (utf8Json == null) throw new ArgumentNullException(nameof(utf8Json));
            try
            {
                using var doc = JsonDocument.Parse(utf8Json, new JsonDocumentOptions { AllowTrailingCommas = true });
                return FromElement(doc.RootElement);
            }
            catch (JsonException ex)
            {
                throw new AiAgentBlocklistException(0, "aab_full.json could not be parsed: " + ex.Message, ex);
            }
        }

        /// <summary>Reads and parses a local copy of <c>aab_full.json</c> (offline use).</summary>
        /// <exception cref="AiAgentBlocklistException">The file cannot be read or parsed.</exception>
        public static Blocklist FromFile(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new AiAgentBlocklistException(0, "cannot read " + path + ": " + ex.Message, ex);
            }
            return FromJson(bytes);
        }

        private static Blocklist FromElement(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
                throw new AiAgentBlocklistException(0, "aab_full.json: the top level is not an object");

            string product = root.TryGetProperty("product", out var p) ? AsString(p) : "";
            string version = root.TryGetProperty("version", out var v) ? AsString(v) : "";

            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            if (root.TryGetProperty("counts", out var c) && c.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in c.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt64(out var n))
                        counts[prop.Name] = n;
                }
            }

            var columns = new List<string>();
            if (root.TryGetProperty("columns", out var cols) && cols.ValueKind == JsonValueKind.Array)
            {
                foreach (var col in cols.EnumerateArray()) columns.Add(AsString(col));
            }

            if (!root.TryGetProperty("entries", out var list) || list.ValueKind != JsonValueKind.Array)
                throw new AiAgentBlocklistException(0, "aab_full.json: no \"entries\" array");

            var entries = new List<Entry>(list.GetArrayLength());
            foreach (var row in list.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                entries.Add(new Entry
                {
                    ToolName = Field(row, "tool_name"),
                    ToolDomain = Field(row, "tool_domain"),
                    Category = Field(row, "category"),
                    Subcategory = Field(row, "subcategory"),
                    AgentType = Field(row, "agent_type"),
                    Kind = Field(row, "kind"),
                    Ident = Field(row, "ident"),
                    Name = Field(row, "name"),
                    Platform = Field(row, "platform"),
                    RiskGrade = Field(row, "risk_grade"),
                });
            }

            return new Blocklist(product, version, counts, columns, entries);
        }

        private static string Field(JsonElement row, string name) =>
            row.TryGetProperty(name, out var value) ? AsString(value) : "";

        private static string AsString(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.String => e.GetString() ?? "",
            JsonValueKind.Null => "",
            JsonValueKind.Undefined => "",
            _ => e.GetRawText(),
        };

        private static void TryAdd(Dictionary<string, Entry> map, string key, Entry e)
        {
            if (!map.ContainsKey(key)) map[key] = e;
        }

        private static string StripExe(string lower) =>
            lower.EndsWith(".exe", StringComparison.Ordinal) ? lower.Substring(0, lower.Length - 4) : lower;

        /// <summary>
        /// Reduces a URL or host to a bare lower-case host name: drops the scheme, user info,
        /// port, path, query, fragment and trailing dots. Returns an empty string for empty input.
        /// </summary>
        public static string NormalizeHost(string? input)
        {
            if (input == null) return "";
            var s = input.Trim();
            var scheme = s.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) s = s.Substring(scheme + 3);
            else if (s.StartsWith("//", StringComparison.Ordinal)) s = s.Substring(2);

            var cut = s.IndexOfAny(new[] { '/', '?', '#' });
            if (cut >= 0) s = s.Substring(0, cut);

            var at = s.LastIndexOf('@');
            if (at >= 0) s = s.Substring(at + 1);

            if (s.StartsWith("[", StringComparison.Ordinal))
            {
                var close = s.IndexOf(']');
                s = close > 0 ? s.Substring(1, close - 1) : s.Substring(1);
            }
            else
            {
                var colon = s.IndexOf(':');
                if (colon >= 0) s = s.Substring(0, colon);
            }

            return s.Trim().ToLowerInvariant().TrimEnd('.');
        }

        /// <summary>PEP 503 name normalisation: lower case, runs of '-', '_' and '.' become one '-'.</summary>
        public static string NormalizePypi(string name) =>
            PypiSeparators.Replace((name ?? "").Trim().ToLowerInvariant(), "-");

        /// <summary>
        /// Checks a host or URL. Returns the listed entry when the host or any parent domain
        /// is on the list (label-boundary suffix match), otherwise null. Only domain rows count.
        /// </summary>
        public Entry? CheckDomain(string? hostOrUrl)
        {
            var host = NormalizeHost(hostOrUrl);
            if (host.Length == 0) return null;
            var candidate = host;
            while (true)
            {
                if (_domains.TryGetValue(candidate, out var hit)) return hit;
                var dot = candidate.IndexOf('.');
                if (dot < 0 || dot == candidate.Length - 1) return null;
                candidate = candidate.Substring(dot + 1);
            }
        }

        /// <summary>
        /// Case-insensitive exact match of a browser extension or IDE plugin ID
        /// (Chrome, Edge, Firefox, VS Code, Open VSX and JetBrains).
        /// </summary>
        public Entry? CheckExtension(string? id)
        {
            if (id == null) return null;
            var key = id.Trim().ToLowerInvariant();
            return key.Length > 0 && _extensions.TryGetValue(key, out var hit) ? hit : null;
        }

        /// <summary>
        /// Case-insensitive match of a process or command name. A full path is reduced to its
        /// file name, and "name.exe" and "name" match each other.
        /// </summary>
        public Entry? CheckProcess(string? name)
        {
            if (name == null) return null;
            var s = name.Trim();
            var slash = s.LastIndexOfAny(new[] { '/', '\\' });
            if (slash >= 0) s = s.Substring(slash + 1);
            var key = s.Trim().ToLowerInvariant();
            if (key.Length == 0) return null;
            if (_processes.TryGetValue(key, out var hit)) return hit;
            var bare = StripExe(key);
            if (bare != key && bare.Length > 0 && _processes.TryGetValue(bare, out hit)) return hit;
            return null;
        }

        /// <summary>
        /// Checks a package name. <paramref name="ecosystem"/> is "npm" (exact, lower case) or
        /// "pypi" (PEP 503 normalisation).
        /// </summary>
        /// <exception cref="ArgumentException">The ecosystem is neither npm nor pypi.</exception>
        public Entry? CheckPackage(string ecosystem, string? name)
        {
            var eco = (ecosystem ?? "").Trim().ToLowerInvariant();
            if (eco != "npm" && eco != "pypi")
                throw new ArgumentException("ecosystem must be \"npm\" or \"pypi\"", nameof(ecosystem));
            if (name == null) return null;
            var trimmed = name.Trim();
            if (trimmed.Length == 0) return null;
            if (eco == "npm") return _npm.TryGetValue(trimmed.ToLowerInvariant(), out var n) ? n : null;
            return _pypi.TryGetValue(NormalizePypi(trimmed), out var p) ? p : null;
        }

        /// <summary>
        /// Checks any identifier: first as a domain (suffix match), then as an exact identifier,
        /// then as a case-insensitive identifier.
        /// </summary>
        public Entry? Check(string? identifier)
        {
            if (identifier == null) return null;
            var domainHit = CheckDomain(identifier);
            if (domainHit != null) return domainHit;
            var s = identifier.Trim();
            if (s.Length == 0) return null;
            if (_identExact.TryGetValue(s, out var exact)) return exact;
            return _identLower.TryGetValue(s.ToLowerInvariant(), out var lower) ? lower : null;
        }

        /// <summary>
        /// Returns the entries that match every given value (case-insensitive); null or empty means any.
        /// </summary>
        public IReadOnlyList<Entry> Filter(string? agentType = null, string? kind = null, string? riskGrade = null)
        {
            var result = new List<Entry>();
            foreach (var e in Entries)
            {
                if (!Matches(agentType, e.AgentType)) continue;
                if (!Matches(kind, e.Kind)) continue;
                if (!Matches(riskGrade, e.RiskGrade)) continue;
                result.Add(e);
            }
            return result;
        }

        private static bool Matches(string? wanted, string actual) =>
            string.IsNullOrEmpty(wanted) || string.Equals(wanted.Trim(), actual, StringComparison.OrdinalIgnoreCase);

        /// <summary>Distinct listed domains (lower case) in file order.</summary>
        public IReadOnlyList<string> Domains() => _domainList.AsReadOnly();

        /// <summary>Distinct agent types, sorted.</summary>
        public IReadOnlyList<string> AgentTypes() => _agentTypes.AsReadOnly();

        /// <summary>Distinct kinds, sorted.</summary>
        public IReadOnlyList<string> Kinds() => _kinds.AsReadOnly();
    }
}
