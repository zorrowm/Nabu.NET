using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Nabu.Mcp.AspNetCore.OData.Tests.Integration
{
    /// <summary>
    /// The application's own authentication and authorization keep working for OData tools: the
    /// MCP endpoint challenges anonymous callers, the pipeline replay enforces [Authorize] and role
    /// requirements on every call, and tool visibility follows the caller's permissions.
    /// </summary>
    public class ODataAuthorizationTests : IClassFixture<ODataApiTestFixture>
    {
        private readonly ODataApiTestFixture _fixture;

        public ODataAuthorizationTests(ODataApiTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task Anonymous_caller_can_invoke_the_anonymous_query_tool()
        {
            var result = await _fixture.CallToolAsync("products_query", new JsonObject { ["top"] = 1 });

            Assert.NotEqual(true, result["isError"]?.GetValue<bool>());
        }

        [Fact]
        public async Task Anonymous_caller_is_challenged_for_a_tool_that_requires_authorization()
        {
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1000,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "products_create",
                    ["arguments"] = new JsonObject { ["name"] = "Sneaky" },
                },
            };

            using var response = await _fixture.PostRawAsync(request.ToJsonString());

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task Authenticated_caller_without_the_role_is_refused_by_the_pipeline()
        {
            var token = await _fixture.GetTokenAsync("alice");
            var result = await _fixture.CallToolAsync("products_delete", new JsonObject { ["key"] = 3 }, token);

            // The synthetic request travelled through UseAuthorization, which answered 403.
            Assert.True(result["isError"]!.GetValue<bool>());

            // The entity is still there.
            var read = await _fixture.CallToolAsync("products_get", new JsonObject { ["key"] = 3 });
            Assert.NotEqual(true, read["isError"]?.GetValue<bool>());
        }

        [Fact]
        public async Task Administrator_can_delete_through_the_same_tool()
        {
            var token = await _fixture.GetTokenAsync("root");
            var result = await _fixture.CallToolAsync("products_delete", new JsonObject { ["key"] = 4 }, token);

            Assert.NotEqual(true, result["isError"]?.GetValue<bool>());

            var read = await _fixture.CallToolAsync("products_get", new JsonObject { ["key"] = 4 });
            Assert.True(read["isError"]!.GetValue<bool>());
        }

        [Fact]
        public async Task Tool_visibility_follows_the_callers_permissions()
        {
            var anonymous = await _fixture.ListToolNamesAsync();
            Assert.Contains("products_query", anonymous);
            Assert.DoesNotContain("products_create", anonymous);
            Assert.DoesNotContain("products_delete", anonymous);

            var alice = await _fixture.GetTokenAsync("alice");
            var user = await _fixture.ListToolNamesAsync(alice);
            Assert.Contains("products_create", user);
            Assert.DoesNotContain("products_delete", user);

            var root = await _fixture.GetTokenAsync("root");
            var admin = await _fixture.ListToolNamesAsync(root);
            Assert.Contains("products_delete", admin);
        }
    }
}
