using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OData;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OData.ModelBuilder;
using Nabu.Mcp.AspNetCore;
using Nabu.Mcp.AspNetCore.OData;
using Nabu.Sample.ODataApi.Models;
using Nabu.Sample.ODataApi.Services;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// A perfectly ordinary OData 8/9 setup: an EDM model, AddOData() with a route
// prefix, and an ODataController per entity set.
// ---------------------------------------------------------------------------
var modelBuilder = new ODataConventionModelBuilder();
modelBuilder.EntitySet<Product>("Products");
var rate = modelBuilder.EntityType<Product>().Action("Rate");
rate.Parameter<int>("stars");
rate.Returns<double>();
modelBuilder.EntityType<Product>().Collection.Function("MostExpensive").Returns<decimal>();
var edmModel = modelBuilder.GetEdmModel();

builder.Services
    .AddControllers()
    .AddOData(options => options
        .EnableQueryFeatures()
        .AddRouteComponents("odata", edmModel));

builder.Services.AddSingleton<ProductCatalog>();
builder.Services.AddSingleton<TokenService>();

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
        };
    });

builder.Services.AddAuthorization();

// ---------------------------------------------------------------------------
// The MCP setup: AddNabuMcp() as always, plus AddNabuMcpOData() to publish the
// [McpTool]-annotated OData actions into the same catalogue. A tool call is
// replayed as a real HTTP request through the pipeline below, so [Authorize],
// [EnableQuery] and the OData formatters behave exactly as they do for
// /odata/Products requests - and the queryable tools additionally accept
// filter/select/orderby/top/skip/count arguments.
// ---------------------------------------------------------------------------
builder.Services.AddNabuMcp(options =>
{
    options.ServerName = "nabu-odata-sample";
    options.ServerVersion = "1.0.0";
    options.Path = "/mcp";
    options.Instructions =
        "Product catalogue tools backed by OData. Querying is open to everyone; creating and " +
        "updating products requires a signed-in caller, and deleting one requires an administrator.";

    options.RequireAuthorization = true;
    options.AnonymousAccess = McpAnonymousAccess.AnonymousTools;
    options.ToolVisibility = McpToolVisibility.Authorized;
});

builder.Services.AddNabuMcpOData();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Mounted after authentication so the MCP endpoint sees the caller's identity.
app.UseNabuMcp();

app.MapControllers();

app.Run();

/// <summary>Exposed so the integration tests can host the application with WebApplicationFactory.</summary>
public partial class Program
{
}
