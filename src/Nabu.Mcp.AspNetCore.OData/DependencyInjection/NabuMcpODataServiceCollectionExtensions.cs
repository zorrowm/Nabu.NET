using System;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nabu.Mcp.AspNetCore.Discovery;
using Nabu.Mcp.AspNetCore.OData.Discovery;
using Nabu.Mcp.AspNetCore.Schema;

namespace Nabu.Mcp.AspNetCore.OData
{
    /// <summary>Registration helpers for publishing OData endpoints as MCP tools.</summary>
    public static class NabuMcpODataServiceCollectionExtensions
    {
        /// <summary>
        /// Publishes the application's <c>[McpTool]</c>-annotated OData actions as MCP tools,
        /// merged into the same catalogue as the HTTP tools. Call it alongside
        /// <c>AddNabuMcp()</c> and <c>AddOData()</c>:
        /// <code>
        /// builder.Services.AddControllers().AddOData(...);
        /// builder.Services.AddNabuMcp(options => { ... })
        ///                 .AddNabuMcpOData();
        /// </code>
        /// Tool calls are replayed through the application's own HTTP pipeline, so authentication,
        /// authorization, <c>[EnableQuery]</c> and the OData formatters keep working exactly as
        /// they do for a client-issued request.
        /// </summary>
        public static IServiceCollection AddNabuMcpOData(this IServiceCollection services, Action<NabuMcpODataOptions>? configure = null)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            services.AddOptions();

            if (configure != null)
            {
                services.Configure(configure);
            }

            services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpToolSource, ODataToolSource>(sp => new ODataToolSource(
                sp.GetService<IActionDescriptorCollectionProvider>(),
                sp.GetRequiredService<IOptions<NabuMcpOptions>>(),
                sp.GetRequiredService<IOptions<NabuMcpODataOptions>>(),
                sp.GetRequiredService<JsonSchemaGenerator>(),
                sp.GetRequiredService<IXmlDocumentationProvider>(),
                sp.GetService<ILogger<ODataToolSource>>())));

            return services;
        }
    }
}
