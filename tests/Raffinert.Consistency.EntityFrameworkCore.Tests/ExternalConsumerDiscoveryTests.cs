using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Raffinert.Consistency.EntityFrameworkCore;

namespace Raffinert.Consistency.Tests;

public sealed class ExternalConsumerDiscoveryTests
{
    [Fact]
    public async Task Source_change_discovers_unloaded_consumers_and_materializes_all_rates_async()
    {
        using var fixture = DiscoveryFixture.Create();
        using var context = fixture.CreateContext();
        var known = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 1);
        var source = known.Source;
        var calls = new Counter();
        var (runtime, mappings, derived) = fixture.CreateModel(known, calls);

        source.UnitValue = 120m;
        await context.SaveChangesConsistentlyAsync(runtime, mappings);

        Assert.Equal(1, calls.Value);
        Assert.Equal(3, context.Associations.Count());
        Assert.Equal(1, runtime.Version);
        foreach (var association in context.Associations.OrderBy(x => x.Id))
        {
            var expected = association.Source.UnitValue / association.Target.UnitValue;
            Assert.Equal(expected, association.UnitRate);
            Assert.Equal(expected, runtime.Get(derived, association));
        }
    }

    [Fact]
    public void Complete_scope_skips_external_resolver()
    {
        using var fixture = DiscoveryFixture.Create();
        using var context = fixture.CreateContext();
        var known = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 1);
        var calls = new Counter();
        var (runtime, mappings, _) = fixture.CreateModel(known, calls);

        known.Source.UnitValue = 125m;
        context.SaveChangesConsistently(runtime, mappings,
            new ConsistencySaveOptions { Scope = new ConsistencyScope().Complete(fixture.Associations) });

        Assert.Equal(0, calls.Value);
    }

    [Fact]
    public async Task Same_unloaded_root_returned_by_two_navigation_resolvers_is_admitted_once()
    {
        using var fixture = DiscoveryFixture.Create();
        using var context = fixture.CreateContext();
        var known = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 1);
        var target = context.Targets.Single(x => x.Id == 2);
        var sourceCalls = new Counter();
        var targetCalls = new Counter();
        var (runtime, mappings, derived) = fixture.CreateModel(known, sourceCalls, targetCalls);

        known.Source.UnitValue = 120m;
        target.UnitValue = 30m;
        await context.SaveChangesConsistentlyAsync(runtime, mappings);

        Assert.Equal(1, sourceCalls.Value);
        Assert.Equal(1, targetCalls.Value);
        Assert.Equal(1, runtime.Version);
        Assert.Equal(3, context.Associations.Count());
        Assert.Equal(3, runtime.GetObjectSetInstancesForClrType(typeof(DiscoveryAssociation)).Count);
        Assert.All(context.Associations, association =>
            Assert.True(runtime.IsRegistered(fixture.Associations.Definition, association)));
        Assert.Equal(4m, runtime.Get(derived, context.Associations.Single(x => x.Id == 2)));
    }

    [Fact]
    public async Task Tracked_added_consumer_is_not_converted_to_coverage_admission()
    {
        using var fixture = DiscoveryFixture.Create();
        using var context = fixture.CreateContext();
        var known = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 1);
        var target = context.Targets.Single(x => x.Id == 3);
        var added = new DiscoveryAssociation
        {
            Id = 4,
            Source = known.Source,
            SourceId = known.SourceId,
            Target = target,
            TargetId = target.Id,
            UnitRate = 4m
        };
        context.Add(added);
        var calls = new Counter();
        var (runtime, mappings, derived) = fixture.CreateModel(known, calls);
        known.Source.UnitValue = 120m;

        using var transaction = context.Database.BeginTransaction();
        var work = context.CaptureConsistencyUnitOfWork(runtime, mappings,
            new ConsistencySaveOptions { DetailLevel = RuntimeImpactDetailLevel.Causal });
        var plan = Assert.IsType<PreparedImpactPlan>(work.PrepareAndPlan());
        Assert.Single(plan.Result.MutationOrigins,
            origin => origin.Kind == MutationOriginKind.ObjectAdded &&
                ReferenceEquals(origin.Source, added));
        context.SaveChanges();
        transaction.Commit();
        work.CommitAfterDatabaseCommit();
        work.Dispatch();

        Assert.Equal(1, runtime.Version);
        Assert.True(runtime.IsRegistered(fixture.Associations.Definition, added));
        Assert.Equal(4.8m, added.UnitRate);
        Assert.Equal(4.8m, runtime.Get(derived, added));
    }

    [Fact]
    public async Task Discovery_SQL_failure_can_retry_with_same_DbContext()
    {
        using var fixture = DiscoveryFixture.Create();
        using var context = fixture.CreateContext();
        var known = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 1);
        var calls = new Counter();
        var (runtime, mappings, derived) = fixture.CreateModel(known, calls);
        known.Source.UnitValue = 120m;
        context.FailSaveChanges = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.SaveChangesConsistentlyAsync(runtime, mappings));

        Assert.Equal(0, runtime.Version);
        var discovered = context.Associations.Single(x => x.Id == 2);
        Assert.False(runtime.IsRegistered(fixture.Associations.Definition, discovered));
        Assert.Equal(5m, discovered.UnitRate);
        Assert.False(context.Entry(discovered).Property(x => x.UnitRate).IsModified);
        Assert.Equal(1, context.SaveChangesInvocations);

        context.FailSaveChanges = false;
        await context.SaveChangesConsistentlyAsync(runtime, mappings);

        Assert.Equal(2, calls.Value);
        Assert.Equal(1, runtime.Version);
        Assert.Equal(6m, discovered.UnitRate);
        Assert.Equal(6m, runtime.Get(derived, discovered));
    }

    [Fact]
    public async Task Discovery_planning_failure_does_not_install_structural_state()
    {
        using var fixture = DiscoveryFixture.Create();
        using var context = fixture.CreateContext();
        var known = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 1);
        var calls = new Counter();
        var (runtime, mappings, _) = fixture.CreateModel(known, calls, sourceQueryHook: () =>
        {
            calls.Value++;
            throw new InvalidOperationException("resolver failure");
        });
        known.Source.UnitValue = 120m;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.SaveChangesConsistentlyAsync(runtime, mappings));

        Assert.Equal(1, calls.Value);
        Assert.Equal(0, runtime.Version);
        Assert.Equal(0, context.SaveChangesInvocations);
        Assert.False(runtime.IsRegistered(fixture.Associations.Definition,
            context.Associations.Single(x => x.Id == 2)));
    }

    [Fact]
    public async Task Discovery_cancellation_before_durability_leaves_runtime_and_materialization_unchanged()
    {
        using var fixture = DiscoveryFixture.Create();
        using var context = fixture.CreateContext();
        var known = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 1);
        var calls = new Counter();
        using var cancellation = new CancellationTokenSource();
        var (runtime, mappings, _) = fixture.CreateModel(known, calls, sourceQueryHook: cancellation.Cancel);
        known.Source.UnitValue = 120m;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.SaveChangesConsistentlyAsync(runtime, mappings, cancellationToken: cancellation.Token));

        Assert.Equal(1, calls.Value);
        Assert.Equal(0, runtime.Version);
        Assert.Equal(0, context.SaveChangesInvocations);
        Assert.Equal(5m, context.Associations.Single(x => x.Id == 2).UnitRate);
        Assert.False(runtime.IsRegistered(fixture.Associations.Definition,
            context.Associations.Single(x => x.Id == 2)));
    }

    [Fact]
    public async Task Later_open_world_save_reruns_consumer_resolver()
    {
        using var fixture = DiscoveryFixture.Create();
        using var context = fixture.CreateContext();
        var known = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 1);
        var calls = new Counter();
        var (runtime, mappings, derived) = fixture.CreateModel(known, calls);
        known.Source.UnitValue = 120m;
        await context.SaveChangesConsistentlyAsync(runtime, mappings);
        Assert.Equal(1, calls.Value);

        using (var external = fixture.CreateContext())
        {
            external.Add(new DiscoveryAssociation { Id = 4, SourceId = 1, TargetId = 3, UnitRate = 4m });
            external.SaveChanges();
        }

        known.Source.UnitValue = 140m;
        await context.SaveChangesConsistentlyAsync(runtime, mappings);

        Assert.Equal(2, calls.Value);
        var discovered = context.Associations.Single(x => x.Id == 4);
        Assert.True(runtime.IsRegistered(fixture.Associations.Definition, discovered));
        Assert.Equal(5.6m, discovered.UnitRate);
        Assert.Equal(5.6m, runtime.Get(derived, discovered));
    }

    [Fact]
    public async Task Resolver_omitting_other_required_reference_fails_before_sql()
    {
        using var fixture = DiscoveryFixture.Create();
        using var context = fixture.CreateContext();
        var known = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 1);
        var calls = new Counter();
        var (runtime, mappings, _) = fixture.CreateModel(known, calls, includeTarget: false);
        known.Source.UnitValue = 120m;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.SaveChangesConsistentlyAsync(runtime, mappings));

        var missing = context.ChangeTracker.Entries<DiscoveryAssociation>()
            .Single(entry => entry.Entity.Id == 2).Entity;
        Assert.Null(missing.Target);
        Assert.False(context.Entry(missing).Navigation(nameof(DiscoveryAssociation.Target)).IsLoaded);
        Assert.Equal(0, runtime.Version);
        Assert.Equal(0, context.SaveChangesInvocations);
        Assert.False(runtime.IsRegistered(fixture.Associations.Definition, missing));
    }

    [Fact]
    public async Task AsNoTracking_or_detached_return_fails()
    {
        using var fixture = DiscoveryFixture.Create();
        using var context = fixture.CreateContext();
        var known = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 1);
        var calls = new Counter();
        var (runtime, mappings, _) = fixture.CreateModel(known, calls, sourceAsNoTracking: true);
        known.Source.UnitValue = 120m;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.SaveChangesConsistentlyAsync(runtime, mappings));

        Assert.Equal(1, calls.Value);
        Assert.Equal(0, runtime.Version);
        Assert.Equal(0, context.SaveChangesInvocations);
    }

    [Fact]
    public async Task Loaded_optional_null_required_reference_is_accepted()
    {
        using var fixture = DiscoveryFixture.Create();
        using (var seed = fixture.CreateContext())
        {
            seed.Add(new DiscoveryAssociation { Id = 4, SourceId = 1, TargetId = null, UnitRate = 0m });
            seed.SaveChanges();
        }

        using var context = fixture.CreateContext();
        var known = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 1);
        var calls = new Counter();
        var (runtime, mappings, derived) = fixture.CreateModel(known, calls);
        known.Source.UnitValue = 120m;

        await context.SaveChangesConsistentlyAsync(runtime, mappings);

        var optional = context.ChangeTracker.Entries<DiscoveryAssociation>()
            .Single(entry => entry.Entity.Id == 4).Entity;
        Assert.Null(optional.Target);
        Assert.True(context.Entry(optional).Navigation(nameof(DiscoveryAssociation.Target)).IsLoaded);
        Assert.True(runtime.IsRegistered(fixture.Associations.Definition, optional));
        Assert.Equal(0m, optional.UnitRate);
        Assert.Equal(0m, runtime.Get(derived, optional));
    }

    [Fact]
    public async Task Non_null_detached_required_reference_fails_before_sql()
    {
        using var fixture = DiscoveryFixture.Create();
        using var context = fixture.CreateContext();
        var known = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 1);
        var calls = new Counter();
        var (runtime, mappings, _) = fixture.CreateModel(known, calls, sourceDetachedTarget: true);
        known.Source.UnitValue = 120m;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.SaveChangesConsistentlyAsync(runtime, mappings));

        Assert.Equal(1, calls.Value);
        Assert.Equal(0, runtime.Version);
        Assert.Equal(0, context.SaveChangesInvocations);
        Assert.False(runtime.IsRegistered(fixture.Associations.Definition,
            context.Associations.Single(x => x.Id == 2)));
    }

    [Fact]
    public void Validate_ignores_materialization_only_discovery()
    {
        using var fixture = DiscoveryFixture.Create();
        using var context = fixture.CreateContext();
        var known = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 1);
        var calls = new Counter();
        var (runtime, mappings, _) = fixture.CreateModel(known, calls);
        known.Source.UnitValue = 120m;

        context.SaveChangesConsistently(runtime, mappings,
            new ConsistencySaveOptions { SaveBehavior = ConsistencySaveBehavior.Validate });

        Assert.Equal(0, calls.Value);
        Assert.Equal(1, runtime.Version);
        Assert.Equal(5m, context.Associations.Single(x => x.Id == 2).UnitRate);
    }

    [Fact]
    public void RecalculateAndValidate_activates_materialization_discovery()
    {
        using var fixture = DiscoveryFixture.Create();
        using var context = fixture.CreateContext();
        var known = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 1);
        var calls = new Counter();
        var (runtime, mappings, _) = fixture.CreateModel(known, calls);
        known.Source.UnitValue = 120m;

        context.SaveChangesConsistently(runtime, mappings);

        Assert.Equal(1, calls.Value);
        Assert.Equal(6m, context.Associations.Single(x => x.Id == 2).UnitRate);
    }

    [Fact]
    public void Enforced_invariant_uses_discovery_in_Validate()
    {
        using var fixture = DiscoveryFixture.Create();
        using var context = fixture.CreateContext();
        var known = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 1);
        var calls = new Counter();
        var (runtime, mappings, _) = fixture.CreateModel(known, calls,
            materialize: false, enforceInvariant: true);
        known.Source.UnitValue = 120m;

        context.SaveChangesConsistently(runtime, mappings,
            new ConsistencySaveOptions { SaveBehavior = ConsistencySaveBehavior.Validate });

        Assert.Equal(1, calls.Value);
        Assert.Equal(1, runtime.Version);
        Assert.Equal(5m, context.Associations.Single(x => x.Id == 2).UnitRate);
    }

    [Fact]
    public void Non_enforced_invariant_does_not_use_discovery()
    {
        using var fixture = DiscoveryFixture.Create();
        using var context = fixture.CreateContext();
        var known = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 1);
        var calls = new Counter();
        var (runtime, mappings, _) = fixture.CreateModel(known, calls, materialize: false);
        known.Source.UnitValue = 120m;

        context.SaveChangesConsistently(runtime, mappings,
            new ConsistencySaveOptions { SaveBehavior = ConsistencySaveBehavior.Validate });

        Assert.Equal(0, calls.Value);
        Assert.Equal(1, runtime.Version);
    }

    [Fact]
    public void Unmaterialized_derived_definition_does_not_use_discovery()
    {
        using var fixture = DiscoveryFixture.Create();
        using var context = fixture.CreateContext();
        var known = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 1);
        var calls = new Counter();
        var (runtime, mappings, _) = fixture.CreateModel(known, calls, materialize: false);
        known.Source.UnitValue = 120m;

        context.SaveChangesConsistently(runtime, mappings);

        Assert.Equal(0, calls.Value);
        Assert.Equal(1, runtime.Version);
    }

    [Fact]
    public async Task Already_registered_resolver_result_is_not_admitted_again()
    {
        using var fixture = DiscoveryFixture.Create();
        using var context = fixture.CreateContext();
        var known = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 1);
        var alreadyKnown = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 2);
        var calls = new Counter();
        var (runtime, mappings, derived) = fixture.CreateModel(known, calls,
            initialAssociations: [known, alreadyKnown]);
        known.Source.UnitValue = 120m;

        await context.SaveChangesConsistentlyAsync(runtime, mappings);

        Assert.Equal(1, calls.Value);
        Assert.Equal(1, runtime.Version);
        Assert.True(runtime.IsRegistered(fixture.Associations.Definition, alreadyKnown));
        Assert.Equal(6m, runtime.Get(derived, alreadyKnown));
    }

    [Fact]
    public async Task Unknown_existing_Modified_consumer_fails_closed()
    {
        using var fixture = DiscoveryFixture.Create();
        using var context = fixture.CreateContext();
        var known = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 1);
        var modified = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 2);
        var calls = new Counter();
        var (runtime, mappings, _) = fixture.CreateModel(known, calls);
        modified.UnitRate = 99m;
        known.Source.UnitValue = 120m;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.SaveChangesConsistentlyAsync(runtime, mappings));

        Assert.Equal(1, calls.Value);
        Assert.Equal(0, runtime.Version);
        Assert.Equal(0, context.SaveChangesInvocations);
        Assert.Equal(EntityState.Modified, context.Entry(modified).State);
    }

    [Fact]
    public async Task Unknown_existing_Deleted_consumer_fails_closed()
    {
        using var fixture = DiscoveryFixture.Create();
        using var context = fixture.CreateContext();
        var known = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 1);
        var deleted = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 2);
        var calls = new Counter();
        var (runtime, mappings, _) = fixture.CreateModel(known, calls);
        context.Remove(deleted);
        known.Source.UnitValue = 120m;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.SaveChangesConsistentlyAsync(runtime, mappings));

        Assert.Equal(1, calls.Value);
        Assert.Equal(0, runtime.Version);
        Assert.Equal(0, context.SaveChangesInvocations);
        Assert.Equal(EntityState.Deleted, context.Entry(deleted).State);
    }

    [Fact]
    public async Task Safe_resolver_superset_is_filtered()
    {
        using var fixture = DiscoveryFixture.Create();
        fixture.AddSecondarySourceAssociation();
        using var context = fixture.CreateContext();
        var known = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 1);
        var calls = new Counter();
        var (runtime, mappings, _) = fixture.CreateModel(known, calls, sourceQuerySuperset: true);
        known.Source.UnitValue = 120m;

        await context.SaveChangesConsistentlyAsync(runtime, mappings);

        var unrelated = context.ChangeTracker.Entries<DiscoveryAssociation>()
            .Single(entry => entry.Entity.Id == 4).Entity;
        Assert.Equal(1, calls.Value);
        Assert.Equal(1, runtime.Version);
        Assert.False(runtime.IsRegistered(fixture.Associations.Definition, unrelated));
    }

    [Fact]
    public async Task Multiple_changed_targets_same_navigation_one_resolver_call()
    {
        using var fixture = DiscoveryFixture.Create();
        fixture.AddSecondarySourceAssociation();
        using var context = fixture.CreateContext();
        var changed = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Where(x => x.Id == 1 || x.Id == 4).OrderBy(x => x.Id).ToArray();
        var calls = new Counter();
        var (runtime, mappings, derived) = fixture.CreateModel(changed[0], calls);
        changed[1].Source.UnitValue = 90m;
        changed[0].Source.UnitValue = 120m;

        await context.SaveChangesConsistentlyAsync(runtime, mappings);

        Assert.Equal(1, calls.Value);
        Assert.Equal(1, runtime.Version);
        Assert.Equal(12m, changed[0].UnitRate);
        Assert.Equal(9m, changed[1].UnitRate);
        Assert.Equal(9m, runtime.Get(derived, changed[1]));
    }

    [Fact]
    public async Task Multiple_changed_targets_on_target_navigation_one_resolver_call()
    {
        using var fixture = DiscoveryFixture.Create();
        using var context = fixture.CreateContext();
        var known = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 1);
        var second = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 2);
        var targetCalls = new Counter();
        var (runtime, mappings, derived) = fixture.CreateModel(known, new Counter(), targetCalls);
        known.Target.UnitValue = 12m;
        second.Target.UnitValue = 25m;

        await context.SaveChangesConsistentlyAsync(runtime, mappings);

        Assert.Equal(1, targetCalls.Value);
        Assert.Equal(1, runtime.Version);
        Assert.Equal(100m / 12m, known.UnitRate);
        Assert.Equal(4m, second.UnitRate);
        Assert.Equal(4m, runtime.Get(derived, second));
    }

    [Fact]
    public async Task Resolver_duplicate_rows_same_reference_do_not_duplicate_admission()
    {
        using var fixture = DiscoveryFixture.Create();
        using var context = fixture.CreateContext();
        var known = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 1);
        var calls = new Counter();
        var (runtime, mappings, _) = fixture.CreateModel(known, calls, duplicateSourceRows: true);
        known.Source.UnitValue = 120m;

        await context.SaveChangesConsistentlyAsync(runtime, mappings);

        Assert.Equal(1, calls.Value);
        Assert.Equal(1, runtime.Version);
        Assert.Equal(3, runtime.GetObjectSetInstancesForClrType(typeof(DiscoveryAssociation)).Count);
    }

    [Fact]
    public async Task Different_instances_with_same_runtime_key_across_resolvers_fail_closed()
    {
        using var fixture = DiscoveryFixture.Create();
        using (var seed = fixture.CreateContext())
        {
            seed.Associations.Single(x => x.Id == 2).RuntimeKey = 20;
            seed.Associations.Single(x => x.Id == 3).RuntimeKey = 20;
            seed.SaveChanges();
        }
        using var context = fixture.CreateContext();
        var known = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 1);
        var changedTarget = context.Targets.Single(x => x.Id == 3);
        var sourceCalls = new Counter();
        var targetCalls = new Counter();
        var (runtime, mappings, _) = fixture.CreateModel(
            known, sourceCalls, targetCalls, useRuntimeKey: true, sourceResultId: 2, targetResultId: 3);
        known.Source.UnitValue = 120m;
        changedTarget.UnitValue = 30m;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.SaveChangesConsistentlyAsync(runtime, mappings));

        Assert.Equal(1, sourceCalls.Value);
        Assert.Equal(1, targetCalls.Value);
        Assert.Equal(0, runtime.Version);
        Assert.Equal(0, context.SaveChangesInvocations);
        Assert.False(runtime.IsRegistered(fixture.Associations.Definition,
            context.Associations.Local.Single(x => x.Id == 2)));
        Assert.False(runtime.IsRegistered(fixture.Associations.Definition,
            context.Associations.Local.Single(x => x.Id == 3)));
        Assert.Equal(5m, context.Associations.Local.Single(x => x.Id == 2).UnitRate);
        Assert.True(context.Entry(known.Source).Property(x => x.UnitValue).IsModified);
    }

    [Fact]
    public async Task All_required_references_loaded_and_tracked_pass()
    {
        using var fixture = DiscoveryFixture.Create();
        using var context = fixture.CreateContext();
        var known = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 1);
        var calls = new Counter();
        var (runtime, mappings, derived) = fixture.CreateModel(known, calls);
        known.Source.UnitValue = 120m;

        await context.SaveChangesConsistentlyAsync(runtime, mappings);

        var discovered = context.Associations.Local.Single(x => x.Id == 2);
        Assert.True(context.Entry(discovered).Reference(x => x.Source).IsLoaded);
        Assert.True(context.Entry(discovered).Reference(x => x.Target).IsLoaded);
        Assert.NotEqual(EntityState.Detached, context.Entry(discovered.Source).State);
        Assert.NotEqual(EntityState.Detached, context.Entry(discovered.Target).State);
        Assert.True(runtime.IsRegistered(fixture.Associations.Definition, discovered));
        Assert.Equal(6m, discovered.UnitRate);
        Assert.Equal(6m, runtime.Get(derived, discovered));
        Assert.Equal(6m, context.Associations.AsNoTracking().Single(x => x.Id == 2).UnitRate);
        Assert.Equal(1, runtime.Version);
    }

    [Fact]
    public async Task Evaluation_closure_uses_exact_ObjectSet_and_navigation_metadata()
    {
        using var fixture = DiscoveryFixture.Create();
        using var context = fixture.CreateContext();
        var known = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 1);
        var builder = new ConsistencyModelBuilder();
        var primary = builder.Objects<DiscoveryAssociation>().Key(x => x.Id);
        var secondary = builder.Objects<DiscoveryAssociation>().Key(x => x.Id);
        var derived = builder.Derived(primary)
            .DependsOn(x => x.Source.UnitValue).DependsOn(x => x.Target.UnitValue)
            .Compute(x => x.Target == null ? 0m : x.Source.UnitValue / x.Target.UnitValue);
        var runtime = builder.Build().CreateRuntime(seed => seed.Add(primary, [known]));
        var mappings = new ConsistencyEfCoreMappings()
            .Map(primary, entry => entry.Entity.Id <= 2)
            .Map(secondary, entry => entry.Entity.Id > 2)
            .Materialize(derived, x => x.UnitRate)
            .DiscoverConsumers(primary, x => x.Source, (db, sources) =>
            {
                var ids = sources.Select(x => x.Id).ToArray();
                return db.Set<DiscoveryAssociation>().Where(x => ids.Contains(x.SourceId))
                    .Include(x => x.Source).Include(x => x.Target);
            })
            .DiscoverConsumers(secondary, x => x.Target, (db, targets) =>
            {
                var ids = targets.Select(x => x.Id).ToArray();
                return db.Set<DiscoveryAssociation>().Where(x => x.TargetId.HasValue &&
                    ids.Contains(x.TargetId.Value)).Include(x => x.Source).Include(x => x.Target);
            });
        known.Source.UnitValue = 120m;

        var error = await Assert.ThrowsAsync<IncompleteConsistencyScopeException>(() =>
            context.SaveChangesConsistentlyAsync(runtime, mappings));

        Assert.Contains(error.Gaps, gap => gap.ObjectSetId == primary.Definition.Id &&
            gap.RequirementKind == ConsistencyScopeRequirementKind.NavigationConsumerCoverage);
        Assert.Equal(0, context.SaveChangesInvocations);
        Assert.Equal(0, runtime.Version);
        Assert.True(context.Entry(known.Source).Property(x => x.UnitValue).IsModified);
    }

    [Fact]
    public void Supported_direct_navigation_with_exact_resolver_can_replace_navigation_complete_scope()
    {
        var model = new ConsistencyModelBuilder();
        var associations = model.Objects<DiscoveryAssociation>().Key(x => x.Id);
        var value = model.Derived(associations).DependsOn(x => x.Source.UnitValue)
            .Compute(x => x.Source.UnitValue);
        var runtime = model.Build().CreateRuntime();
        var mappings = new ConsistencyEfCoreMappings().Map(associations)
            .Materialize(value, x => x.UnitRate)
            .DiscoverConsumers(associations, x => x.Source, (db, _) => db.Set<DiscoveryAssociation>());

        Assert.Empty(mappings.GetScopeGaps(runtime, null, ConsistencySaveBehavior.RecalculateAndValidate));
        Assert.Equal(0, runtime.Version);
    }

    [Fact]
    public void Supported_and_unsupported_navigation_on_same_set_remain_fail_closed()
    {
        var model = new ConsistencyModelBuilder();
        var associations = model.Objects<DiscoveryAssociation>().Key(x => x.Id);
        var value = model.Derived(associations)
            .DependsOn(x => x.Source.UnitValue).DependsOn(x => x.Source.Parent!.UnitValue)
            .Compute(x => x.Source.UnitValue + (x.Source.Parent == null ? 0m : x.Source.Parent.UnitValue));
        var runtime = model.Build().CreateRuntime();
        var mappings = new ConsistencyEfCoreMappings().Map(associations)
            .Materialize(value, x => x.UnitRate)
            .DiscoverConsumers(associations, x => x.Source, (db, _) => db.Set<DiscoveryAssociation>());

        var gaps = mappings.GetScopeGaps(runtime, null, ConsistencySaveBehavior.RecalculateAndValidate);

        Assert.Contains(gaps, gap => gap.ObjectSetId == associations.Definition.Id &&
            gap.RequirementKind == ConsistencyScopeRequirementKind.NavigationConsumerCoverage);
        Assert.Equal(0, runtime.Version);
    }

    [Fact]
    public void Upstream_derived_navigation_requirement_is_preserved()
    {
        var model = new ConsistencyModelBuilder();
        var associations = model.Objects<DiscoveryAssociation>().Key(x => x.Id);
        var upstream = model.Derived(associations).DependsOn(x => x.Source.Parent!.UnitValue)
            .Compute(x => x.Source.Parent == null ? 0m : x.Source.Parent.UnitValue);
        var downstream = model.Derived(associations).Using(upstream)
            .Compute((x, value) => x.Source.UnitValue + value);
        var runtime = model.Build().CreateRuntime();
        var mappings = new ConsistencyEfCoreMappings().Map(associations)
            .Materialize(downstream, x => x.UnitRate)
            .DiscoverConsumers(associations, x => x.Source, (db, _) => db.Set<DiscoveryAssociation>());

        var gaps = mappings.GetScopeGaps(runtime, null, ConsistencySaveBehavior.RecalculateAndValidate);

        Assert.Contains(gaps, gap => gap.RequirementKind ==
            ConsistencyScopeRequirementKind.NavigationConsumerCoverage);
        Assert.Equal(0, runtime.Version);
    }

    [Fact]
    public void All_active_navigation_obligations_require_exact_resolvers()
    {
        var model = new ConsistencyModelBuilder();
        var associations = model.Objects<DiscoveryAssociation>().Key(x => x.Id);
        var value = model.Derived(associations)
            .DependsOn(x => x.Source.UnitValue).DependsOn(x => x.Target.UnitValue)
            .Compute(x => x.Target == null ? 0m : x.Source.UnitValue / x.Target.UnitValue);
        var runtime = model.Build().CreateRuntime();
        var mappings = new ConsistencyEfCoreMappings().Map(associations)
            .Materialize(value, x => x.UnitRate)
            .DiscoverConsumers(associations, x => x.Source, (db, _) => db.Set<DiscoveryAssociation>());

        var gaps = mappings.GetScopeGaps(runtime, null, ConsistencySaveBehavior.RecalculateAndValidate);

        Assert.Contains(gaps, gap => gap.RequirementKind ==
            ConsistencyScopeRequirementKind.NavigationConsumerCoverage);
        Assert.Equal(0, runtime.Version);
    }

    [Fact]
    public void Same_CLR_type_in_two_ObjectSets_does_not_cross_satisfy_resolver()
    {
        var model = new ConsistencyModelBuilder();
        var required = model.Objects<DiscoveryAssociation>().Key(x => x.Id);
        var other = model.Objects<DiscoveryAssociation>().Key(x => x.Id);
        var value = model.Derived(required).DependsOn(x => x.Source.UnitValue)
            .Compute(x => x.Source.UnitValue);
        var runtime = model.Build().CreateRuntime();
        var mappings = new ConsistencyEfCoreMappings().Map(required).Map(other)
            .Materialize(value, x => x.UnitRate)
            .DiscoverConsumers(other, x => x.Source, (db, _) => db.Set<DiscoveryAssociation>());

        var gaps = mappings.GetScopeGaps(runtime, null, ConsistencySaveBehavior.RecalculateAndValidate);

        Assert.Contains(gaps, gap => gap.ObjectSetId == required.Definition.Id &&
            gap.RequirementKind == ConsistencyScopeRequirementKind.NavigationConsumerCoverage);
        Assert.Equal(0, runtime.Version);
    }

    [Fact]
    public void RelationSourceCoverage_is_never_substituted_by_DiscoverConsumers()
    {
        var (runtime, mappings, associations, _) = CreateRelationScopeScenario();

        var gaps = mappings.GetScopeGaps(runtime, null, ConsistencySaveBehavior.RecalculateAndValidate);

        Assert.Contains(gaps, gap => gap.ObjectSetId == associations.Definition.Id &&
            gap.RequirementKind == ConsistencyScopeRequirementKind.RelationSourceCoverage);
        Assert.Equal(0, runtime.Version);
    }

    [Fact]
    public void RelationTargetCoverage_is_never_substituted_by_DiscoverConsumers()
    {
        var (runtime, mappings, _, targets) = CreateRelationScopeScenario();

        var gaps = mappings.GetScopeGaps(runtime, null, ConsistencySaveBehavior.RecalculateAndValidate);

        Assert.Contains(gaps, gap => gap.ObjectSetId == targets.Definition.Id &&
            gap.RequirementKind == ConsistencyScopeRequirementKind.RelationTargetCoverage);
        Assert.Equal(0, runtime.Version);
    }

    [Fact]
    public void ProjectedConsumerCoverage_is_never_substituted_by_DiscoverConsumers()
    {
        var model = new ConsistencyModelBuilder();
        var targets = model.Objects<DiscoveryTarget>().Key(x => x.Id);
        var associations = model.Objects<DiscoveryAssociation>().Key(x => x.Id);
        var targetValue = model.Derived(targets).Compute(x => x.UnitValue);
        var projected = model.Derived(associations).Using(x => x.Target, targetValue)
            .Compute((_, value) => value);
        var runtime = model.Build().CreateRuntime();
        var mappings = new ConsistencyEfCoreMappings().Map(targets).Map(associations)
            .Materialize(projected, x => x.UnitRate)
            .DiscoverConsumers(associations, x => x.Target, (db, _) => db.Set<DiscoveryAssociation>());

        var gaps = mappings.GetScopeGaps(runtime, null, ConsistencySaveBehavior.RecalculateAndValidate);

        Assert.Contains(gaps, gap => gap.ObjectSetId == associations.Definition.Id &&
            gap.RequirementKind == ConsistencyScopeRequirementKind.ProjectedConsumerCoverage);
        Assert.Equal(0, runtime.Version);
    }

    private static (ConsistencyRuntime Runtime, ConsistencyEfCoreMappings Mappings,
        ObjectSet<DiscoveryAssociation> Associations, ObjectSet<DiscoveryTarget> Targets)
        CreateRelationScopeScenario()
    {
        var model = new ConsistencyModelBuilder();
        var associations = model.Objects<DiscoveryAssociation>().Key(x => x.Id);
        var targets = model.Objects<DiscoveryTarget>().Key(x => x.Id);
        var relation = model.Relation(associations, targets).Where((left, right) => left.TargetId == right.Id);
        var count = model.Derived(associations).Using(relation)
            .Compute((source, rows) => rows.Count + (source.Source.UnitValue * 0m));
        var runtime = model.Build().CreateRuntime();
        var mappings = new ConsistencyEfCoreMappings().Map(associations).Map(targets)
            .Materialize(count, x => x.UnitRate)
            .DiscoverConsumers(associations, x => x.Source, (db, _) => db.Set<DiscoveryAssociation>());
        return (runtime, mappings, associations, targets);
    }

    private sealed class DiscoveryFixture : IDisposable
    {
        private readonly SqliteConnection _connection;
        internal ObjectSet<DiscoveryAssociation> Associations { get; private set; }

        private DiscoveryFixture(SqliteConnection connection, ObjectSet<DiscoveryAssociation> associations)
        {
            _connection = connection;
            Associations = associations;
        }

        internal static DiscoveryFixture Create()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            var options = new DbContextOptionsBuilder<DiscoveryContext>().UseSqlite(connection).Options;
            using (var context = new DiscoveryContext(options))
            {
                context.Database.EnsureCreated();
                var source = new DiscoverySource { Id = 1, UnitValue = 100m };
                var target1 = new DiscoveryTarget { Id = 1, UnitValue = 10m };
                var target2 = new DiscoveryTarget { Id = 2, UnitValue = 20m };
                var target3 = new DiscoveryTarget { Id = 3, UnitValue = 25m };
                context.AddRange(source, target1, target2, target3,
                    new DiscoveryAssociation { Id = 1, Source = source, Target = target1, UnitRate = 10m },
                    new DiscoveryAssociation { Id = 2, Source = source, Target = target2, UnitRate = 5m },
                    new DiscoveryAssociation { Id = 3, Source = source, Target = target3, UnitRate = 4m });
                context.SaveChanges();
            }
            var model = new ConsistencyModelBuilder();
            var associations = model.Objects<DiscoveryAssociation>().Key(x => x.Id);
            return new DiscoveryFixture(connection, associations);
        }

        internal DiscoveryContext CreateContext() => new(new DbContextOptionsBuilder<DiscoveryContext>()
            .UseSqlite(_connection).Options);

        internal (ConsistencyRuntime Runtime, ConsistencyEfCoreMappings Mappings, Derived<DiscoveryAssociation, decimal> Derived)
            CreateModel(DiscoveryAssociation known, Counter calls, Counter? targetCalls = null,
            Action? sourceQueryHook = null, bool includeTarget = true, bool sourceAsNoTracking = false,
                bool materialize = true, bool enforceInvariant = false,
            bool sourceQuerySuperset = false, IReadOnlyList<DiscoveryAssociation>? initialAssociations = null,
                bool duplicateSourceRows = false, bool sourceDetachedTarget = false,
                bool useRuntimeKey = false, int? sourceResultId = null, int? targetResultId = null)
        {
            var builder = new ConsistencyModelBuilder();
            var associations = useRuntimeKey
                ? builder.Objects<DiscoveryAssociation>().Key(x => x.RuntimeKey)
                : builder.Objects<DiscoveryAssociation>().Key(x => x.Id);
            Associations = associations;
            var derived = builder.Derived(associations)
                .DependsOn(x => x.Source.UnitValue).DependsOn(x => x.Target.UnitValue)
                .Compute(x => x.Target == null ? 0m : x.Source.UnitValue / x.Target.UnitValue);
            var invariant = builder.Invariant(associations).Using(derived).Must((_, value) => value >= 0m);
            var runtime = builder.Build().CreateRuntime(seed => seed.Add(associations,
                initialAssociations ?? [known]));
            var mappings = new ConsistencyEfCoreMappings().Map(associations);
            if (materialize)
                mappings.Materialize(derived, x => x.UnitRate);
            if (enforceInvariant)
                mappings.Enforce(invariant);
            mappings.DiscoverConsumers(associations, x => x.Source, (db, sources) =>
                {
                    sourceQueryHook?.Invoke();
                    calls.Value++;
                    var ids = sources.Select(x => x.Id).ToArray();
                    IQueryable<DiscoveryAssociation> query = db.Set<DiscoveryAssociation>();
                    if (!sourceQuerySuperset)
                        query = query.Where(x => ids.Contains(x.SourceId));
                    if (sourceResultId.HasValue)
                        query = query.Where(x => x.Id == sourceResultId.Value);
                    query = query.Include(x => x.Source);
                    if (includeTarget && !sourceDetachedTarget)
                        query = query.Include(x => x.Target);
                    if (duplicateSourceRows)
                        query = query.Concat(query);
                    if (sourceAsNoTracking)
                        query = query.AsNoTracking();
                    if (sourceDetachedTarget)
                    {
                        var materialized = query.ToArray();
                        foreach (var association in materialized)
                            association.Target = new DiscoveryTarget { Id = association.TargetId!.Value };
                        return materialized.AsQueryable();
                    }
                    return query;
                })
                .DiscoverConsumers(associations, x => x.Target, (db, targets) =>
                {
                    if (targetCalls is not null)
                        targetCalls.Value++;
                    var ids = targets.Select(x => x.Id).ToArray();
                    return db.Set<DiscoveryAssociation>().Where(x => x.TargetId.HasValue &&
                        ids.Contains(x.TargetId.Value) &&
                        (!targetResultId.HasValue || x.Id == targetResultId.Value))
                        .Include(x => x.Source).Include(x => x.Target);
                });
            return (runtime, mappings, derived);
        }

        internal void AddSecondarySourceAssociation()
        {
            using var context = CreateContext();
            var source = new DiscoverySource { Id = 2, UnitValue = 80m };
            var association = new DiscoveryAssociation { Id = 4, Source = source, TargetId = 1, UnitRate = 8m };
            context.AddRange(source, association);
            context.SaveChanges();
        }

        public void Dispose() => _connection.Dispose();
    }

    private sealed class DiscoveryContext(DbContextOptions<DiscoveryContext> options) : DbContext(options)
    {
        internal bool FailSaveChanges { get; set; }
        internal int SaveChangesInvocations { get; private set; }
        public DbSet<DiscoveryAssociation> Associations => Set<DiscoveryAssociation>();
        public DbSet<DiscoveryTarget> Targets => Set<DiscoveryTarget>();

        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            SaveChangesInvocations++;
            if (FailSaveChanges)
                throw new InvalidOperationException("Injected database failure.");
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess,
            CancellationToken cancellationToken = default)
        {
            SaveChangesInvocations++;
            if (FailSaveChanges)
                throw new InvalidOperationException("Injected database failure.");
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<DiscoveryAssociation>().HasOne(x => x.Source).WithMany().HasForeignKey(x => x.SourceId);
            model.Entity<DiscoveryAssociation>().HasOne(x => x.Target).WithMany().HasForeignKey(x => x.TargetId)
                .IsRequired(false);
        }
    }

    private sealed class DiscoverySource
    {
        public int Id { get; set; }
        public decimal UnitValue { get; set; }
        public DiscoverySource? Parent { get; set; }
    }
    private sealed class DiscoveryTarget { public int Id { get; set; } public decimal UnitValue { get; set; } }
    private sealed class DiscoveryAssociation
    {
        public int Id { get; set; }
        public int RuntimeKey { get; set; }
        public int SourceId { get; set; }
        public DiscoverySource Source { get; set; } = null!;
        public int? TargetId { get; set; }
        public DiscoveryTarget Target { get; set; } = null!;
        public decimal UnitRate { get; set; }
    }

    private sealed class Counter { public int Value { get; set; } }
}
