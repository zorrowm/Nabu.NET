using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nabu.Mcp.AspNetCore.Discovery;
using Nabu.Mcp.AspNetCore.Execution;
using Xunit;

namespace Nabu.Mcp.AspNetCore.Tests.Unit
{
    public class McpToolOutputShaperTests
    {
        [Fact]
        public void Exclude_removes_nested_fields_and_traverses_arrays()
        {
            var shaped = Shape(
                "{\"items\":[{\"id\":1,\"owner\":\"alice\",\"nested\":{\"secret\":true,\"keep\":1}}]," +
                "\"owner\":\"root\",\"totalCount\":1}",
                excludeFields: new[] { "items.owner", "items.nested.secret", "owner" });

            var root = shaped!.AsObject();
            Assert.Null(root["owner"]);
            Assert.Equal(1, root["totalCount"]!.GetValue<int>());

            var item = root["items"]!.AsArray()[0]!.AsObject();
            Assert.Equal(1, item["id"]!.GetValue<int>());
            Assert.Null(item["owner"]);
            Assert.Null(item["nested"]!["secret"]);
            Assert.Equal(1, item["nested"]!["keep"]!.GetValue<int>());
        }

        [Fact]
        public void Include_keeps_only_listed_paths()
        {
            var shaped = Shape(
                "{\"items\":[{\"id\":1,\"title\":\"a\",\"owner\":\"alice\"}],\"totalCount\":1,\"page\":0}",
                includeFields: new[] { "items.id", "items.title", "totalCount" });

            var root = shaped!.AsObject();
            Assert.Null(root["page"]);
            Assert.Equal(1, root["totalCount"]!.GetValue<int>());

            var item = root["items"]!.AsArray()[0]!.AsObject();
            Assert.Equal("a", item["title"]!.GetValue<string>());
            Assert.Null(item["owner"]);
        }

        [Fact]
        public void Include_terminal_path_keeps_whole_subtree()
        {
            var shaped = Shape(
                "{\"data\":{\"a\":1,\"b\":{\"c\":2}},\"extra\":true}",
                includeFields: new[] { "data" });

            var root = shaped!.AsObject();
            Assert.Null(root["extra"]);
            Assert.Equal(2, root["data"]!["b"]!["c"]!.GetValue<int>());
        }

        [Fact]
        public void Field_matching_is_case_insensitive()
        {
            var shaped = Shape(
                "{\"Items\":[{\"Owner\":\"alice\",\"Id\":1}]}",
                excludeFields: new[] { "items.owner" });

            var item = shaped!["Items"]!.AsArray()[0]!.AsObject();
            Assert.Null(item["Owner"]);
            Assert.Equal(1, item["Id"]!.GetValue<int>());
        }

        [Fact]
        public void Exclude_applies_after_include()
        {
            var shaped = Shape(
                "{\"item\":{\"id\":1,\"title\":\"a\",\"owner\":\"alice\"},\"extra\":true}",
                includeFields: new[] { "item" },
                excludeFields: new[] { "item.owner" });

            var root = shaped!.AsObject();
            Assert.Null(root["extra"]);
            Assert.Null(root["item"]!["owner"]);
            Assert.Equal("a", root["item"]!["title"]!.GetValue<string>());
        }

        [Fact]
        public void Unknown_paths_change_nothing()
        {
            var shaped = Shape("{\"a\":1}", excludeFields: new[] { "does.not.exist" });
            Assert.Equal(1, shaped!["a"]!.GetValue<int>());
        }

        [Fact]
        public void Array_root_is_traversed()
        {
            var shaped = Shape("[{\"id\":1,\"owner\":\"alice\"}]", excludeFields: new[] { "owner" });
            var item = shaped!.AsArray()[0]!.AsObject();
            Assert.Null(item["owner"]);
            Assert.Equal(1, item["id"]!.GetValue<int>());
        }

        [Fact]
        public void Non_json_content_type_fails_closed()
        {
            var failure = ShapeFailure("<xml/>", contentType: "application/xml", excludeFields: new[] { "a" });
            Assert.Contains("only JSON", failure);
        }

        [Fact]
        public void Invalid_json_body_fails_closed()
        {
            var failure = ShapeFailure("{\"a\":", contentType: "application/json", excludeFields: new[] { "a" });
            Assert.Contains("not valid JSON", failure);
        }

