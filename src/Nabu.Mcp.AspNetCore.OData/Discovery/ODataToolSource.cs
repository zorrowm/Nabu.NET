using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.AspNetCore.OData.Formatter;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Routing;
using Microsoft.AspNetCore.OData.Routing.Controllers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;
using Nabu.Mcp.AspNetCore.Discovery;
using Nabu.Mcp.AspNetCore.Schema;

namespace Nabu.Mcp.AspNetCore.OData.Discovery
{
    /// <summary>
    /// Discovers MCP tools from the application's OData endpoints (Microsoft.AspNetCore.OData 8+).
    /// OData-routed actions are found through the MVC action table (the OData routing conventions
    /// stamp <c>ODataRoutingMetadata</c> onto every selector), published with the same
    /// <c>[McpTool]</c> / <c>[McpIgnore]</c> / <c>[McpParameter]</c> / <c>[McpToolOutput]</c>
    /// attributes controllers use, and invoked through the standard HTTP pipeline replay - so
    /// authentication, authorization, <c>[EnableQuery]</c> and the OData formatters all behave
    /// exactly as they do for a client-issued request.
    /// </summary>
    /// <remarks>
    /// On top of the action's own parameters, queryable GET actions advertise the OData query
    /// options (<c>$filter</c>, <c>$select</c>, <c>$orderby</c>, <c>$top</c>, ...) as optional tool
    /// inputs - see <see cref="NabuMcpODataOptions.QueryOptions"/>. Request bodies are advertised
    /// with the EDM property names the OData deserializer expects, and <c>Delta&lt;T&gt;</c>
    /// parameters become all-optional partial payloads.
    /// </remarks>
    public sealed class ODataToolSource : IMcpToolSource
    {
        private static readonly Type[] IgnoredParameterTypes =
        {
            typeof(CancellationToken),
            typeof(HttpContext),
            typeof(HttpRequest),
            typeof(HttpResponse),
            typeof(System.Security.Claims.ClaimsPrincipal),
            typeof(Stream),
            typeof(ODataQueryOptions),
            typeof(Microsoft.OData.UriParser.ODataPath),
        };

        /// <summary>
        /// The query options a queryable action can advertise, in the order they are appended to
        /// the input schema.
        /// </summary>
        private static readonly QueryOptionDefinition[] QueryOptionDefinitions =
        {
            new QueryOptionDefinition(
                AllowedQueryOptions.Filter, "filter", "$filter", typeof(string),
                "OData $filter expression selecting the items to return, e.g. \"Price gt 10 and contains(Name,'a')\". Property names are the EDM names and are case sensitive."),
            new QueryOptionDefinition(
                AllowedQueryOptions.Select, "select", "$select", typeof(string),
                "Comma-separated list of properties to return, e.g. \"Name,Price\"."),
            new QueryOptionDefinition(
                AllowedQueryOptions.OrderBy, "orderby", "$orderby", typeof(string),
                "Comma-separated sort order, e.g. \"Price desc,Name\"."),
            new QueryOptionDefinition(
                AllowedQueryOptions.Expand, "expand", "$expand", typeof(string),
                "Comma-separated list of navigation properties to include inline, e.g. \"Orders\"."),
            new QueryOptionDefinition(
                AllowedQueryOptions.Top, "top", "$top", typeof(int),
                "Maximum number of items to return."),
            new QueryOptionDefinition(
                AllowedQueryOptions.Skip, "skip", "$skip", typeof(int),
                "Number of items to skip before returning results."),
            new QueryOptionDefinition(
                AllowedQueryOptions.Count, "count", "$count", typeof(bool),
                "When true, includes the total item count as @odata.count."),
            new QueryOptionDefinition(
                AllowedQueryOptions.Search, "search", "$search", typeof(string),
                "Free-text search expression, applied with the application's OData search binder."),
            new QueryOptionDefinition(
                AllowedQueryOptions.Apply, "apply", "$apply", typeof(string),
                "OData $apply aggregation expression, e.g. \"groupby((Category),aggregate(Price with sum as Total))\"."),
            new QueryOptionDefinition(
                AllowedQueryOptions.Compute, "compute", "$compute", typeof(string),
                "OData $compute expression adding computed properties, e.g. \"Price mul Quantity as Total\"."),
        };

