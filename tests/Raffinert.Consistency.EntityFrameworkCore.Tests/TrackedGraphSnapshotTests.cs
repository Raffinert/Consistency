using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Raffinert.Consistency.EntityFrameworkCore;

namespace Raffinert.Consistency.Tests;

public sealed class TrackedGraphSnapshotTests
{
    [Fact]
    public void Unchanged_reference_and_collection_emit_no_navigation_mutations()
    {
        using var context = CreateContext();
        var parent = new Parent { Id = 1 };
        var child = new Child { Id = 1, Parent = parent };
        parent.Children.Add(child);
        context.AddRange(parent, child);
        context.SaveChanges();

        var changes = ChangeTrackerAdapter.CreateChangeSet(context.ChangeTracker);

        Assert.Null(changes);
    }

    [Fact]
    public void Dependent_reference_captures_retarget_and_null_transitions()
    {
        using var context = CreateContext();
        var first = new Parent { Id = 1 };
        var second = new Parent { Id = 2 };
        var child = new Child { Id = 1, Parent = first };
        context.AddRange(first, second, child);
        context.SaveChanges();

        child.Parent = second;
        child.ParentId = second.Id;
        var retarget = ReferenceChange(context, child, nameof(Child.Parent));
        Assert.Same(first, retarget.OldValue);
        Assert.Same(second, retarget.NewValue);

        context.ChangeTracker.AcceptAllChanges();
        child.Parent = null;
        child.ParentId = null;
        var removed = ReferenceChange(context, child, nameof(Child.Parent));
        Assert.Same(second, removed.OldValue);
        Assert.Null(removed.NewValue);

        context.ChangeTracker.AcceptAllChanges();
        child.Parent = first;
        child.ParentId = first.Id;
        var added = ReferenceChange(context, child, nameof(Child.Parent));
        Assert.Null(added.OldValue);
        Assert.Same(first, added.NewValue);
    }

    [Fact]
    public void Composite_and_nullable_foreign_keys_preserve_exact_reference_values()
    {
        using var context = CreateContext();
        var first = new CompositeParent { Partition = 1, Code = "A" };
        var second = new CompositeParent { Partition = 2, Code = "B" };
        var child = new CompositeChild { Id = 1, Parent = first };
        var optional = new CompositeChild { Id = 2 };
        context.AddRange(first, second, child, optional);
        context.SaveChanges();

        child.Parent = second;
        child.ParentPartition = second.Partition;
        child.ParentCode = second.Code;
        var change = ReferenceChange(context, child, nameof(CompositeChild.Parent));

        Assert.Same(first, change.OldValue);
        Assert.Same(second, change.NewValue);
        Assert.DoesNotContain(NavigationMutations(context).OfType<PropertyChange>(), mutation =>
            ReferenceEquals(mutation.Instance, optional) && mutation.Member.Name == nameof(CompositeChild.Parent));
    }

    [Fact]
    public void Partial_null_composite_foreign_keys_are_null_relationships()
    {
        using var context = CreateContext();
        var parent = new CompositeParent { Partition = 1, Code = "A" };
        var nullNull = new CompositeChild { Id = 1 };
        var valueNull = new CompositeChild { Id = 2, ParentPartition = 1 };
        var nullValue = new CompositeChild { Id = 3, ParentCode = "A" };
        var complete = new CompositeChild
        {
            Id = 4,
            ParentPartition = 1,
            ParentCode = "A"
        };
        context.AddRange(parent, nullNull, valueNull, nullValue, complete);
        context.SaveChanges();

        Assert.Same(parent, complete.Parent);
        Assert.Null(nullNull.Parent);
        Assert.Null(valueNull.Parent);
        Assert.Null(nullValue.Parent);
        Assert.DoesNotContain(NavigationMutations(context).OfType<PropertyChange>(), mutation =>
            mutation.Member.Name == nameof(CompositeChild.Parent));
    }

