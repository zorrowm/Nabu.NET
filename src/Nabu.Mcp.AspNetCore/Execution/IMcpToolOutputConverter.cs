using System;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Nabu.Mcp.AspNetCore.Discovery;

namespace Nabu.Mcp.AspNetCore.Execution
{
    /// <summary>
    /// Transforms the JSON a tool is about to return, named by
    /// <see cref="McpToolOutputAttribute.Converter"/>. Runs after the attribute's field filters, so
    /// it receives the already-narrowed payload. Implementations are resolved from the request's
    /// services when registered - so they may take dependencies - and constructed with
    /// <c>ActivatorUtilities</c> otherwise.
    /// </summary>
    /// <remarks>
    /// A converter only ever runs on successful, non-empty responses. Shaping is fail-closed: an
    /// exception thrown here turns the tool result into an error rather than exposing the
    /// untransformed response, so a converter meant to hide data never leaks it by failing.
    /// </remarks>
    public interface IMcpToolOutputConverter
    {
        /// <summary>
        /// Returns the JSON to expose to the MCP client in place of <paramref name="output"/>.
        /// The node may be mutated and returned, or replaced entirely; returning <c>null</c>
        /// exposes an empty result.
        /// </summary>
        /// <param name="context">The tool, the raw HTTP outcome, and the current request services.</param>
        /// <param name="output">
        /// The parsed response body after <see cref="McpToolOutputAttribute.IncludeFields"/> and
        /// <see cref="McpToolOutputAttribute.ExcludeFields"/> were applied. <c>null</c> when the
        /// body was the JSON literal <c>null</c>.
        /// </param>
        JsonNode? Convert(McpToolOutputContext context, JsonNode? output);
    }

    /// <summary>What an <see cref="IMcpToolOutputConverter"/> gets to look at while transforming.</summary>
    public sealed class McpToolOutputContext
    {
        public McpToolOutputContext(McpToolDescriptor tool, McpToolInvocationResult result, HttpContext httpContext)
        {
            Tool = tool ?? throw new ArgumentNullException(nameof(tool));
            Result = result ?? throw new ArgumentNullException(nameof(result));
            HttpContext = httpContext ?? throw new ArgumentNullException(nameof(httpContext));
        }

        /// <summary>The tool being answered.</summary>
        public McpToolDescriptor Tool { get; }

        /// <summary>The raw HTTP outcome of the replayed request - status code, headers, body.</summary>
        public McpToolInvocationResult Result { get; }

        /// <summary>The MCP request's <see cref="Microsoft.AspNetCore.Http.HttpContext"/>.</summary>
        public HttpContext HttpContext { get; }

        /// <summary>The current request's service provider.</summary>
        public IServiceProvider Services
        {
            get { return HttpContext.RequestServices; }
        }
    }
}
