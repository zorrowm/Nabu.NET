using System.Collections.Generic;
using System.Linq;
using Nabu.Sample.ODataApi.Models;

namespace Nabu.Sample.ODataApi.Services
{
    /// <summary>In-memory product store. A real application would use a database and EF Core.</summary>
    public sealed class ProductCatalog
    {
        private readonly object _sync = new object();
        private readonly List<Product> _products;
        private int _nextId;

        public ProductCatalog()
        {
            _products = new List<Product>
            {
                new Product { Id = 1, Name = "Keyboard", Category = "peripherals", Price = 49m, CostPrice = 18m, Rating = 4.2 },
                new Product { Id = 2, Name = "Mouse", Category = "peripherals", Price = 19m, CostPrice = 6m, Rating = 4.6 },
                new Product { Id = 3, Name = "Monitor", Category = "displays", Price = 249m, CostPrice = 140m, Rating = 4.4 },
                new Product { Id = 4, Name = "Dock", Category = "accessories", Price = 129m, CostPrice = 65m, Rating = 3.9 },
            };
            _nextId = 5;
        }

        public IQueryable<Product> Query()
        {
            lock (_sync)
            {
                return _products.ToList().AsQueryable();
            }
        }

        public Product? Find(int id)
        {
            lock (_sync)
            {
                return _products.FirstOrDefault(p => p.Id == id);
            }
        }

        public Product Add(Product product)
        {
            lock (_sync)
            {
                product.Id = _nextId++;
                _products.Add(product);
                return product;
            }
        }

        public bool Remove(int id)
        {
            lock (_sync)
            {
                return _products.RemoveAll(p => p.Id == id) > 0;
            }
        }
    }
}
