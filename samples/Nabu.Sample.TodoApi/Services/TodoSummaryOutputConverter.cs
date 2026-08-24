using System.Text.Json.Nodes;
using Nabu.Mcp.AspNetCore.Execution;

namespace Nabu.Sample.TodoApi.Services
{
    /// <summary>
    /// Output converter behind the <c>todos_get_summary</c> tool: collapses the full
    /// <see cref="Models.TodoItem"/> JSON into a one-line summary. It runs after the attribute's
    /// field filters, so the <c>attachments</c> list is already gone by the time it looks at the
    /// item. Converters are resolved from the request's services when registered - this one is not,
    /// so Nabu constructs it on demand.
    /// </summary>
    public sealed class TodoSummaryOutputConverter : IMcpToolOutputConverter
    {
        public JsonNode? Convert(McpToolOutputContext context, JsonNode? output)
        {
            var item = output as JsonObject;
            if (item == null)
            {
                return output;
            }

            var title = item["title"]?.GetValue<string>() ?? "(untitled)";
            var completed = item["isCompleted"]?.GetValue<bool>() ?? false;

            return new JsonObject
            {
                ["id"] = item["id"]?.DeepClone(),
                ["summary"] = (completed ? "[done] " : "[open] ") + title,
                ["dueOn"] = item["dueOn"]?.DeepClone(),
            };
        }
    }
}