    [Fact]
    public void Composite_reference_transitions_to_and_from_partial_null_emit_exact_values()
    {
        using (var context = CreateContext())
        {
            var parent = new CompositeParent { Partition = 1, Code = "A" };
            var child = new CompositeChild { Id = 1, Parent = parent };
            context.AddRange(parent, child);
            context.SaveChanges();

            child.Parent = null;
            child.ParentCode = null;
            var removed = ReferenceChange(context, child, nameof(CompositeChild.Parent));

            Assert.Same(parent, removed.OldValue);
            Assert.Null(removed.NewValue);
        }

        using (var context = CreateContext())
        {
            var parent = new CompositeParent { Partition = 1, Code = "A" };
            var child = new CompositeChild { Id = 1, ParentPartition = 1 };
            context.AddRange(parent, child);
            context.SaveChanges();

            child.Parent = parent;
            child.ParentCode = parent.Code;
            var added = ReferenceChange(context, child, nameof(CompositeChild.Parent));

            Assert.Null(added.OldValue);
            Assert.Same(parent, added.NewValue);
        }
    }

    [Fact]
    public void Partial_null_composite_foreign_keys_reset_only_real_parent_collections()
    {
        using (var context = CreateContext())
        {
            var first = new CompositeParent { Partition = 1, Code = "A" };
            var second = new CompositeParent { Partition = 2, Code = "B" };
            var child = new CompositeChild { Id = 1, Parent = first };
            first.Children.Add(child);
            context.AddRange(first, second, child);
            context.SaveChanges();

            child.Parent = null;
            child.ParentCode = null;
            var resets = NavigationMutations(context).OfType<CollectionChange>().ToArray();

            Assert.Single(resets, mutation => ReferenceEquals(mutation.Owner, first));
            Assert.DoesNotContain(resets, mutation => ReferenceEquals(mutation.Owner, second));
        }

        using (var context = CreateContext())
        {
            var first = new CompositeParent { Partition = 1, Code = "A" };
            var second = new CompositeParent { Partition = 2, Code = "B" };
            var child = new CompositeChild { Id = 1, ParentPartition = 1 };
            context.AddRange(first, second, child);
            context.SaveChanges();

            child.Parent = second;
            child.ParentPartition = second.Partition;
            child.ParentCode = second.Code;
            var resets = NavigationMutations(context).OfType<CollectionChange>().ToArray();

            Assert.Single(resets, mutation => ReferenceEquals(mutation.Owner, second));
            Assert.DoesNotContain(resets, mutation => ReferenceEquals(mutation.Owner, first));
        }

        using (var context = CreateContext())
        {
            var first = new CompositeParent { Partition = 1, Code = "A" };
            var second = new CompositeParent { Partition = 2, Code = "B" };
            var child = new CompositeChild { Id = 1, ParentPartition = 1 };
            context.AddRange(first, second, child);
            context.SaveChanges();

            child.ParentPartition = 2;
            var resets = NavigationMutations(context).OfType<CollectionChange>().ToArray();

            Assert.Empty(resets);
        }
    }

    [Fact]
    public void Principal_side_one_to_one_reference_uses_original_dependent_index()
    {
        using var context = CreateContext();
        var principal = new Parent { Id = 1 };
        var oldDetail = new Detail { Id = 1, Parent = principal };
        principal.Detail = oldDetail;
        context.AddRange(principal, oldDetail);
        context.SaveChanges();
        var replacement = new Detail { Id = 2, Parent = principal, ParentId = principal.Id };
        principal.Detail = replacement;
        context.Add(replacement);
        context.Entry(replacement).Property(value => value.ParentId).OriginalValue = null;

        var change = ReferenceChange(context, principal, nameof(Parent.Detail));

        Assert.Same(oldDetail, change.OldValue);
        Assert.Same(replacement, change.NewValue);
    }

    [Fact]
    public void Ambiguous_original_principal_side_match_fails_closed()
    {
        using var context = CreateContext();
        var principal = new Parent { Id = 1 };
        var first = new Detail { Id = 1, ParentId = 1 };
        var second = new Detail { Id = 2, ParentId = 1 };
        context.AttachRange(principal, first, second);
        context.Entry(first).Property(value => value.ParentId).OriginalValue = 1;
        context.Entry(second).Property(value => value.ParentId).OriginalValue = 1;

        var error = Assert.Throws<InvalidOperationException>(() =>
            ChangeTrackerAdapter.CreateChangeSet(context.ChangeTracker));

        Assert.Contains("not tracked unambiguously", error.Message);
    }

    [Fact]
    public void Ambiguous_original_dependent_side_match_fails_closed()
    {
        using var context = CreateContext();
        var first = new Parent { Id = 1 };
        var second = new Parent { Id = 2 };
        var child = new Child { Id = 1, Parent = first };
        context.AddRange(first, second, child);
        context.SaveChanges();
        second.Id = first.Id;

        var error = Assert.Throws<InvalidOperationException>(() =>
            ChangeTrackerAdapter.CreateChangeSet(context.ChangeTracker));

        Assert.Contains("not tracked unambiguously", error.Message);
    }

