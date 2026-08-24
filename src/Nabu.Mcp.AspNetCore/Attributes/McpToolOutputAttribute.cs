using System;

namespace Nabu.Mcp.AspNetCore
{
    /// <summary>
    /// Controls what a tool returns to the model. The underlying action keeps producing its full
    /// response - this attribute shapes the JSON body <em>after</em> the pipeline has run and before
    /// it becomes tool content, so fields can be hidden from MCP clients without touching the API
    /// contract that HTTP callers see.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Field selection uses dot-separated paths matched case-insensitively against JSON property
    /// names (<c>"items.owner"</c>). Arrays are traversed transparently: a segment that lands on an
    /// array applies to every element. <see cref="IncludeFields"/> keeps only the listed paths,
    /// <see cref="ExcludeFields"/> removes the listed paths, and when both are set the include list
    /// is applied first. <see cref="Converter"/> then receives the (already filtered) JSON and may
    /// reduce, transform or replace it entirely.
    /// </para>
    /// <para>
    /// Shaping is deliberately fail-closed: when a successful response cannot be shaped - the body
    /// is not valid JSON, or the converter throws - the tool answers an error instead of exposing
    /// the raw response, so a filter meant to hide data never leaks it by failing.
    /// </para>
    /// <para>
    /// The attribute may be applied several times. An occurrence with <see cref="Tool"/> set applies
    /// only to the tool of that name, which is how each variant of a multi-<see cref="McpToolAttribute"/>
    /// action gets its own output shape; an occurrence without <see cref="Tool"/> applies to every
    /// tool the action publishes. A method-level occurrence takes precedence over a controller-level
    /// one, and within one level a <see cref="Tool"/>-scoped occurrence wins over an unscoped one.
    /// </para>
    /// <para>
    /// Error responses (as decided by <see cref="NabuMcpOptions.TreatErrorStatusAsToolError"/>) and
    /// empty bodies are not shaped: filters describe the success payload, not the failure text.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// [HttpGet]
    /// [McpTool("todos_list")]
    /// [McpTool("todos_list_compact")]
    /// [McpToolOutput(Tool = "todos_list_compact",
    ///     IncludeFields = new[] { "items.id", "items.title", "totalCount" })]
    /// [McpToolOutput(Tool = "todos_list",
    ///     ExcludeFields = new[] { "items.owner" })]
    /// public ActionResult&lt;TodoPage&gt; List(...)
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
    public sealed class McpToolOutputAttribute : Attribute
    {
        public McpToolOutputAttribute()
        {
        }

        /// <param name="converter">
        /// A type implementing <see cref="Execution.IMcpToolOutputConverter"/> - see <see cref="Converter"/>.
        /// </param>
        public McpToolOutputAttribute(Type converter)
        {
            Converter = converter;
        }

        /// <summary>
        /// Name of the tool this occurrence applies to. <c>null</c> - the default - applies it to
        /// every tool published by the action. Matches the advertised tool name or the name declared
        /// on the corresponding <see cref="McpToolAttribute"/>, case-insensitively.
        /// </summary>
        public string? Tool { get; set; }

        /// <summary>
        /// Restricts the response to this set of fields; every property not on a listed path is
        /// removed. Paths are dot-separated (<c>"items.id"</c>) and traverse arrays transparently.
        /// <c>null</c> or empty means "keep every field", which is the default.
        /// </summary>
        public string[]? IncludeFields { get; set; }

        /// <summary>
        /// Removes these fields from the response. Applied after <see cref="IncludeFields"/>, so a
        /// field can be carved out of an included subtree.
        /// </summary>
        public string[]? ExcludeFields { get; set; }

        /// <summary>
        /// A type implementing <see cref="Execution.IMcpToolOutputConverter"/> that transforms the
        /// response after the field filters have run. Resolved from the request's services when
        /// registered, otherwise constructed with <c>ActivatorUtilities</c>, so it may take
        /// dependencies. It receives the parsed JSON and returns the JSON to expose - or <c>null</c>
        /// to expose nothing.
        /// </summary>
        public Type? Converter { get; set; }
    }
}