        [Fact]
        public void Converter_receives_filtered_output_and_replaces_it()
        {
            var shaped = Shape(
                "{\"title\":\"a\",\"owner\":\"alice\"}",
                excludeFields: new[] { "owner" },
                converterType: typeof(RecordingConverter));

            Assert.Equal("a", shaped!["seenTitle"]!.GetValue<string>());
            Assert.False(shaped["sawOwner"]!.GetValue<bool>());
        }

        [Fact]
        public void Converter_is_resolved_from_services_when_registered()
        {
            var services = new ServiceCollection();
            services.AddSingleton(new ConverterDependency { Marker = "from-di" });
            services.AddSingleton<DependentConverter>();

            var shaped = Shape(
                "{}",
                converterType: typeof(DependentConverter),
                provider: services.BuildServiceProvider());

            Assert.Equal("from-di", shaped!["marker"]!.GetValue<string>());
        }

        [Fact]
        public void Converter_dependencies_are_activated_when_not_registered()
        {
            var services = new ServiceCollection();
            services.AddSingleton(new ConverterDependency { Marker = "activated" });

            var shaped = Shape(
                "{}",
                converterType: typeof(DependentConverter),
                provider: services.BuildServiceProvider());

            Assert.Equal("activated", shaped!["marker"]!.GetValue<string>());
        }

        [Fact]
        public void Throwing_converter_fails_closed()
        {
            var failure = ShapeFailure(
                "{\"secret\":true}",
                contentType: "application/json",
                converterType: typeof(ThrowingConverter));

            Assert.Contains("ThrowingConverter", failure);
            Assert.DoesNotContain("secret", failure);
        }

        private static JsonNode? Shape(
            string body,
            string[]? includeFields = null,
            string[]? excludeFields = null,
            Type? converterType = null,
            IServiceProvider? provider = null)
        {
            JsonNode? shaped;
            string? failure;
            var ok = TryShape(body, "application/json", includeFields, excludeFields, converterType, provider, out shaped, out failure);
            Assert.True(ok, "Shaping unexpectedly failed: " + failure);
            return shaped;
        }

        private static string ShapeFailure(
            string body,
            string contentType,
            string[]? includeFields = null,
            string[]? excludeFields = null,
            Type? converterType = null)
        {
            JsonNode? shaped;
            string? failure;
            var ok = TryShape(body, contentType, includeFields, excludeFields, converterType, null, out shaped, out failure);
            Assert.False(ok, "Shaping unexpectedly succeeded.");
            return failure!;
        }

        private static bool TryShape(
            string body,
            string? contentType,
            string[]? includeFields,
            string[]? excludeFields,
            Type? converterType,
            IServiceProvider? provider,
            out JsonNode? shaped,
            out string? failure)
        {
            var tool = new McpToolDescriptor(
                "test_tool",
                new List<McpToolParameterDescriptor>(),
                new JsonObject(),
                new McpToolAnnotations())
            {
                Output = new McpToolOutputDescriptor(includeFields, excludeFields, converterType),
            };

            var result = new McpToolInvocationResult(200, contentType, body, new Dictionary<string, string>());
            var context = new DefaultHttpContext
            {
                RequestServices = provider ?? new ServiceCollection().BuildServiceProvider(),
            };

            return McpToolOutputShaper.TryShape(tool, result, context, NullLogger.Instance, out shaped, out failure);
        }

        private sealed class RecordingConverter : IMcpToolOutputConverter
        {
            public JsonNode? Convert(McpToolOutputContext context, JsonNode? output)
            {
                var obj = output!.AsObject();
                return new JsonObject
                {
                    ["seenTitle"] = obj["title"]!.GetValue<string>(),
                    ["sawOwner"] = obj["owner"] != null,
                };
            }
        }

        private sealed class ConverterDependency
        {
            public string Marker { get; set; } = string.Empty;
        }

        private sealed class DependentConverter : IMcpToolOutputConverter
        {
            private readonly ConverterDependency _dependency;

            public DependentConverter(ConverterDependency dependency)
            {
                _dependency = dependency;
            }

            public JsonNode? Convert(McpToolOutputContext context, JsonNode? output)
            {
                return new JsonObject { ["marker"] = _dependency.Marker };
            }
        }

        private sealed class ThrowingConverter : IMcpToolOutputConverter
        {
            public JsonNode? Convert(McpToolOutputContext context, JsonNode? output)
            {
                throw new InvalidOperationException("the secret is 42");
            }
        }
    }
}