    [Fact]
    public void Entry_lookup_uses_reference_identity_for_every_tracked_state()
    {
        using var context = CreateContext();
        var unchanged = new EqualityEntity { Id = 1 };
        var modified = new EqualityEntity { Id = 2 };
        var deleted = new EqualityEntity { Id = 3 };
        context.AddRange(unchanged, modified, deleted);
        context.SaveChanges();
        modified.Value = 1;
        context.Remove(deleted);
        var added = new EqualityEntity { Id = 4 };
        context.Add(added);
        context.ChangeTracker.DetectChanges();
        var snapshot = TrackedGraphSnapshot.Create(context.ChangeTracker);

        AssertEntry(snapshot, unchanged, EntityState.Unchanged);
        AssertEntry(snapshot, modified, EntityState.Modified);
        AssertEntry(snapshot, deleted, EntityState.Deleted);
        AssertEntry(snapshot, added, EntityState.Added);
        Assert.Null(snapshot.FindEntry(new EqualityEntity { Id = unchanged.Id }));
        Assert.Null(snapshot.FindEntry(new EqualityEntity { Id = 99 }));
    }

    [Fact]
    public void Byte_array_key_resolution_uses_ef_structural_key_comparer()
    {
        using var context = CreateContext();
        var parent = new BinaryParent { Id = [1, 2, 3] };
        var child = new BinaryChild { Id = 1 };
        context.AddRange(parent, child);
        context.SaveChanges();

        child.ParentId = [1, 2, 3];
        var change = ReferenceChange(context, child, nameof(BinaryChild.Parent));

        Assert.Null(change.OldValue);
        Assert.Same(parent, change.NewValue);
    }

    [Fact]
    public void Converted_key_resolution_uses_configured_ef_key_comparer()
    {
        using var context = CreateContext();
        var first = new ConvertedParent { Id = new InsensitiveKey("first") };
        var second = new ConvertedParent { Id = new InsensitiveKey("second") };
        var child = new ConvertedChild { Id = 1, Parent = first };
        context.AddRange(first, second, child);
        context.SaveChanges();

        child.ParentId = new InsensitiveKey("SECOND");
        var change = ReferenceChange(context, child, nameof(ConvertedChild.Parent));

        Assert.Same(first, change.OldValue);
        Assert.Same(second, change.NewValue);
    }

    [Fact]
    public void Added_and_deleted_dependents_preserve_lifecycle_and_collection_evidence()
    {
        using var context = CreateContext();
        var parent = new Parent { Id = 1 };
        var removed = new Child { Id = 1, Parent = parent };
        parent.Children.Add(removed);
        context.AddRange(parent, removed);
        context.SaveChanges();
        var added = new Child { Id = 2, Parent = parent };
        parent.Children.Add(added);
        context.Add(added);
        context.Remove(removed);
        var model = new ConsistencyModelBuilder();
        var parents = model.Objects<Parent>().Key(value => value.Id);
        var children = model.Objects<Child>().Key(value => value.Id);
        var mappings = new ConsistencyUnitOfWorkMappings().Map(parents).Map(children);

        var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);

