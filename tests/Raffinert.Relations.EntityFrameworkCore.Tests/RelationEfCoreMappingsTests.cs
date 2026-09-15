using Microsoft.EntityFrameworkCore;
using Raffinert.Relations.EntityFrameworkCore;

namespace Raffinert.Relations.Tests;

public sealed class RelationEfCoreMappingsTests
{
    [Fact]
    public void Enforce_accepts_unnamed_invariant()
    {
        using var context = new MappingContext();
        var entity = Seed(context);
        var model = new RelationModelBuilder(); var objects = model.Objects<MappingEntity>().Key(x => x.Id);
        var value = model.Derived(objects).Compute(x => x.Input);
        var invariant = model.Invariant(objects).Using(value).Must((_, current) => current < 2);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        entity.Input = 2;

        Assert.Throws<RelationInvariantViolationException>(() => context.SaveChangesConsistently(runtime,
            new RelationEfCoreMappings().Map(objects).Enforce(invariant)));
    }

    [Fact]
    public void Materialize_accepts_normal_mapped_writable_property()
    {
        using var context = new MappingContext(); var entity = Seed(context);
        var model = new RelationModelBuilder(); var objects = model.Objects<MappingEntity>().Key(x => x.Id);
        var value = model.Derived(objects).Compute(x => x.Input * 2);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        entity.Input = 3;

        context.SaveChangesConsistently(runtime,
            new RelationEfCoreMappings().Map(objects).Materialize(value, x => x.Mirror));

        Assert.Equal(6, entity.Mirror);
    }

    [Theory]
    [InlineData(nameof(MappingEntity.Id))]
    [InlineData(nameof(MappingEntity.Alternate))]
    [InlineData(nameof(MappingEntity.Generated))]
    [InlineData(nameof(MappingEntity.Unmapped))]
    public void Materialize_rejects_invalid_ef_property(string propertyName)
    {
        using var context = new MappingContext(); var entity = Seed(context);
        var model = new RelationModelBuilder(); var objects = model.Objects<MappingEntity>().Key(x => x.Id);
        var value = model.Derived(objects).Compute(x => x.Input);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        entity.Input = 2;
        var mappings = new RelationEfCoreMappings().Map(objects);
        if (propertyName == nameof(MappingEntity.Id)) mappings.Materialize(value, x => x.Id);
        else if (propertyName == nameof(MappingEntity.Alternate)) mappings.Materialize(value, x => x.Alternate);
        else if (propertyName == nameof(MappingEntity.Generated)) mappings.Materialize(value, x => x.Generated);
        else mappings.Materialize(value, x => x.Unmapped);

        Assert.Throws<InvalidOperationException>(() => context.SaveChangesConsistently(runtime, mappings));
    }

    [Fact]
    public void Materialize_rejects_relations_dependency_member()
    {
        using var context = new MappingContext(); var entity = Seed(context);
        var model = new RelationModelBuilder(); var objects = model.Objects<MappingEntity>().Key(x => x.Id);
        var value = model.Derived(objects).Compute(x => x.Input);
        model.Derived(objects).Compute(x => x.Mirror + 1);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        entity.Input = 2;

        Assert.Throws<InvalidOperationException>(() => context.SaveChangesConsistently(runtime,
            new RelationEfCoreMappings().Map(objects).Materialize(value, x => x.Mirror)));
    }

    [Fact]
    public void Materialize_rejects_duplicate_target_and_duplicate_derived()
    {
        var model = new RelationModelBuilder(); var objects = model.Objects<MappingEntity>().Key(x => x.Id);
        var first = model.Derived(objects).Compute(x => x.Input);
        var second = model.Derived(objects).Compute(x => x.Input + 1);
        var mappings = new RelationEfCoreMappings().Materialize(first, x => x.Mirror);

        Assert.Throws<InvalidOperationException>(() => mappings.Materialize(second, x => x.Mirror));
        Assert.Throws<InvalidOperationException>(() => mappings.Materialize(first, x => x.OtherMirror));
    }

    [Fact]
    public void Materialize_rejects_handle_from_another_runtime()
    {
        using var context = new MappingContext(); var entity = Seed(context);
        var firstModel = new RelationModelBuilder(); var firstObjects = firstModel.Objects<MappingEntity>().Key(x => x.Id);
        var foreign = firstModel.Derived(firstObjects).Compute(x => x.Input);
        firstModel.Build();
        var secondModel = new RelationModelBuilder(); var secondObjects = secondModel.Objects<MappingEntity>().Key(x => x.Id);
        var runtime = secondModel.Build().CreateRuntime(seed => seed.Add(secondObjects, [entity]));
        entity.Input = 2;

        Assert.Throws<ArgumentException>(() => context.SaveChangesConsistently(runtime,
            new RelationEfCoreMappings().Map(secondObjects).Materialize(foreign, x => x.Mirror)));
    }

    private static MappingEntity Seed(MappingContext context)
    {
        var entity = new MappingEntity { Id = 1, Alternate = 10, Input = 1 };
        context.Add(entity); context.SaveChanges(); return entity;
    }

    private sealed class MappingContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options) =>
            options.UseInMemoryDatabase($"mapping-{Guid.NewGuid()}");
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<MappingEntity>().HasAlternateKey(x => x.Alternate);
            model.Entity<MappingEntity>().Property(x => x.Generated).ValueGeneratedOnAdd();
            model.Entity<MappingEntity>().Ignore(x => x.Unmapped);
        }
    }

    private sealed class MappingEntity
    {
        public int Id { get; set; }
        public int Alternate { get; set; }
        public int Generated { get; set; }
        public int Input { get; set; }
        public int Mirror { get; set; }
        public int OtherMirror { get; set; }
        public int Unmapped { get; set; }
    }
}
