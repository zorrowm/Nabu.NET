using System.ComponentModel.DataAnnotations;

namespace Nabu.Sample.ODataApi.Models
{
    /// <summary>One product in the catalogue.</summary>
    public class Product
    {
        /// <summary>Server-assigned identifier.</summary>
        public int Id { get; set; }

        /// <summary>Display name.</summary>
        [Required]
        public string Name { get; set; } = string.Empty;

        /// <summary>Catalogue category, for example "peripherals".</summary>
        public string Category { get; set; } = string.Empty;

        /// <summary>Listed sales price.</summary>
        public decimal Price { get; set; }

        /// <summary>
        /// Internal purchase price. Never shown to MCP callers - the tools exclude it through
        /// <c>[McpToolOutput]</c>.
        /// </summary>
        public decimal CostPrice { get; set; }

        /// <summary>Average customer rating, 0 when unrated.</summary>
        public double Rating { get; set; }
    }

    public sealed class LoginRequest
    {
        [Required]
        public string Username { get; set; } = string.Empty;

        [Required]
        public string Password { get; set; } = string.Empty;
    }

    public sealed class LoginResponse
    {
        public string AccessToken { get; set; } = string.Empty;

        public int ExpiresInSeconds { get; set; }
    }
}