        Assert.Contains(unit.Mutations, mutation => mutation is ObjectAdded addedMutation &&
            ReferenceEquals(addedMutation.Instance, added));
        Assert.Contains(unit.Mutations, mutation => mutation is ObjectRemoved removedMutation &&
            ReferenceEquals(removedMutation.Instance, removed));
        Assert.Single(unit.Mutations.OfType<CollectionChange>(), mutation =>
            ReferenceEquals(mutation.Owner, parent) && mutation.Member.Name == nameof(Parent.Children));
    }

    [Fact]
    public void Collection_move_and_fk_only_retarget_reset_each_owner_once()
    {
        using var context = CreateContext();
        var first = new Parent { Id = 1 };
        var second = new Parent { Id = 2 };
        var children = Enumerable.Range(1, 2)
            .Select(id => new Child { Id = id, Parent = first }).ToArray();
        first.Children.Add(children[0]);
        first.Children.Add(children[1]);
        context.AddRange(first, second);
        context.AddRange(children);
        context.SaveChanges();

        children[0].Parent = second;
        children[0].ParentId = second.Id;
        children[1].ParentId = second.Id;
        context.ChangeTracker.DetectChanges();
        var resets = NavigationMutations(context).OfType<CollectionChange>().ToArray();

        Assert.Single(resets, mutation => ReferenceEquals(mutation.Owner, first) &&
            mutation.Member.Name == nameof(Parent.Children));
        Assert.Single(resets, mutation => ReferenceEquals(mutation.Owner, second) &&
            mutation.Member.Name == nameof(Parent.Children));
    }

    [Fact]
    public void Composite_collection_and_self_reference_changes_are_captured()
    {
        using var context = CreateContext();
        var first = new CompositeParent { Partition = 1, Code = "A" };
        var second = new CompositeParent { Partition = 2, Code = "B" };
        var child = new CompositeChild { Id = 1, Parent = first };
        first.Children.Add(child);
        var root = new Node { Id = 1 };
        var nested = new Node { Id = 2, Parent = root };
        root.Children.Add(nested);
        context.AddRange(first, second, child, root, nested);
        context.SaveChanges();

        child.Parent = second;
        child.ParentPartition = second.Partition;
        child.ParentCode = second.Code;
        nested.Parent = null;
        nested.ParentId = null;
        var mutations = NavigationMutations(context);

        Assert.Contains(mutations.OfType<CollectionChange>(), value => ReferenceEquals(value.Owner, first));
        Assert.Contains(mutations.OfType<CollectionChange>(), value => ReferenceEquals(value.Owner, second));
        Assert.Contains(mutations.OfType<CollectionChange>(), value => ReferenceEquals(value.Owner, root));
        var selfReference = Assert.Single(mutations.OfType<PropertyChange>(), value =>
            ReferenceEquals(value.Instance, nested) && value.Member.Name == nameof(Node.Parent));
        Assert.Same(root, selfReference.OldValue);
        Assert.Null(selfReference.NewValue);
    }

    [Fact]
    public void Repeated_capture_is_fingerprint_stable()
    {
        using var context = CreateContext();
        var first = new Parent { Id = 1 };
        var second = new Parent { Id = 2 };
        var child = new Child { Id = 1, Parent = first };
        context.AddRange(first, second, child);
        context.SaveChanges();
        child.Parent = second;
        child.ParentId = second.Id;
        var model = new ConsistencyModelBuilder();
        var children = model.Objects<Child>().Key(value => value.Id);
        var mappings = new ConsistencyUnitOfWorkMappings().Map(children);

        var firstCapture = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);
        var secondCapture = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);

        Assert.True(EfMutationFingerprint.Create(firstCapture.Mutations)
            .Equals(EfMutationFingerprint.Create(secondCapture.Mutations)));
    }

    [Fact]
    public void Relationship_lookup_work_scales_with_navigations_not_tracked_entries_squared()
    {
        var small = CaptureDiagnostics(100);
        var large = CaptureDiagnostics(1_000);

        Assert.True(small.TrackedEntries > 0);
        Assert.True(small.ReferenceNavigationsVisited > 0);
        Assert.True(small.ReferenceIndexLookups > small.ReferenceNavigationsVisited);
        Assert.Equal(0, small.ReferenceCandidateChecks);
        Assert.Equal(0, large.ReferenceCandidateChecks);
        Assert.True(large.ReferenceIndexLookups < small.ReferenceIndexLookups * 20);
        Assert.True(large.CollectionIndexLookups < small.CollectionIndexLookups * 20);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1_000)]
    [InlineData(10_000)]
    public void Generated_fixup_policy_capture_uses_one_reference_lookup_per_modified_fk(int count)
    {
        using var context = CreateGeneratedContext();
        var original = new GeneratedParent();
        var children = Enumerable.Range(1, count)
            .Select(id => new GeneratedChild { Id = id, Parent = original }).ToArray();
        context.Add(original);
        context.AddRange(children);
        context.SaveChanges();
        var replacement = new GeneratedParent();
        context.Add(replacement);
        foreach (var child in children)
        {
            child.Parent = replacement;
            child.ParentId = replacement.Id;
        }
        var model = new ConsistencyModelBuilder();
        var mappings = new ConsistencyUnitOfWorkMappings()
            .Map(model.Objects<GeneratedParent>().Key(value => value.Id))
            .Map(model.Objects<GeneratedChild>().Key(value => value.Id));
        var diagnostics = new EfFingerprintDiagnostics();

        _ = ChangeTrackerAdapter.CapturePolicyAwareSnapshot(
            context.ChangeTracker, mappings, (_, _) => true, diagnostics);

        Assert.Equal(count, diagnostics.GeneratedFixupPrincipalLookups);
        Assert.Equal(0, diagnostics.GeneratedFixupTrackedEntryScans);
    }

    [Fact]
    public void Existing_principal_key_mutation_remains_rejected_by_ef()
    {
        using var context = CreateContext();
        var parent = new Parent { Id = 1 };
        context.Add(parent);
        context.SaveChanges();
        parent.Id = 2;

        Assert.Throws<InvalidOperationException>(() => context.ChangeTracker.DetectChanges());
    }

    private static PropertyChange ReferenceChange(DbContext context, object owner, string member) =>
        Assert.Single(NavigationMutations(context).OfType<PropertyChange>(), value =>
            ReferenceEquals(value.Instance, owner) && value.Member.Name == member);

    private static void AssertEntry(
        TrackedGraphSnapshot snapshot,
        object entity,
        EntityState state)
    {
        var entry = Assert.IsAssignableFrom<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry>(
            snapshot.FindEntry(entity));
        Assert.Same(entity, entry.Entity);
        Assert.Equal(state, entry.State);
    }

    private static IReadOnlyList<RuntimeMutation> NavigationMutations(DbContext context)
    {
        var model = new ConsistencyModelBuilder();
        var mappings = new ConsistencyUnitOfWorkMappings()
            .Map(model.Objects<Parent>().Key(value => value.Id))
            .Map(model.Objects<Child>().Key(value => value.Id))
            .Map(model.Objects<Detail>().Key(value => value.Id))
            .Map(model.Objects<CompositeParent>().Key(value => new { value.Partition, value.Code }))
            .Map(model.Objects<CompositeChild>().Key(value => value.Id))
            .Map(model.Objects<Node>().Key(value => value.Id));
        return ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings).Mutations;
    }

    private static EfFingerprintDiagnostics CaptureDiagnostics(int count)
    {
        using var context = CreateContext();
        var parents = Enumerable.Range(1, count)
            .Select(id => new Parent { Id = id }).ToArray();
        var children = parents.Select(parent => new Child { Id = parent.Id, Parent = parent }).ToArray();
        foreach (var pair in parents.Zip(children))
            pair.First.Children.Add(pair.Second);
        context.AddRange(parents);
        context.AddRange(children);
        context.SaveChanges();
        children[0].Parent = parents[1];
        children[0].ParentId = parents[1].Id;
        var model = new ConsistencyModelBuilder();
        var parentSet = model.Objects<Parent>().Key(value => value.Id);
        var childSet = model.Objects<Child>().Key(value => value.Id);
        var mappings = new ConsistencyUnitOfWorkMappings().Map(parentSet).Map(childSet);
        var diagnostics = new EfFingerprintDiagnostics();

        _ = ChangeTrackerAdapter.CaptureUnitOfWork(
            context.ChangeTracker, mappings, (_, _) => true, diagnostics);

        return diagnostics;
    }

    private static NavigationContext CreateContext() => new(
        new DbContextOptionsBuilder<NavigationContext>()
            .UseInMemoryDatabase($"tracked-graph-{Guid.NewGuid()}").Options);

    private static GeneratedContext CreateGeneratedContext() => new(
        new DbContextOptionsBuilder<GeneratedContext>()
            .UseInMemoryDatabase($"generated-graph-{Guid.NewGuid()}").Options);

    private sealed class GeneratedContext(DbContextOptions<GeneratedContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<GeneratedParent>().Property(value => value.Id).ValueGeneratedOnAdd();
            model.Entity<GeneratedChild>().Property(value => value.Id).ValueGeneratedNever();
            model.Entity<GeneratedChild>().HasOne(value => value.Parent).WithMany()
                .HasForeignKey(value => value.ParentId);
        }
    }

    private sealed class NavigationContext(DbContextOptions<NavigationContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<Parent>().Property(value => value.Id).ValueGeneratedNever();
            model.Entity<Child>().Property(value => value.Id).ValueGeneratedNever();
            model.Entity<Child>().HasOne(value => value.Parent).WithMany(value => value.Children)
                .HasForeignKey(value => value.ParentId).IsRequired(false);
            model.Entity<Detail>().Property(value => value.Id).ValueGeneratedNever();
            model.Entity<Parent>().HasOne(value => value.Detail).WithOne(value => value.Parent)
                .HasForeignKey<Detail>(value => value.ParentId).IsRequired(false);
            model.Entity<CompositeParent>().HasKey(value => new { value.Partition, value.Code });
            model.Entity<CompositeChild>().Property(value => value.Id).ValueGeneratedNever();
            model.Entity<CompositeChild>().HasOne(value => value.Parent).WithMany(value => value.Children)
                .HasForeignKey(value => new { value.ParentPartition, value.ParentCode }).IsRequired(false);
            model.Entity<Node>().Property(value => value.Id).ValueGeneratedNever();
            model.Entity<Node>().HasOne(value => value.Parent).WithMany(value => value.Children)
                .HasForeignKey(value => value.ParentId).IsRequired(false);
            model.Entity<EqualityEntity>().Property(value => value.Id).ValueGeneratedNever();
            model.Entity<BinaryParent>().HasKey(value => value.Id);
            model.Entity<BinaryChild>().Property(value => value.Id).ValueGeneratedNever();
            model.Entity<BinaryChild>().HasOne(value => value.Parent).WithMany()
                .HasForeignKey(value => value.ParentId).IsRequired(false);
            var keyComparer = new ValueComparer<InsensitiveKey>(
                (left, right) => string.Equals(left.Value, right.Value, StringComparison.OrdinalIgnoreCase),
                value => StringComparer.OrdinalIgnoreCase.GetHashCode(value.Value),
                value => new InsensitiveKey(value.Value));
            model.Entity<ConvertedParent>().HasKey(value => value.Id);
            model.Entity<ConvertedParent>().Property(value => value.Id)
                .HasConversion(value => value.Value, value => new InsensitiveKey(value))
                .Metadata.SetValueComparer(keyComparer);
            model.Entity<ConvertedChild>().Property(value => value.Id).ValueGeneratedNever();
            model.Entity<ConvertedChild>().Property(value => value.ParentId)
                .HasConversion(value => value.Value, value => new InsensitiveKey(value))
                .Metadata.SetValueComparer(keyComparer);
            model.Entity<ConvertedChild>().HasOne(value => value.Parent).WithMany()
                .HasForeignKey(value => value.ParentId);
        }
    }

    private sealed class Parent
    {
        public int Id { get; set; }
        public ICollection<Child> Children { get; } = [];
        public Detail? Detail { get; set; }
    }

    private sealed class Child
    {
        public int Id { get; set; }
        public int? ParentId { get; set; }
        public Parent? Parent { get; set; }
    }

    private sealed class Detail
    {
        public int Id { get; set; }
        public int? ParentId { get; set; }
        public Parent? Parent { get; set; }
    }

    private sealed class CompositeParent
    {
        public int Partition { get; set; }
        public string Code { get; set; } = "";
        public ICollection<CompositeChild> Children { get; } = [];
    }

    private sealed class CompositeChild
    {
        public int Id { get; set; }
        public int? ParentPartition { get; set; }
        public string? ParentCode { get; set; }
        public CompositeParent? Parent { get; set; }
    }

    private sealed class Node
    {
        public int Id { get; set; }
        public int? ParentId { get; set; }
        public Node? Parent { get; set; }
        public ICollection<Node> Children { get; } = [];
    }

    private sealed class EqualityEntity
    {
        public int Id { get; set; }
        public int Value { get; set; }
        public override bool Equals(object? value) => value is EqualityEntity other && Id == other.Id;
        public override int GetHashCode() => Id;
    }

    private sealed class BinaryParent { public byte[] Id { get; set; } = []; }
    private sealed class BinaryChild
    {
        public int Id { get; set; }
        public byte[]? ParentId { get; set; }
        public BinaryParent? Parent { get; set; }
    }

    private readonly record struct InsensitiveKey(string Value);
    private sealed class ConvertedParent { public InsensitiveKey Id { get; set; } }
    private sealed class ConvertedChild
    {
        public int Id { get; set; }
        public InsensitiveKey ParentId { get; set; }
        public ConvertedParent Parent { get; set; } = null!;
    }

    private sealed class GeneratedParent { public int Id { get; set; } }
    private sealed class GeneratedChild
    {
        public int Id { get; set; }
        public int ParentId { get; set; }
        public GeneratedParent Parent { get; set; } = null!;
    }
}
