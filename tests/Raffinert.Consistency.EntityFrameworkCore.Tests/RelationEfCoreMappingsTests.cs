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
        var value = model.Derived(objects).Select(x => x.Input);
        var invariant = model.Invariant(objects).From(value).Must((_, current) => current < 2);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        entity.Input = 2;

        Assert.Throws<ConsistencyInvariantViolationException>(() => context.SaveChangesConsistently(runtime,
            new ConsistencyEfCoreMappings().Map(objects).Enforce(invariant)));
    }

    [Fact]
    public void MaterializeTo_accepts_normal_mapped_writable_property()
    {
        using var context = new MappingContext(); var entity = Seed(context);
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MappingEntity>().Key(x => x.Id);
        var value = model.Derived(objects).Select(x => x.Input * 2).MaterializeTo(x => x.Mirror);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        entity.Input = 3;

        context.SaveChangesConsistently(runtime,
            new ConsistencyEfCoreMappings().Map(objects));

        Assert.Equal(6, entity.Mirror);
    }

    [Theory]
    [InlineData(nameof(MappingEntity.Id))]
    [InlineData(nameof(MappingEntity.Alternate))]
    [InlineData(nameof(MappingEntity.Generated))]
    [InlineData(nameof(MappingEntity.Unmapped))]
    public void MaterializeTo_rejects_invalid_ef_property(string propertyName)
    {
        using var context = new MappingContext(); var entity = Seed(context);
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MappingEntity>().Key(x => x.Id);
        var value = model.Derived(objects).Select(x => x.Input);
        if (propertyName == nameof(MappingEntity.Id)) value.MaterializeTo(x => x.Id);
        else if (propertyName == nameof(MappingEntity.Alternate)) value.MaterializeTo(x => x.Alternate);
        else if (propertyName == nameof(MappingEntity.Generated)) value.MaterializeTo(x => x.Generated);
        else value.MaterializeTo(x => x.Unmapped);
        if (propertyName == nameof(MappingEntity.Id))
        {
            Assert.Throws<InvalidOperationException>(model.Build);
            return;
        }
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        entity.Input = 2;
        var mappings = new ConsistencyEfCoreMappings().Map(objects);

        Assert.Throws<InvalidOperationException>(() => context.SaveChangesConsistently(runtime, mappings));
    }

    [Fact]
    public void MaterializeTo_rejects_derived_dependency_member()
    {
        using var context = new MappingContext(); var entity = Seed(context);
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MappingEntity>().Key(x => x.Id);
        var value = model.Derived(objects).Select(x => x.Input).MaterializeTo(x => x.Mirror);
        model.Derived(objects).Select(x => x.Mirror + 1);
        var error = Assert.Throws<InvalidOperationException>(model.Build);
        Assert.Contains("DerivedDependency", error.Message);
    }

    [Fact]
    public void MaterializeTo_rejects_relation_predicate_member()
    {
        using var context = new MappingContext(); var entity = Seed(context);
        var related = new RelatedEntity { Id = 1, Value = 0 }; context.Add(related); context.SaveChanges();
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MappingEntity>().Key(x => x.Id);
        var relatedObjects = model.Objects<RelatedEntity>().Key(x => x.Id);
        model.Relation(objects, relatedObjects).Where((left, right) => left.Mirror == right.Value);
        model.Derived(objects).Select(x => x.Input).MaterializeTo(x => x.Mirror);
        var error = Assert.Throws<InvalidOperationException>(model.Build);
        Assert.Contains("RelationDependency", error.Message);
    }

    [Fact]
    public void MaterializeTo_rejects_invariant_dependency_member()
    {
        using var context = new MappingContext(); var entity = Seed(context);
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MappingEntity>().Key(x => x.Id);
        var value = model.Derived(objects).Select(x => x.Input).MaterializeTo(x => x.Mirror);
        model.Invariant(objects).From(value).Must((source, current) => source.Mirror <= current);
        var error = Assert.Throws<InvalidOperationException>(model.Build);
        Assert.Contains("InvariantDependency", error.Message);
    }

    [Fact]
    public void MaterializeTo_rejects_projected_selector_member()
    {
        using var context = new MappingContext();
        var target = new ProjectionTarget { Id = 1, Value = 1 };
        var link = new ProjectionLink { Id = 1, Target = target };
        context.Add(link); context.SaveChanges();
        var model = new ConsistencyModelBuilder(); var links = model.Objects<ProjectionLink>().Key(x => x.Id);
        var targets = model.Objects<ProjectionTarget>().Key(x => x.Id);
        var upstream = model.Derived(targets).Select(x => x.Value);
        model.Derived(links).From(x => x.Target, upstream).Select((_, value) => value);
        model.Derived(links).Select(_ => target).AllowIncompleteDependencies().MaterializeTo(x => x.Target);
        var error = Assert.Throws<InvalidOperationException>(model.Build);
        Assert.Contains("ProjectedSelector", error.Message);
    }

    [Fact]
    public void MaterializeTo_rejects_duplicate_target_and_duplicate_derived()
    {
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MappingEntity>().Key(x => x.Id);
        var first = model.Derived(objects).Select(x => x.Input);
        var second = model.Derived(objects).Select(x => x.Input + 1);
        first.MaterializeTo(x => x.Mirror);

        Assert.Throws<InvalidOperationException>(() => second.MaterializeTo(x => x.Mirror));
        Assert.Throws<InvalidOperationException>(() => first.MaterializeTo(x => x.OtherMirror));
    }

    [Fact]
    public void MaterializeTo_rejects_configuration_after_model_build()
    {
        using var context = new MappingContext(); var entity = Seed(context);
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MappingEntity>().Key(x => x.Id);
        var value = model.Derived(objects).Select(x => x.Input);
        model.Build();

        Assert.Throws<InvalidOperationException>(() => value.MaterializeTo(x => x.Mirror));
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
