using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nabu.Mcp.AspNetCore.Discovery;

namespace Nabu.Mcp.AspNetCore.Execution
{
    /// <summary>
    /// Applies a tool's <see cref="McpToolOutputDescriptor"/> to the response body: parses the JSON,
    /// prunes it to the include/exclude field paths, and runs the converter. Shaping either succeeds
    /// or reports a reason - it never hands back the unshaped body, because the caller treats a
    /// failure as a tool error (fail-closed) rather than exposing what the shaping meant to hide.
    /// </summary>
    internal static class McpToolOutputShaper
    {
        /// <summary>
        /// Shapes the body of a successful, non-empty response. On <c>false</c>,
        /// <paramref name="failure"/> explains why in a sentence fit for the model to read.
        /// </summary>
        public static bool TryShape(
            McpToolDescriptor tool,
            McpToolInvocationResult result,
            HttpContext context,
            ILogger logger,
            out JsonNode? shaped,
            out string? failure)
        {
            shaped = null;
            failure = null;
            var output = tool.Output;
            if (output == null)
            {
                shaped = null;
                failure = "the tool declares no output shaping.";
                return false;
            }

            var contentType = result.ContentType;
            if (contentType != null && contentType.IndexOf("json", StringComparison.OrdinalIgnoreCase) < 0)
            {
                failure = "the response is '" + contentType + "', and only JSON responses can be shaped.";
                return false;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(result.Body);
            }
            catch (JsonException)
            {
                failure = result.Truncated
                    ? "the response body is not valid JSON because it exceeded the response size cap and was truncated."
                    : "the response body is not valid JSON.";
                return false;
            }

            if (node != null)
            {
                var include = BuildTree(output.IncludeFields);
                if (include != null)
                {
                    ApplyInclude(node, include);
                }

                var exclude = BuildTree(output.ExcludeFields);
                if (exclude != null)
                {
                    ApplyExclude(node, exclude);
                }
            }

            if (output.ConverterType != null)
            {
                try
                {
                    var converter = ResolveConverter(output.ConverterType, context);
                    node = converter.Convert(new McpToolOutputContext(tool, result, context), node);
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Nabu MCP output converter {Converter} failed for tool {Tool}.",
                        output.ConverterType.FullName,
                        tool.Name);
                    failure = "the output converter '" + output.ConverterType.Name + "' failed with " + ex.GetType().Name + ".";
                    return false;
                }
            }

            shaped = node;
            return true;
        }

        private static IMcpToolOutputConverter ResolveConverter(Type converterType, HttpContext context)
        {
            var registered = context.RequestServices.GetService(converterType);
            if (registered != null)
            {
                return (IMcpToolOutputConverter)registered;
            }

            return (IMcpToolOutputConverter)ActivatorUtilities.CreateInstance(context.RequestServices, converterType);
        }

        /// <summary>One level of the field-path tree. A terminal node means "the whole subtree".</summary>
        private sealed class PathNode
        {
            public readonly Dictionary<string, PathNode> Children =
                new Dictionary<string, PathNode>(StringComparer.OrdinalIgnoreCase);

            public bool IsTerminal;
        }

        private static PathNode? BuildTree(IReadOnlyList<string>? paths)
        {
            if (paths == null || paths.Count == 0)
            {
                return null;
            }

            var root = new PathNode();
            foreach (var path in paths)
            {
                var current = root;
                foreach (var segment in path.Split('.'))
                {
                    // "a" being terminal already covers "a.b"; don't grow a branch below it.
                    if (current.IsTerminal)
                    {
                        break;
                    }

                    PathNode? child;
                    if (!current.Children.TryGetValue(segment, out child))
                    {
                        child = new PathNode();
                        current.Children[segment] = child;
                    }

                    current = child;
                }

                current.IsTerminal = true;
            }

            return root.Children.Count == 0 ? null : root;
        }

        /// <summary>
        /// Keeps only the properties on a listed path. A terminal match keeps the whole subtree; a
        /// non-terminal match keeps the property and recurses. Arrays are traversed transparently.
        /// </summary>
        private static void ApplyInclude(JsonNode node, PathNode tree)
        {
            var obj = node as JsonObject;
            if (obj != null)
            {
                foreach (var name in SnapshotKeys(obj))
                {
                    PathNode? child;
                    if (!tree.Children.TryGetValue(name, out child))
                    {
                        obj.Remove(name);
                        continue;
                    }

                    if (!child.IsTerminal)
                    {
                        var value = obj[name];
                        if (value != null)
                        {
                            ApplyInclude(value, child);
                        }
                    }
                }

                return;
            }

            var array = node as JsonArray;
            if (array != null)
            {
                foreach (var element in array)
                {
                    if (element != null)
                    {
                        ApplyInclude(element, tree);
                    }
                }
            }
        }

        /// <summary>Removes the properties on a listed path. Arrays are traversed transparently.</summary>
        private static void ApplyExclude(JsonNode node, PathNode tree)
        {
            var obj = node as JsonObject;
            if (obj != null)
            {
                foreach (var name in SnapshotKeys(obj))
                {
                    PathNode? child;
                    if (!tree.Children.TryGetValue(name, out child))
                    {
                        continue;
                    }

                    if (child.IsTerminal)
                    {
                        obj.Remove(name);
                        continue;
                    }

                    var value = obj[name];
                    if (value != null)
                    {
                        ApplyExclude(value, child);
                    }
                }

                return;
            }

            var array = node as JsonArray;
            if (array != null)
            {
                foreach (var element in array)
                {
                    if (element != null)
                    {
                        ApplyExclude(element, tree);
                    }
                }
            }
        }

        private static List<string> SnapshotKeys(JsonObject obj)
        {
            // The property set is mutated while filtering, so iterate a copy of the names.
            var names = new List<string>(obj.Count);
            foreach (var pair in obj)
            {
                names.Add(pair.Key);
            }

            return names;
        }
    }
}
