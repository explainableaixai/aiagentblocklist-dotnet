using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AiAgentBlocklist
{
    /// <summary>Settings for <see cref="AiAgentBlocklistClient"/>.</summary>
    public sealed class AiAgentBlocklistOptions
    {
        /// <summary>Default site address.</summary>
        public const string DefaultBaseUrl = "https://www.aiagentblocklist.com";

        /// <summary>Default User-Agent header.</summary>
        public const string DefaultUserAgent = "aiagentblocklist-dotnet/1.0.0 (+https://www.aiagentblocklist.com)";

        /// <summary>Site address; the default is https://www.aiagentblocklist.com.</summary>
        public string BaseUrl { get; set; } = DefaultBaseUrl;

        /// <summary>Time limit for one request; the default is 30 seconds.</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>User-Agent header sent with every request.</summary>
        public string UserAgent { get; set; } = DefaultUserAgent;
    }

    /// <summary>
    /// Feed client for the AI Agent Blocklist. It downloads the subscriber files behind a private
    /// 32-character token, keeps local copies fresh with conditional GET requests and parses
    /// <c>aab_full.json</c> into a <see cref="Blocklist"/> for local matching.
    /// </summary>
    public sealed class AiAgentBlocklistClient : IDisposable
    {
        /// <summary>Environment variable read when no token is passed.</summary>
        public const string TokenEnvironmentVariable = "AIAGENTBLOCKLIST_TOKEN";

        /// <summary>Every file in the subscriber download folder, in the documented order.</summary>
        public static readonly IReadOnlyList<string> AllFiles = new[]
        {
            "aab_full.csv",
            "aab_full.json",
            "domains.txt",
            "firewall_edl.txt",
            "hosts.txt",
            "proxy.pac",
            "dns_rpz.zone",
            "chrome_extension_blocklist.json",
            "edge_extension_blocklist.json",
            "chrome_edge_extension_blocklist.reg",
            "chrome_edge_extensions.mobileconfig",
            "firefox_extension_ids.txt",
            "macos_bundle_ids.txt",
            "windows_package_ids.txt",
            "process_names.txt",
            "npm_packages.txt",
            "pypi_packages.txt",
            "counts.json",
            "README.txt",
        };

        private static readonly Regex TokenPattern = new Regex("^[A-Za-z0-9]{32}$", RegexOptions.CultureInvariant);
        private static readonly Regex FileNamePattern = new Regex(
            "^[A-Za-z0-9][A-Za-z0-9_]*\\.(csv|json|txt|pac|zone|reg|mobileconfig)$", RegexOptions.CultureInvariant);

        private readonly HttpClient _http;
        private readonly bool _ownsHttp;
        private readonly string _token;
        private readonly string _baseUrl;
        private readonly TimeSpan _timeout;
        private readonly string _userAgent;

        /// <summary>
        /// Creates a client. When <paramref name="token"/> is null or empty the
        /// <c>AIAGENTBLOCKLIST_TOKEN</c> environment variable is used.
        /// </summary>
        /// <param name="token">The 32-character download token.</param>
        /// <param name="options">Optional base URL, timeout and User-Agent.</param>
        /// <param name="httpClient">Optional HttpClient (for dependency injection or tests). It is not disposed by this client.</param>
        /// <exception cref="TokenException">The token is missing or malformed.</exception>
        public AiAgentBlocklistClient(string? token = null, AiAgentBlocklistOptions? options = null, HttpClient? httpClient = null)
        {
            options ??= new AiAgentBlocklistOptions();
            var t = string.IsNullOrWhiteSpace(token)
                ? (Environment.GetEnvironmentVariable(TokenEnvironmentVariable) ?? "").Trim()
                : token!.Trim();
            if (t.Length == 0)
                throw new TokenException("no download token: pass one or set " + TokenEnvironmentVariable);
            if (!TokenPattern.IsMatch(t))
                throw new TokenException("the download token must be exactly 32 letters and digits (got " + t.Length + " characters)");

            _token = t;
            _baseUrl = (string.IsNullOrWhiteSpace(options.BaseUrl) ? AiAgentBlocklistOptions.DefaultBaseUrl : options.BaseUrl.Trim()).TrimEnd('/');
            _timeout = options.Timeout > TimeSpan.Zero ? options.Timeout : TimeSpan.FromSeconds(30);
            _userAgent = string.IsNullOrWhiteSpace(options.UserAgent) ? AiAgentBlocklistOptions.DefaultUserAgent : options.UserAgent;

            if (httpClient != null)
            {
                _http = httpClient;
                _ownsHttp = false;
            }
            else
            {
                _http = new HttpClient(new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                })
                {
                    Timeout = System.Threading.Timeout.InfiniteTimeSpan,
                };
                _ownsHttp = true;
            }
        }

        /// <summary>Returns true when <paramref name="name"/> is an allowed feed file name.</summary>
        public static bool IsValidFileName(string? name) => name != null && FileNamePattern.IsMatch(name);

        /// <summary>Full download URL of one file.</summary>
        /// <exception cref="ArgumentException">The file name is not allowed.</exception>
        public string FileUrl(string name)
        {
            if (!IsValidFileName(name))
                throw new ArgumentException("not a feed file name: \"" + name + "\"", nameof(name));
            return _baseUrl + "/download/" + _token + "/" + name;
        }

        /// <summary>Downloads one file and returns its bytes.</summary>
        public async Task<byte[]> DownloadAsync(string name, CancellationToken cancellationToken = default)
        {
            var url = FileUrl(name);
            using var request = NewRequest(url);
            using var cts = LinkedTimeout(cancellationToken);
            using var response = await SendAsync(request, name, cts, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response, name);
            return await ReadBodyAsync(response, name, cts.Token, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Downloads one file and returns it as UTF-8 text.</summary>
        public async Task<string> DownloadStringAsync(string name, CancellationToken cancellationToken = default)
        {
            var bytes = await DownloadAsync(name, cancellationToken).ConfigureAwait(false);
            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>
        /// Conditional download into <paramref name="path"/>. The ETag is kept in
        /// <c>path + ".etag"</c> and sent as If-None-Match next time. Returns false when the
        /// file was unchanged (304, or the same ETag) and true when a new copy was written
        /// atomically (temp file and rename).
        /// </summary>
        public async Task<bool> DownloadToAsync(string name, string path, CancellationToken cancellationToken = default)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            var url = FileUrl(name);
            var full = Path.GetFullPath(path);
            var sidecar = full + ".etag";
            var dir = Path.GetDirectoryName(full);

            string stored = "";
            try
            {
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(full) && File.Exists(sidecar)) stored = File.ReadAllText(sidecar).Trim();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new AiAgentBlocklistException(0, "cannot prepare " + full + ": " + ex.Message, ex);
            }

            using var request = NewRequest(url);
            if (stored.Length > 0) request.Headers.TryAddWithoutValidation("If-None-Match", stored);

            using var cts = LinkedTimeout(cancellationToken);
            using var response = await SendAsync(request, name, cts, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified) return false;
            EnsureSuccess(response, name);

            var newTag = ReadETag(response);
            if (stored.Length > 0 && newTag.Length > 0 && BareETag(stored) == BareETag(newTag) && File.Exists(full))
                return false;

            var body = await ReadBodyAsync(response, name, cts.Token, cancellationToken).ConfigureAwait(false);
            var temp = full + ".tmp" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await fs.WriteAsync(body, 0, body.Length, cancellationToken).ConfigureAwait(false);
                    await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
                    fs.Flush(true);
                }
                File.Move(temp, full, true);
                File.WriteAllText(sidecar, newTag);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Remove only the half-written temp file this method created; never the target.
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw new AiAgentBlocklistException(0, "cannot write " + full + ": " + ex.Message, ex);
            }
            return true;
        }

        /// <summary>
        /// Runs <see cref="DownloadToAsync"/> for each name into <paramref name="directory"/> and
        /// returns name to updated. All 19 files are synced when <paramref name="names"/> is null.
        /// Stops at the first error.
        /// </summary>
        public async Task<IReadOnlyDictionary<string, bool>> SyncAsync(string directory, IEnumerable<string>? names = null,
            CancellationToken cancellationToken = default)
        {
            if (directory == null) throw new ArgumentNullException(nameof(directory));
            var list = (names ?? AllFiles).ToList();
            foreach (var n in list) FileUrl(n);

            var result = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var n in list)
            {
                result[n] = await DownloadToAsync(n, Path.Combine(directory, n), cancellationToken).ConfigureAwait(false);
            }
            return result;
        }

        /// <summary>Downloads <c>aab_full.json</c> and parses it.</summary>
        public async Task<Blocklist> LoadBlocklistAsync(CancellationToken cancellationToken = default)
        {
            var bytes = await DownloadAsync("aab_full.json", cancellationToken).ConfigureAwait(false);
            return Blocklist.FromJson(bytes);
        }

        /// <summary>
        /// Reduces an ETag to its bare value: drops a weak "W/" prefix, the quotes and a
        /// "-gzip" suffix that some servers add to compressed answers.
        /// </summary>
        public static string BareETag(string? tag)
        {
            var s = (tag ?? "").Trim();
            if (s.StartsWith("W/", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2).Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"') s = s.Substring(1, s.Length - 2);
            if (s.EndsWith("-gzip", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 5);
            return s;
        }

        /// <summary>Disposes the internal HttpClient (a client passed in by the caller is left open).</summary>
        public void Dispose()
        {
            if (_ownsHttp) _http.Dispose();
        }

        private HttpRequestMessage NewRequest(string url)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            request.Headers.TryAddWithoutValidation("Accept", "*/*");
            return request;
        }

        private CancellationTokenSource LinkedTimeout(CancellationToken cancellationToken)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);
            return cts;
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string name,
            CancellationTokenSource cts, CancellationToken callerToken)
        {
            try
            {
                return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                throw new AiAgentBlocklistException(0, name + ": request timed out after " + _timeout.TotalSeconds + " s", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new AiAgentBlocklistException(0, name + ": network error: " + ex.Message, ex);
            }
        }

        private async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, string name, CancellationToken token,
            CancellationToken callerToken)
        {
            try
            {
#if NET5_0_OR_GREATER
                return await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
#else
                return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
#endif
            }
            catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex is HttpRequestException || ex is IOException)
            {
                throw new AiAgentBlocklistException(0, name + ": reading the body failed: " + ex.Message, ex);
            }
        }

        private static void EnsureSuccess(HttpResponseMessage response, string name)
        {
            var status = (int)response.StatusCode;
            if (status >= 200 && status < 300) return;
            switch (status)
            {
                case 404:
                    throw new NotFoundException(name + ": not found (unknown token or file name)");
                case 403:
                    throw new AccessEndedException(name + ": access ended (subscription ended or paused)");
                default:
                    throw new HttpStatusException(status, name + ": HTTP " + status + " " + response.ReasonPhrase);
            }
        }

        private static string ReadETag(HttpResponseMessage response)
        {
            if (response.Headers.TryGetValues("ETag", out var values))
            {
                var first = values.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(first)) return first.Trim();
            }
            return response.Headers.ETag?.ToString() ?? "";
        }
    }
}
