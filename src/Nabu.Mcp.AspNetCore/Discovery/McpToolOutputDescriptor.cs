using System;
using System.Collections.Generic;

namespace Nabu.Mcp.AspNetCore.Discovery
{
    /// <summary>
    /// How a tool's response body is shaped before it becomes tool content: the field paths to keep
    /// or drop, and an optional converter type. Resolved from <see cref="McpToolOutputAttribute"/>
    /// during discovery; a tool without one (<see cref="McpToolDescriptor.Output"/> is <c>null</c>)
    /// exposes its response as-is.
    /// </summary>
    public sealed class McpToolOutputDescriptor
    {
        public McpToolOutputDescriptor(
            IReadOnlyList<string>? includeFields,
            IReadOnlyList<string>? excludeFields,
            Type? converterType)
        {
            IncludeFields = includeFields != null && includeFields.Count > 0 ? includeFields : null;
            ExcludeFields = excludeFields != null && excludeFields.Count > 0 ? excludeFields : null;
            ConverterType = converterType;
        }

        /// <summary>
        /// Dot-separated field paths to keep; everything else is removed. <c>null</c> keeps every field.
        /// </summary>
        public IReadOnlyList<string>? IncludeFields { get; }

        /// <summary>Dot-separated field paths removed after <see cref="IncludeFields"/> was applied.</summary>
        public IReadOnlyList<string>? ExcludeFields { get; }

        /// <summary>
        /// The <see cref="Execution.IMcpToolOutputConverter"/> implementation run after the field
        /// filters, or <c>null</c> for filter-only shaping.
        /// </summary>
        public Type? ConverterType { get; }

        /// <summary>True when this descriptor changes nothing - no filters and no converter.</summary>
        public bool IsEmpty
        {
            get { return IncludeFields == null && ExcludeFields == null && ConverterType == null; }
        }
    }
}
