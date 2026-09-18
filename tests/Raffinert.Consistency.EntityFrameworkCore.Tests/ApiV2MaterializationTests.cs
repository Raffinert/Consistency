using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Raffinert.Consistency.EntityFrameworkCore.Tests;

public sealed class ApiV2MaterializationTests
{
    [Fact]
    public void Evaluate_and_Core_materialize_have_expected_EF_tracking_scope()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<ApiV2Context>().UseSqlite(connection).Options;
        using var context = new ApiV2Context(options);
        context.Database.EnsureCreated();
        var entity = new ApiV2Entity
        {
            Id = 1,
            Input = 60m,
            PriceMirror = 6m,
            UnitMirror = 6m,
            Unrelated = "preserved"
        };
        context.Add(entity);
        context.SaveChanges();

        var model = new ConsistencyModelBuilder();
        var objects = model.Objects<ApiV2Entity>().Key(x => x.Id);
        var price = model.Derived(objects).Select(x => x.Input / 10m)
            .MaterializeTo(x => x.PriceMirror).Named("price");
        var unit = model.Derived(objects).From(price).Select((_, value) => value)
            .MaterializeTo(x => x.UnitMirror).Named("unit");
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        var mappings = new ConsistencyEfCoreMappings().Map(objects);
        _ = mappings.Validate(context, runtime);
        Assert.Equal(2, mappings.Materializations.Count);

        entity.Input = 55m;
        runtime.Apply(Change.Property(objects, entity, x => x.Input, 60m, 55m));
        Assert.Equal(5.5m, runtime.Evaluate(price, entity));
        context.ChangeTracker.DetectChanges();
        Assert.False(context.Entry(entity).Property(x => x.PriceMirror).IsModified);
        Assert.False(context.Entry(entity).Property(x => x.UnitMirror).IsModified);

        Assert.Equal(5.5m, runtime.Materialize(price, entity));
        context.ChangeTracker.DetectChanges();
        Assert.True(context.Entry(entity).Property(x => x.PriceMirror).IsModified);
        Assert.False(context.Entry(entity).Property(x => x.UnitMirror).IsModified);
        Assert.False(context.Entry(entity).Property(x => x.Unrelated).IsModified);

        entity.PriceMirror = 6m;
        entity.UnitMirror = 6m;
        context.ChangeTracker.AcceptAllChanges();
        runtime.Materialize(entity);
        context.ChangeTracker.DetectChanges();
        Assert.True(context.Entry(entity).Property(x => x.PriceMirror).IsModified);
        Assert.True(context.Entry(entity).Property(x => x.UnitMirror).IsModified);
        Assert.False(context.Entry(entity).Property(x => x.Unrelated).IsModified);

        context.ChangeTracker.AcceptAllChanges();
        var priceWrites = entity.PriceWrites;
        var unitWrites = entity.UnitWrites;
        runtime.Materialize(entity);
        context.ChangeTracker.DetectChanges();
        Assert.Equal(priceWrites, entity.PriceWrites);
        Assert.Equal(unitWrites, entity.UnitWrites);
        Assert.False(context.Entry(entity).Property(x => x.PriceMirror).IsModified);
        Assert.False(context.Entry(entity).Property(x => x.UnitMirror).IsModified);
    }

    [Fact]
    public void Consistent_save_materializes_old_mapping_and_Core_descriptor_with_parity()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<ApiV2Context>().UseSqlite(connection).Options;
        using var context = new ApiV2Context(options);
        context.Database.EnsureCreated();
        var entity = new ApiV2Entity
        {
            Id = 1,
            Input = 2m,
            PriceMirror = 4m,
            OldMirror = 4m,
            Unrelated = "preserved"
        };
        context.Add(entity);
        context.SaveChanges();

        var model = new ConsistencyModelBuilder();
        var objects = model.Objects<ApiV2Entity>().Key(x => x.Id);
        var oldValue = model.Derived(objects).Select(x => x.Input * 2m).Named("old-value").MaterializeTo(x => x.OldMirror);
        _ = model.Derived(objects).Select(x => x.Input * 2m)
            .MaterializeTo(x => x.PriceMirror).Named("v2-value");
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        var mappings = new ConsistencyEfCoreMappings()
            .Map(objects)
            ;

        entity.Input = 3m;
        context.SaveChangesConsistently(runtime, mappings);

        Assert.Equal(6m, entity.OldMirror);
        Assert.Equal(6m, entity.PriceMirror);
        Assert.Equal("preserved", entity.Unrelated);
        Assert.Equal(2, mappings.Materializations.Count);
        var persisted = context.Set<ApiV2Entity>().AsNoTracking().Single();
        Assert.Equal(6m, persisted.OldMirror);
        Assert.Equal(6m, persisted.PriceMirror);
        Assert.Equal("preserved", persisted.Unrelated);
    }

    [Fact]
    public void Object_materialize_rolls_back_tracked_physical_values_when_a_setter_fails()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<ApiV2Context>().UseSqlite(connection).Options;
        using var context = new ApiV2Context(options);
        context.Database.EnsureCreated();
        var entity = new ApiV2Entity
        {
            Id = 1,
            Input = 2m,
            PriceMirror = 2m,
            UnitMirror = 2m
        };
        context.Add(entity);
        context.SaveChanges();

        var model = new ConsistencyModelBuilder();
        var objects = model.Objects<ApiV2Entity>().Key(x => x.Id);
        _ = model.Derived(objects).Select(x => x.Input)
            .MaterializeTo(x => x.PriceMirror);
        _ = model.Derived(objects).Select(x => x.Input)
            .MaterializeTo(x => x.UnitMirror);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        entity.Input = 3m;
        runtime.Apply(Change.Property(objects, entity, x => x.Input, 2m, 3m));
        entity.ThrowUnitWrites = true;

        Assert.Throws<InvalidOperationException>(() => runtime.Materialize(entity));
        Assert.Equal(2m, entity.PriceMirror);
        Assert.Equal(2m, entity.UnitMirror);
        context.ChangeTracker.DetectChanges();
        Assert.False(context.Entry(entity).Property(x => x.PriceMirror).IsModified);
        Assert.False(context.Entry(entity).Property(x => x.UnitMirror).IsModified);
    }

    private sealed class ApiV2Context(DbContextOptions<ApiV2Context> options) : DbContext(options)
    {
        public DbSet<ApiV2Entity> Entities => Set<ApiV2Entity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ApiV2Entity>().HasKey(x => x.Id);
            modelBuilder.Entity<ApiV2Entity>().Ignore(x => x.PriceWrites);
            modelBuilder.Entity<ApiV2Entity>().Ignore(x => x.UnitWrites);
            modelBuilder.Entity<ApiV2Entity>().Ignore(x => x.ThrowUnitWrites);
        }
    }

    private sealed class ApiV2Entity
    {
        private decimal _priceMirror;
        private decimal _unitMirror;

        public int Id { get; init; }
        public decimal Input { get; set; }
        public decimal PriceMirror
        {
            get => _priceMirror;
            set { _priceMirror = value; PriceWrites++; }
        }
        public decimal UnitMirror
        {
            get => _unitMirror;
            set
            {
                if (ThrowUnitWrites)
                    throw new InvalidOperationException("Injected UnitMirror setter failure.");
                _unitMirror = value;
                UnitWrites++;
            }
        }
        public decimal OldMirror { get; set; }
        public string Unrelated { get; set; } = string.Empty;
        public int PriceWrites { get; private set; }
        public int UnitWrites { get; private set; }
        public bool ThrowUnitWrites { get; set; }
    }
}
