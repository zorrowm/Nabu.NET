#if !NETSTANDARD2_0
using System;
using Microsoft.AspNetCore.Builder;

namespace Nabu.Mcp.AspNetCore
{
    /// <summary>
    /// Publishes Minimal API route handlers as MCP tools:
    /// <c>app.MapGet("/customers/{id}", ...).McpTool()</c>.
    /// </summary>
    /// <remarks>
    /// The endpoint keeps behaving like a normal route handler. As with controller actions, Nabu never
    /// calls the handler directly; a tool call is replayed as a synthetic HTTP request through the
    /// application pipeline, so authentication, authorization (<c>RequireAuthorization()</c>,
    /// <c>[Authorize]</c>), endpoint filters and validation all still run.
    /// </remarks>
    public static class NabuMcpEndpointConventionBuilderExtensions
    {
        /// <summary>Publishes the endpoint as an MCP tool with a generated name.</summary>
        public static TBuilder McpTool<TBuilder>(this TBuilder builder)
            where TBuilder : IEndpointConventionBuilder
        {
            return builder.McpTool(new McpToolAttribute());
        }

        /// <summary>Publishes the endpoint as an MCP tool.</summary>
        /// <param name="builder">The endpoint convention builder.</param>
        /// <param name="name">Tool name advertised to MCP clients.</param>
        /// <param name="description">Description shown to the model.</param>
        public static TBuilder McpTool<TBuilder>(this TBuilder builder, string name, string? description = null)
            where TBuilder : IEndpointConventionBuilder
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("The tool name must not be empty.", nameof(name));
            }

            return builder.McpTool(new McpToolAttribute(name) { Description = description });
        }

        /// <summary>
        /// Publishes the endpoint as an MCP tool configured through a delegate, giving access to the
        /// full <see cref="McpToolAttribute"/> surface - parameter sets, constants, behaviour hints.
        /// May be called several times to publish one endpoint as several tools.
        /// </summary>
        /// <example>
        /// <code>
        /// app.MapGet("/todos", (bool? isCompleted, int page) => ...)
        ///    .McpTool(tool =>
        ///    {
        ///        tool.Name = "todos_list_open";
        ///        tool.ConstantParameters = new[] { "isCompleted=false" };
        ///    });
        /// </code>
        /// </example>
        public static TBuilder McpTool<TBuilder>(this TBuilder builder, Action<McpToolAttribute> configure)
            where TBuilder : IEndpointConventionBuilder
        {
            if (configure == null)
            {
                throw new ArgumentNullException(nameof(configure));
            }

            var tool = new McpToolAttribute();
            configure(tool);
            return builder.McpTool(tool);
        }

        /// <summary>Publishes the endpoint as an MCP tool described by <paramref name="tool"/>.</summary>
        public static TBuilder McpTool<TBuilder>(this TBuilder builder, McpToolAttribute tool)
            where TBuilder : IEndpointConventionBuilder
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            if (tool == null)
            {
                throw new ArgumentNullException(nameof(tool));
            }

            builder.Add(endpoint => endpoint.Metadata.Add(tool));
            return builder;
        }

        /// <summary>
        /// Shapes what the endpoint's tools return to the model, giving access to the full
        /// <see cref="McpToolOutputAttribute"/> surface - field include/exclude paths and an output
        /// converter. Scope an occurrence to one tool of a multi-<c>McpTool</c> endpoint with
        /// <see cref="McpToolOutputAttribute.Tool"/>.
        /// </summary>
        /// <example>
        /// <code>
        /// app.MapGet("/customers/{id}", (int id) => ...)
        ///    .McpTool("customers_get")
        ///    .McpToolOutput(output => output.ExcludeFields = new[] { "ssn", "addresses.street" });
        /// </code>
        /// </example>
        public static TBuilder McpToolOutput<TBuilder>(this TBuilder builder, Action<McpToolOutputAttribute> configure)
            where TBuilder : IEndpointConventionBuilder
        {
            if (configure == null)
            {
                throw new ArgumentNullException(nameof(configure));
            }

            var output = new McpToolOutputAttribute();
            configure(output);
            return builder.McpToolOutput(output);
        }

        /// <summary>Shapes what the endpoint's tools return, as described by <paramref name="output"/>.</summary>
        public static TBuilder McpToolOutput<TBuilder>(this TBuilder builder, McpToolOutputAttribute output)
            where TBuilder : IEndpointConventionBuilder
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            if (output == null)
            {
                throw new ArgumentNullException(nameof(output));
            }

            builder.Add(endpoint => endpoint.Metadata.Add(output));
            return builder;
        }

        /// <summary>
        /// Keeps the endpoint out of MCP discovery. Relevant with
        /// <see cref="NabuMcpOptions.ExposeAllActions"/>, which otherwise publishes every route handler.
        /// </summary>
        public static TBuilder McpIgnore<TBuilder>(this TBuilder builder)
            where TBuilder : IEndpointConventionBuilder
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            builder.Add(endpoint => endpoint.Metadata.Add(new McpIgnoreAttribute()));
            return builder;
        }
    }
}
#endif
