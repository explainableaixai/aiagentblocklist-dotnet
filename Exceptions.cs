using System;

namespace AiAgentBlocklist
{
    /// <summary>
    /// Base type for every error raised by this library. <see cref="Status"/> holds the HTTP
    /// status code, or 0 when no HTTP answer was received (network, IO or parse errors).
    /// </summary>
    public class AiAgentBlocklistException : Exception
    {
        /// <summary>HTTP status code of the failed request, or 0 when there was none.</summary>
        public int Status { get; }

        /// <summary>Creates an error with a status code and a message.</summary>
        public AiAgentBlocklistException(int status, string message)
            : base(message)
        {
            Status = status;
        }

        /// <summary>Creates an error that wraps a lower-level cause (network, IO, JSON).</summary>
        public AiAgentBlocklistException(int status, string message, Exception? innerException)
            : base(message, innerException)
        {
            Status = status;
        }
    }

    /// <summary>HTTP 404: the download token or the file name is unknown.</summary>
    public sealed class NotFoundException : AiAgentBlocklistException
    {
        /// <summary>Creates the error.</summary>
        public NotFoundException(string message) : base(404, message) { }
    }

    /// <summary>HTTP 403: the subscription behind the token has ended or is paused.</summary>
    public sealed class AccessEndedException : AiAgentBlocklistException
    {
        /// <summary>Creates the error.</summary>
        public AccessEndedException(string message) : base(403, message) { }
    }

    /// <summary>Any other non-success HTTP status (for example 500 or 429).</summary>
    public sealed class HttpStatusException : AiAgentBlocklistException
    {
        /// <summary>Creates the error.</summary>
        public HttpStatusException(int status, string message) : base(status, message) { }
    }

    /// <summary>The download token is missing or does not have the 32 letters and digits format.</summary>
    public sealed class TokenException : AiAgentBlocklistException
    {
        /// <summary>Creates the error.</summary>
        public TokenException(string message) : base(0, message) { }
    }
}
