using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Raffinert.Consistency.EntityFrameworkCore;

namespace Raffinert.Consistency.Tests;

public sealed class NavigationScopeEfCoreTests
{
    [Fact]
    public void Nested_navigation_incomplete_runtime_cannot_make_false_valid_claim()
    {
        using var database = new NavigationDatabase(); using var context = database.CreateContext();
        var product = new Product { CurrentLimit = 100 };
        var safe = new OrderLine { Product = product, ReservedQuantity = 60 };
        var violating = new OrderLine { Product = product, ReservedQuantity = 120 };
        context.AddRange(product, safe, violating); context.SaveChanges();
        var model = CreateLimitModel(out var products, out var lines, out var invariant);
        var compiled = model.Build();
        var partial = compiled.CreateRuntime(seed => { seed.Add(products, [product]); seed.Add(lines, [safe]); });
        var authoritative = compiled.CreateRuntime(seed => { seed.Add(products, [product]); seed.Add(lines, [safe, violating]); });
        var mappings = new ConsistencyEfCoreMappings().Map(products).Map(lines).Enforce(invariant);
        product.CurrentLimit = 80;

        var error = Assert.Throws<IncompleteConsistencyScopeException>(() =>
            context.SaveChangesConsistently(partial, mappings));
        var gap = Assert.Single(error.Gaps);
        Assert.Equal(typeof(OrderLine), gap.ObjectType);
        Assert.Equal(ConsistencyScopeRequirementKind.NavigationConsumerCoverage, gap.RequirementKind);
        Assert.Equal(100, database.CreateContext().Products.AsNoTracking().Single().CurrentLimit);
        Assert.Equal(0, partial.Version);

        Assert.Throws<ConsistencyInvariantViolationException>(() =>
            context.SaveChangesConsistently(authoritative, mappings,
                new ConsistencySaveOptions { Scope = new ConsistencyScope().Complete(lines) }));
        Assert.Equal(100, database.CreateContext().Products.AsNoTracking().Single().CurrentLimit);
        Assert.Equal(0, authoritative.Version);
    }

    [Fact]
    public void Nested_navigation_incomplete_runtime_cannot_persist_false_mirror()
    {
        using var database = new NavigationDatabase(); using var context = database.CreateContext();
        var product = new Product { Price = 10 };
        var first = new OrderLine { Product = product };
        var second = new OrderLine { Product = product };
        context.AddRange(product, first, second); context.SaveChanges();
        var model = new ConsistencyModelBuilder();
        var products = model.Objects<Product>().Key(x => x.Id);
        var lines = model.Objects<OrderLine>().Key(x => x.Id);
        var currentPrice = model.Derived(lines).Compute(line => line.Product.Price);
        var compiled = model.Build();
        var partial = compiled.CreateRuntime(seed => { seed.Add(products, [product]); seed.Add(lines, [first]); });
        var authoritative = compiled.CreateRuntime(seed => { seed.Add(products, [product]); seed.Add(lines, [first, second]); });
        var mappings = new ConsistencyEfCoreMappings().Map(products).Map(lines)
            .Materialize(currentPrice, line => line.CurrentPriceMirror);
        product.Price = 25;

        Assert.Throws<IncompleteConsistencyScopeException>(() =>
            context.SaveChangesConsistently(partial, mappings));
        Assert.Equal(0, first.MirrorWrites);
        Assert.Equal([0, 0], database.CreateContext().Lines.AsNoTracking()
            .OrderBy(line => line.Id).Select(line => line.CurrentPriceMirror).ToArray());

        context.SaveChangesConsistently(authoritative, mappings,
            new ConsistencySaveOptions { Scope = new ConsistencyScope().Complete(lines) });
        Assert.Equal([25, 25], database.CreateContext().Lines.AsNoTracking()
            .OrderBy(line => line.Id).Select(line => line.CurrentPriceMirror).ToArray());
    }

    [Fact]
    public void Interceptor_and_extension_report_same_navigation_scope_gap()
    {
        using var database = new NavigationDatabase();
        using (var seed = database.CreateContext())
        {
            var product = new Product { CurrentLimit = 100 };
            seed.AddRange(product, new OrderLine { Product = product, ReservedQuantity = 60 });
            seed.SaveChanges();
        }
        var model = CreateLimitModel(out var products, out var lines, out var invariant);
        var compiled = model.Build();
        var mappings = new ConsistencyEfCoreMappings().Map(products).Map(lines).Enforce(invariant);
        IncompleteConsistencyScopeException extension;
        using (var context = database.CreateContext())
        {
            var line = context.Lines.Include(x => x.Product).Single();
            var runtime = compiled.CreateRuntime(seed => { seed.Add(products, [line.Product]); seed.Add(lines, [line]); });
            line.Product.CurrentLimit = 80;
            extension = Assert.Throws<IncompleteConsistencyScopeException>(() =>
                context.SaveChangesConsistently(runtime, mappings));
        }

        using var loaded = database.CreateContext();
        var interceptedLine = loaded.Lines.Include(x => x.Product).Single();
        var interceptedRuntime = compiled.CreateRuntime(seed =>
        {
            seed.Add(products, [interceptedLine.Product]); seed.Add(lines, [interceptedLine]);
        });
        var interceptor = new ConsistencySaveChangesInterceptor(interceptedRuntime, mappings, new());
        using var contextWithInterceptor = database.CreateContext(interceptor);
        contextWithInterceptor.Attach(interceptedLine);
        contextWithInterceptor.Attach(interceptedLine.Product);
        interceptedLine.Product.CurrentLimit = 80;

        var error = Assert.Throws<IncompleteConsistencyScopeException>(() => contextWithInterceptor.SaveChanges());
        Assert.Equal(extension.Gaps, error.Gaps);
    }

    private static ConsistencyModelBuilder CreateLimitModel(out ObjectSet<Product> products,
        out ObjectSet<OrderLine> lines, out Invariant<OrderLine> invariant)
    {
        var model = new ConsistencyModelBuilder();
        products = model.Objects<Product>().Key(x => x.Id);
        lines = model.Objects<OrderLine>().Key(x => x.Id);
        var limit = model.Derived(lines).Compute(line => line.Product.CurrentLimit);
        invariant = model.Invariant(lines).Using(limit).Must((line, value) => line.ReservedQuantity <= value);
        return model;
    }

    private sealed class NavigationDatabase : IDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        public NavigationDatabase() { _connection.Open(); using var context = CreateContext(); context.Database.EnsureCreated(); }
        public NavigationContext CreateContext(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) => new(_connection, interceptors);
        public void Dispose() => _connection.Dispose();
    }

    private sealed class NavigationContext(SqliteConnection connection,
        Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) : DbContext
    {
        public DbSet<Product> Products => Set<Product>();
        public DbSet<OrderLine> Lines => Set<OrderLine>();
        protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseSqlite(connection).AddInterceptors(interceptors);
        protected override void OnModelCreating(ModelBuilder model) =>
            model.Entity<OrderLine>().HasOne(line => line.Product).WithMany().HasForeignKey(line => line.ProductId);
    }

    private sealed class Product { public int Id { get; set; } public int CurrentLimit { get; set; } public int Price { get; set; } }
    private sealed class OrderLine
    {
        private int _mirror;
        public int Id { get; set; }
        public int ProductId { get; set; }
        public Product Product { get; set; } = null!;
        public int ReservedQuantity { get; set; }
        public int CurrentPriceMirror { get => _mirror; set { _mirror = value; MirrorWrites++; } }
        public int MirrorWrites { get; private set; }
    }
}
