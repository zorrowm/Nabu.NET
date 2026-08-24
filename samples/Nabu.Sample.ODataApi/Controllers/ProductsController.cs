using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.AspNetCore.OData.Formatter;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Routing.Controllers;
using Nabu.Mcp.AspNetCore;
using Nabu.Sample.ODataApi.Models;
using Nabu.Sample.ODataApi.Services;

namespace Nabu.Sample.ODataApi.Controllers
{
    /// <summary>
    /// The OData entity set behind <c>/odata/Products</c>. Each action carries a plain
    /// <c>[McpTool]</c>, exactly as a REST controller would; the OData query options
    /// (<c>$filter</c>, <c>$select</c>, ...) are advertised automatically on the queryable
    /// actions, and <c>[McpToolOutput]</c> keeps the internal purchase price out of every tool
    /// result.
    /// </summary>
    public class ProductsController : ODataController
    {
        private readonly ProductCatalog _catalog;

        public ProductsController(ProductCatalog catalog)
        {
            _catalog = catalog;
        }

        /// <summary>Queries the product catalogue.</summary>
        [EnableQuery]
        [McpTool(Name = "products_query", Description =
            "Queries the product catalogue with OData query options: filter, select, orderby, expand, top, skip and count.")]
        [McpToolOutput(ExcludeFields = new[] { "value.CostPrice", "CostPrice" })]
        public IQueryable<Product> Get()
        {
            return _catalog.Query();
        }

        /// <summary>Reads one product by its identifier.</summary>
        [EnableQuery]
        [McpTool(Name = "products_get")]
        [McpToolOutput(ExcludeFields = new[] { "CostPrice" })]
        public IActionResult Get(int key)
        {
            var product = _catalog.Find(key);
            return product == null ? (IActionResult)NotFound() : Ok(product);
        }

        /// <summary>Adds a product to the catalogue. Requires a signed-in caller.</summary>
        [Authorize]
        [McpTool(Name = "products_create")]
        [McpToolOutput(ExcludeFields = new[] { "CostPrice" })]
        public IActionResult Post([FromBody] Product product)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            return Created(_catalog.Add(product));
        }

        /// <summary>Updates parts of a product. Requires a signed-in caller.</summary>
        [Authorize]
        [McpTool(Name = "products_update")]
        [McpToolOutput(ExcludeFields = new[] { "CostPrice" })]
        public IActionResult Patch(int key, Delta<Product> delta)
        {
            var product = _catalog.Find(key);
            if (product == null)
            {
                return NotFound();
            }

            delta.Patch(product);
            return Updated(product);
        }

        /// <summary>Removes a product from the catalogue. Requires an administrator.</summary>
        [Authorize(Roles = "admin")]
        [McpTool(Name = "products_delete")]
        public IActionResult Delete(int key)
        {
            return _catalog.Remove(key) ? (IActionResult)NoContent() : NotFound();
        }

        /// <summary>Rates a product; the OData action recomputes and returns its average rating.</summary>
        [HttpPost]
        [McpTool(Name = "products_rate")]
        public IActionResult Rate(int key, ODataActionParameters parameters)
        {
            var product = _catalog.Find(key);
            if (product == null)
            {
                return NotFound();
            }

            if (parameters == null || !parameters.TryGetValue("stars", out var value) || !(value is int stars) || stars < 1 || stars > 5)
            {
                return BadRequest("The 'stars' parameter must be an integer between 1 and 5.");
            }

            product.Rating = product.Rating <= 0 ? stars : (product.Rating + stars) / 2.0;
            return Ok(product.Rating);
        }

        /// <summary>Returns the highest listed price in the catalogue.</summary>
        [HttpGet]
        [McpTool(Name = "products_most_expensive")]
        public IActionResult MostExpensive()
        {
            return Ok(_catalog.Query().Max(p => p.Price));
        }
    }
}
