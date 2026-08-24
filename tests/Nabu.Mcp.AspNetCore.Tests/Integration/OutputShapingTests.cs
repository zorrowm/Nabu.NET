using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Nabu.Mcp.AspNetCore.Tests.Integration
{
    /// <summary>
    /// Drives the sample application's [McpToolOutput]-shaped tools end to end: the field filters,
    /// the converter, the Tool-scoping that leaves sibling variants untouched, and the Minimal API
    /// counterpart.
    /// </summary>
    [Collection(McpTestCollection.Name)]
    public class OutputShapingTests
    {
        private readonly McpTestFixture _fixture;

        public OutputShapingTests(McpTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task Include_fields_narrow_the_response_to_the_listed_paths()
        {
            var token = await _fixture.GetTokenAsync("alice");
            await CreateTodoAsync("Shaping include check", token);

            var result = await _fixture.CallToolAsync("todos_list_compact", new JsonObject(), token);

            Assert.False(McpTestFixture.IsError(result));
            var payload = McpTestFixture.JsonOf(result).AsObject();

            // The page envelope keeps only totalCount and items; paging fields are gone.
            Assert.True(payload.ContainsKey("totalCount"));
            Assert.False(payload.ContainsKey("page"));
            Assert.False(payload.ContainsKey("pageSize"));

            var items = payload["items"]!.AsArray();
            Assert.NotEmpty(items);
            foreach (var item in items)
            {
                var obj = item!.AsObject();
                Assert.True(obj.ContainsKey("id"));
                Assert.True(obj.ContainsKey("title"));
                Assert.True(obj.ContainsKey("isCompleted"));
                Assert.False(obj.ContainsKey("owner"));
                Assert.False(obj.ContainsKey("notes"));
                Assert.False(obj.ContainsKey("attachments"));
            }
        }

        [Fact]
        public async Task Structured_content_is_shaped_too()
        {
            var token = await _fixture.GetTokenAsync("alice");
            await CreateTodoAsync("Shaping structured check", token);

            var result = await _fixture.CallToolAsync("todos_list_compact", new JsonObject(), token);

            var structured = result["structuredContent"]!.AsObject();
            Assert.True(structured.ContainsKey("items"));
            foreach (var item in structured["items"]!.AsArray())
            {
                Assert.False(item!.AsObject().ContainsKey("owner"));
            }
        }

        [Fact]
        public async Task Shaping_is_scoped_to_its_tool_and_leaves_sibling_variants_untouched()
        {
            var token = await _fixture.GetTokenAsync("alice");
            await CreateTodoAsync("Shaping scoping check", token);

            var result = await _fixture.CallToolAsync("todos_list", new JsonObject(), token);

            var items = McpTestFixture.JsonOf(result)["items"]!.AsArray();
            Assert.NotEmpty(items);
            Assert.True(items[0]!.AsObject().ContainsKey("owner"));
        }

        [Fact]
        public async Task A_converter_transforms_the_filtered_response()
        {
            var token = await _fixture.GetTokenAsync("alice");
            var created = await CreateTodoAsync("Converter check", token);
            var id = created["id"]!.GetValue<string>();

            var result = await _fixture.CallToolAsync("todos_get_summary", new JsonObject { ["id"] = id }, token);

            Assert.False(McpTestFixture.IsError(result));
            var payload = McpTestFixture.JsonOf(result).AsObject();

            Assert.Equal(id, payload["id"]!.GetValue<string>());
            Assert.Equal("[open] Converter check", payload["summary"]!.GetValue<string>());
            Assert.False(payload.ContainsKey("owner"));
            Assert.False(payload.ContainsKey("attachments"));

            // The full-record sibling still answers everything.
            var full = await _fixture.CallToolAsync("todos_get_by_id", new JsonObject { ["id"] = id }, token);
            Assert.True(McpTestFixture.JsonOf(full).AsObject().ContainsKey("owner"));
        }

        [Fact]
        public async Task Error_responses_are_not_shaped()
        {
            var token = await _fixture.GetTokenAsync("alice");

            var result = await _fixture.CallToolAsync(
                "todos_get_summary",
                new JsonObject { ["id"] = "00000000-0000-0000-0000-000000000000" },
                token);

            // The 404 body passes through as the usual tool error, untouched by the converter.
            Assert.True(McpTestFixture.IsError(result));
            Assert.Contains("HTTP 404", McpTestFixture.TextOf(result));
        }

        [Fact]
        public async Task Minimal_api_output_shaping_excludes_fields_per_tool()
        {
            var brief = await _fixture.CallToolAsync(
                "server_time_in_zone_brief",
                new JsonObject { ["timeZone"] = "UTC" });

            var payload = McpTestFixture.JsonOf(brief).AsObject();
            Assert.True(payload.ContainsKey("timeZone"));
            Assert.True(payload.ContainsKey("now"));
            Assert.False(payload.ContainsKey("utcOffset"));

            var full = await _fixture.CallToolAsync(
                "server_time_in_zone",
                new JsonObject { ["timeZone"] = "UTC" });

            Assert.True(McpTestFixture.JsonOf(full).AsObject().ContainsKey("utcOffset"));
        }

        [Fact]
        public async Task Shaped_tools_are_advertised_like_any_other()
        {
            var token = await _fixture.GetTokenAsync("alice");
            var envelope = await _fixture.RpcAsync("tools/list", token: token);
            var tools = envelope["result"]!["tools"]!.AsArray();

            var names = new System.Collections.Generic.List<string>();
            foreach (var tool in tools)
            {
                names.Add(tool!["name"]!.GetValue<string>());
            }

            Assert.Contains("todos_list_compact", names);
            Assert.Contains("todos_get_summary", names);
            Assert.Contains("server_time_in_zone_brief", names);
        }

        private async Task<JsonObject> CreateTodoAsync(string title, System.Net.Http.Headers.AuthenticationHeaderValue token)
        {
            var created = await _fixture.CallToolAsync(
                "todos_create",
                new JsonObject { ["title"] = title },
                token);

            Assert.False(McpTestFixture.IsError(created));
            return McpTestFixture.JsonOf(created).AsObject();
        }
    }
}
