using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nabu.Mcp.AspNetCore.Discovery;
using Nabu.Mcp.AspNetCore.OData.Discovery;
using Xunit;

namespace Nabu.Mcp.AspNetCore.OData.Tests.Integration
{
    /// <summary>What the OData sample advertises over <c>tools/list</c>.</summary>
    public class ODataToolCatalogueTests : IClassFixture<ODataApiTestFixture>
    {
        private readonly ODataApiTestFixture _fixture;

        public ODataToolCatalogueTests(ODataApiTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task Advertises_every_annotated_odata_action()
        {
            var token = await _fixture.GetTokenAsync("root");
            var names = await _fixture.ListToolNamesAsync(token);

            Assert.Contains("products_query", names);
            Assert.Contains("products_get", names);
            Assert.Contains("products_create", names);
            Assert.Contains("products_update", names);
            Assert.Contains("products_delete", names);
            Assert.Contains("products_rate", names);
            Assert.Contains("products_most_expensive", names);
        }

        [Fact]
        public async Task Never_advertises_the_metadata_or_service_document_endpoints()
        {
            var token = await _fixture.GetTokenAsync("root");
            var names = await _fixture.ListToolNamesAsync(token);

            Assert.DoesNotContain(names, n => n.Contains("metadata"));
            Assert.DoesNotContain(names, n => n.Contains("service_document"));
        }

        [Fact]
        public async Task Queryable_action_advertises_odata_query_options()
        {
            var tool = await FindToolAsync("products_query");

            var properties = tool["inputSchema"]!["properties"]!.AsObject();
            Assert.True(properties.ContainsKey("filter"));
            Assert.True(properties.ContainsKey("select"));
            Assert.True(properties.ContainsKey("orderby"));
            Assert.True(properties.ContainsKey("expand"));
            Assert.True(properties.ContainsKey("top"));
            Assert.True(properties.ContainsKey("skip"));
            Assert.True(properties.ContainsKey("count"));

            // Search, apply and compute need extra server-side wiring, so they are not advertised
            // by default.
            Assert.False(properties.ContainsKey("search"));
            Assert.False(properties.ContainsKey("apply"));

            Assert.Equal("integer", properties["top"]!["type"]!.GetValue<string>());
            Assert.Equal("boolean", properties["count"]!["type"]!.GetValue<string>());

            // Every query option is optional.
            var required = tool["inputSchema"]!["required"];
            Assert.True(required == null || required.AsArray().Count == 0);
        }

        [Fact]
        public async Task Non_queryable_action_advertises_no_query_options()
        {
            var tool = await FindToolAsync("products_delete");
            var properties = tool["inputSchema"]!["properties"]!.AsObject();

            Assert.True(properties.ContainsKey("key"));
            Assert.False(properties.ContainsKey("filter"));
            Assert.False(properties.ContainsKey("top"));
        }

        [Fact]
        public async Task Keyed_get_requires_the_key_and_keeps_query_options()
        {
            var tool = await FindToolAsync("products_get");
            var properties = tool["inputSchema"]!["properties"]!.AsObject();

            Assert.True(properties.ContainsKey("key"));
            Assert.Equal("integer", properties["key"]!["type"]!.GetValue<string>());

            // [EnableQuery] on the single-entity read still allows $select/$expand.
            Assert.True(properties.ContainsKey("select"));

            var required = tool["inputSchema"]!["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
            Assert.Contains("key", required);
        }

        [Fact]
        public async Task Entity_body_is_flattened_into_top_level_inputs()
        {
            var tool = await FindToolAsync("products_create");
            var properties = tool["inputSchema"]!["properties"]!.AsObject();

            Assert.True(properties.ContainsKey("name"));
            Assert.True(properties.ContainsKey("category"));
            Assert.True(properties.ContainsKey("price"));

            var required = tool["inputSchema"]!["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
            Assert.Contains("name", required);
        }

        [Fact]
        public async Task Delta_body_advertises_every_property_as_optional()
        {
            var tool = await FindToolAsync("products_update");
            var properties = tool["inputSchema"]!["properties"]!.AsObject();

            Assert.True(properties.ContainsKey("name"));
            Assert.True(properties.ContainsKey("price"));

            var required = tool["inputSchema"]!["required"];
            var names = required?.AsArray().Select(n => n!.GetValue<string>()).ToList();
            Assert.True(names == null || names.All(n => n == "key"));
        }

        [Fact]
        public void Descriptors_prefer_key_as_segment_templates_and_dispatch_over_http()
        {
            var source = _fixture.Services.GetServices<IMcpToolSource>().OfType<ODataToolSource>().Single();
            var tools = source.GetTools();

            var get = tools.Single(t => t.Name == "products_get");
            Assert.Equal("odata/Products/{key}", get.RouteTemplate);
            Assert.Equal("GET", get.HttpMethod);
            Assert.Null(get.InvokerType); // standard HTTP pipeline replay

            var rate = tools.Single(t => t.Name == "products_rate");
            Assert.Equal("odata/Products/{key}/Rate", rate.RouteTemplate);
            Assert.Equal("POST", rate.HttpMethod);

            var mostExpensive = tools.Single(t => t.Name == "products_most_expensive");
            Assert.Equal("odata/Products/MostExpensive()", mostExpensive.RouteTemplate);
        }

        [Fact]
        public void Descriptors_carry_the_actions_authorization_and_http_annotations()
        {
            var source = _fixture.Services.GetServices<IMcpToolSource>().OfType<ODataToolSource>().Single();
            var tools = source.GetTools();

            var query = tools.Single(t => t.Name == "products_query");
            Assert.False(query.Authorization.RequiresAuthorization);
            Assert.True(query.Annotations.ReadOnly);

            var delete = tools.Single(t => t.Name == "products_delete");
            Assert.True(delete.Authorization.RequiresAuthorization);
            Assert.True(delete.Annotations.Destructive);

            var create = tools.Single(t => t.Name == "products_create");
            Assert.True(create.Authorization.RequiresAuthorization);
            Assert.False(create.Annotations.ReadOnly);
        }

        private async Task<JsonObject> FindToolAsync(string name)
        {
            var token = await _fixture.GetTokenAsync("root");
            var tools = await _fixture.ListToolsAsync(token);
            var tool = tools.FirstOrDefault(t => t!["name"]!.GetValue<string>() == name);
            Assert.True(tool != null, "Tool '" + name + "' was not advertised.");
            return tool!.AsObject();
        }
    }
}
