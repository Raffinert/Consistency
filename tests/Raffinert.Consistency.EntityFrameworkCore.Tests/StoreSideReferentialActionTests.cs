using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Raffinert.Consistency.EntityFrameworkCore;

namespace Raffinert.Consistency.Tests;

public sealed class StoreSideReferentialActionTests
{
    [Fact]
    public void Untracked_store_cascade_is_rejected_before_sql()
    {
        using var database = new ReferentialDatabase();
        int parentId;
        int childId;
        using (var seed = database.CreateContext())
        {
            var parent = new CascadeParent();
            var child = new CascadeChild { Parent = parent };
            seed.Add(child);
            seed.SaveChanges();
            parentId = parent.Id;
            childId = child.Id;
        }
        using var context = database.CreateContext();
        var deleted = context.Set<CascadeParent>().Single(x => x.Id == parentId);
        CascadeChild runtimeChild;
        using (var read = database.CreateContext())
            runtimeChild = read.Set<CascadeChild>().AsNoTracking().Single(x => x.Id == childId);
        var model = new ConsistencyModelBuilder();
        var parents = model.Objects<CascadeParent>().Key(x => x.Id);
        var children = model.Objects<CascadeChild>().Key(x => x.Id);
        var relation = model.Relation(parents, children).Where((p, c) => p.Id == c.ParentId);
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(parents, [deleted]);
            seed.Add(children, [runtimeChild]);
        });
        context.Remove(deleted);

        Assert.ThrowsAny<Exception>(() => context.SaveChangesConsistently(
            runtime,
            new ConsistencyEfCoreMappings().Map(parents).Map(children),
            Complete(parents, children)));

        using var verify = database.CreateContext();
        Assert.Equal(1, verify.Set<CascadeParent>().Count());
        Assert.Equal(1, verify.Set<CascadeChild>().Count());
        Assert.Equal([runtimeChild], runtime.Related(relation, deleted));
        Assert.Equal(0, runtime.Version);
    }

    [Fact]
    public void Untracked_store_set_null_is_rejected_before_sql()
    {
        using var database = new ReferentialDatabase();
        int parentId;
        int childId;
        using (var seed = database.CreateContext())
        {
            var parent = new NullParent();
            var child = new NullChild { Parent = parent };
            seed.Add(child);
            seed.SaveChanges();
            parentId = parent.Id;
            childId = child.Id;
        }
        using var context = database.CreateContext();
        var deleted = context.Set<NullParent>().Single(x => x.Id == parentId);
        NullChild runtimeChild;
        using (var read = database.CreateContext())
            runtimeChild = read.Set<NullChild>().AsNoTracking().Single(x => x.Id == childId);
        var model = new ConsistencyModelBuilder();
        var parents = model.Objects<NullParent>().Key(x => x.Id);
        var children = model.Objects<NullChild>().Key(x => x.Id);
        var relation = model.Relation(parents, children).Where((p, c) => p.Id == c.ParentId);
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(parents, [deleted]);
            seed.Add(children, [runtimeChild]);
        });
        context.Remove(deleted);

        Assert.ThrowsAny<Exception>(() => context.SaveChangesConsistently(
            runtime,
            new ConsistencyEfCoreMappings().Map(parents).Map(children),
            Complete(parents, children)));

        using var verify = database.CreateContext();
        Assert.Equal(parentId, verify.Set<NullChild>().Single().ParentId);
        Assert.Equal([runtimeChild], runtime.Related(relation, deleted));
        Assert.Equal(0, runtime.Version);
    }

    [Fact]
    public void Transitive_store_cascade_to_consistency_state_is_rejected_before_sql()
    {
        using var database = new ReferentialDatabase();
        int rootId;
        Leaf runtimeLeaf;
        using (var seed = database.CreateContext())
        {
            var root = new Root();
            var intermediate = new Intermediate { Root = root };
            var leaf = new Leaf { Intermediate = intermediate };
            seed.Add(leaf);
            seed.SaveChanges();
            rootId = root.Id;
            runtimeLeaf = leaf;
        }
        using var context = database.CreateContext();
        var deleted = context.Set<Root>().Single(x => x.Id == rootId);
        var model = new ConsistencyModelBuilder();
        var leaves = model.Objects<Leaf>().Key(x => x.Id);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(leaves, [runtimeLeaf]));
        context.Remove(deleted);

        Assert.ThrowsAny<Exception>(() => context.SaveChangesConsistently(
            runtime, new ConsistencyEfCoreMappings().Map(leaves), Complete(leaves)));

        using var verify = database.CreateContext();
        Assert.Equal(1, verify.Set<Root>().Count());
        Assert.Equal(1, verify.Set<Intermediate>().Count());
        Assert.Equal(1, verify.Set<Leaf>().Count());
        Assert.Equal(0, runtime.Version);
    }

    [Fact]
    public void Irrelevant_store_cascade_and_owned_delete_remain_supported()
    {
        using var database = new ReferentialDatabase();
        using (var context = database.CreateContext())
        {
            var parent = new AuditParent { Detail = new AuditDetail() };
            context.Add(parent);
            context.SaveChanges();
            var model = new ConsistencyModelBuilder();
            var parents = model.Objects<AuditParent>().Key(x => x.Id);
            var runtime = model.Build().CreateRuntime(seed => seed.Add(parents, [parent]));
            context.Remove(parent);
            context.SaveChangesConsistently(runtime, new ConsistencyEfCoreMappings().Map(parents));
            Assert.Equal(0, context.Set<AuditDetail>().Count());
            Assert.Equal(1, runtime.Version);
        }
        using (var context = database.CreateContext())
        {
            var owner = new OwnedRoot { Value = new OwnedValue { Note = "x" } };
            context.Add(owner);
            context.SaveChanges();
            var model = new ConsistencyModelBuilder();
            var owners = model.Objects<OwnedRoot>().Key(x => x.Id);
            var runtime = model.Build().CreateRuntime(seed => seed.Add(owners, [owner]));
            context.Remove(owner);
            context.SaveChangesConsistently(runtime, new ConsistencyEfCoreMappings().Map(owners));
            Assert.Equal(1, runtime.Version);
        }
    }

    private static ConsistencySaveOptions Complete<T>(ObjectSet<T> set) where T : class =>
        new() { Scope = new ConsistencyScope().Complete(set) };

    private static ConsistencySaveOptions Complete<T1, T2>(ObjectSet<T1> first, ObjectSet<T2> second)
        where T1 : class where T2 : class =>
        new() { Scope = new ConsistencyScope().Complete(first).Complete(second) };

    private sealed class ReferentialDatabase : IDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        public ReferentialDatabase()
        {
            _connection.Open();
            using var context = CreateContext();
            context.Database.EnsureCreated();
        }
        public ReferentialContext CreateContext(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) =>
            new(_connection, interceptors);
        public void Dispose() => _connection.Dispose();
    }

    private sealed class ReferentialContext(SqliteConnection connection,
        Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options) =>
            options.UseSqlite(connection).AddInterceptors(interceptors);
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<CascadeChild>().HasOne(x => x.Parent).WithMany()
                .HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Cascade);
            model.Entity<NullChild>().HasOne(x => x.Parent).WithMany()
                .HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.SetNull);
            model.Entity<Intermediate>().HasOne(x => x.Root).WithMany()
                .HasForeignKey(x => x.RootId).OnDelete(DeleteBehavior.Cascade);
            model.Entity<Leaf>().HasOne(x => x.Intermediate).WithMany()
                .HasForeignKey(x => x.IntermediateId).OnDelete(DeleteBehavior.Cascade);
            model.Entity<AuditDetail>().HasOne(x => x.Parent).WithOne(x => x.Detail)
                .HasForeignKey<AuditDetail>(x => x.ParentId).OnDelete(DeleteBehavior.Cascade);
            model.Entity<OwnedRoot>().OwnsOne(x => x.Value);
        }
    }

    private sealed class CascadeParent { public int Id { get; set; } }
    private sealed class CascadeChild { public int Id { get; set; } public int ParentId { get; set; } public CascadeParent Parent { get; set; } = null!; }
    private sealed class NullParent { public int Id { get; set; } }
    private sealed class NullChild { public int Id { get; set; } public int? ParentId { get; set; } public NullParent? Parent { get; set; } }
    private sealed class Root { public int Id { get; set; } }
    private sealed class Intermediate { public int Id { get; set; } public int RootId { get; set; } public Root Root { get; set; } = null!; }
    private sealed class Leaf { public int Id { get; set; } public int IntermediateId { get; set; } public Intermediate Intermediate { get; set; } = null!; }
    private sealed class AuditParent { public int Id { get; set; } public AuditDetail Detail { get; set; } = null!; }
    private sealed class AuditDetail { public int Id { get; set; } public int ParentId { get; set; } public AuditParent Parent { get; set; } = null!; }
    private sealed class OwnedRoot { public int Id { get; set; } public OwnedValue Value { get; set; } = null!; }
    [Owned] private sealed class OwnedValue { public string Note { get; set; } = ""; }
}
