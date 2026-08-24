using Microsoft.AspNetCore.OData.Query;

namespace Nabu.Mcp.AspNetCore.OData
{
    /// <summary>Options for publishing OData endpoints as MCP tools.</summary>
    public class NabuMcpODataOptions
    {
        /// <summary>
        /// Publish every OData-routed controller action, not just the ones annotated with
        /// <c>[McpTool]</c>. <c>[McpIgnore]</c> still wins, and the built-in metadata and service
        /// document endpoints are never published. Defaults to <c>false</c> - publishing an
        /// endpoint hands every MCP caller the ability to invoke it, so opt in deliberately,
        /// exactly as with controllers and hubs.
        /// </summary>
        public bool ExposeAllODataActions { get; set; }

        /// <summary>
        /// Which OData query options are advertised as tool inputs on queryable GET actions - the
        /// ones marked <c>[EnableQuery]</c> or taking an <c>ODataQueryOptions</c> parameter. The
        /// advertised set is further narrowed by each action's own
        /// <see cref="EnableQueryAttribute.AllowedQueryOptions"/>, so a tool never advertises an
        /// option the action would refuse. This shapes what is <em>advertised</em>, never what is
        /// allowed: the action's own <c>[EnableQuery]</c> validation still runs on every call.
        /// </summary>
        /// <remarks>
        /// <see cref="AllowedQueryOptions.Search"/>, <see cref="AllowedQueryOptions.Apply"/> and
        /// <see cref="AllowedQueryOptions.Compute"/> are not advertised by default because they
        /// need extra server-side wiring (a search binder, aggregation support) that most
        /// applications do not configure.
        /// </remarks>
        public AllowedQueryOptions QueryOptions { get; set; } =
            AllowedQueryOptions.Filter |
            AllowedQueryOptions.Select |
            AllowedQueryOptions.OrderBy |
            AllowedQueryOptions.Expand |
            AllowedQueryOptions.Top |
            AllowedQueryOptions.Skip |
            AllowedQueryOptions.Count;
    }
}
