using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Raffinert.Consistency.EntityFrameworkCore;

namespace Raffinert.Consistency.Tests;

public sealed class EntityFrameworkCoreSqliteTests
{
    [Fact]
    public void Database_failure_discards_prepared_runtime_mutation()
    {
        using var database = new SqliteFixture();
        using var context = database.CreateContext();
        var model = new ConsistencyModelBuilder();
        var objects = model.Objects<UniqueEntity>().Key(entity => entity.Id);
        var runtime = model.Build().CreateRuntime();
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        var first = new UniqueEntity { Id = Guid.NewGuid(), Code = "duplicate" };
        var second = new UniqueEntity { Id = Guid.NewGuid(), Code = "duplicate" };
        context.AddRange(first, second);

        Assert.Throws<DbUpdateException>(() => context.SaveChangesAndApply(runtime, mappings));

        Assert.Equal(0, runtime.Version);
        Assert.False(runtime.Remove(objects, first));
        Assert.False(runtime.Remove(objects, second));
    }

    [Fact]
    public void Database_success_followed_by_runtime_failure_has_dedicated_exception()
    {
        using var database = new SqliteFixture();
        using var context = database.CreateContext();
        var gate = new ThrowingRelationGate();
        var model = new ConsistencyModelBuilder();
        var objects = model.Objects<UniqueEntity>().Key(entity => entity.Id);
        var relation = model.Relation(objects, objects)
            .Where((left, right) => gate.Match(left, right))
            .AllowIncompleteDependencies();
        _ = model.Derived(objects).Using(relation).Compute((_, matches) => matches.Count)
            .AllowIncompleteDependencies();
        var runtime = model.Build().CreateRuntime();
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        var entity = new UniqueEntity { Id = Guid.NewGuid(), Code = "persisted" };
        context.Add(entity);
        gate.Throw = true;

        var error = Assert.Throws<RelationRuntimeSynchronizationException>(() =>
            context.SaveChangesAndApply(runtime, mappings));

        Assert.True(error.DatabaseOperationSucceeded);
        Assert.Equal(0, error.RuntimeVersion);
        Assert.IsType<DeliberateRuntimeFailure>(error.InnerException);
        Assert.Equal(1, context.Set<UniqueEntity>().Count());
        Assert.Equal(0, runtime.Version);
        Assert.False(runtime.Remove(objects, entity));
    }

    [Fact]
    public void Rolled_back_explicit_transaction_discards_prepared_runtime_mutation()
    {
        using var database = new SqliteFixture();
        using var context = database.CreateContext();
        var model = new ConsistencyModelBuilder();
        var objects = model.Objects<GeneratedEntity>().Key(entity => entity.Id);
        var runtime = model.Build().CreateRuntime();
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        var entity = new GeneratedEntity { Code = "rollback" };
        context.Add(entity);
        var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);
        unit.Prepare(runtime);

        using (var transaction = context.Database.BeginTransaction())
        {
            context.SaveChanges();
            transaction.Rollback();
        }