        private readonly IActionDescriptorCollectionProvider? _actionProvider;
        private readonly NabuMcpOptions _coreOptions;
        private readonly NabuMcpODataOptions _options;
        private readonly JsonSchemaGenerator _schemaGenerator;
        private readonly IXmlDocumentationProvider _documentation;
        private readonly ILogger _logger;

        public ODataToolSource(
            IActionDescriptorCollectionProvider? actionProvider,
            IOptions<NabuMcpOptions> coreOptions,
            IOptions<NabuMcpODataOptions> options,
            JsonSchemaGenerator schemaGenerator,
            IXmlDocumentationProvider documentation,
            ILogger<ODataToolSource>? logger = null)
        {
            _actionProvider = actionProvider;
            _coreOptions = (coreOptions ?? throw new ArgumentNullException(nameof(coreOptions))).Value;
            _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
            _schemaGenerator = schemaGenerator ?? throw new ArgumentNullException(nameof(schemaGenerator));
            _documentation = documentation ?? NullXmlDocumentationProvider.Instance;
            _logger = (ILogger?)logger ?? NullLogger.Instance;
        }

        public IReadOnlyList<McpToolDescriptor> GetTools()
        {
            var results = new List<McpToolDescriptor>();
            if (_actionProvider == null)
            {
                return results;
            }

            var usedNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var candidate in SelectActions(_actionProvider.ActionDescriptors.Items))
            {
                try
                {
                    results.AddRange(CreateTools(candidate.Action, candidate.Metadata, usedNames));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Nabu MCP could not expose the OData action {Controller}.{Action} as a tool.",
                        candidate.Action.ControllerName,
                        candidate.Action.ActionName);
                }
            }

