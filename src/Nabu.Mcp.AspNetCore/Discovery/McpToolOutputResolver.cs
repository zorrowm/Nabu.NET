using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Nabu.Mcp.AspNetCore.Execution;

namespace Nabu.Mcp.AspNetCore.Discovery
{
    /// <summary>
    /// Turns the <see cref="McpToolOutputAttribute"/> occurrences found on an action, endpoint or hub
    /// method into the <see cref="McpToolOutputDescriptor"/> for one published tool. Shared by every
    /// discovery path so the precedence rules stay identical: method level beats class level, and a
    /// <see cref="McpToolOutputAttribute.Tool"/>-scoped occurrence beats an unscoped one.
    /// </summary>
    internal static class McpToolOutputResolver
    {
        internal static readonly McpToolOutputAttribute[] None = new McpToolOutputAttribute[0];

        /// <summary>
        /// Selects the occurrence that applies to the tool named <paramref name="resolvedName"/>
        /// (declared as <paramref name="declaredName"/> on its <see cref="McpToolAttribute"/>, when
        /// one was) and validates it. Returns <c>false</c> when the configuration is invalid - the
        /// converter type does not implement <see cref="IMcpToolOutputConverter"/> - in which case
        /// the tool must not be published: an output filter exists to hide data, so a broken one
        /// fails closed rather than publishing the tool unshaped. <paramref name="matched"/> records
        /// which occurrences applied somewhere, for the unmatched-name warning.
        /// </summary>
        internal static bool TrySelect(
            IReadOnlyList<McpToolOutputAttribute> methodLevel,
            IReadOnlyList<McpToolOutputAttribute> classLevel,
            string resolvedName,
            string? declaredName,
            string display,
            ILogger logger,
            ISet<McpToolOutputAttribute>? matched,
            out McpToolOutputDescriptor? output)
        {
            output = null;

            var attribute = Pick(methodLevel, resolvedName, declaredName, scoped: true, display, logger)
                            ?? Pick(methodLevel, resolvedName, declaredName, scoped: false, display, logger)
                            ?? Pick(classLevel, resolvedName, declaredName, scoped: true, display, logger)
                            ?? Pick(classLevel, resolvedName, declaredName, scoped: false, display, logger);

            if (attribute == null)
            {
                return true;
            }

            matched?.Add(attribute);
            return TryCreate(attribute, resolvedName, display, logger, out output);
        }

        /// <summary>
        /// A misspelled <see cref="McpToolOutputAttribute.Tool"/> would otherwise fail silently - the
        /// tool would simply publish unshaped - so every scoped occurrence that applied to nothing is
        /// reported. Only method-level occurrences are checked: a class-level one may legitimately
        /// target another member of the same class.
        /// </summary>
        internal static void WarnUnmatched(
            IReadOnlyList<McpToolOutputAttribute> methodLevel,
            ISet<McpToolOutputAttribute> matched,
            string display,
            ILogger logger)
        {
            foreach (var attribute in methodLevel)
            {
                if (!string.IsNullOrEmpty(attribute.Tool) && !matched.Contains(attribute))
                {
                    logger.LogWarning(
                        "Nabu MCP ignored an [McpToolOutput] occurrence on {Endpoint}: it targets Tool = '{Tool}', " +
                        "but no tool of that name is published there.",
                        display,
                        attribute.Tool);
                }
            }
        }

        private static McpToolOutputAttribute? Pick(
            IReadOnlyList<McpToolOutputAttribute> attributes,
            string resolvedName,
            string? declaredName,
            bool scoped,
            string display,
            ILogger logger)
        {
            McpToolOutputAttribute? found = null;

            foreach (var attribute in attributes)
            {
                var isScoped = !string.IsNullOrEmpty(attribute.Tool);
                if (isScoped != scoped)
                {
                    continue;
                }

                if (scoped &&
                    !string.Equals(attribute.Tool, resolvedName, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(attribute.Tool, declaredName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (found != null)
                {
                    logger.LogWarning(
                        "Nabu MCP found more than one applicable [McpToolOutput] occurrence for tool '{Tool}' on {Endpoint}; " +
                        "only the first is applied. Scope each occurrence with its Tool property.",
                        resolvedName,
                        display);
                    break;
                }

                found = attribute;
            }

            return found;
        }

        private static bool TryCreate(
            McpToolOutputAttribute attribute,
            string resolvedName,
            string display,
            ILogger logger,
            out McpToolOutputDescriptor? output)
        {
            output = null;

            var converter = attribute.Converter;
            if (converter != null &&
                (!typeof(IMcpToolOutputConverter).IsAssignableFrom(converter) || converter.IsAbstract))
            {
                logger.LogWarning(
                    "Nabu MCP did not publish tool '{Tool}' on {Endpoint}: its [McpToolOutput] names {Converter} " +
                    "as the output converter, but that type is not a concrete IMcpToolOutputConverter.",
                    resolvedName,
                    display,
                    converter.FullName);
                return false;
            }

            var include = NormalizePaths(attribute.IncludeFields, display, logger);
            var exclude = NormalizePaths(attribute.ExcludeFields, display, logger);

            var descriptor = new McpToolOutputDescriptor(include, exclude, converter);
            if (descriptor.IsEmpty)
            {
                logger.LogWarning(
                    "Nabu MCP ignored an [McpToolOutput] occurrence on {Endpoint}: it lists no fields and names no converter.",
                    display);
                return true;
            }

            output = descriptor;
            return true;
        }

        private static IReadOnlyList<string>? NormalizePaths(string[]? paths, string display, ILogger logger)
        {
            if (paths == null || paths.Length == 0)
            {
                return null;
            }

            var normalized = new List<string>(paths.Length);
            foreach (var path in paths)
            {
                var clean = NormalizePath(path);
                if (clean == null)
                {
                    logger.LogWarning(
                        "Nabu MCP ignored the [McpToolOutput] field path '{Path}' on {Endpoint}: " +
                        "paths are dot-separated property names with no empty segments.",
                        path,
                        display);
                    continue;
                }

                normalized.Add(clean);
            }

            return normalized.Count == 0 ? null : normalized;
        }

        /// <summary>Trims each segment, or returns <c>null</c> for a path with an empty segment.</summary>
        private static string? NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var segments = path!.Split('.');
            for (var i = 0; i < segments.Length; i++)
            {
                segments[i] = segments[i].Trim();
                if (segments[i].Length == 0)
                {
                    return null;
                }
            }

            return string.Join(".", segments);
        }
    }
}
