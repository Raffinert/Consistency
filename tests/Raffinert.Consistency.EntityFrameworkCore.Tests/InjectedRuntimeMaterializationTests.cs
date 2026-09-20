using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Raffinert.Consistency.EntityFrameworkCore;

namespace Raffinert.Consistency.Tests;

public sealed class InjectedRuntimeMaterializationTests
{
    [Fact]
    public async Task Injected_service_materializes_before_save_and_persists_with_ordinary_save()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<LinkService>();

        await service.ChangeLeftValueAsync(fixture.LinkId, 55m, CancellationToken.None);

        Assert.Equal(5.5m, service.ObservedRatio);
        Assert.Equal(5.5m, service.ObservedNormalizedRatio);
        await using var verification = fixture.CreateContext();
        var persisted = await verification.Links.AsNoTracking()
            .Include(value => value.Left).Include(value => value.Right).SingleAsync();
        Assert.Equal(55m, persisted.Left.Value);
        Assert.Equal(5.5m, persisted.Ratio);
        Assert.Equal(5.5m, persisted.NormalizedRatio);
    }

    [Fact]
    public async Task Runtime_and_context_resolution_order_uses_one_scoped_runtime_and_session()
    {
        await using var fixture = await Fixture.CreateAsync();
        ConsistencyRuntime firstRuntime;
        await using (var firstScope = fixture.Provider.CreateAsyncScope())
        {
            _ = firstScope.ServiceProvider.GetRequiredService<LinkContext>();
            var runtime = firstRuntime = firstScope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
            var applicationRuntime = firstScope.ServiceProvider.GetRequiredService<IConsistencyRuntime>();
            var session = firstScope.ServiceProvider.GetRequiredService<ConsistencyEfCoreSession<LinkContext>>();
            Assert.Same(runtime, applicationRuntime);
            Assert.Same(applicationRuntime,
                firstScope.ServiceProvider.GetRequiredService<IConsistencyRuntime>());
            Assert.Same(runtime, session.Runtime);
            Assert.Same(firstScope.ServiceProvider.GetRequiredService<LinkContext>(), session.Context);
        }

        await using (var secondScope = fixture.Provider.CreateAsyncScope())
        {
            var runtime = secondScope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
            var applicationRuntime = secondScope.ServiceProvider.GetRequiredService<IConsistencyRuntime>();
            var context = secondScope.ServiceProvider.GetRequiredService<LinkContext>();
            var session = secondScope.ServiceProvider.GetRequiredService<ConsistencyEfCoreSession<LinkContext>>();
            Assert.Same(runtime, applicationRuntime);
            Assert.Same(runtime, session.Runtime);
            Assert.Same(context, session.Context);
            Assert.NotSame(firstRuntime, runtime);
            Assert.NotSame(firstRuntime, applicationRuntime);
        }
    }

    [Fact]
    public async Task Runtime_created_before_query_admits_tracked_entities_automatically()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();

        link.Left.Value = 50m;
        runtime.Materialize(link);

        Assert.Equal(5m, link.Ratio);
        Assert.Equal(5m, link.NormalizedRatio);
    }

    [Fact]
    public async Task Entities_tracked_before_runtime_resolution_are_admitted_when_runtime_binds()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();

        link.Left.Value = 55m;
        runtime.Materialize(link);

        Assert.Equal(5.5m, link.Ratio);
        Assert.Equal(5.5m, link.NormalizedRatio);
    }

    [Fact]
    public async Task Navigation_materialize_keeps_committed_navigation_index_on_old_target_until_save()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();
        var originalLeft = link.Left;
        var replacement = await context.Items.SingleAsync(value => value.Id == 3);
        var leftMember = typeof(Link).GetProperty(nameof(Link.Left))!;

        runtime.Materialize(link);
        Assert.Contains(link, runtime.GetNavigationOwners(leftMember, originalLeft));
        link.Left = replacement;
        link.LeftId = replacement.Id;
        var version = runtime.Version;

        runtime.Materialize(link);

        Assert.Equal(5m, link.Ratio);
        Assert.Equal(5m, link.NormalizedRatio);
        Assert.Equal(version, runtime.Version);
        Assert.Contains(link, runtime.GetNavigationOwners(leftMember, originalLeft));
        Assert.DoesNotContain(link, runtime.GetNavigationOwners(leftMember, replacement));
    }

    [Fact]
    public async Task Navigation_materialize_sql_failure_keeps_committed_navigation_index_on_old_target()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();
        var originalLeft = link.Left;
        var replacement = await context.Items.SingleAsync(value => value.Id == 3);
        var leftMember = typeof(Link).GetProperty(nameof(Link.Left))!;
        var version = runtime.Version;

        runtime.Materialize(link);
        link.Left = replacement;
        link.LeftId = replacement.Id;
        runtime.Materialize(link);
        link.FailureMarker = -1;

        Assert.Equal(5m, link.Ratio);
        Assert.Equal(version, runtime.Version);
        Assert.Contains(link, runtime.GetNavigationOwners(leftMember, originalLeft));
        Assert.DoesNotContain(link, runtime.GetNavigationOwners(leftMember, replacement));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        Assert.Equal(version, runtime.Version);
        Assert.Contains(link, runtime.GetNavigationOwners(leftMember, originalLeft));
        Assert.DoesNotContain(link, runtime.GetNavigationOwners(leftMember, replacement));

        link.FailureMarker = 0;
        runtime.Materialize(link);
        await context.SaveChangesAsync();

        Assert.True(runtime.Version > version);
        Assert.DoesNotContain(link, runtime.GetNavigationOwners(leftMember, originalLeft));
        Assert.Contains(link, runtime.GetNavigationOwners(leftMember, replacement));
    }

    [Fact]
    public async Task Navigation_materialize_success_commits_new_target_only_after_save()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();
        var originalLeft = link.Left;
        var replacement = await context.Items.SingleAsync(value => value.Id == 3);
        var leftMember = typeof(Link).GetProperty(nameof(Link.Left))!;

        runtime.Materialize(link);
        link.Left = replacement;
        link.LeftId = replacement.Id;
        var version = runtime.Version;
        runtime.Materialize(link);

        Assert.Equal(5m, link.Ratio);
        Assert.Equal(version, runtime.Version);
        Assert.Contains(link, runtime.GetNavigationOwners(leftMember, originalLeft));

        await context.SaveChangesAsync();

        Assert.True(runtime.Version > version);
        Assert.DoesNotContain(link, runtime.GetNavigationOwners(leftMember, originalLeft));
        Assert.Contains(link, runtime.GetNavigationOwners(leftMember, replacement));
    }

    [Fact]
    public async Task Projection_selector_materialize_keeps_committed_projection_index_on_old_target()
    {
        await using var fixture = await Fixture.CreateAsync(includeProjection: true);
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();
        var originalLeft = link.Left;
        var replacement = await context.Items.SingleAsync(value => value.Id == 3);
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var version = runtime.Version;
        var leftMember = typeof(Link).GetProperty(nameof(Link.Left))!;

        runtime.Materialize(link);
        Assert.Contains(link, runtime.GetProjectedDownstreams(leftMember, originalLeft));
        link.Left = replacement;
        link.LeftId = replacement.Id;
        runtime.Materialize(link);

        Assert.Equal(100m, link.ProjectedLeftValue);
        Assert.Equal(version, runtime.Version);
        Assert.Contains(link, runtime.GetProjectedDownstreams(leftMember, originalLeft));
        Assert.DoesNotContain(link, runtime.GetProjectedDownstreams(leftMember, replacement));

        await context.SaveChangesAsync();

        Assert.DoesNotContain(link, runtime.GetProjectedDownstreams(leftMember, originalLeft));
        Assert.Contains(link, runtime.GetProjectedDownstreams(leftMember, replacement));
    }

    [Fact]
    public async Task Projection_selector_sql_failure_keeps_committed_projection_index_until_retry()
    {
        await using var fixture = await Fixture.CreateAsync(includeProjection: true);
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();
        var originalLeft = link.Left;
        var replacement = await context.Items.SingleAsync(value => value.Id == 3);
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var leftMember = typeof(Link).GetProperty(nameof(Link.Left))!;

        runtime.Materialize(link);
        link.Left = replacement;
        link.LeftId = replacement.Id;
        runtime.Materialize(link);
        link.FailureMarker = -1;

        Assert.Contains(link, runtime.GetProjectedDownstreams(leftMember, originalLeft));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Contains(link, runtime.GetProjectedDownstreams(leftMember, originalLeft));
        Assert.DoesNotContain(link, runtime.GetProjectedDownstreams(leftMember, replacement));

        link.FailureMarker = 0;
        runtime.Materialize(link);
        await context.SaveChangesAsync();

        Assert.DoesNotContain(link, runtime.GetProjectedDownstreams(leftMember, originalLeft));
        Assert.Contains(link, runtime.GetProjectedDownstreams(leftMember, replacement));
    }

    [Fact]
    public async Task Dirty_scalar_before_first_runtime_resolution_is_rejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();

        link.Left.Value = 55m;

        var error = Assert.Throws<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>());
        Assert.Contains("before mutating", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mapped_irrelevant_scalar_before_first_runtime_resolution_does_not_block_binding()
    {
        await using var fixture = await Fixture.CreateAsync(includeRepairPolicy: true);
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();

        link.Comment = "changed";

        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();

        Assert.True(runtime.IsRegistered(fixture.Links.Definition, link));
        Assert.True(runtime.IsRegistered(fixture.Items.Definition, link.Left));
        Assert.True(runtime.IsRegistered(fixture.Items.Definition, link.Right));
        Assert.Equal(0, runtime.Version);
        Assert.Equal(0, fixture.EvaluationCounter.RepairCallbacks);

        link.Left.Value = 55m;
        runtime.Materialize(link);
        await context.SaveChangesAsync();

        await using var verification = fixture.CreateContext();
        var persisted = await verification.Links.AsNoTracking()
            .Include(value => value.Left).Include(value => value.Right).SingleAsync();
        Assert.Equal("changed", persisted.Comment);
        Assert.Equal(55m, persisted.Left.Value);
        Assert.Equal(5.5m, persisted.Ratio);
        Assert.Equal(5.5m, persisted.NormalizedRatio);
    }

    [Fact]
    public async Task Dirty_navigation_before_first_runtime_resolution_is_rejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();
        var replacement = await context.Items.SingleAsync(value => value.Id == 3);

        link.Left = replacement;
        link.LeftId = replacement.Id;

        var error = Assert.Throws<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>());
        Assert.Contains("before mutating", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Dirty_collection_before_first_runtime_resolution_is_rejected()
    {
        await using var fixture = await Fixture.CreateAsync(includeCollectionDependency: true);
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var left = await context.Items.SingleAsync(value => value.Id == 1);
        var right = await context.Items.SingleAsync(value => value.Id == 2);

        left.LeftLinks.Add(new Link
        {
            Id = 2,
            Left = left,
            Right = right,
            LeftId = left.Id,
            RightId = right.Id
        });
        context.ChangeTracker.DetectChanges();

        var error = Assert.Throws<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>());
        Assert.Contains("before mutating", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Added_mapped_entity_before_first_runtime_resolution_is_rejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var left = await context.Items.SingleAsync(value => value.Id == 1);
        var right = await context.Items.SingleAsync(value => value.Id == 2);
        context.Add(new Link { Id = 2, Left = left, Right = right });

        var error = Assert.Throws<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>());

        Assert.Contains("before mutating", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Removed_mapped_entity_before_first_runtime_resolution_is_rejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();
        context.Remove(link);

        var error = Assert.Throws<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>());

        Assert.Contains("before mutating", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Intervening_change_rebuilds_the_pending_plan()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();

        link.Left.Value = 55m;
        runtime.Materialize(link);
        Assert.Equal(5.5m, link.Ratio);
        link.Left.Value = 50m;

        await context.SaveChangesAsync();

        Assert.Equal(5m, link.Ratio);
        Assert.Equal(5m, link.NormalizedRatio);
        await using var verification = fixture.CreateContext();
        var persisted = await verification.Links.AsNoTracking()
            .Include(value => value.Left).Include(value => value.Right).SingleAsync();
        Assert.Equal(50m, persisted.Left.Value);
        Assert.Equal(5m, persisted.Ratio);
        Assert.Equal(5m, persisted.NormalizedRatio);
    }

    [Fact]
    public async Task Save_without_explicit_materialize_still_persists_mirrors()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        _ = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();

        link.Left.Value = 55m;
        await context.SaveChangesAsync();

        await using var verification = fixture.CreateContext();
        var persisted = await verification.Links.AsNoTracking()
            .Include(value => value.Left).Include(value => value.Right).SingleAsync();
        Assert.Equal(55m, persisted.Left.Value);
        Assert.Equal(5.5m, persisted.Ratio);
        Assert.Equal(5.5m, persisted.NormalizedRatio);
    }

    [Fact]
    public async Task Materialize_does_not_advance_runtime_before_save_and_failure_is_retryable()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();
        var version = runtime.Version;

        link.Left.Value = -1m;
        runtime.Materialize(link);
        Assert.Equal(version, runtime.Version);
        Assert.Equal(-0.1m, link.Ratio);

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Equal(version, runtime.Version);

        link.Left.Value = 55m;
        await context.SaveChangesAsync();
        Assert.True(runtime.Version > version);
        Assert.Equal(5.5m, link.Ratio);
    }

    [Fact]
    public async Task Clean_tracking_before_first_pending_plan_reuses_materialized_plan_at_save()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();
        link.Counter = fixture.EvaluationCounter;
        fixture.EvaluationCounter.ResetMaterialization();
        var revisionBeforeMaterialize = runtime.BaselineRevision;
        var version = runtime.Version;

        Assert.True(revisionBeforeMaterialize > 0);
        Assert.Equal(0, version);

        link.Left.Value = 55m;
        runtime.Materialize(link);
        var materializedRevision = runtime.BaselineRevision;

        Assert.Equal(1, fixture.EvaluationCounter.RatioEvaluations);
        Assert.Equal(1, fixture.EvaluationCounter.NormalizedEvaluations);
        Assert.Equal(1, fixture.EvaluationCounter.RatioWrites);
        Assert.Equal(1, fixture.EvaluationCounter.NormalizedRatioWrites);
        Assert.Equal(version, runtime.Version);

        await context.SaveChangesAsync();

        Assert.Equal(materializedRevision, runtime.BaselineRevision);
        Assert.Equal(1, fixture.EvaluationCounter.RatioEvaluations);
        Assert.Equal(1, fixture.EvaluationCounter.NormalizedEvaluations);
        Assert.Equal(1, fixture.EvaluationCounter.RatioWrites);
        Assert.Equal(1, fixture.EvaluationCounter.NormalizedRatioWrites);
        Assert.Equal(version + 1, runtime.Version);

        await using var verification = fixture.CreateContext();
        var persisted = await verification.Links.AsNoTracking()
            .Include(value => value.Left).Include(value => value.Right).SingleAsync();
        Assert.Equal(55m, persisted.Left.Value);
        Assert.Equal(5.5m, persisted.Ratio);
        Assert.Equal(5.5m, persisted.NormalizedRatio);
    }

    [Fact]
    public async Task Repeated_materialize_without_tracking_keeps_baseline_revision_and_reuses_plan()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();
        link.Counter = fixture.EvaluationCounter;
        fixture.EvaluationCounter.ResetPhysicalWrites();

        link.Left.Value = 55m;
        runtime.Materialize(link);
        var revision = runtime.BaselineRevision;
        var version = runtime.Version;
        runtime.Materialize(link);
        runtime.Materialize(link);

        Assert.Equal(revision, runtime.BaselineRevision);
        Assert.Equal(version, runtime.Version);
        await context.SaveChangesAsync();

        Assert.Equal(1, fixture.EvaluationCounter.RatioEvaluations);
        Assert.Equal(1, fixture.EvaluationCounter.NormalizedEvaluations);
        Assert.Equal(1, fixture.EvaluationCounter.RatioWrites);
        Assert.Equal(1, fixture.EvaluationCounter.NormalizedRatioWrites);
        Assert.True(runtime.Version > version);
    }

    [Fact]
    public async Task Tracking_mapped_consumer_after_materialize_invalidates_pending_plan()
    {
        await using var fixture = await Fixture.CreateAsync(includeSecondLink: true);
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var first = await context.Links.Include(value => value.Left).Include(value => value.Right)
            .SingleAsync(value => value.Id == fixture.LinkId);
        first.Counter = fixture.EvaluationCounter;
        fixture.EvaluationCounter.ResetPhysicalWrites();
        first.Left.Value = 55m;
        runtime.Materialize(first);
        var pendingRevision = runtime.BaselineRevision;
        var version = runtime.Version;

        var second = await context.Links.Include(value => value.Left).Include(value => value.Right)
            .SingleAsync(value => value.Id == 2);
        second.Counter = fixture.EvaluationCounter;

        Assert.True(runtime.BaselineRevision > pendingRevision);
        Assert.Equal(version, runtime.Version);
        await context.SaveChangesAsync();

        Assert.Equal(3, fixture.EvaluationCounter.RatioEvaluations);
        Assert.Equal(3, fixture.EvaluationCounter.NormalizedEvaluations);
        Assert.Equal(version + 1, runtime.Version);
        await using var verification = fixture.CreateContext();
        var persisted = await verification.Links.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync();
        Assert.All(persisted, value =>
        {
            Assert.Equal(5.5m, value.Ratio);
            Assert.Equal(5.5m, value.NormalizedRatio);
        });
    }

    [Fact]
    public async Task Stale_pending_materialization_is_rolled_back_before_revision_rebuild()
    {
        await using var fixture = await Fixture.CreateAsync(includeSecondLink: true);
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var first = await context.Links.Include(value => value.Left).Include(value => value.Right)
            .SingleAsync(value => value.Id == fixture.LinkId);
        first.Counter = fixture.EvaluationCounter;
        fixture.EvaluationCounter.ResetPhysicalWrites();
        first.Left.Value = 55m;

        runtime.Materialize(first);
        _ = await context.Links.Include(value => value.Left).Include(value => value.Right)
            .SingleAsync(value => value.Id == 2);
        await context.SaveChangesAsync();

        Assert.Equal(5.5m, first.Ratio);
        Assert.Equal([5.5m, 6m, 5.5m], fixture.EvaluationCounter.RatioAssignments.Take(3));
        Assert.Equal(0, fixture.EvaluationCounter.RepairCallbacks);
    }

    [Fact]
    public async Task External_discovery_tracking_rebuilds_plan_against_final_baseline_revision()
    {
        await using var fixture = await Fixture.CreateAsync(
            includeRepairPolicy: true, includeSecondLink: true, useConsumerDiscovery: true);
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var first = await context.Links.Include(value => value.Left).Include(value => value.Right)
            .SingleAsync(value => value.Id == fixture.LinkId);
        first.Counter = fixture.EvaluationCounter;
        fixture.EvaluationCounter.ResetPhysicalWrites();
        first.Left.Value = 110m;
        var revision = runtime.BaselineRevision;
        var version = runtime.Version;

        runtime.Materialize(first);

        Assert.True(runtime.BaselineRevision > revision);
        Assert.Equal(version, runtime.Version);
        Assert.Equal(4, fixture.EvaluationCounter.RatioEvaluations);
        Assert.Equal(4, fixture.EvaluationCounter.NormalizedEvaluations);
        await context.SaveChangesAsync();

        Assert.Equal(version + 1, runtime.Version);
        Assert.Equal(0, fixture.EvaluationCounter.RepairCallbacks);
        await using var verification = fixture.CreateContext();
        var persisted = await verification.Links.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync();
        Assert.All(persisted, value =>
        {
            Assert.Equal(11m, value.Ratio);
            Assert.Equal(11m, value.NormalizedRatio);
        });
    }

    [Fact]
    public async Task External_discovery_during_async_save_rebuilds_against_final_baseline_revision()
    {
        await using var fixture = await Fixture.CreateAsync(
            includeRepairPolicy: true, includeSecondLink: true, useConsumerDiscovery: true);
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var first = await context.Links.Include(value => value.Left).Include(value => value.Right)
            .SingleAsync(value => value.Id == fixture.LinkId);
        first.Left.Value = 110m;
        var revision = runtime.BaselineRevision;
        var version = runtime.Version;

        await context.SaveChangesAsync();

        Assert.True(runtime.BaselineRevision > revision);
        Assert.Equal(version + 1, runtime.Version);
        Assert.Equal(4, fixture.EvaluationCounter.RatioEvaluations);
        Assert.Equal(4, fixture.EvaluationCounter.NormalizedEvaluations);
        Assert.Equal(0, fixture.EvaluationCounter.RepairCallbacks);
        await using var verification = fixture.CreateContext();
        var persisted = await verification.Links.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync();
        Assert.All(persisted, value => Assert.Equal(11m, value.Ratio));
    }

    [Fact]
    public async Task Dirty_unmapped_entity_does_not_block_first_runtime_binding()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();
        var note = await context.Notes.SingleAsync();
        note.Text = "changed";

        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();

        Assert.True(runtime.IsRegistered(fixture.Items.Definition, link.Left));
        link.Left.Value = 55m;
        runtime.Materialize(link);
        await context.SaveChangesAsync();
        await using var verification = fixture.CreateContext();
        Assert.Equal("changed", (await verification.Notes.AsNoTracking().SingleAsync()).Text);
        Assert.Equal(5.5m, (await verification.Links.AsNoTracking().SingleAsync()).Ratio);
    }

    [Fact]
    public async Task Targeted_materialize_writes_only_selected_representation_before_save()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();
        link.Counter = fixture.EvaluationCounter;
        fixture.EvaluationCounter.ResetPhysicalWrites();
        link.Left.Value = 55m;
        var version = runtime.Version;

        var value = runtime.Materialize(fixture.Ratio, link);

        Assert.Equal(5.5m, value);
        Assert.Equal(5.5m, link.Ratio);
        Assert.Equal(6m, link.NormalizedRatio);
        Assert.Equal(version, runtime.Version);
        Assert.Equal(1, fixture.EvaluationCounter.RatioEvaluations);
        Assert.Equal(1, fixture.EvaluationCounter.RatioWrites);
        Assert.Equal(0, fixture.EvaluationCounter.NormalizedRatioWrites);

        await context.SaveChangesAsync();

        Assert.Equal(5.5m, link.NormalizedRatio);
        Assert.Equal(1, fixture.EvaluationCounter.RatioEvaluations);
        Assert.Equal(1, fixture.EvaluationCounter.RatioWrites);
        Assert.Equal(1, fixture.EvaluationCounter.NormalizedRatioWrites);
    }

    [Fact]
    public async Task Baseline_admission_does_not_dispatch_repair_until_successful_domain_save()
    {
        await using var fixture = await Fixture.CreateAsync(includeRepairPolicy: true);
        await using var scope = fixture.Provider.CreateAsyncScope();
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();

        Assert.Equal(0, fixture.EvaluationCounter.RepairCallbacks);
        Assert.Equal(0, runtime.Version);

        link.Left.Value = 110m;
        await context.SaveChangesAsync();

        Assert.Equal(0, fixture.EvaluationCounter.RepairCallbacks);
        Assert.True(runtime.Version > 0);
    }

    [Fact]
    public async Task Enforced_repair_enabled_invariant_exposes_filtered_request_before_sql()
    {
        await using var fixture = await Fixture.CreateAsync(
            enforceRatioInvariant: true,
            includeUnenforcedRepairInvariant: true);
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();
        var version = runtime.Version;

        link.Left.Value = -1m;

        var error = await Assert.ThrowsAsync<ConsistencyInvariantViolationException>(
            () => context.SaveChangesAsync());

        var violation = Assert.Single(error.Violations);
        Assert.Equal("ratio-valid", violation.DefinitionKey);
        Assert.Same(link, violation.Source);
        var request = Assert.Single(error.RepairRequests);
        Assert.Equal("ratio-valid", request.DefinitionKey);
        Assert.Same(link, request.Source);
        Assert.Equal(version, runtime.Version);
        Assert.Equal(6m, link.Ratio);
        await using var verification = fixture.CreateContext();
        Assert.Equal(60m, (await verification.Items.AsNoTracking().SingleAsync(value => value.Id == 1)).Value);
    }

    [Fact]
    public async Task Rejected_preview_fingerprints_each_read_and_reuses_current_rejected_plan_only()
    {
        await using var fixture = await Fixture.CreateAsync(enforceRatioInvariant: true);
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var session = scope.ServiceProvider.GetRequiredService<ConsistencyEfCoreSession<LinkContext>>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();
        link.Left.Value = -1m;
        var firstError = await Assert.ThrowsAsync<ConsistencyInvariantViolationException>(
            () => context.SaveChangesAsync());

        using var firstPreview = session.CreateRejectedPreview(firstError);
        Assert.Equal(-0.1m, firstPreview.Evaluate(fixture.Ratio, link));
        Assert.Equal(-0.1m, firstPreview.Evaluate(fixture.Ratio, link));
        Assert.True(session.RejectedPreviewValidationCount > 1);
        Assert.Equal(0L, runtime.Version);

        var secondError = await Assert.ThrowsAsync<ConsistencyInvariantViolationException>(
            () => context.SaveChangesAsync());

        var stale = Assert.Throws<InvalidOperationException>(() =>
            firstPreview.Evaluate(fixture.Ratio, link));
        Assert.Contains("no longer current", stale.Message);
        var superseded = Assert.Throws<InvalidOperationException>(() =>
            session.CreateRejectedPreview(firstError));
        Assert.Contains("current rejected save", superseded.Message);
        using var secondPreview = session.CreateRejectedPreview(secondError);
        Assert.Equal(-0.1m, secondPreview.Evaluate(fixture.Ratio, link));
        link.Left.Value = -2m;
        Assert.Throws<InvalidOperationException>(() =>
            secondPreview.Evaluate(fixture.Ratio, link));
        Assert.Equal(0L, runtime.Version);
    }

    [Fact]
    public async Task Rejected_preview_becomes_stale_after_navigation_retarget()
    {
        await using var fixture = await Fixture.CreateAsync(enforceRatioInvariant: true);
        await using var scope = fixture.Provider.CreateAsyncScope();
        _ = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var session = scope.ServiceProvider.GetRequiredService<ConsistencyEfCoreSession<LinkContext>>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();
        var replacement = await context.Items.SingleAsync(value => value.Id == 3);
        link.Left.Value = -1m;
        var error = await Assert.ThrowsAsync<ConsistencyInvariantViolationException>(
            () => context.SaveChangesAsync());
        using var preview = session.CreateRejectedPreview(error);

        link.Left = replacement;
        link.LeftId = replacement.Id;

        Assert.Throws<InvalidOperationException>(() => preview.Evaluate(fixture.Ratio, link));
    }

    [Fact]
    public async Task Rejected_preview_becomes_stale_after_tracked_addition()
    {
        await using var fixture = await Fixture.CreateAsync(enforceRatioInvariant: true);
        await using var scope = fixture.Provider.CreateAsyncScope();
        _ = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var session = scope.ServiceProvider.GetRequiredService<ConsistencyEfCoreSession<LinkContext>>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();
        link.Left.Value = -1m;
        var error = await Assert.ThrowsAsync<ConsistencyInvariantViolationException>(
            () => context.SaveChangesAsync());
        using var preview = session.CreateRejectedPreview(error);

        context.Add(new Link { Id = 99, Left = link.Left, Right = link.Right, Ratio = 1m });

        Assert.Throws<InvalidOperationException>(() => preview.Evaluate(fixture.Ratio, link));
    }

    [Fact]
    public async Task Rejected_preview_becomes_stale_after_tracked_removal()
    {
        await using var fixture = await Fixture.CreateAsync(
            enforceRatioInvariant: true, includeSecondLink: true);
        await using var scope = fixture.Provider.CreateAsyncScope();
        _ = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var session = scope.ServiceProvider.GetRequiredService<ConsistencyEfCoreSession<LinkContext>>();
        var links = await context.Links.Include(value => value.Left).Include(value => value.Right)
            .OrderBy(value => value.Id).ToArrayAsync();
        links[0].Left.Value = -1m;
        var error = await Assert.ThrowsAsync<ConsistencyInvariantViolationException>(
            () => context.SaveChangesAsync());
        using var preview = session.CreateRejectedPreview(error);

        context.Remove(links[1]);

        Assert.Throws<InvalidOperationException>(() => preview.Evaluate(fixture.Ratio, links[0]));
    }

    [Fact]
    public async Task Rejected_preview_ignores_property_outside_consistency_semantics()
    {
        await using var fixture = await Fixture.CreateAsync(enforceRatioInvariant: true);
        await using var scope = fixture.Provider.CreateAsyncScope();
        _ = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var session = scope.ServiceProvider.GetRequiredService<ConsistencyEfCoreSession<LinkContext>>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();
        link.Left.Value = -1m;
        var error = await Assert.ThrowsAsync<ConsistencyInvariantViolationException>(
            () => context.SaveChangesAsync());
        using var preview = session.CreateRejectedPreview(error);

        link.Comment = "irrelevant after rejection";

        Assert.Equal(-0.1m, preview.Evaluate(fixture.Ratio, link));
    }

    [Fact]
    public async Task Enforced_non_repair_invariant_rejects_without_repair_request()
    {
        await using var fixture = await Fixture.CreateAsync(
            enforceRatioInvariant: true,
            repairEnabledInvariant: false);
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();
        var version = runtime.Version;
        link.Left.Value = -1m;

        var error = await Assert.ThrowsAsync<ConsistencyInvariantViolationException>(
            () => context.SaveChangesAsync());

        Assert.Equal("ratio-valid", Assert.Single(error.Violations).DefinitionKey);
        Assert.Empty(error.RepairRequests);
        Assert.Equal(version, runtime.Version);
        await using var verification = fixture.CreateContext();
        Assert.Equal(60m, (await verification.Items.AsNoTracking().SingleAsync(value => value.Id == 1)).Value);
    }

    [Fact]
    public async Task Incomplete_scope_still_blocks_injected_save()
    {
        await using var fixture = await Fixture.CreateAsync(completeScope: false);
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();

        link.Left.Value = 55m;

        await Assert.ThrowsAsync<IncompleteConsistencyScopeException>(() => context.SaveChangesAsync());
        Assert.Equal(0, runtime.Version);
    }

    [Fact]
    public async Task Generated_consistency_key_guard_still_applies_to_injected_save()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        _ = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var left = await context.Items.SingleAsync(value => value.Id == 1);
        var right = await context.Items.SingleAsync(value => value.Id == 2);

        context.Links.Add(new Link
        {
            Left = left,
            Right = right,
            LeftId = left.Id,
            RightId = right.Id
        });

        await Assert.ThrowsAsync<ConsistencyStoreGeneratedKeyRequiresManualWorkflowException>(
            () => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Setter_failure_rolls_back_injected_materialization_without_runtime_commit()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();
        link.Counter = fixture.EvaluationCounter;
        fixture.EvaluationCounter.ResetPhysicalWrites();
        link.ThrowOnNormalizedWrite = true;
        link.Left.Value = 55m;

        Assert.Throws<InvalidOperationException>(() => runtime.Materialize(link));

        Assert.Equal(6m, link.Ratio);
        Assert.Equal(6m, link.NormalizedRatio);
        Assert.Equal(0, runtime.Version);
        Assert.Equal(2, fixture.EvaluationCounter.RatioWrites);
        Assert.Equal(1, fixture.EvaluationCounter.NormalizedRatioWrites);

        link.ThrowOnNormalizedWrite = false;
        runtime.Materialize(link);
        await context.SaveChangesAsync();
        Assert.Equal(5.5m, link.Ratio);
        Assert.Equal(5.5m, link.NormalizedRatio);
    }

    [Fact]
    public void Second_registration_for_same_context_is_rejected()
    {
        var modelBuilder = new ConsistencyModelBuilder();
        var links = modelBuilder.Objects<Link>().Key(value => value.Id);
        var model = modelBuilder.Build();
        var mappings = new ConsistencyEfCoreMappings().Map(links);
        var services = new ServiceCollection();

        Assert.Same(services, services.AddRaffinertConsistency<LinkContext>(model, mappings));
        var error = Assert.Throws<InvalidOperationException>(() =>
            services.AddRaffinertConsistency<LinkContext>(model, mappings));

        Assert.Contains("Exactly one Raffinert EF Core integration registration", error.Message);
    }

    [Fact]
    public void Second_registration_for_different_context_is_rejected()
    {
        var firstBuilder = new ConsistencyModelBuilder();
        var links = firstBuilder.Objects<Link>().Key(value => value.Id);
        var secondBuilder = new ConsistencyModelBuilder();
        var dualItems = secondBuilder.Objects<DualItem>().Key(value => value.Id);
        var services = new ServiceCollection();
        services.AddRaffinertConsistency<LinkContext>(
            firstBuilder.Build(), new ConsistencyEfCoreMappings().Map(links));

        var error = Assert.Throws<InvalidOperationException>(() =>
            services.AddRaffinertConsistency<DualContext>(
                secondBuilder.Build(), new ConsistencyEfCoreMappings().Map(dualItems)));

        Assert.Contains("Exactly one Raffinert EF Core integration registration", error.Message);
    }

    [Fact]
    public async Task Baseline_admission_preserves_exact_object_set_identity_for_same_clr_type()
    {
        var modelBuilder = new ConsistencyModelBuilder();
        var firstSet = modelBuilder.Objects<DualItem>().Named("first").Key(value => value.Id);
        var secondSet = modelBuilder.Objects<DualItem>().Named("second").Key(value => value.Id);
        var model = modelBuilder.Build();
        var databaseName = $"dual-{Guid.NewGuid()}";
        await using (var seed = new DualContext(
                         new DbContextOptionsBuilder<DualContext>().UseInMemoryDatabase(databaseName).Options))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.AddRange(new DualItem { Id = 1, Kind = 1 }, new DualItem { Id = 2, Kind = 2 });
            await seed.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddDbContext<DualContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddRaffinertConsistency<DualContext>(model,
            new ConsistencyEfCoreMappings()
                .Map(firstSet, entry => entry.Entity.Kind == 1)
                .Map(secondSet, entry => entry.Entity.Kind == 2));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var context = scope.ServiceProvider.GetRequiredService<DualContext>();
        var values = await context.Items.OrderBy(value => value.Id).ToArrayAsync();
        var session = scope.ServiceProvider.GetRequiredService<ConsistencyEfCoreSession<DualContext>>();

        Assert.Same(runtime, session.Runtime);
        Assert.True(runtime.IsRegistered(firstSet.Definition, values[0]));
        Assert.False(runtime.IsRegistered(firstSet.Definition, values[1]));
        Assert.False(runtime.IsRegistered(secondSet.Definition, values[0]));
        Assert.True(runtime.IsRegistered(secondSet.Definition, values[1]));
        Assert.Throws<InvalidOperationException>(() =>
            runtime.AdmitBaseline(firstSet.Definition, new DualItem { Id = 1, Kind = 1 }));
    }

    private sealed class LinkService(LinkContext db, IConsistencyRuntime consistency)
    {
        public decimal? ObservedRatio { get; private set; }
        public decimal? ObservedNormalizedRatio { get; private set; }

        public async Task ChangeLeftValueAsync(
            long linkId,
            decimal newValue,
            CancellationToken cancellationToken)
        {
            var link = await db.Links
                .Include(value => value.Left)
                .Include(value => value.Right)
                .SingleAsync(value => value.Id == linkId, cancellationToken);
            link.Left.Value = newValue;
            consistency.Materialize(link);
            ObservedRatio = link.Ratio;
            ObservedNormalizedRatio = link.NormalizedRatio;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            ServiceProvider provider,
            SqliteConnection connection,
            long linkId,
            CompiledConsistencyModel model,
            ConsistencyEfCoreMappings mappings,
            EvaluationCounter evaluationCounter,
            Derived<Link, decimal>? projectedLeft,
            ObjectSet<Item> items,
            ObjectSet<Link> links,
            Derived<Link, decimal?> ratio)
        {
            Provider = provider;
            Connection = connection;
            LinkId = linkId;
            Model = model;
            Mappings = mappings;
            EvaluationCounter = evaluationCounter;
            ProjectedLeft = projectedLeft;
            Items = items;
            Links = links;
            Ratio = ratio;
        }

        public ServiceProvider Provider { get; }
        public SqliteConnection Connection { get; }
        public long LinkId { get; }
        public CompiledConsistencyModel Model { get; }
        public ConsistencyEfCoreMappings Mappings { get; }
        public EvaluationCounter EvaluationCounter { get; }
        public Derived<Link, decimal>? ProjectedLeft { get; }
        public ObjectSet<Item> Items { get; }
        public ObjectSet<Link> Links { get; }
        public Derived<Link, decimal?> Ratio { get; }

        public static async Task<Fixture> CreateAsync(
            bool includeProjection = false,
            bool includeRepairPolicy = false,
            bool enforceRatioInvariant = false,
            bool repairEnabledInvariant = true,
            bool includeUnenforcedRepairInvariant = false,
            bool completeScope = true,
            bool includeSecondLink = false,
            bool useConsumerDiscovery = false,
            bool includeCollectionDependency = false)
        {
            var modelBuilder = new ConsistencyModelBuilder();
            var evaluationCounter = new EvaluationCounter();
            var items = modelBuilder.Objects<Item>().Named("items").Key(value => value.Id);
            var links = modelBuilder.Objects<Link>().Named("links").Key(value => value.Id);
            if (includeCollectionDependency)
                _ = modelBuilder.Relation(items, links).Where((item, link) =>
                    item.LeftLinks.Any(candidate => candidate.Id == link.Id));
            var ratio = modelBuilder.Derived(links)
                .DependsOn(value => value.Left.Value, value => value.Right.Value)
                .Select((Func<Link, decimal?>)(value =>
                {
                    evaluationCounter.RatioEvaluations++;
                    return value.Right.Value == 0m
                        ? null
                        : value.Left.Value / value.Right.Value;
                }))
                .MaterializeTo(value => value.Ratio)
                .Named("ratio");
            var normalizedRatio = modelBuilder.Derived(links).From(ratio)
                .Select((_, value) => CountNormalized(evaluationCounter, value))
                .MaterializeTo(value => value.NormalizedRatio)
                .Named("normalized-ratio")
                .AllowIncompleteDependencies();
            Invariant<Link>? ratioInvariant = null;
            if (includeRepairPolicy || enforceRatioInvariant)
            {
                ratioInvariant = modelBuilder.Invariant(links).From(ratio)
                    .Must((_, value) => value == null || value >= 0m);
                if (repairEnabledInvariant)
                    ratioInvariant.RepairWhenViolated();
                ratioInvariant.Named("ratio-valid");
            }
            if (includeUnenforcedRepairInvariant)
                modelBuilder.Invariant(links).From(ratio)
                    .Must((_, value) => value == null || value >= 1m)
                    .RepairWhenViolated()
                    .Named("unenforced-ratio-repair");
            Derived<Link, decimal>? projectedLeft = null;
            if (includeProjection)
            {
                var doubledItemValue = modelBuilder.Derived(items)
                    .Select(value => value.Value * 2m)
                    .Named("doubled-item-value");
                projectedLeft = modelBuilder.Derived(links)
                    .From(value => value.Left, doubledItemValue)
                    .Select((_, value) => value)
                    .MaterializeTo(value => value.ProjectedLeftValue)
                    .Named("projected-left");
            }
            var model = modelBuilder.Build();
            var mappings = new ConsistencyEfCoreMappings().Map(items).Map(links);
            if (enforceRatioInvariant)
                mappings.Enforce(ratioInvariant!);
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using (var seed = new LinkContext(
                             new DbContextOptionsBuilder<LinkContext>().UseSqlite(connection).Options))
            {
                await seed.Database.EnsureCreatedAsync();
                var left = new Item { Id = 1, Value = 60m };
                var right = new Item { Id = 2, Value = 10m };
                var replacement = new Item { Id = 3, Value = 50m };
                seed.AddRange(left, right, replacement, new Note { Id = 1, Text = "original" }, new Link
                {
                    Id = 1,
                    Left = left,
                    Right = right,
                    Comment = "original",
                    Ratio = 6m,
                    NormalizedRatio = 6m,
                    ProjectedLeftValue = 120m
                });
                if (includeSecondLink)
                    seed.Add(new Link
                    {
                        Id = 2,
                        Left = left,
                        Right = right,
                        Comment = "original",
                        Ratio = 6m,
                        NormalizedRatio = 6m,
                        ProjectedLeftValue = 120m
                    });
                await seed.SaveChangesAsync();
            }

            var services = new ServiceCollection();
            services.AddDbContext<LinkContext>(options => options.UseSqlite(connection));
            if (useConsumerDiscovery)
                mappings
                    .DiscoverConsumers(links, value => value.Left, (db, targets) =>
                    {
                        var ids = targets.Select(value => value.Id).ToArray();
                        return db.Set<Link>().Where(value => ids.Contains(value.LeftId))
                            .Include(value => value.Left).Include(value => value.Right);
                    })
                    .DiscoverConsumers(links, value => value.Right, (db, targets) =>
                    {
                        var ids = targets.Select(value => value.Id).ToArray();
                        return db.Set<Link>().Where(value => ids.Contains(value.RightId))
                            .Include(value => value.Left).Include(value => value.Right);
                    });
            services.AddRaffinertConsistency<LinkContext>(model, mappings,
                new ConsistencySaveOptions
                {
                    Scope = completeScope
                        ? useConsumerDiscovery
                            ? new ConsistencyScope().Complete(items)
                            : new ConsistencyScope().Complete(items).Complete(links)
                        : new ConsistencyScope().Complete(items)
                });
            services.AddScoped<LinkService>();
            return new Fixture(services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            }), connection, 1, model, mappings, evaluationCounter, projectedLeft, items, links, ratio);
        }

        public LinkContext CreateContext() => new(
            new DbContextOptionsBuilder<LinkContext>().UseSqlite(Connection).Options);

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class LinkContext(DbContextOptions<LinkContext> options) : DbContext(options)
    {
        public DbSet<Item> Items => Set<Item>();
        public DbSet<Link> Links => Set<Link>();
        public DbSet<Note> Notes => Set<Note>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Item>();
            modelBuilder.Entity<Note>();
            modelBuilder.Entity<Link>(entity =>
            {
                entity.HasOne(value => value.Left).WithMany(value => value.LeftLinks).HasForeignKey(value => value.LeftId);
                entity.HasOne(value => value.Right).WithMany().HasForeignKey(value => value.RightId);
                entity.ToTable(table => table.HasCheckConstraint(
                    "CK_Link_Ratio_NonNegative", "Ratio >= 0 OR Ratio IS NULL"));
                entity.ToTable(table => table.HasCheckConstraint(
                    "CK_Link_FailureMarker_NonNegative", "FailureMarker >= 0"));
            });
        }
    }

    private sealed class DualContext(DbContextOptions<DualContext> options) : DbContext(options)
    {
        public DbSet<DualItem> Items => Set<DualItem>();
    }

    private sealed class Item
    {
        public long Id { get; set; }
        public decimal Value { get; set; }
        public ICollection<Link> LeftLinks { get; } = [];
    }

    public sealed class EvaluationCounter
    {
        public int RatioEvaluations { get; set; }
        public int NormalizedEvaluations { get; set; }
        public int RatioWrites { get; set; }
        public int NormalizedRatioWrites { get; set; }
        public int ProjectedLeftWrites { get; set; }
        public int RepairCallbacks { get; set; }
        public List<decimal?> RatioAssignments { get; } = [];

        public void ResetMaterialization()
        {
            RatioEvaluations = 0;
            NormalizedEvaluations = 0;
            ResetPhysicalWrites();
        }

        public void ResetPhysicalWrites()
        {
            RatioWrites = 0;
            NormalizedRatioWrites = 0;
            ProjectedLeftWrites = 0;
            RatioAssignments.Clear();
        }
    }

    private sealed class Link
    {
        public long Id { get; set; }
        public long LeftId { get; set; }
        public Item Left { get; set; } = null!;
        public long RightId { get; set; }
        public Item Right { get; set; } = null!;
        public string Comment { get; set; } = "";
        private decimal? _ratio;
        private decimal? _normalizedRatio;
        private decimal _projectedLeftValue;

        public decimal? Ratio
        {
            get => _ratio;
            set
            {
                if (Counter is not null)
                {
                    Counter.RatioWrites++;
                    Counter.RatioAssignments.Add(value);
                }
                _ratio = value;
            }
        }

        public decimal? NormalizedRatio
        {
            get => _normalizedRatio;
            set
            {
                if (Counter is not null)
                    Counter.NormalizedRatioWrites++;
                if (ThrowOnNormalizedWrite)
                    throw new InvalidOperationException("Injected normalized-ratio setter failure.");
                _normalizedRatio = value;
            }
        }

        public decimal ProjectedLeftValue
        {
            get => _projectedLeftValue;
            set
            {
                if (Counter is not null)
                    Counter.ProjectedLeftWrites++;
                _projectedLeftValue = value;
            }
        }

        public int FailureMarker { get; set; }

        [NotMapped]
        public EvaluationCounter? Counter { get; set; }

        [NotMapped]
        public bool ThrowOnNormalizedWrite { get; set; }
    }

    private sealed class DualItem
    {
        public long Id { get; set; }
        public int Kind { get; set; }
    }

    private sealed class Note
    {
        public long Id { get; set; }
        public string Text { get; set; } = "";
    }

    private static decimal? CountNormalized(EvaluationCounter counter, decimal? value)
    {
        counter.NormalizedEvaluations++;
        return value == null
            ? null
            : value == 0m ? 0m : value >= 1m ? value : 1m / value;
    }
}