        Assert.True(entity.Id > 0);
        Assert.Equal(0, runtime.Version);
        Assert.False(runtime.Remove(objects, entity));
    }

    [Fact]
    public void Explicit_transaction_applies_prepared_runtime_state_after_commit()
    {
        using var database = new SqliteFixture();
        using var context = database.CreateContext();
        var model = new ConsistencyModelBuilder();
        var objects = model.Objects<GeneratedEntity>().Key(entity => entity.Id);
        var runtime = model.Build().CreateRuntime();
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        var entity = new GeneratedEntity { Code = "commit" };
        context.Add(entity);
        var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);
        unit.Prepare(runtime);

        using (var transaction = context.Database.BeginTransaction())
        {
            context.SaveChanges();
            transaction.Commit();
        }
        unit.Commit(runtime);
        unit.Dispatch(runtime);

        Assert.True(entity.Id > 0);
        Assert.True(runtime.Remove(objects, entity));
    }

    [Fact]
    public void Explicit_transaction_can_preview_generated_identity_before_runtime_commit()
    {
        using var database = new SqliteFixture();
        using var context = database.CreateContext();
        var model = new ConsistencyModelBuilder();
        var objects = model.Objects<GeneratedEntity>().Named("generated").Key(entity => entity.Id);
        model.Derived(objects).Compute(entity => entity.Code).Named("code");
        var runtime = model.Build().CreateRuntime();
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        var entity = new GeneratedEntity { Code = "outbox" };
        context.Add(entity);
        var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);
        unit.Prepare(runtime);

        PreparedImpactPlan? plan;
        using (var transaction = context.Database.BeginTransaction())
        {
            context.SaveChanges();
            plan = unit.PlanDetailed(runtime, RuntimeImpactDetailLevel.Causal);
            Assert.NotNull(plan);
            Assert.True(entity.Id > 0);
            Assert.Equal(0, runtime.Version);
            transaction.Commit();
        }

        var committed = unit.CommitDetailed(runtime, RuntimeImpactDetailLevel.Causal);
        Assert.Same(plan!.Result, committed);
        Assert.Equal(1, runtime.Version);
    }

    [Fact]
    public void Save_without_accept_all_changes_can_commit_runtime_then_accept_tracking_state()
    {
        using var database = new SqliteFixture();
        using var context = database.CreateContext();
        var model = new ConsistencyModelBuilder();
        var objects = model.Objects<GeneratedEntity>().Key(entity => entity.Id);
        var runtime = model.Build().CreateRuntime();
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        var entity = new GeneratedEntity { Code = "deferred-accept" };
        context.Add(entity);
        var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);
        unit.Prepare(runtime);

        context.SaveChanges(acceptAllChangesOnSuccess: false);
        unit.Commit(runtime);
        unit.Dispatch(runtime);

        Assert.Equal(EntityState.Added, context.Entry(entity).State);
        context.ChangeTracker.AcceptAllChanges();
        Assert.Equal(EntityState.Unchanged, context.Entry(entity).State);
        Assert.True(runtime.Remove(objects, entity));
    }

    [Fact]
    public void Concurrency_failure_does_not_commit_prepared_runtime_changes()
    {
        using var database = new SqliteFixture();
        var id = Guid.NewGuid();
        using (var seed = database.CreateContext())
        {
            seed.ConcurrencyEntities.Add(new ConcurrencyEntity { Id = id, Code = "A", Version = 0 });
            seed.SaveChanges();
        }
        using var firstContext = database.CreateContext();
        using var secondContext = database.CreateContext();
        var first = firstContext.ConcurrencyEntities.Single(entity => entity.Id == id);
        var second = secondContext.ConcurrencyEntities.Single(entity => entity.Id == id);
        var model = new ConsistencyModelBuilder();
        var objects = model.Objects<ConcurrencyEntity>().Key(entity => entity.Id);
        var runtime = model.Build().CreateRuntime();
        runtime.Add(objects, first);
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        second.Code = "B";
        second.Version = 1;
        secondContext.SaveChanges();
        first.Code = "C";
        first.Version = 1;

        Assert.Throws<DbUpdateConcurrencyException>(() =>
            firstContext.SaveChangesAndApply(runtime, mappings));

        Assert.Equal(1, runtime.Version);
        Assert.True(runtime.Remove(objects, first));
    }

    [Fact]
    public void Store_generated_key_is_registered_only_after_value_is_available()
    {
        using var database = new SqliteFixture();
        using var context = database.CreateContext();
        var model = new ConsistencyModelBuilder();
        var objects = model.Objects<GeneratedEntity>().Key(entity => entity.Id);
        var runtime = model.Build().CreateRuntime();
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        var entity = new GeneratedEntity { Code = "generated" };
        context.Add(entity);
        Assert.Equal(0, entity.Id);

        context.SaveChangesAndApply(runtime, mappings);

        Assert.True(entity.Id > 0);
        Assert.True(runtime.Remove(objects, entity));
    }

    [Fact]
    public void Two_generated_entities_and_two_outbox_rows_use_only_plan_result_durable_identities()
    {
        using var database = new SqliteFixture();
        using var context = database.CreateContext();
        var model = new ConsistencyModelBuilder();
        var objects = model.Objects<GeneratedEntity>().Named("generated").Key(entity => entity.Id);
        var code = model.Derived(objects).Compute(entity => entity.Code).Named("code");
        model.Invariant(objects).Using(code).Must((_, value) => value == "valid")
            .ScheduleRepairWith(_ => { }).Named("repair");
        var compiled = model.Build();
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        var first = new GeneratedEntity { Code = "valid" };
        var second = new GeneratedEntity { Code = "valid" };
        context.AddRange(first, second);
        ConsistencyRuntime runtime;
        RelationUnitOfWork unit;

        using (var transaction = context.Database.BeginTransaction())
        {
            context.SaveChanges();
            Assert.NotEqual(first.Id, second.Id);
            runtime = compiled.CreateRuntime(seed => seed.Add(objects, [first, second]));
            first.Code = "invalid";
            second.Code = "invalid";
            unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);
            unit.Prepare(runtime);
            var plan = unit.PlanDetailed(runtime, RuntimeImpactDetailLevel.Causal);
            var durableWork = plan!.Result.GetDurablePolicyWork();
            Assert.Equal(2, durableWork.RepairRequests.Count);
            context.Outbox.AddRange(durableWork.RepairRequests.Select(request => new OutboxRecord
            {
                Payload = CreateOutboxPayload(request)
            }));
            context.SaveChanges();
            transaction.Commit();
        }
        unit.Commit(runtime);

        Assert.True(runtime.Remove(objects, first));
        Assert.True(runtime.Remove(objects, second));
        Assert.Equal(
            [$"repair:{first.Id}", $"repair:{second.Id}"],
            context.Outbox.OrderBy(row => row.Id).Select(row => row.Payload).ToArray());
    }

    [Fact]
    public void Dependent_reference_retarget_uses_original_fk_without_manual_reference_flag()
    {
        using var database = new SqliteFixture();
        using var context = database.CreateContext();
        var oldParent = new CascadeParent { Id = Guid.NewGuid() };
        var newParent = new CascadeParent { Id = Guid.NewGuid() };
        var child = new CascadeChild { Id = Guid.NewGuid(), Parent = oldParent };
        context.AddRange(oldParent, newParent, child);
        context.SaveChanges();
        var model = new ConsistencyModelBuilder();
        var children = model.Objects<CascadeChild>().Key(entity => entity.Id);
        model.Derived(children)
            .Impact(policy => policy.SourceMemberChanged(entity => entity.Parent, (oldValue, newValue) =>
                ReferenceEquals(oldValue, oldParent) && ReferenceEquals(newValue, newParent)
                    ? DependencySeverity.Invalid
                    : DependencySeverity.Dirty))
            .Compute(entity => entity.Parent);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(children, [child]));
        var mappings = new RelationUnitOfWorkMappings().Map(children);
        child.Parent = newParent;
        child.ParentId = newParent.Id;

        var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);
        unit.Prepare(runtime);
        var plan = unit.PlanDetailed(runtime, RuntimeImpactDetailLevel.Causal);

        Assert.Equal(DependencySeverity.Invalid,
            plan!.Result.DerivedImpacts.Single().Sources.Single().Severity);
    }

    [Fact]
    public void Collection_add_emits_reset_from_added_dependent_fk_evidence()
    {
        using var database = new SqliteFixture(); using var context = database.CreateContext();
        var parent = new CascadeParent { Id = Guid.NewGuid() };
        context.Add(parent); context.SaveChanges();
        var label = new LabelEntity { Id = Guid.NewGuid(), Code = "A" };
        var model = new ConsistencyModelBuilder(); var parents = model.Objects<CascadeParent>().Key(x => x.Id);
        var labels = model.Objects<LabelEntity>().Key(x => x.Id);
        var relation = model.Relation(parents, labels).Where((left, right) => left.Children.Any(x => x.Code == right.Code));
        var runtime = model.Build().CreateRuntime(seed => { seed.Add(parents, [parent]); seed.Add(labels, [label]); });
        var child = new CascadeChild { Id = Guid.NewGuid(), Parent = parent, Code = "A" };
        parent.Children.Add(child); context.Add(child);

        context.SaveChangesAndApply(runtime, new RelationUnitOfWorkMappings().Map(parents));

        Assert.Equal([label], runtime.Related(relation, parent));
    }

    [Fact]
    public void Collection_remove_emits_reset_from_deleted_dependent_fk_evidence()
    {
        using var database = new SqliteFixture(); using var context = database.CreateContext();
        var parent = new CascadeParent { Id = Guid.NewGuid() };
        var child = new CascadeChild { Id = Guid.NewGuid(), Parent = parent, Code = "A" };
        parent.Children.Add(child); context.Add(parent); context.SaveChanges();
        var label = new LabelEntity { Id = Guid.NewGuid(), Code = "A" };
        var model = new ConsistencyModelBuilder(); var parents = model.Objects<CascadeParent>().Key(x => x.Id);
        var labels = model.Objects<LabelEntity>().Key(x => x.Id);
        var relation = model.Relation(parents, labels).Where((left, right) => left.Children.Any(x => x.Code == right.Code));
        var runtime = model.Build().CreateRuntime(seed => { seed.Add(parents, [parent]); seed.Add(labels, [label]); });
        Assert.Equal([label], runtime.Related(relation, parent));
        parent.Children.Remove(child); context.Remove(child);

        context.SaveChangesAndApply(runtime, new RelationUnitOfWorkMappings().Map(parents));

        Assert.Empty(runtime.Related(relation, parent));
    }

    [Fact]
    public void Cascade_delete_removes_tracked_principal_and_dependents()
    {
        using var database = new SqliteFixture();
        using var context = database.CreateContext();
        var parent = new CascadeParent { Id = Guid.NewGuid() };
        var child = new CascadeChild { Id = Guid.NewGuid(), Parent = parent };
        context.AddRange(parent, child);
        context.SaveChanges();
        var model = new ConsistencyModelBuilder();
        var parents = model.Objects<CascadeParent>().Key(entity => entity.Id);
        var children = model.Objects<CascadeChild>().Key(entity => entity.Id);
        var runtime = model.Build().CreateRuntime();
        runtime.Add(parents, parent);
        runtime.Add(children, child);
        var mappings = new RelationUnitOfWorkMappings().Map(parents).Map(children);

        context.Remove(parent);
        context.SaveChangesAndApply(runtime, mappings);

        Assert.False(runtime.Remove(parents, parent));
        Assert.False(runtime.Remove(children, child));
    }

    [Fact]
    public void Owned_value_change_updates_nested_relation_dependency()
    {
        using var database = new SqliteFixture();
        using var context = database.CreateContext();
        var owner = new OwnedOwner { Id = Guid.NewGuid(), Settings = new OwnedSettings { Code = "A" } };
        context.Add(owner);
        context.SaveChanges();
        var model = new ConsistencyModelBuilder();
        var owners = model.Objects<OwnedOwner>().Key(entity => entity.Id);
        var labels = model.Objects<LabelEntity>().Key(entity => entity.Id);
        var relation = model.Relation(owners, labels).Where((left, right) => left.Settings!.Code == right.Code);
        var runtime = model.Build().CreateRuntime();
        var label = new LabelEntity { Id = Guid.NewGuid(), Code = "B" };
        runtime.Add(owners, owner);
        runtime.Add(labels, label);
        var mappings = new RelationUnitOfWorkMappings().Map(owners);
        owner.Settings!.Code = "B";

        context.SaveChangesAndApply(runtime, mappings);

        Assert.Equal([label], runtime.Related(relation, owner));
    }

    [Fact]
    public void Owned_optional_reference_nonnull_to_null_is_captured_from_tracked_dependent()
    {
        using var database = new SqliteFixture(); using var context = database.CreateContext();
        var settings = new OwnedSettings { Code = "A" };
        var owner = new OwnedOwner { Id = Guid.NewGuid(), Settings = settings };
        context.Add(owner); context.SaveChanges();

        owner.Settings = null;
        context.ChangeTracker.DetectChanges();
        Assert.False(context.Entry(owner).Reference(x => x.Settings).IsModified);
        var changes = Assert.IsType<ChangeSet>(ChangeTrackerAdapter.CreateChangeSet(context.ChangeTracker));

        var change = Assert.Single(changes.Changes, x =>
            ReferenceEquals(x.Instance, owner) && x.Member.Name == nameof(OwnedOwner.Settings));
        Assert.Same(settings, change.OldValue);
        Assert.Null(change.NewValue);
    }

    [Fact]
    public void Owned_optional_reference_replacement_captures_real_old_and_new_targets()
    {
        using var database = new SqliteFixture(); using var context = database.CreateContext();
        var oldSettings = new OwnedSettings { Code = "A" };
        var newSettings = new OwnedSettings { Code = "B" };
        var owner = new OwnedOwner { Id = Guid.NewGuid(), Settings = oldSettings };
        context.Add(owner); context.SaveChanges();

        owner.Settings = newSettings;
        var changes = Assert.IsType<ChangeSet>(ChangeTrackerAdapter.CreateChangeSet(context.ChangeTracker));

        var change = Assert.Single(changes.Changes, x =>
            ReferenceEquals(x.Instance, owner) && x.Member.Name == nameof(OwnedOwner.Settings));
        Assert.Same(oldSettings, change.OldValue);
        Assert.Same(newSettings, change.NewValue);
    }

    [Fact]
    public void Owned_optional_reference_null_to_instance_is_captured()
    {
        using var database = new SqliteFixture(); using var context = database.CreateContext();
        var owner = new OwnedOwner { Id = Guid.NewGuid(), Settings = null };
        context.Add(owner); context.SaveChanges();
        var settings = new OwnedSettings { Code = "A" };
        owner.Settings = settings;

        var changes = Assert.IsType<ChangeSet>(ChangeTrackerAdapter.CreateChangeSet(context.ChangeTracker));

        var change = Assert.Single(changes.Changes, x =>
            ReferenceEquals(x.Instance, owner) && x.Member.Name == nameof(OwnedOwner.Settings));
        Assert.Null(change.OldValue);
        Assert.Same(settings, change.NewValue);
    }

    [Fact]
    public void Many_to_many_skip_navigation_change_emits_collection_reset()
    {
        using var database = new SqliteFixture();
        using var context = database.CreateContext();
        var group = new TagGroup { Id = Guid.NewGuid() };
        var tag = new TagEntity { Id = Guid.NewGuid(), Name = "B" };
        context.AddRange(group, tag);
        context.SaveChanges();
        var model = new ConsistencyModelBuilder();
        var groups = model.Objects<TagGroup>().Key(entity => entity.Id);
        var labels = model.Objects<LabelEntity>().Key(entity => entity.Id);
        var relation = model.Relation(groups, labels).Where((left, right) =>
            left.Tags.Any(value => value.Name == right.Code));
        var runtime = model.Build().CreateRuntime();
        var label = new LabelEntity { Id = Guid.NewGuid(), Code = "B" };
        runtime.Add(groups, group);
        runtime.Add(labels, label);
        var mappings = new RelationUnitOfWorkMappings().Map(groups);
        group.Tags.Add(tag);

        context.SaveChangesAndApply(runtime, mappings);

        Assert.Equal([label], runtime.Related(relation, group));
    }

    [Fact]
    public void Multiple_save_changes_calls_advance_one_runtime_consistently()
    {
        using var database = new SqliteFixture();
        using var context = database.CreateContext();
        var model = new ConsistencyModelBuilder();
        var objects = model.Objects<UniqueEntity>().Key(entity => entity.Id);
        var runtime = model.Build().CreateRuntime();
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        var entity = new UniqueEntity { Id = Guid.NewGuid(), Code = "first" };
        context.Add(entity);
        context.SaveChangesAndApply(runtime, mappings);
        entity.Code = "second";
        context.SaveChangesAndApply(runtime, mappings);

        Assert.Equal(2, runtime.Version);
        Assert.True(runtime.Remove(objects, entity));
    }

    [Fact]
    public void Binding_plan_and_outbox_row_commit_in_one_sqlite_transaction()
    {
        using var database = new SqliteFixture();
        using var context = database.CreateContext();
        var entity = new UniqueEntity { Id = Guid.NewGuid(), Code = "before" };
        context.Add(entity);
        context.SaveChanges();
        var model = new ConsistencyModelBuilder();
        var objects = model.Objects<UniqueEntity>().Named("entities").Key(value => value.Id);
        var code = model.Derived(objects).Compute(value => value.Code).Named("code");
        model.Invariant(objects).Using(code).Must((_, value) => value == "before")
            .ScheduleRepairWith(_ => { }).Named("repair");
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        entity.Code = "after";
        var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);
        unit.Prepare(runtime);

        PreparedImpactPlan plan;
        using (var transaction = context.Database.BeginTransaction())
        {
            context.SaveChanges();
            plan = unit.PlanDetailed(runtime, RuntimeImpactDetailLevel.Causal)!;
            var durableWork = plan.Result.GetDurablePolicyWork();
            context.Outbox.Add(new OutboxRecord
            {
                Payload = CreateOutboxPayload(durableWork.RepairRequests.Single())
            });
            context.SaveChanges();
            transaction.Commit();
        }
        unit.Commit(runtime);
        unit.Dispatch(runtime);

        Assert.Equal(1, runtime.Version);
        Assert.Equal($"repair:{entity.Id:D}", context.Outbox.Single().Payload);
    }

    [Fact]
    public void Database_commit_succeeds_then_runtime_plan_install_failure_requires_runtime_rebuild()
    {
        using var database = new SqliteFixture();
        using var context = database.CreateContext();
        var model = new ConsistencyModelBuilder();
        var objects = model.Objects<GeneratedEntity>().Named("generated").Key(entity => entity.Id);
        var code = model.Derived(objects).Compute(entity => entity.Code).Named("code");
        var compiled = model.Build();
        var runtime = compiled.CreateRuntime();
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        var entity = new GeneratedEntity { Code = "durable" };
        context.Add(entity);
        var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);

        using (var transaction = context.Database.BeginTransaction())
        {
            context.SaveChanges();
            unit.Prepare(runtime);
            var plan = unit.PlanDetailed(runtime, RuntimeImpactDetailLevel.Causal)!;
            var identity = plan.Result.MutationOrigins.Single().SourceIdentity?.DurableIdentity
                ?? throw new InvalidOperationException("Outbox mutation source must have a durable identity.");
            context.Outbox.Add(new OutboxRecord
            {
                Payload = $"added:{identity.KeyParts.Single().Value}"
            });
            context.SaveChanges();
            transaction.Commit();
        }

        runtime.FailAfterNextForwardPatchApplyForTesting();
        Assert.Throws<InvalidOperationException>(() => unit.Commit(runtime));

        Assert.Equal(0, runtime.Version);
        Assert.False(runtime.Remove(objects, entity));
        using var verification = database.CreateContext();
        var authoritative = verification.Set<GeneratedEntity>().AsNoTracking().Single();
        Assert.Equal("durable", authoritative.Code);
        Assert.Equal($"added:{authoritative.Id}", verification.Outbox.Single().Payload);

        // A durable DB commit cannot be rolled back by runtime code. Recovery rebuilds or reconciles
        // the runtime from the authoritative database/outbox before accepting more runtime work.
        var rebuilt = compiled.CreateRuntime(seed => seed.Add(objects, [authoritative]));
        Assert.Equal(0, rebuilt.Version);
        Assert.Equal("durable", rebuilt.Get(code, authoritative));
        Assert.True(rebuilt.Remove(objects, authoritative));
    }

    [Fact]
    public void Dispatch_failure_after_database_and_runtime_commit_keeps_outbox_and_allows_dispatch_retry()
    {
        using var database = new SqliteFixture();
        using var context = database.CreateContext();
        var entity = new UniqueEntity { Id = Guid.NewGuid(), Code = "before" };
        context.Add(entity);
        context.SaveChanges();
        var failDispatch = true;
        var dispatchCount = 0;
        var model = new ConsistencyModelBuilder();
        var objects = model.Objects<UniqueEntity>().Named("entities").Key(value => value.Id);
        var code = model.Derived(objects).Compute(value => value.Code).Named("code");
        model.Invariant(objects).Using(code)
            .Must((_, value) => value == "before")
            .ScheduleRepairWith(_ =>
            {
                dispatchCount++;
                if (failDispatch)
                    throw new DeliberateRuntimeFailure();
            }).Named("repair");
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        entity.Code = "after";
        var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);
        unit.Prepare(runtime);

        using (var transaction = context.Database.BeginTransaction())
        {
            context.SaveChanges();
            var plan = unit.PlanDetailed(runtime, RuntimeImpactDetailLevel.Causal)!;
            var durableWork = plan.Result.GetDurablePolicyWork();
            context.Outbox.Add(new OutboxRecord
            {
                Payload = CreateOutboxPayload(durableWork.RepairRequests.Single())
            });
            context.SaveChanges();
            transaction.Commit();
        }
        unit.Commit(runtime);

        Assert.Throws<DeliberateRuntimeFailure>(() => unit.Dispatch(runtime));
        Assert.Equal(1, runtime.Version);
        Assert.Equal(1, dispatchCount);
        using (var verification = database.CreateContext())
        {
            Assert.Equal("after", verification.Set<UniqueEntity>().Single().Code);
            Assert.Equal($"repair:{entity.Id:D}", verification.Outbox.Single().Payload);
        }

        failDispatch = false;
        unit.Dispatch(runtime);

        Assert.Equal(1, runtime.Version);
        Assert.Equal(2, dispatchCount);
        Assert.Single(context.Outbox);
    }

    private static string CreateOutboxPayload(DurableRepairRequestInfo request)
    {
        return $"{request.DefinitionKey}:{request.Source.KeyParts.Single().Value}";
    }

    [Fact]
    public void Nondurable_policy_work_rolls_back_business_transaction_before_outbox_durability()
    {
        using var database = new SqliteFixture();
        using var context = database.CreateContext();
        var entity = new UniqueEntity { Id = Guid.NewGuid(), Code = "before" };
        context.Add(entity);
        context.SaveChanges();
        var model = new ConsistencyModelBuilder();
        var objects = model.Objects<UniqueEntity>().Named("entities").Key(value => value.Id);
        var code = model.Derived(objects).Compute(value => value.Code).Named("code");
        model.Invariant(objects).Using(code).Must((_, value) => value == "before")
            .ScheduleRepairWith(_ => { });
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        entity.Code = "after";
        var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);
        unit.Prepare(runtime);

        using (var transaction = context.Database.BeginTransaction())
        {
            context.SaveChanges();
            var plan = unit.PlanDetailed(runtime, RuntimeImpactDetailLevel.Causal)!;

            Assert.Throws<InvalidOperationException>(() => plan.Result.GetDurablePolicyWork());
            Assert.Empty(context.ChangeTracker.Entries<OutboxRecord>());
            transaction.Rollback();
        }

        Assert.Equal(0, runtime.Version);
        using var verification = database.CreateContext();
        Assert.Equal("before", verification.Set<UniqueEntity>().Single().Code);
        Assert.Empty(verification.Outbox);
    }

    [Fact]
    public void Outbox_save_failure_rolls_back_business_row_and_leaves_runtime_uncommitted()
    {
        using var database = new SqliteFixture();
        using var context = database.CreateContext();
        var entity = new UniqueEntity { Id = Guid.NewGuid(), Code = "before" };
        context.Add(entity);
        context.Outbox.Add(new OutboxRecord { Payload = "duplicate" });
        context.SaveChanges();
        var model = new ConsistencyModelBuilder();
        var objects = model.Objects<UniqueEntity>().Key(value => value.Id);
        model.Derived(objects).Compute(value => value.Code);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        entity.Code = "after";
        var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);
        unit.Prepare(runtime);

        using (var transaction = context.Database.BeginTransaction())
        {
            context.SaveChanges();
            _ = unit.PlanDetailed(runtime);
            context.Outbox.Add(new OutboxRecord { Payload = "duplicate" });
            Assert.Throws<DbUpdateException>(() => context.SaveChanges());
            transaction.Rollback();
        }

        Assert.Equal(0, runtime.Version);
        using var verification = database.CreateContext();
        Assert.Equal("before", verification.Set<UniqueEntity>().Single(value => value.Id == entity.Id).Code);
        Assert.Single(verification.Outbox);
    }

    [Fact]
    public void Plan_failure_after_generated_key_save_rolls_back_database_and_runtime()
    {
        using var database = new SqliteFixture();
        using var context = database.CreateContext();
        var gate = new ThrowingGeneratedGate { Throw = true };
        var model = new ConsistencyModelBuilder();
        var objects = model.Objects<GeneratedEntity>().Key(value => value.Id);
        var relation = model.Relation(objects, objects).Where((left, right) => gate.Match(left, right))
            .AllowIncompleteDependencies();
        model.Derived(objects).Using(relation).Compute((_, matches) => matches.Count)
            .AllowIncompleteDependencies();
        var runtime = model.Build().CreateRuntime();
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        var entity = new GeneratedEntity { Code = "rollback-plan" };
        context.Add(entity);
        var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);

        using (var transaction = context.Database.BeginTransaction())
        {
            context.SaveChanges();
            Assert.True(entity.Id > 0);
            unit.Prepare(runtime);
            Assert.Throws<DeliberateRuntimeFailure>(() => unit.PlanDetailed(runtime));
            transaction.Rollback();
        }

        Assert.Equal(0, runtime.Version);
        using var verification = database.CreateContext();
        Assert.Empty(verification.Set<GeneratedEntity>());
    }

    [Fact]
    public void Sqlite_violating_evaluated_plan_is_rejected_before_database_durability()
    {
        using var database = new SqliteFixture();
        using var context = database.CreateContext();
        var entity = new UniqueEntity { Id = Guid.NewGuid(), Code = "valid" };
        context.Add(entity); context.SaveChanges();
        var model = new ConsistencyModelBuilder();
        var objects = model.Objects<UniqueEntity>().Named("stable-entities").Key(x => x.Id);
        var code = model.Derived(objects).Compute(x => x.Code).Named("stable-code");
        var repairs = 0;
        var invariant = model.Invariant(objects).Using(code).Must((_, value) => value != "invalid")
            .Named("stable-code-invariant").ScheduleRepairWith(_ => repairs++);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        Assert.True(runtime.Evaluate(invariant, entity));
        entity.Code = "invalid";
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);
        unit.Prepare(runtime);

        var plan = unit.PlanDetailed(runtime, RuntimeImpactDetailLevel.Causal,
            PlannedInvariantEvaluationMode.Affected)!;

        Assert.True(plan.HasInvariantViolations);
        Assert.Equal(0, runtime.Version); Assert.Equal(0, repairs);
        Assert.Equal(InvariantEvaluationState.Valid, runtime.GetState(invariant, entity));
        using (var verification = database.CreateContext())
            Assert.Equal("valid", verification.Set<UniqueEntity>().AsNoTracking().Single().Code);
        Assert.Equal(EntityState.Modified, context.Entry(entity).State);
        context.Entry(entity).Reload();
        Assert.Equal("valid", entity.Code);
    }

    [Fact]
    public void Sqlite_valid_evaluated_plan_persists_then_installs_exact_runtime_plan()
    {
        using var database = new SqliteFixture(); using var context = database.CreateContext();
        var entity = new UniqueEntity { Id = Guid.NewGuid(), Code = "before" };
        context.Add(entity); context.SaveChanges();
        var counter = new EvaluationCounter(); var model = new ConsistencyModelBuilder();
        var objects = model.Objects<UniqueEntity>().Named("valid-entities").Key(x => x.Id);
        var code = model.Derived(objects).Compute(x => x.Code);
        var invariant = model.Invariant(objects).Using(code).Must((_, value) => counter.IsValid(value))
            .AllowIncompleteDependencies().Named("valid-code-invariant");
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        Assert.True(runtime.Evaluate(invariant, entity)); entity.Code = "after";
        var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker,
            new RelationUnitOfWorkMappings().Map(objects));
        unit.Prepare(runtime);
        var plan = unit.PlanDetailed(runtime, RuntimeImpactDetailLevel.Causal,
            PlannedInvariantEvaluationMode.Affected)!;
        var callsAfterPlan = counter.Calls;
        Assert.False(plan.HasInvariantViolations); context.SaveChanges();
        unit.Commit(runtime); unit.Dispatch(runtime);
        Assert.Equal(callsAfterPlan, counter.Calls); Assert.Equal(1, runtime.Version);
        Assert.Equal(InvariantEvaluationState.Valid, runtime.GetState(invariant, entity));
        using var verification = database.CreateContext();
        Assert.Equal("after", verification.Set<UniqueEntity>().AsNoTracking().Single().Code);
    }

    [Theory]
    [InlineData("valid-generated", false)]
    [InlineData("invalid-generated", true)]
    public void Generated_key_evaluated_plan_uses_transactional_identity(string code, bool violates)
    {
        using var database = new SqliteFixture(); using var context = database.CreateContext();
        var model = new ConsistencyModelBuilder();
        var objects = model.Objects<GeneratedEntity>().Named("generated-guard").Key(x => x.Id);
        var value = model.Derived(objects).Compute(x => x.Code);
        var invariant = model.Invariant(objects).Using(value).Must((_, current) => current != "invalid-generated")
            .Named("generated-guard-invariant");
        var runtime = model.Build().CreateRuntime(); var entity = new GeneratedEntity { Code = code };
        context.Add(entity);
        var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker,
            new RelationUnitOfWorkMappings().Map(objects));
        using (var transaction = context.Database.BeginTransaction())
        {
            context.SaveChanges(); Assert.True(entity.Id > 0); unit.Prepare(runtime);
            var plan = unit.PlanDetailed(runtime, RuntimeImpactDetailLevel.Causal,
                PlannedInvariantEvaluationMode.Affected)!;
            Assert.Equal(violates, plan.HasInvariantViolations);
            Assert.Equal(entity.Id, Assert.Single(plan.InvariantEvaluations).SourceIdentity!.SourceKey);
            Assert.Equal(0, runtime.Version);
            if (violates) transaction.Rollback(); else transaction.Commit();
            if (!violates) { unit.Commit(runtime); unit.Dispatch(runtime); }
        }
        using var verification = database.CreateContext();
        Assert.Equal(!violates, verification.Set<GeneratedEntity>().Any(x => x.Id == entity.Id));
        Assert.Equal(violates ? 0 : 1, runtime.Version);
        if (violates) Assert.False(runtime.Remove(objects, entity));
        else Assert.True(runtime.GetState(invariant, entity) == InvariantEvaluationState.Valid);
    }

    [Fact]
    public void Generated_key_valid_plan_materializes_before_database_and_runtime_commit()
    {
        using var database = new SqliteFixture(); using var context = database.CreateContext();
        var model = new ConsistencyModelBuilder();
        var objects = model.Objects<GeneratedEntity>().Key(x => x.Id);
        var length = model.Derived(objects).Compute(x => x.Code.Length);
        var runtime = model.Build().CreateRuntime();
        var entity = new GeneratedEntity { Code = "generated" };
        context.Add(entity);
        var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker,
            new RelationUnitOfWorkMappings().Map(objects));

        using (var transaction = context.Database.BeginTransaction())
        {
            context.SaveChanges();
            Assert.True(entity.Id > 0);
            unit.Prepare(runtime);
            var plan = unit.PlanDetailed(runtime, RuntimeImpactDetailLevel.Causal,
                PlannedInvariantEvaluationMode.None, PlannedDerivedEvaluationMode.Affected)!;
            var evaluation = Assert.Single(plan.DerivedEvaluations);
            Assert.Equal(entity.Id, evaluation.SourceIdentity!.SourceKey);
            entity.Mirror = Assert.IsType<int>(evaluation.Value);
            context.ChangeTracker.DetectChanges();
            context.SaveChanges();
            transaction.Commit();
            unit.Commit(runtime); unit.Dispatch(runtime);
        }

        var persisted = database.CreateContext().Set<GeneratedEntity>().AsNoTracking().Single();
        Assert.Equal(entity.Code.Length, persisted.Mirror);
        Assert.Equal(1, runtime.Version);
    }

    private sealed class EvaluationCounter
    {
        public int Calls { get; private set; }
        public bool IsValid(string value) { Calls++; return value.Length > 0; }
    }

    [Fact]
    public void Consistent_save_rejects_enforced_violation_and_materializes_valid_value()
    {
        using var database = new SqliteFixture(); using var context = database.CreateContext();
        var entity = new MirrorEntity { Id = Guid.NewGuid(), Input = 2, Mirror = 4 };
        context.Add(entity); context.SaveChanges();
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MirrorEntity>().Key(x => x.Id);
        var doubled = model.Derived(objects).Compute(x => x.Input * 2);
        var invariant = model.Invariant(objects).Using(doubled).Must((_, value) => value <= 10);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        var mappings = new RelationEfCoreMappings().Map(objects).Materialize(doubled, x => x.Mirror).Enforce(invariant);
        entity.Input = 20;
        Assert.Throws<RelationInvariantViolationException>(() => context.SaveChangesConsistently(runtime, mappings));
        Assert.Equal(0, runtime.Version); Assert.Equal(4, database.CreateContext().Set<MirrorEntity>().AsNoTracking().Single().Mirror);
        entity.Input = 5;
        context.SaveChangesConsistently(runtime, mappings);
        Assert.Equal(1, runtime.Version); Assert.Equal(10, entity.Mirror);
        Assert.Equal(10, database.CreateContext().Set<MirrorEntity>().AsNoTracking().Single().Mirror);
    }

    [Fact]
    public void Consistent_save_rejects_store_generated_relations_key_before_database_write()
    {
        using var database = new SqliteFixture(); using var context = database.CreateContext();
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<GeneratedEntity>().Key(x => x.Id);
        var runtime = model.Build().CreateRuntime();
        var mappings = new RelationEfCoreMappings().Map(objects);
        var entity = new GeneratedEntity { Code = "new" };
        context.Add(entity);

        Assert.Throws<RelationStoreGeneratedKeyRequiresManualWorkflowException>(
            () => context.SaveChangesConsistently(runtime, mappings));

        Assert.Equal(0, entity.Id);
        Assert.Equal(0, runtime.Version);
        Assert.False(database.CreateContext().Set<GeneratedEntity>().Any());
    }

    [Fact]
    public void Consistency_interceptor_materializes_and_commits_runtime_after_database_success()
    {
        using var database = new SqliteFixture();
        var entity = new MirrorEntity { Id = Guid.NewGuid(), Input = 2, Mirror = 4 };
        using (var seed = database.CreateContext()) { seed.Add(entity); seed.SaveChanges(); seed.Entry(entity).State = EntityState.Detached; }
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MirrorEntity>().Key(x => x.Id);
        var doubled = model.Derived(objects).Compute(x => x.Input * 2);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        var mappings = new RelationEfCoreMappings().Map(objects).Materialize(doubled, x => x.Mirror);
        var interceptor = new RelationConsistencySaveChangesInterceptor(runtime, mappings, new());
        using var context = database.CreateContext(interceptor);
        context.Attach(entity);
        entity.Input = 5;

        context.SaveChanges();

        Assert.Equal(1, runtime.Version);
        Assert.Equal(10, entity.Mirror);
        Assert.Equal(10, database.CreateContext().Set<MirrorEntity>().AsNoTracking().Single().Mirror);
    }

    [Fact]
    public void Materialization_setter_failure_restores_prior_mirror_writes_before_sql()
    {
        using var database = new SqliteFixture(); using var context = database.CreateContext();
        var first = new ThrowingMirrorEntity { Id = Guid.NewGuid() };
        var second = new ThrowingMirrorEntity { Id = Guid.NewGuid() };
        context.AddRange(first, second); context.SaveChanges();
        var model = new ConsistencyModelBuilder();
        var objects = model.Objects<ThrowingMirrorEntity>().Key(x => x.Id);
        var doubled = model.Derived(objects).Compute(x => x.Input * 2);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [first, second]));
        var mappings = new RelationEfCoreMappings().Map(objects).Materialize(doubled, x => x.Mirror);
        first.Input = 1;
        second.Input = 2;
        second.ThrowOnFour = true;

        Assert.Throws<InvalidOperationException>(() => context.SaveChangesConsistently(runtime, mappings));

        Assert.Equal(0, first.Mirror);
        Assert.Equal(0, second.Mirror);
        Assert.Equal(0, runtime.Version);
        var persisted = database.CreateContext().Set<ThrowingMirrorEntity>().AsNoTracking().ToArray();
        Assert.All(persisted, x => { Assert.Equal(0, x.Input); Assert.Equal(0, x.Mirror); });
    }

    [Fact]
    public void Invariant_exception_prevents_sql_mirror_write_and_runtime_commit()
    {
        using var database = new SqliteFixture(); using var context = database.CreateContext();
        var entity = new MirrorEntity { Id = Guid.NewGuid(), Input = 1, Mirror = 2 };
        context.Add(entity); context.SaveChanges();
        var gate = new ThrowingInvariantGate();
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MirrorEntity>().Key(x => x.Id);
        var doubled = model.Derived(objects).Compute(x => x.Input * 2);
        var invariant = model.Invariant(objects).Using(doubled).Must((_, value) => gate.Check(value))
            .AllowIncompleteDependencies();
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        var mappings = new RelationEfCoreMappings().Map(objects).Materialize(doubled, x => x.Mirror).Enforce(invariant);
        entity.Input = 2;
        gate.Throw = true;

        Assert.Throws<InvalidOperationException>(() => context.SaveChangesConsistently(runtime, mappings));

        Assert.Equal(2, entity.Mirror);
        Assert.Equal(0, runtime.Version);
        var persisted = database.CreateContext().Set<MirrorEntity>().AsNoTracking().Single();
        Assert.Equal(1, persisted.Input);
        Assert.Equal(2, persisted.Mirror);
    }

    [Fact]
    public void Validate_mode_persists_domain_change_without_materializing_mirror()
    {
        using var database = new SqliteFixture(); using var context = database.CreateContext();
        var entity = new MirrorEntity { Id = Guid.NewGuid(), Input = 1, Mirror = 2 };
        context.Add(entity); context.SaveChanges();
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MirrorEntity>().Key(x => x.Id);
        var doubled = model.Derived(objects).Compute(x => x.Input * 2);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        entity.Input = 3;

        context.SaveChangesConsistently(runtime,
            new RelationEfCoreMappings().Map(objects).Materialize(doubled, x => x.Mirror),
            new RelationEfCoreConsistencyOptions { SaveBehavior = RelationEfCoreSaveBehavior.Validate });

        var persisted = database.CreateContext().Set<MirrorEntity>().AsNoTracking().Single();
        Assert.Equal(3, persisted.Input);
        Assert.Equal(2, persisted.Mirror);
        Assert.Equal(1, runtime.Version);
    }

    [Fact]
    public void Unenforced_invariant_violation_does_not_block_consistent_save()
    {
        using var database = new SqliteFixture(); using var context = database.CreateContext();
        var entity = new MirrorEntity { Id = Guid.NewGuid(), Input = 1, Mirror = 2 };
        context.Add(entity); context.SaveChanges();
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MirrorEntity>().Key(x => x.Id);
        var doubled = model.Derived(objects).Compute(x => x.Input * 2);
        model.Invariant(objects).Using(doubled).Must((_, value) => value <= 2);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        entity.Input = 3;

        context.SaveChangesConsistently(runtime,
            new RelationEfCoreMappings().Map(objects).Materialize(doubled, x => x.Mirror));

        Assert.Equal(6, database.CreateContext().Set<MirrorEntity>().AsNoTracking().Single().Mirror);
        Assert.Equal(1, runtime.Version);
    }

    [Fact]
    public void Explicit_transaction_is_rejected_before_consistent_save_sql()
    {
        using var database = new SqliteFixture(); using var context = database.CreateContext();
        var entity = new MirrorEntity { Id = Guid.NewGuid(), Input = 1, Mirror = 2 };
        context.Add(entity); context.SaveChanges();
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MirrorEntity>().Key(x => x.Id);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        entity.Input = 3;
        using var transaction = context.Database.BeginTransaction();

        Assert.Throws<RelationUnsupportedTransactionException>(() => context.SaveChangesConsistently(runtime,
            new RelationEfCoreMappings().Map(objects)));

        Assert.Equal(1, database.CreateContext().Set<MirrorEntity>().AsNoTracking().Single().Input);
        Assert.Equal(0, runtime.Version);
    }

    [Fact]
    public void Ambient_transaction_is_rejected_before_consistent_save_sql()
    {
        using var database = new SqliteFixture(); using var context = database.CreateContext();
        var entity = new MirrorEntity { Id = Guid.NewGuid(), Input = 1, Mirror = 2 };
        context.Add(entity); context.SaveChanges();
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MirrorEntity>().Key(x => x.Id);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        entity.Input = 3;
        using var transaction = new System.Transactions.TransactionScope();

        Assert.Throws<RelationUnsupportedTransactionException>(() => context.SaveChangesConsistently(runtime,
            new RelationEfCoreMappings().Map(objects)));

        Assert.Equal(0, runtime.Version);
    }

    [Fact]
    public void Interceptor_database_failure_clears_pending_state_for_next_save()
    {
        using var database = new SqliteFixture();
        using (var seed = database.CreateContext())
        {
            seed.Add(new UniqueEntity { Id = Guid.NewGuid(), Code = "DUP" });
            seed.SaveChanges();
        }
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<UniqueEntity>().Key(x => x.Id);
        var runtime = model.Build().CreateRuntime();
        var mappings = new RelationEfCoreMappings().Map(objects);
        var interceptor = new RelationConsistencySaveChangesInterceptor(runtime, mappings, new());
        using var context = database.CreateContext(interceptor);
        var entity = new UniqueEntity { Id = Guid.NewGuid(), Code = "DUP" };
        context.Add(entity);

        Assert.Throws<DbUpdateException>(() => context.SaveChanges());
        Assert.Equal(0, runtime.Version);
        entity.Code = "OK";
        context.SaveChanges();

        Assert.Equal(1, runtime.Version);
        Assert.Equal(2, database.CreateContext().Set<UniqueEntity>().Count());
    }

    [Fact]
    public void Interceptor_rejects_store_generated_relations_key_before_sql()
    {
        using var database = new SqliteFixture();
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<GeneratedEntity>().Key(x => x.Id);
        var runtime = model.Build().CreateRuntime();
        var interceptor = new RelationConsistencySaveChangesInterceptor(runtime,
            new RelationEfCoreMappings().Map(objects), new());
        using var context = database.CreateContext(interceptor);
        var entity = new GeneratedEntity { Code = "new" };
        context.Add(entity);

        Assert.Throws<RelationStoreGeneratedKeyRequiresManualWorkflowException>(() => context.SaveChanges());

        Assert.Equal(0, entity.Id);
        Assert.Equal(0, runtime.Version);
        Assert.False(database.CreateContext().Set<GeneratedEntity>().Any());
    }

    [Fact]
    public void Interceptor_rejects_reentrant_save_during_consistency_planning()
    {
        using var database = new SqliteFixture();
        var gate = new ReentrantSaveGate();
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MirrorEntity>().Key(x => x.Id);
        var derived = model.Derived(objects).Compute(x => gate.Compute(x.Input)).AllowIncompleteDependencies();
        var runtime = model.Build().CreateRuntime();
        var interceptor = new RelationConsistencySaveChangesInterceptor(runtime,
            new RelationEfCoreMappings().Map(objects).Materialize(derived, x => x.Mirror), new());
        using var context = database.CreateContext(interceptor);
        gate.Context = context;
        var entity = new MirrorEntity { Id = Guid.NewGuid(), Input = 1 };
        context.Add(entity);

        var error = Assert.Throws<InvalidOperationException>(() => context.SaveChanges());

        Assert.Contains("already pending", error.Message);
        Assert.Equal(0, runtime.Version);
        Assert.False(database.CreateContext().Set<MirrorEntity>().Any());
    }

    [Fact]
    public async Task Interceptor_async_save_materializes_and_commits_after_database_success()
    {
        using var database = new SqliteFixture();
        var entity = new MirrorEntity { Id = Guid.NewGuid(), Input = 1, Mirror = 2 };
        using (var seed = database.CreateContext())
        {
            seed.Add(entity); await seed.SaveChangesAsync(); seed.Entry(entity).State = EntityState.Detached;
        }
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MirrorEntity>().Key(x => x.Id);
        var doubled = model.Derived(objects).Compute(x => x.Input * 2);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        var interceptor = new RelationConsistencySaveChangesInterceptor(runtime,
            new RelationEfCoreMappings().Map(objects).Materialize(doubled, x => x.Mirror), new());
        await using var context = database.CreateContext(interceptor);
        context.Attach(entity); entity.Input = 4;

        await context.SaveChangesAsync();

        Assert.Equal(8, entity.Mirror);
        Assert.Equal(1, runtime.Version);
        Assert.Equal(8, database.CreateContext().Set<MirrorEntity>().AsNoTracking().Single().Mirror);
    }

    [Fact]
    public void Interceptor_rejects_explicit_transaction_before_sql()
    {
        using var database = new SqliteFixture();
        var entity = new MirrorEntity { Id = Guid.NewGuid(), Input = 1, Mirror = 2 };
        using (var seed = database.CreateContext()) { seed.Add(entity); seed.SaveChanges(); seed.Entry(entity).State = EntityState.Detached; }
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MirrorEntity>().Key(x => x.Id);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        var interceptor = new RelationConsistencySaveChangesInterceptor(runtime,
            new RelationEfCoreMappings().Map(objects), new());
        using var context = database.CreateContext(interceptor);
        context.Attach(entity); entity.Input = 3;
        using var transaction = context.Database.BeginTransaction();

        Assert.Throws<RelationUnsupportedTransactionException>(() => context.SaveChanges());

        Assert.Equal(0, runtime.Version);
        Assert.Equal(1, database.CreateContext().Set<MirrorEntity>().AsNoTracking().Single().Input);
    }

    [Fact]
    public void Interceptor_rejects_ambient_transaction_before_sql()
    {
        using var database = new SqliteFixture();
        var entity = new MirrorEntity { Id = Guid.NewGuid(), Input = 1, Mirror = 2 };
        using (var seed = database.CreateContext()) { seed.Add(entity); seed.SaveChanges(); seed.Entry(entity).State = EntityState.Detached; }
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MirrorEntity>().Key(x => x.Id);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        var interceptor = new RelationConsistencySaveChangesInterceptor(runtime,
            new RelationEfCoreMappings().Map(objects), new());
        using var context = database.CreateContext(interceptor);
        context.Attach(entity); entity.Input = 3;
        using var transaction = new System.Transactions.TransactionScope();

        Assert.Throws<RelationUnsupportedTransactionException>(() => context.SaveChanges());

        Assert.Equal(0, runtime.Version);
    }

    [Fact]
    public void Interceptor_state_is_reusable_across_dbcontext_instances()
    {
        using var database = new SqliteFixture();
        var first = new MirrorEntity { Id = Guid.NewGuid(), Input = 1 };
        var second = new MirrorEntity { Id = Guid.NewGuid(), Input = 1 };
        using (var seed = database.CreateContext())
        {
            seed.AddRange(first, second); seed.SaveChanges();
            seed.Entry(first).State = EntityState.Detached; seed.Entry(second).State = EntityState.Detached;
        }
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MirrorEntity>().Key(x => x.Id);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [first, second]));
        var interceptor = new RelationConsistencySaveChangesInterceptor(runtime,
            new RelationEfCoreMappings().Map(objects), new());
        using (var firstContext = database.CreateContext(interceptor))
        {
            firstContext.Attach(first); first.Input = 2; firstContext.SaveChanges();
        }
        using (var secondContext = database.CreateContext(interceptor))
        {
            secondContext.Attach(second); second.Input = 3; secondContext.SaveChanges();
        }

        Assert.Equal(2, runtime.Version);
        Assert.Equal(5, database.CreateContext().Set<MirrorEntity>().Sum(x => x.Input));
    }

    [Fact]
    public void Interceptor_runtime_install_failure_after_sql_uses_synchronization_exception()
    {
        using var database = new SqliteFixture();
        var saved = new MirrorEntity { Id = Guid.NewGuid(), Input = 1 };
        var independent = new MirrorEntity { Id = Guid.NewGuid(), Input = 1 };
        using (var seed = database.CreateContext()) { seed.Add(saved); seed.SaveChanges(); seed.Entry(saved).State = EntityState.Detached; }
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MirrorEntity>().Key(x => x.Id);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [saved, independent]));
        var consistency = new RelationConsistencySaveChangesInterceptor(runtime,
            new RelationEfCoreMappings().Map(objects), new());
        var advance = new AdvanceRuntimeOnSavingInterceptor(() =>
        {
            independent.Input = 2;
            runtime.Apply(Change.Property(objects, independent, x => x.Input, 1, 2));
        });
        using var context = database.CreateContext(consistency, advance);
        context.Attach(saved); saved.Input = 4;

        var error = Assert.Throws<RelationRuntimeSynchronizationException>(() => context.SaveChanges());

        Assert.True(error.DatabaseOperationSucceeded);
        Assert.Equal(1, runtime.Version);
        Assert.Equal(4, database.CreateContext().Set<MirrorEntity>().AsNoTracking().Single().Input);
    }

    [Fact]
    public void Unenforced_schedule_repair_dispatches_without_blocking_sql()
    {
        using var database = new SqliteFixture(); using var context = database.CreateContext();
        var entity = new MirrorEntity { Id = Guid.NewGuid(), Input = 1 };
        context.Add(entity); context.SaveChanges();
        var repairs = new List<MirrorEntity>();
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MirrorEntity>().Key(x => x.Id);
        var value = model.Derived(objects).Compute(x => x.Input);
        model.Invariant(objects).Using(value).Must((_, current) => current <= 2).ScheduleRepairWith(repairs.Add);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        entity.Input = 3;

        context.SaveChangesConsistently(runtime, new RelationEfCoreMappings().Map(objects));

        Assert.Equal([entity], repairs);
        Assert.Equal(3, database.CreateContext().Set<MirrorEntity>().AsNoTracking().Single().Input);
        Assert.Equal(1, runtime.Version);
    }

    [Fact]
    public void Unaffected_unknown_invariant_state_does_not_block_enforced_save()
    {
        using var database = new SqliteFixture(); using var context = database.CreateContext();
        var untouched = new MirrorEntity { Id = Guid.NewGuid(), Input = -1 };
        var changed = new MirrorEntity { Id = Guid.NewGuid(), Input = 1 };
        context.AddRange(untouched, changed); context.SaveChanges();
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MirrorEntity>().Key(x => x.Id);
        var value = model.Derived(objects).Compute(x => x.Input);
        var invariant = model.Invariant(objects).Using(value).Must((_, current) => current >= 0);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [untouched, changed]));
        Assert.Equal(InvariantEvaluationState.Unknown, runtime.GetState(invariant, untouched));
        changed.Input = 2;

        context.SaveChangesConsistently(runtime,
            new RelationEfCoreMappings().Map(objects).Enforce(invariant));

        Assert.Equal(InvariantEvaluationState.Unknown, runtime.GetState(invariant, untouched));
        Assert.Equal(2, database.CreateContext().Set<MirrorEntity>().AsNoTracking().Single(x => x.Id == changed.Id).Input);
    }

    [Fact]
    public void Interceptor_dispatch_failure_occurs_after_database_and_runtime_commit()
    {
        using var database = new SqliteFixture();
        var entity = new MirrorEntity { Id = Guid.NewGuid(), Input = 1 };
        using (var seed = database.CreateContext()) { seed.Add(entity); seed.SaveChanges(); seed.Entry(entity).State = EntityState.Detached; }
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<MirrorEntity>().Key(x => x.Id);
        var value = model.Derived(objects).Compute(x => x.Input);
        model.Invariant(objects).Using(value).Must((_, current) => current <= 1)
            .ScheduleRepairWith(_ => throw new DispatchFailure());
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        var interceptor = new RelationConsistencySaveChangesInterceptor(runtime,
            new RelationEfCoreMappings().Map(objects), new());
        using var context = database.CreateContext(interceptor);
        context.Attach(entity); entity.Input = 2;

        Assert.Throws<DispatchFailure>(() => context.SaveChanges());

        Assert.Equal(1, runtime.Version);
        Assert.Equal(2, database.CreateContext().Set<MirrorEntity>().AsNoTracking().Single().Input);
    }

    [Fact]
    public void Materialization_source_must_be_same_instance_tracked_by_saving_context()
    {
        using var database = new SqliteFixture(); using var context = database.CreateContext();
        var source = new MirrorEntity { Id = Guid.NewGuid(), Input = 1 };
        var item = new MaterializationItem { Id = Guid.NewGuid(), Value = 1 };
        context.AddRange(source, item); context.SaveChanges();
        var model = new ConsistencyModelBuilder(); var sources = model.Objects<MirrorEntity>().Key(x => x.Id);
        var items = model.Objects<MaterializationItem>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((left, right) => left.Input == right.Value);
        var count = model.Derived(sources).Using(relation).Compute((_, matches) => matches.Count);
        var runtime = model.Build().CreateRuntime(seed => { seed.Add(sources, [source]); seed.Add(items, [item]); });
        context.Entry(source).State = EntityState.Detached;
        item.Value = 2;

        Assert.Throws<RelationMaterializationSourceNotTrackedException>(() => context.SaveChangesConsistently(runtime,
            new RelationEfCoreMappings().Map(sources).Map(items).Materialize(count, x => x.Mirror)));

        Assert.Equal(0, runtime.Version);
        Assert.Equal(1, database.CreateContext().Set<MaterializationItem>().AsNoTracking().Single().Value);
    }

    [Fact]
    public void Equal_materialized_value_skips_clr_setter()
    {
        using var database = new SqliteFixture(); using var context = database.CreateContext();
        var entity = new ThrowingMirrorEntity { Id = Guid.NewGuid(), Input = 1, Mirror = 1 };
        context.Add(entity); context.SaveChanges();
        var writes = entity.MirrorWrites;
        var model = new ConsistencyModelBuilder(); var objects = model.Objects<ThrowingMirrorEntity>().Key(x => x.Id);
        var parity = model.Derived(objects).Compute(x => x.Input % 2);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        entity.Input = 3;

        context.SaveChangesConsistently(runtime,
            new RelationEfCoreMappings().Map(objects).Materialize(parity, x => x.Mirror));

        Assert.Equal(writes, entity.MirrorWrites);
        Assert.Equal(1, entity.Mirror);
        Assert.Equal(1, runtime.Version);
    }

    private sealed class SqliteFixture : IDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        public SqliteFixture()
        {
            _connection.Open();
            using var context = CreateContext();
            context.Database.EnsureCreated();
        }

        public SqliteContext CreateContext(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) =>
            new(_connection, interceptors);
        public void Dispose() => _connection.Dispose();
    }

    private sealed class SqliteContext(SqliteConnection connection,
        Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) : DbContext
    {
        public DbSet<ConcurrencyEntity> ConcurrencyEntities => Set<ConcurrencyEntity>();
        public DbSet<OutboxRecord> Outbox => Set<OutboxRecord>();

        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            options.UseSqlite(connection);
            if (interceptors.Length > 0) options.AddInterceptors(interceptors);
        }

        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<UniqueEntity>().HasIndex(entity => entity.Code).IsUnique();
            model.Entity<GeneratedEntity>();
            model.Entity<ConcurrencyEntity>().Property(entity => entity.Version).IsConcurrencyToken();
            model.Entity<OutboxRecord>().HasIndex(entity => entity.Payload).IsUnique();
            model.Entity<MirrorEntity>();
            model.Entity<ThrowingMirrorEntity>().Ignore(x => x.ThrowOnFour);
            model.Entity<ThrowingMirrorEntity>().Ignore(x => x.MirrorWrites);
            model.Entity<MaterializationItem>();
            model.Entity<CascadeChild>().HasOne(entity => entity.Parent).WithMany(entity => entity.Children)
                .HasForeignKey(entity => entity.ParentId).OnDelete(DeleteBehavior.Cascade);
            model.Entity<OwnedOwner>().OwnsOne(entity => entity.Settings);
            model.Entity<OwnedOwner>().Navigation(entity => entity.Settings).IsRequired(false);
            model.Entity<TagGroup>().HasMany(entity => entity.Tags).WithMany(entity => entity.Groups);
        }
    }

    private sealed class UniqueEntity
    {
        public Guid Id { get; init; }
        public string Code { get; set; } = "";
    }

    private sealed class ThrowingRelationGate
    {
        public bool Throw { get; set; }
        public bool Match(UniqueEntity left, UniqueEntity right) =>
            Throw ? throw new DeliberateRuntimeFailure() : left.Code == right.Code;
    }

    private sealed class DeliberateRuntimeFailure : Exception;

    private sealed class ThrowingGeneratedGate
    {
        public bool Throw { get; set; }
        public bool Match(GeneratedEntity left, GeneratedEntity right) =>
            Throw ? throw new DeliberateRuntimeFailure() : left.Id == right.Id;
    }

    private sealed class GeneratedEntity
    {
        public int Id { get; set; }
        public string Code { get; set; } = "";
        public int Mirror { get; set; }
    }

    private sealed class ConcurrencyEntity
    {
        public Guid Id { get; init; }
        public string Code { get; set; } = "";
        public int Version { get; set; }
    }

    private sealed class OutboxRecord
    {
        public long Id { get; set; }
        public string Payload { get; set; } = "";
    }

    private sealed class MirrorEntity
    {
        public Guid Id { get; init; }
        public int Input { get; set; }
        public int Mirror { get; set; }
    }

    private sealed class ThrowingMirrorEntity
    {
        private int _mirror;
        public Guid Id { get; init; }
        public int Input { get; set; }
        public bool ThrowOnFour { get; set; }
        public int MirrorWrites { get; private set; }
        public int Mirror
        {
            get => _mirror;
            set
            {
                MirrorWrites++;
                _mirror = ThrowOnFour && value == 4
                    ? throw new InvalidOperationException("Mirror setter failure.")
                    : value;
            }
        }
    }

    private sealed class MaterializationItem
    {
        public Guid Id { get; init; }
        public int Value { get; set; }
    }

    private sealed class ThrowingInvariantGate
    {
        public bool Throw { get; set; }
        public bool Check(int value) => Throw ? throw new InvalidOperationException("Invariant failure.") : value >= 0;
    }

    private sealed class ReentrantSaveGate
    {
        public DbContext Context { get; set; } = null!;
        public int Compute(int value)
        {
            Context.SaveChanges();
            return value * 2;
        }
    }

    private sealed class AdvanceRuntimeOnSavingInterceptor(Action advance)
        : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> SavingChanges(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result)
        {
            advance();
            return result;
        }
    }

    private sealed class DispatchFailure : Exception;

    private sealed class CascadeParent
    {
        public Guid Id { get; init; }
        public List<CascadeChild> Children { get; } = [];
    }

    private sealed class CascadeChild
    {
        public Guid Id { get; init; }
        public Guid ParentId { get; set; }
        public CascadeParent Parent { get; set; } = null!;
        public string Code { get; set; } = "";
    }

    private sealed class OwnedOwner
    {
        public Guid Id { get; init; }
        public OwnedSettings? Settings { get; set; }
    }

    private sealed class OwnedSettings
    {
        public string Code { get; set; } = "";
    }

    private sealed class LabelEntity
    {
        public Guid Id { get; init; }
        public string Code { get; set; } = "";
    }

    private sealed class TagGroup
    {
        public Guid Id { get; init; }
        public List<TagEntity> Tags { get; } = [];
    }

    private sealed class TagEntity
    {
        public Guid Id { get; init; }
        public string Name { get; set; } = "";
        public List<TagGroup> Groups { get; } = [];
    }
}
