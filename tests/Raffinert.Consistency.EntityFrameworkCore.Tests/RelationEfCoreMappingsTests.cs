using Microsoft.EntityFrameworkCore;
using Raffinert.Consistency.EntityFrameworkCore;

namespace Raffinert.Consistency.Tests;

public sealed class RelationEfCoreMappingsTests
{
    [Fact]
    public void Enforce_accepts_unnamed_invariant()
    {
        using var context = new MappingContext();
        var entity = Seed(context);
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MappingEntity>().Key(x => x.Id);
        var value = model.Derived(objects).Compute(x => x.Input);
        var invariant = model.Invariant(objects).Using(value).Must((_, current) => current < 2);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        entity.Input = 2;

        Assert.Throws<ConsistencyInvariantViolationException>(() => context.SaveChangesConsistently(runtime,
            new ConsistencyEfCoreMappings().Map(objects).Enforce(invariant)));
    }

    [Fact]
    public void Materialize_accepts_normal_mapped_writable_property()
    {
        using var context = new MappingContext(); var entity = Seed(context);
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MappingEntity>().Key(x => x.Id);
        var value = model.Derived(objects).Compute(x => x.Input * 2);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        entity.Input = 3;

        context.SaveChangesConsistently(runtime,
            new ConsistencyEfCoreMappings().Map(objects).Materialize(value, x => x.Mirror));

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
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MappingEntity>().Key(x => x.Id);
        var value = model.Derived(objects).Compute(x => x.Input);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        entity.Input = 2;
        var mappings = new ConsistencyEfCoreMappings().Map(objects);
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
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MappingEntity>().Key(x => x.Id);
        var value = model.Derived(objects).Compute(x => x.Input);
        model.Derived(objects).Compute(x => x.Mirror + 1);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        entity.Input = 2;

        var error = Assert.Throws<InvalidOperationException>(() => context.SaveChangesConsistently(runtime,
            new ConsistencyEfCoreMappings().Map(objects).Materialize(value, x => x.Mirror)));
        Assert.Contains("DerivedDependency", error.Message);
    }

    [Fact]
    public void Materialize_rejects_relation_predicate_member()
    {
        using var context = new MappingContext(); var entity = Seed(context);
        var related = new RelatedEntity { Id = 1, Value = 0 }; context.Add(related); context.SaveChanges();
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MappingEntity>().Key(x => x.Id);
        var relatedObjects = model.Objects<RelatedEntity>().Key(x => x.Id);
        model.Relation(objects, relatedObjects).Where((left, right) => left.Mirror == right.Value);
        var value = model.Derived(objects).Compute(x => x.Input);
        var runtime = model.Build().CreateRuntime(seed => { seed.Add(objects, [entity]); seed.Add(relatedObjects, [related]); });
        entity.Input = 2;

        var error = Assert.Throws<InvalidOperationException>(() => context.SaveChangesConsistently(runtime,
            new ConsistencyEfCoreMappings().Map(objects).Materialize(value, x => x.Mirror)));
        Assert.Contains("RelationDependency", error.Message);
    }

    [Fact]
    public void Materialize_rejects_invariant_dependency_member()
    {
        using var context = new MappingContext(); var entity = Seed(context);
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MappingEntity>().Key(x => x.Id);
        var value = model.Derived(objects).Compute(x => x.Input);
        model.Invariant(objects).Using(value).Must((source, current) => source.Mirror <= current);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        entity.Input = 2;

        var error = Assert.Throws<InvalidOperationException>(() => context.SaveChangesConsistently(runtime,
            new ConsistencyEfCoreMappings().Map(objects).Materialize(value, x => x.Mirror)));
        Assert.Contains("InvariantDependency", error.Message);
    }

    [Fact]
    public void Materialize_rejects_projected_selector_member()
    {
        using var context = new MappingContext();
        var target = new ProjectionTarget { Id = 1, Value = 1 };
        var link = new ProjectionLink { Id = 1, Target = target };
        context.Add(link); context.SaveChanges();
        var model = new ConsistencyModelBuilder(); var links = model.Objects<ProjectionLink>().Key(x => x.Id);
        var targets = model.Objects<ProjectionTarget>().Key(x => x.Id);
        var upstream = model.Derived(targets).Compute(x => x.Value);
        model.Derived(links).Using(x => x.Target, upstream).Compute((_, value) => value);
        var mirror = model.Derived(links).Compute(_ => target).AllowIncompleteDependencies();
        var runtime = model.Build().CreateRuntime(seed => { seed.Add(links, [link]); seed.Add(targets, [target]); });
        target.Value = 2;

        var error = Assert.Throws<InvalidOperationException>(() => context.SaveChangesConsistently(runtime,
            new ConsistencyEfCoreMappings().Map(links).Materialize(mirror, x => x.Target)));
        Assert.Contains("ProjectedSelector", error.Message);
    }

    [Fact]
    public void Materialize_rejects_duplicate_target_and_duplicate_derived()
    {
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MappingEntity>().Key(x => x.Id);
        var first = model.Derived(objects).Compute(x => x.Input);
        var second = model.Derived(objects).Compute(x => x.Input + 1);
        var mappings = new ConsistencyEfCoreMappings().Materialize(first, x => x.Mirror);

        Assert.Throws<InvalidOperationException>(() => mappings.Materialize(second, x => x.Mirror));
        Assert.Throws<InvalidOperationException>(() => mappings.Materialize(first, x => x.OtherMirror));
    }

    [Fact]
    public void Materialize_rejects_handle_from_another_runtime()
    {
        using var context = new MappingContext(); var entity = Seed(context);
        var firstModel = new ConsistencyModelBuilder(); var firstObjects = firstModel.Objects<MappingEntity>().Key(x => x.Id);
        var foreign = firstModel.Derived(firstObjects).Compute(x => x.Input);
        firstModel.Build();
        var secondModel = new ConsistencyModelBuilder(); var secondObjects = secondModel.Objects<MappingEntity>().Key(x => x.Id);
        var runtime = secondModel.Build().CreateRuntime(seed => seed.Add(secondObjects, [entity]));
        entity.Input = 2;

        Assert.Throws<ArgumentException>(() => context.SaveChangesConsistently(runtime,
            new ConsistencyEfCoreMappings().Map(secondObjects).Materialize(foreign, x => x.Mirror)));
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
            model.Entity<RelatedEntity>();
            model.Entity<ProjectionLink>().HasOne(x => x.Target).WithMany();
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

    private sealed class RelatedEntity
    {
        public int Id { get; set; }
        public int Value { get; set; }
    }

    private sealed class ProjectionLink
    {
        public int Id { get; set; }
        public ProjectionTarget Target { get; set; } = null!;
    }

    private sealed class ProjectionTarget
    {
        public int Id { get; set; }
        public int Value { get; set; }
    }
}