            return results;
        }

        private readonly struct Candidate
        {
            public Candidate(ControllerActionDescriptor action, IODataRoutingMetadata metadata)
            {
                Action = action;
                Metadata = metadata;
            }

            public ControllerActionDescriptor Action { get; }

            public IODataRoutingMetadata Metadata { get; }
        }

        /// <summary>
        /// Picks one action descriptor per (method, route prefix, verb). The OData conventions
        /// register the same action under several templates - <c>Products({key})</c> and
        /// <c>Products/{key}</c>, <c>Default.Rate</c> and <c>Rate</c>, plus a <c>$count</c>
        /// projection - which are one operation, not many tools.
        /// </summary>
        private IEnumerable<Candidate> SelectActions(IReadOnlyList<Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor> actions)
        {
            var best = new Dictionary<(MethodInfo, string, string), Candidate>();
            var order = new List<(MethodInfo, string, string)>();

            foreach (var descriptor in actions)
            {
                if (!(descriptor is ControllerActionDescriptor action))
                {
                    continue;
                }

                var template = action.AttributeRouteInfo?.Template;
                if (string.IsNullOrEmpty(template))
                {
                    continue;
                }

                var metadata = FindRoutingMetadata(action);
                if (metadata == null)
                {
                    continue;
                }

                // The service document and $metadata endpoints are protocol infrastructure, not
                // application surface; they are never published, even under ExposeAllODataActions.
                if (typeof(MetadataController).IsAssignableFrom(action.ControllerTypeInfo))
                {
                    continue;
                }

                var key = (action.MethodInfo, metadata.Prefix ?? string.Empty, McpToolRegistry.ResolveHttpMethod(action));

                Candidate existing;
                if (!best.TryGetValue(key, out existing))
                {
                    best[key] = new Candidate(action, metadata);
                    order.Add(key);
                }
                else if (CompareTemplates(template!, existing.Action.AttributeRouteInfo!.Template!) < 0)
                {
                    best[key] = new Candidate(action, metadata);
                }
            }

            foreach (var key in order)
            {
                yield return best[key];
            }
        }

        internal static IODataRoutingMetadata? FindRoutingMetadata(ControllerActionDescriptor action)
        {
            var metadata = action.EndpointMetadata;
            if (metadata == null)
            {
                return null;
            }

            foreach (var entry in metadata)
            {
                if (entry is IODataRoutingMetadata routing)
                {
                    return routing;
                }
            }

            return null;
        }

        /// <summary>
        /// Orders the template variants of one action: plain templates beat <c>$count</c>-style
        /// projections, key-as-segment beats key-in-parenthesis (string keys then need no OData
        /// literal quoting), and the unqualified operation name beats the namespace-qualified one.
        /// </summary>
        internal static int CompareTemplates(string left, string right)
        {
            var byDollar = DollarSegments(left).CompareTo(DollarSegments(right));
            if (byDollar != 0)
            {
                return byDollar;
            }

            var byParens = ParenCount(left).CompareTo(ParenCount(right));
            if (byParens != 0)
            {
                return byParens;
            }

            var byLength = left.Length.CompareTo(right.Length);
            return byLength != 0 ? byLength : string.CompareOrdinal(left, right);
        }

        private static int DollarSegments(string template)
        {
            return template.IndexOf("/$", StringComparison.Ordinal) >= 0 ? 1 : 0;
        }

        private static int ParenCount(string template)
        {
            var count = 0;
            foreach (var c in template)
            {
                if (c == '(')
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>Builds every tool one OData action contributes. Exposed for tests.</summary>
        internal IReadOnlyList<McpToolDescriptor> CreateTools(
            ControllerActionDescriptor action,
            IODataRoutingMetadata metadata,
            ISet<string> usedNames)
        {
            var results = new List<McpToolDescriptor>();
            var method = action.MethodInfo;
            var controllerType = action.ControllerTypeInfo;

            if (method.GetCustomAttribute<McpIgnoreAttribute>() != null ||
                controllerType.GetCustomAttribute<McpIgnoreAttribute>() != null)
            {
                return results;
            }

            var methodAttributes = method.GetCustomAttributes<McpToolAttribute>(inherit: true).ToList();
            var controllerAttributes = controllerType.GetCustomAttributes<McpToolAttribute>(inherit: true).ToList();
            var controllerAttribute = controllerAttributes.Count > 0 ? controllerAttributes[0] : null;

            // Same semantics as controllers: a controller-wide [McpTool] is a default for actions
            // that carry none of their own; an action that declares variants replaces it.
            var isMethodLevel = methodAttributes.Count > 0;
            var variants = new List<McpToolAttribute?>();
            variants.AddRange(isMethodLevel ? methodAttributes : controllerAttributes);

            if (variants.Count == 0)
            {
                if (!_options.ExposeAllODataActions)
                {
                    return results;
                }

                variants.Add(null);
            }

            var methodOutputs = method.GetCustomAttributes<McpToolOutputAttribute>(inherit: true).ToList();
            var controllerOutputs = controllerType.GetCustomAttributes<McpToolOutputAttribute>(inherit: true).ToList();
            var matchedOutputs = new HashSet<McpToolOutputAttribute>();

            var display = action.ControllerName + "." + action.ActionName;
            var httpMethod = McpToolRegistry.ResolveHttpMethod(action);
            var routeTemplate = BuildRouteTemplate(action);
            var allParameters = BuildParameters(action, metadata, httpMethod, routeTemplate);

            foreach (var attribute in variants)
            {
                if (attribute != null && !attribute.Enabled)
                {
                    continue;
                }

                List<McpToolParameterDescriptor> parameters;
                List<McpToolConstantDescriptor> constants;
                if (!McpToolRegistry.TryApplyVariant(display, attribute, routeTemplate, allParameters, _logger, out parameters, out constants))
                {
                    continue;
                }

                var methodAttribute = isMethodLevel ? attribute : null;
                var name = ResolveName(action, methodAttribute, httpMethod, routeTemplate, usedNames);
                var annotations = McpToolRegistry.BuildAnnotations(
                    attribute, methodAttribute, httpMethod, McpToolRegistry.Humanize(action.ActionName));

                McpToolOutputDescriptor? output;
                if (!McpToolOutputResolver.TrySelect(
                        methodOutputs, controllerOutputs, name, methodAttribute?.Name, display, _logger, matchedOutputs, out output))
                {
                    continue;
                }

                results.Add(new McpToolDescriptor(
                    name, httpMethod, routeTemplate, action, parameters,
                    McpToolRegistry.BuildInputSchema(parameters), annotations)
                {
                    Description = ResolveDescription(action, methodAttribute, controllerAttribute, httpMethod, routeTemplate),
                    ConstantRouteValues = new Dictionary<string, string?>(action.RouteValues, StringComparer.OrdinalIgnoreCase),
                    Constants = constants,
                    Authorization = McpToolRegistry.ResolveAuthorization(action),
                    Output = output,
                });
            }

            McpToolOutputResolver.WarnUnmatched(methodOutputs, matchedOutputs, display, _logger);

            return results;
        }

        /// <summary>
        /// Normalizes the OData route template and wraps string-typed tokens that sit inside a
        /// parenthesis group - a key or a function parameter - in the single quotes the OData
        /// literal syntax expects, so <c>Products({key})</c> with a string key renders as
        /// <c>Products('{key}')</c>.
        /// </summary>
        internal static string BuildRouteTemplate(ControllerActionDescriptor action)
        {
            var template = RouteTemplateHelper.Normalize(action.AttributeRouteInfo!.Template!);
            return QuoteStringTokens(template, name => IsStringParameter(action, name));
        }

        internal static string QuoteStringTokens(string template, Func<string, bool> isString)
        {
            var builder = new StringBuilder(template.Length + 8);
            var parenDepth = 0;

            for (var i = 0; i < template.Length; i++)
            {
                var c = template[i];
                if (c == '(')
                {
                    parenDepth++;
                }
                else if (c == ')')
                {
                    parenDepth--;
                }
                else if (c == '{')
                {
                    var close = template.IndexOf('}', i);
                    if (close < 0)
                    {
                        builder.Append(template, i, template.Length - i);
                        break;
                    }

                    var name = template.Substring(i + 1, close - i - 1);
                    var alreadyQuoted = i > 0 && template[i - 1] == '\'';
                    if (parenDepth > 0 && !alreadyQuoted && isString(name))
                    {
                        builder.Append('\'').Append('{').Append(name).Append('}').Append('\'');
                    }
                    else
                    {
                        builder.Append('{').Append(name).Append('}');
                    }

                    i = close;
                    continue;
                }

                builder.Append(c);
            }

            return builder.ToString();
        }

        private static bool IsStringParameter(ControllerActionDescriptor action, string tokenName)
        {
            foreach (var parameter in action.Parameters)
            {
                var bindingName = parameter.BindingInfo?.BinderModelName ?? parameter.Name;
                if (string.Equals(bindingName, tokenName, StringComparison.OrdinalIgnoreCase))
                {
                    return parameter.ParameterType == typeof(string);
                }
            }

            return false;
        }

        private List<McpToolParameterDescriptor> BuildParameters(
            ControllerActionDescriptor action,
            IODataRoutingMetadata metadata,
            string httpMethod,
            string routeTemplate)
        {
            var parameters = new List<McpToolParameterDescriptor>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var parameter in action.Parameters)
            {
                var controllerParameter = parameter as ControllerParameterDescriptor;
                var parameterInfo = controllerParameter?.ParameterInfo;

                if (parameterInfo?.GetCustomAttribute<McpIgnoreAttribute>() != null)
                {
                    continue;
                }

                if (IsIgnoredType(parameter.ParameterType))
                {
                    continue;
                }

                var source = parameter.BindingInfo?.BindingSource;
                if (source != null && (source.Id == BindingSource.Services.Id || source.Id == BindingSource.Special.Id))
                {
                    continue;
                }

                var bindingName = parameter.BindingInfo?.BinderModelName ?? parameter.Name;

                if (RouteTemplateHelper.ContainsToken(routeTemplate, bindingName))
                {
                    AddRouteParameter(action, parameter, parameterInfo, bindingName, parameters, seen);
                    continue;
                }

                if (typeof(ODataActionParameters).IsAssignableFrom(parameter.ParameterType) ||
                    typeof(ODataUntypedActionParameters).IsAssignableFrom(parameter.ParameterType))
                {
                    AddActionParametersParameter(action, parameter, parameterInfo, metadata, parameters, seen);
                    continue;
                }

                Type? deltaElement;
                if (IsDelta(parameter.ParameterType, out deltaElement))
                {
                    AddBodyParameters(action, parameter, parameterInfo, deltaElement!, metadata.Model, isDelta: true, parameters, seen);
                    continue;
                }

                var allowsBody = httpMethod == "POST" || httpMethod == "PUT" || httpMethod == "PATCH";
                var isBody = (source != null && source.Id == BindingSource.Body.Id) ||
                             (source == null && allowsBody && JsonSchemaGenerator.IsComplexObject(parameter.ParameterType));
                if (isBody)
                {
                    AddBodyParameters(action, parameter, parameterInfo, parameter.ParameterType, metadata.Model, isDelta: false, parameters, seen);
                    continue;
                }

                AddQueryParameter(action, parameter, parameterInfo, bindingName, parameters, seen);
            }

            AppendQueryOptions(action, httpMethod, parameters, seen);

            return parameters;
        }

        private void AddRouteParameter(
            ControllerActionDescriptor action,
            Microsoft.AspNetCore.Mvc.Abstractions.ParameterDescriptor parameter,
            ParameterInfo? parameterInfo,
            string bindingName,
            List<McpToolParameterDescriptor> parameters,
            ISet<string> seen)
        {
            var attributes = parameterInfo?.GetCustomAttributes().ToList() ?? new List<Attribute>();
            var mcpAttribute = attributes.OfType<McpParameterAttribute>().FirstOrDefault();
            var schema = _schemaGenerator.Generate(parameter.ParameterType, attributes);
            var description = ResolveParameterDescription(action, parameter.Name, mcpAttribute);
            if (!string.IsNullOrEmpty(description) && schema["description"] == null)
            {
                schema["description"] = description;
            }

            var name = Deduplicate(mcpAttribute?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(bindingName), seen);

            parameters.Add(new McpToolParameterDescriptor(name, bindingName, McpParameterSource.Route, parameter.ParameterType, true, schema)
            {
                Description = description,
            });
        }

        /// <summary>
        /// An <see cref="ODataActionParameters"/> parameter is the whole request body of an OData
        /// action invocation: a JSON object holding the action's named parameters.
        /// </summary>
        private void AddActionParametersParameter(
            ControllerActionDescriptor action,
            Microsoft.AspNetCore.Mvc.Abstractions.ParameterDescriptor parameter,
            ParameterInfo? parameterInfo,
            IODataRoutingMetadata metadata,
            List<McpToolParameterDescriptor> parameters,
            ISet<string> seen)
        {
            var mcpAttribute = parameterInfo?.GetCustomAttribute<McpParameterAttribute>();
            var schema = new JsonObject
            {
                ["type"] = "object",
                ["description"] = "The OData action's named parameters, as a JSON object of name/value pairs.",
                ["additionalProperties"] = new JsonObject(),
            };

            var description = ResolveParameterDescription(action, parameter.Name, mcpAttribute);
            if (!string.IsNullOrEmpty(description))
            {
                schema["description"] = description;
            }

            var name = Deduplicate(mcpAttribute?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(parameter.Name), seen);

            parameters.Add(new McpToolParameterDescriptor(name, parameter.Name, McpParameterSource.Body, parameter.ParameterType, true, schema)
            {
                IsBodyRoot = true,
                Description = description,
            });
        }

        /// <summary>
        /// Advertises an entity (or <c>Delta&lt;T&gt;</c>) request body. Property names are taken
        /// from the EDM model - that is what the OData deserializer matches on - and, mirroring the
        /// core package, a single complex body is flattened into top-level inputs when
        /// <see cref="NabuMcpOptions.FlattenBodyParameter"/> is on. A delta body advertises every
        /// property as optional: it is a partial update.
        /// </summary>
        private void AddBodyParameters(
            ControllerActionDescriptor action,
            Microsoft.AspNetCore.Mvc.Abstractions.ParameterDescriptor parameter,
            ParameterInfo? parameterInfo,
            Type payloadType,
            IEdmModel? model,
            bool isDelta,
            List<McpToolParameterDescriptor> parameters,
            ISet<string> seen)
        {
            var edmType = FindEdmStructuredType(model, payloadType);

            IList<JsonSchemaGenerator.ExpandedProperty> expanded;
            if (_coreOptions.FlattenBodyParameter && _schemaGenerator.TryExpandObject(payloadType, out expanded) && expanded.Count > 0)
            {
                foreach (var property in expanded)
                {
                    var edmProperty = edmType != null ? FindEdmProperty(edmType, property.Name) : null;
                    if (edmProperty != null)
                    {
                        var nested = UnwrapStructuredType(edmProperty.Type);
                        if (nested != null)
                        {
                            RenameToEdm(property.Schema["items"] as JsonObject ?? property.Schema, nested);
                        }
                    }

                    parameters.Add(new McpToolParameterDescriptor(
                        Deduplicate(property.Name, seen),
                        edmProperty?.Name ?? property.Name,
                        McpParameterSource.Body,
                        property.ClrType,
                        !isDelta && property.IsRequired,
                        property.Schema)
                    {
                        Description = property.Description,
                    });
                }

                return;
            }

            var attributes = parameterInfo?.GetCustomAttributes().ToList() ?? new List<Attribute>();
            var mcpAttribute = attributes.OfType<McpParameterAttribute>().FirstOrDefault();
            var schema = _schemaGenerator.Generate(payloadType, attributes);
            if (edmType != null)
            {
                RenameToEdm(schema, edmType);
            }

            if (isDelta)
            {
                schema.Remove("required");
            }

            var description = ResolveParameterDescription(action, parameter.Name, mcpAttribute)
                              ?? (isDelta ? "Partial update: supply only the properties to change." : null);
            if (!string.IsNullOrEmpty(description) && schema["description"] == null)
            {
                schema["description"] = description;
            }

            var name = Deduplicate(mcpAttribute?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(parameter.Name), seen);
            var nullable = parameterInfo != null ? NullabilityHelper.IsNullable(parameterInfo) : null;

            parameters.Add(new McpToolParameterDescriptor(name, parameter.Name, McpParameterSource.Body, payloadType, nullable != true, schema)
            {
                IsBodyRoot = true,
                Description = description,
            });
        }

        private void AddQueryParameter(
            ControllerActionDescriptor action,
            Microsoft.AspNetCore.Mvc.Abstractions.ParameterDescriptor parameter,
            ParameterInfo? parameterInfo,
            string bindingName,
            List<McpToolParameterDescriptor> parameters,
            ISet<string> seen)
        {
            var attributes = parameterInfo?.GetCustomAttributes().ToList() ?? new List<Attribute>();
            var mcpAttribute = attributes.OfType<McpParameterAttribute>().FirstOrDefault();
            var nullable = parameterInfo != null ? NullabilityHelper.IsNullable(parameterInfo) : null;
            var schema = _schemaGenerator.Generate(parameter.ParameterType, attributes, nullable);

            var description = ResolveParameterDescription(action, parameter.Name, mcpAttribute);
            if (!string.IsNullOrEmpty(description) && schema["description"] == null)
            {
                schema["description"] = description;
            }

            bool isRequired;
            if (mcpAttribute?.RequiredOverride != null)
            {
                isRequired = mcpAttribute.RequiredOverride.Value;
            }
            else if (attributes.Any(a => a is System.ComponentModel.DataAnnotations.RequiredAttribute || a is BindRequiredAttribute))
            {
                isRequired = true;
            }
            else if (parameterInfo?.HasDefaultValue == true)
            {
                isRequired = false;
            }
            else if (parameter.ParameterType.IsValueType && Nullable.GetUnderlyingType(parameter.ParameterType) == null)
            {
                // MVC model binding gives a missing non-nullable value type its default value.
                isRequired = false;
            }
            else
            {
                isRequired = nullable == false;
            }

            var name = Deduplicate(mcpAttribute?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(bindingName), seen);

            parameters.Add(new McpToolParameterDescriptor(name, bindingName, McpParameterSource.Query, parameter.ParameterType, isRequired, schema)
            {
                Description = description,
            });
        }

        /// <summary>
        /// Appends the OData query options a queryable GET action accepts, as optional tool inputs
        /// bound to the <c>$</c>-prefixed query keys.
        /// </summary>
        private void AppendQueryOptions(
            ControllerActionDescriptor action,
            string httpMethod,
            List<McpToolParameterDescriptor> parameters,
            ISet<string> seen)
        {
            if (httpMethod != "GET")
            {
                return;
            }

            var enableQuery = FindEnableQuery(action);
            var hasOptionsParameter = action.Parameters.Any(p => typeof(ODataQueryOptions).IsAssignableFrom(p.ParameterType));
            if (enableQuery == null && !hasOptionsParameter)
            {
                return;
            }

            var allowed = _options.QueryOptions;
            if (enableQuery != null)
            {
                allowed &= enableQuery.AllowedQueryOptions;
            }

            foreach (var definition in QueryOptionDefinitions)
            {
                if ((allowed & definition.Flag) == 0)
                {
                    continue;
                }

                var schema = new JsonObject { ["description"] = definition.Description };
                if (definition.ClrType == typeof(int))
                {
                    schema["type"] = "integer";
                    schema["minimum"] = 0;
                }
                else if (definition.ClrType == typeof(bool))
                {
                    schema["type"] = "boolean";
                }
                else
                {
                    schema["type"] = "string";
                }

                parameters.Add(new McpToolParameterDescriptor(
                    Deduplicate(definition.Name, seen),
                    definition.BindingName,
                    McpParameterSource.Query,
                    definition.ClrType,
                    false,
                    schema)
                {
                    Description = definition.Description,
                });
            }
        }

        internal static EnableQueryAttribute? FindEnableQuery(ControllerActionDescriptor action)
        {
            var fromMethod = action.MethodInfo.GetCustomAttributes(inherit: true).OfType<EnableQueryAttribute>().FirstOrDefault();
            if (fromMethod != null)
            {
                return fromMethod;
            }

            var fromController = action.ControllerTypeInfo.GetCustomAttributes(inherit: true).OfType<EnableQueryAttribute>().FirstOrDefault();
            if (fromController != null)
            {
                return fromController;
            }

            if (action.FilterDescriptors != null)
            {
                foreach (var filter in action.FilterDescriptors)
                {
                    if (filter.Filter is EnableQueryAttribute attribute)
                    {
                        return attribute;
                    }
                }
            }

            return null;
        }

        private static bool IsDelta(Type type, out Type? element)
        {
            element = null;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Delta<>))
            {
                element = type.GetGenericArguments()[0];
                return true;
            }

            return false;
        }

        private static bool IsIgnoredType(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            foreach (var ignored in IgnoredParameterTypes)
            {
                if (ignored.IsAssignableFrom(underlying))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Finds the EDM structured type behind a CLR payload type, preferring the CLR annotation
        /// the model builder stamps onto every mapped type and falling back to a full-name match.
        /// </summary>
        internal static IEdmStructuredType? FindEdmStructuredType(IEdmModel? model, Type clrType)
        {
            if (model == null)
            {
                return null;
            }

            foreach (var element in model.SchemaElements)
            {
                if (!(element is IEdmStructuredType structured))
                {
                    continue;
                }

                var annotation = model.GetAnnotationValue<ClrTypeAnnotation>(element);
                if (annotation?.ClrType == clrType)
                {
                    return structured;
                }
            }

            return model.FindDeclaredType(clrType.FullName ?? clrType.Name) as IEdmStructuredType;
        }

        private static IEdmProperty? FindEdmProperty(IEdmStructuredType type, string name)
        {
            foreach (var property in type.Properties())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return property;
                }
            }

            return null;
        }

        private static IEdmStructuredType? UnwrapStructuredType(IEdmTypeReference reference)
        {
            var definition = reference.Definition;
            if (definition is IEdmCollectionType collection)
            {
                definition = collection.ElementType.Definition;
            }

            return definition as IEdmStructuredType;
        }

        /// <summary>
        /// Rewrites a generated JSON schema's property names to the EDM names the OData
        /// deserializer matches on - <c>name</c> becomes <c>Name</c> unless the application enabled
        /// lower-camel-casing on its model. Nested complex and collection properties are rewritten
        /// recursively, and <c>required</c> lists follow the renames.
        /// </summary>
        internal static void RenameToEdm(JsonObject? schema, IEdmStructuredType edmType)
        {
            var properties = schema?["properties"] as JsonObject;
            if (schema == null || properties == null)
            {
                return;
            }

            var renames = new Dictionary<string, string>(StringComparer.Ordinal);
            var names = new List<string>(properties.Count);
            foreach (var pair in properties)
            {
                names.Add(pair.Key);
            }

            foreach (var name in names)
            {
                var edmProperty = FindEdmProperty(edmType, name);
                if (edmProperty == null)
                {
                    continue;
                }

                var node = properties[name];

                if (!string.Equals(edmProperty.Name, name, StringComparison.Ordinal))
                {
                    properties.Remove(name);
                    properties[edmProperty.Name] = node;
                    renames[name] = edmProperty.Name;
                }

                var nested = UnwrapStructuredType(edmProperty.Type);
                if (nested != null && node is JsonObject nodeObject)
                {
                    RenameToEdm(nodeObject["items"] as JsonObject ?? nodeObject, nested);
                }
            }

            if (renames.Count > 0 && schema["required"] is JsonArray required)
            {
                for (var i = 0; i < required.Count; i++)
                {
                    var value = required[i]?.GetValue<string>();
                    string? renamed;
                    if (value != null && renames.TryGetValue(value, out renamed))
                    {
                        required[i] = renamed;
                    }
                }
            }
        }

        private string? ResolveParameterDescription(ControllerActionDescriptor action, string parameterName, McpParameterAttribute? attribute)
        {
            return attribute?.Description ?? _documentation.GetParameterDescription(action.MethodInfo, parameterName);
        }

        private string? ResolveDescription(
            ControllerActionDescriptor action,
            McpToolAttribute? methodAttribute,
            McpToolAttribute? controllerAttribute,
            string httpMethod,
            string routeTemplate)
        {
            if (!string.IsNullOrEmpty(methodAttribute?.Description))
            {
                return methodAttribute!.Description;
            }

            var summary = _documentation.GetSummary(action.MethodInfo);
            if (!string.IsNullOrEmpty(summary))
            {
                var returns = _documentation.GetReturnsDescription(action.MethodInfo);
                return string.IsNullOrEmpty(returns) ? summary : summary + " Returns: " + returns;
            }

            if (!string.IsNullOrEmpty(controllerAttribute?.Description))
            {
                return controllerAttribute!.Description;
            }

            return "Invokes " + httpMethod + " /" + routeTemplate + " on the " + action.ControllerName + " OData API.";
        }

        private string ResolveName(
            ControllerActionDescriptor action,
            McpToolAttribute? methodAttribute,
            string httpMethod,
            string routeTemplate,
            ISet<string> usedNames)
        {
            var name = methodAttribute?.Name;
            var generated = string.IsNullOrEmpty(name);
            if (generated)
            {
                var context = new McpToolNamingContext(action.ControllerName, action.ActionName, httpMethod, routeTemplate);
                name = _coreOptions.ToolNameFactory(context);
            }

            name = McpToolRegistry.Sanitize(name!);
            if (name.Length == 0)
            {
                name = "tool";
            }

            if (usedNames.Add(name))
            {
                return name;
            }

            // OData overloads collide by design - Get() and Get(key) share a generated name - so
            // before falling back to a numeric suffix, disambiguate by the route tokens: the keyed
            // overload becomes products_get_by_key.
            if (generated)
            {
                var tokens = RouteTemplateHelper.GetTokenNames(routeTemplate);
                if (tokens.Count > 0)
                {
                    var candidate = McpToolRegistry.Sanitize(
                        name + "_by_" + string.Join("_", tokens.Select(NabuMcpOptions.ToSnakeCase)));
                    if (usedNames.Add(candidate))
                    {
                        return candidate;
                    }
                }
            }

            var suffix = 2;
            string numbered;
            do
            {
                numbered = name + "_" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
                suffix++;
            }
            while (!usedNames.Add(numbered));

            _logger.LogWarning(
                "Nabu MCP tool name '{Name}' was already taken; {Controller}.{Action} is exposed as '{Candidate}'. Give it an explicit [McpTool(Name = ...)].",
                name,
                action.ControllerName,
                action.ActionName,
                numbered);

            return numbered;
        }

        private static string Deduplicate(string name, ISet<string> seen)
        {
            if (seen.Add(name))
            {
                return name;
            }

            var suffix = 2;
            string candidate;
            do
            {
                candidate = name + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
                suffix++;
            }
            while (!seen.Add(candidate));

            return candidate;
        }

        private readonly struct QueryOptionDefinition
        {
            public QueryOptionDefinition(AllowedQueryOptions flag, string name, string bindingName, Type clrType, string description)
            {
                Flag = flag;
                Name = name;
                BindingName = bindingName;
                ClrType = clrType;
                Description = description;
            }

            public AllowedQueryOptions Flag { get; }

            public string Name { get; }

            public string BindingName { get; }

            public Type ClrType { get; }

            public string Description { get; }
        }
    }
}
