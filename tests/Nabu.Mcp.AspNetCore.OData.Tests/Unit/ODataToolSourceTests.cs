using System.Text.Json.Nodes;
using Microsoft.OData.Edm;
using Nabu.Mcp.AspNetCore.OData.Discovery;
using Xunit;

namespace Nabu.Mcp.AspNetCore.OData.Tests.Unit
{
    public class ODataToolSourceTests
    {
        [Theory]
        [InlineData("odata/Customers({key})", "key", "odata/Customers('{key}')")]
        [InlineData("odata/Products/ByName(name={name})", "name", "odata/Products/ByName(name='{name}')")]
        [InlineData("odata/Customers/{key}", "key", "odata/Customers/{key}")]
        public void QuoteStringTokens_quotes_string_tokens_inside_parentheses(string template, string stringToken, string expected)
        {
            var result = ODataToolSource.QuoteStringTokens(template, name => name == stringToken);

            Assert.Equal(expected, result);
        }

        [Fact]
        public void QuoteStringTokens_leaves_non_string_tokens_alone()
        {
            var result = ODataToolSource.QuoteStringTokens("odata/Products({key})", _ => false);

            Assert.Equal("odata/Products({key})", result);
        }

        [Fact]
        public void QuoteStringTokens_does_not_double_quote()
        {
            var result = ODataToolSource.QuoteStringTokens("odata/Products(name='{name}')", _ => true);

            Assert.Equal("odata/Products(name='{name}')", result);
        }

        [Fact]
        public void CompareTemplates_prefers_key_as_segment_over_parenthesis()
        {
            Assert.True(ODataToolSource.CompareTemplates("odata/Products/{key}", "odata/Products({key})") < 0);
        }

        [Fact]
        public void CompareTemplates_prefers_plain_templates_over_dollar_projections()
        {
            Assert.True(ODataToolSource.CompareTemplates("odata/Products", "odata/Products/$count") < 0);
        }

        [Fact]
        public void CompareTemplates_prefers_the_unqualified_operation_name()
        {
            Assert.True(ODataToolSource.CompareTemplates(
                "odata/Products/{key}/Rate",
                "odata/Products/{key}/Default.Rate") < 0);
        }

        [Fact]
        public void RenameToEdm_rewrites_property_names_and_required_entries()
        {
            var edmType = new EdmEntityType("Default", "Product");
            edmType.AddStructuralProperty("Name", EdmPrimitiveTypeKind.String);
            edmType.AddStructuralProperty("Price", EdmPrimitiveTypeKind.Decimal);

            var schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["name"] = new JsonObject { ["type"] = "string" },
                    ["price"] = new JsonObject { ["type"] = "number" },
                    ["unmapped"] = new JsonObject { ["type"] = "string" },
                },
                ["required"] = new JsonArray("name"),
            };

            ODataToolSource.RenameToEdm(schema, edmType);

            var properties = schema["properties"]!.AsObject();
            Assert.True(properties.ContainsKey("Name"));
            Assert.True(properties.ContainsKey("Price"));
            Assert.False(properties.ContainsKey("name"));

            // A property the EDM model does not declare keeps its name: it may be a dynamic
            // property of an open type.
            Assert.True(properties.ContainsKey("unmapped"));

            Assert.Equal("Name", schema["required"]!.AsArray()[0]!.GetValue<string>());
        }

        [Fact]
        public void RenameToEdm_recurses_into_complex_and_collection_properties()
        {
            var address = new EdmComplexType("Default", "Address");
            address.AddStructuralProperty("Street", EdmPrimitiveTypeKind.String);

            var edmType = new EdmEntityType("Default", "Customer");
            edmType.AddStructuralProperty("Home", new EdmComplexTypeReference(address, isNullable: true));
            edmType.AddStructuralProperty(
                "Offices",
                new EdmCollectionTypeReference(new EdmCollectionType(new EdmComplexTypeReference(address, isNullable: true))));

            var schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["home"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject { ["street"] = new JsonObject { ["type"] = "string" } },
                    },
                    ["offices"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject { ["street"] = new JsonObject { ["type"] = "string" } },
                        },
                    },
                },
            };

            ODataToolSource.RenameToEdm(schema, edmType);

            var properties = schema["properties"]!.AsObject();
            Assert.True(properties["Home"]!["properties"]!.AsObject().ContainsKey("Street"));
            Assert.True(properties["Offices"]!["items"]!["properties"]!.AsObject().ContainsKey("Street"));
        }
    }
}
