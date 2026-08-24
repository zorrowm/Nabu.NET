using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Nabu.Mcp.AspNetCore.OData.Tests.Integration
{
    /// <summary>
    /// Calls the OData-backed tools end to end: through the MCP endpoint, into the synthetic HTTP
    /// request, through routing, [EnableQuery] and the OData formatters, and back out as tool
    /// content. Read-only calls live here; mutating calls get their own classes (and so their own
    /// application instances), keeping the seed catalogue predictable.
    /// </summary>
    public class ODataToolInvocationTests : IClassFixture<ODataApiTestFixture>
    {
        private readonly ODataApiTestFixture _fixture;

        public ODataToolInvocationTests(ODataApiTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task Query_applies_filter_orderby_and_select()
        {
            var result = await _fixture.CallToolAsync("products_query", new JsonObject
            {
                ["filter"] = "Price gt 100",
                ["orderby"] = "Price desc",
                ["select"] = "Name,Price",
            });

            Assert.NotEqual(true, result["isError"]?.GetValue<bool>());

            var items = result["structuredContent"]!["value"]!.AsArray();
            Assert.Equal(2, items.Count);
            Assert.Equal("Monitor", items[0]!["Name"]!.GetValue<string>());
            Assert.Equal("Dock", items[1]!["Name"]!.GetValue<string>());

            // $select trimmed the payload to the requested properties.
            Assert.Null(items[0]!["Category"]);
        }

        [Fact]
        public async Task Query_supports_top_skip_and_count()
        {
            var result = await _fixture.CallToolAsync("products_query", new JsonObject
            {
                ["orderby"] = "Id",
                ["top"] = 1,
                ["skip"] = 1,
                ["count"] = true,
            });

            var content = result["structuredContent"]!.AsObject();
            Assert.Equal(4, content["@odata.count"]!.GetValue<int>());

            var items = content["value"]!.AsArray();
            Assert.Single(items);
            Assert.Equal("Mouse", items[0]!["Name"]!.GetValue<string>());
        }

        [Fact]
        public async Task Query_output_shaping_hides_the_internal_cost_price()
        {
            var result = await _fixture.CallToolAsync("products_query", new JsonObject());

            var items = result["structuredContent"]!["value"]!.AsArray();
            Assert.True(items.Count >= 4);
            Assert.All(items, item =>
            {
                Assert.NotNull(item!["Name"]);
                Assert.Null(item["CostPrice"]);
            });

            var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
            Assert.DoesNotContain("CostPrice", text);
        }

        [Fact]
        public async Task Keyed_get_reads_one_entity_and_stays_shaped()
        {
            var result = await _fixture.CallToolAsync("products_get", new JsonObject { ["key"] = 2 });

            var content = result["structuredContent"]!.AsObject();
            Assert.Equal("Mouse", content["Name"]!.GetValue<string>());
            Assert.Null(content["CostPrice"]);
        }

        [Fact]
        public async Task Keyed_get_for_a_missing_entity_is_a_tool_error()
        {
            var result = await _fixture.CallToolAsync("products_get", new JsonObject { ["key"] = 999 });

            Assert.True(result["isError"]!.GetValue<bool>());
        }

        [Fact]
        public async Task Bound_function_invokes_through_the_odata_route()
        {
            var result = await _fixture.CallToolAsync("products_most_expensive", new JsonObject());

            Assert.NotEqual(true, result["isError"]?.GetValue<bool>());
            Assert.Equal(249, result["structuredContent"]!["value"]!.GetValue<decimal>());
        }
    }

    /// <summary>Mutating calls: create, update, rate. Own fixture instance, own seed data.</summary>
    public class ODataToolMutationTests : IClassFixture<ODataApiTestFixture>
    {
        private readonly ODataApiTestFixture _fixture;

        public ODataToolMutationTests(ODataApiTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task Create_posts_the_flattened_body_through_the_odata_formatter()
        {
            var token = await _fixture.GetTokenAsync("alice");
            var result = await _fixture.CallToolAsync("products_create", new JsonObject
            {
                ["name"] = "Webcam",
                ["category"] = "peripherals",
                ["price"] = 59,
                ["costPrice"] = 25,
            }, token);

            Assert.NotEqual(true, result["isError"]?.GetValue<bool>());

            var content = result["structuredContent"]!.AsObject();
            Assert.Equal("Webcam", content["Name"]!.GetValue<string>());
            Assert.True(content["Id"]!.GetValue<int>() > 0);

            // Output shaping applies to the create result as well.
            Assert.Null(content["CostPrice"]);
        }

        [Fact]
        public async Task Patch_sends_a_partial_delta_payload()
        {
            var token = await _fixture.GetTokenAsync("alice");
            var patch = await _fixture.CallToolAsync("products_update", new JsonObject
            {
                ["key"] = 4,
                ["price"] = 99,
            }, token);

            Assert.NotEqual(true, patch["isError"]?.GetValue<bool>());

            var read = await _fixture.CallToolAsync("products_get", new JsonObject { ["key"] = 4 });
            var content = read["structuredContent"]!.AsObject();
            Assert.Equal(99, content["Price"]!.GetValue<decimal>());
            Assert.Equal("Dock", content["Name"]!.GetValue<string>());
        }

        [Fact]
        public async Task Bound_action_reads_its_named_parameters_from_the_body()
        {
            var result = await _fixture.CallToolAsync("products_rate", new JsonObject
            {
                ["key"] = 1,
                ["parameters"] = new JsonObject { ["stars"] = 5 },
            });

            Assert.NotEqual(true, result["isError"]?.GetValue<bool>());
            var rating = result["structuredContent"]!["value"]!.GetValue<double>();
            Assert.InRange(rating, 1, 5);
        }

        [Fact]
        public async Task Bound_action_with_invalid_parameters_surfaces_the_validation_error()
        {
            var result = await _fixture.CallToolAsync("products_rate", new JsonObject
            {
                ["key"] = 1,
                ["parameters"] = new JsonObject { ["stars"] = 9 },
            });

            Assert.True(result["isError"]!.GetValue<bool>());
        }
    }
}
