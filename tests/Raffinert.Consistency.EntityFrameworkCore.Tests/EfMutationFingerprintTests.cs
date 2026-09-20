using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Raffinert.Consistency.EntityFrameworkCore;

namespace Raffinert.Consistency.Tests;

public sealed class EfMutationFingerprintTests
{
    [Fact]
    public void Byte_array_fingerprint_snapshots_mutable_current_value()
    {
        using var context = CreateContext();
        var entity = new FingerprintEntity { Id = 1, Bytes = [0] };
        context.Add(entity);
        context.SaveChanges();
        entity.Bytes = [1];
        var current = entity.Bytes;

        var first = Capture(context);
        current[0] = 2;
        var second = Capture(context);

        Assert.Same(current, entity.Bytes);
        Assert.False(first.Equals(second));
    }

    [Fact]
    public void Configured_value_comparer_snapshots_mutable_current_value()
    {
        using var context = CreateContext();
        var entity = new FingerprintEntity { Id = 1, Token = new MutableToken(0) };
        context.Add(entity);
        context.SaveChanges();
        entity.Token = new MutableToken(1);
        var current = entity.Token;

        var first = Capture(context);
        current.Value = 2;
        var second = Capture(context);

        Assert.Same(current, entity.Token);
        Assert.False(first.Equals(second));
    }

    [Fact]
    public void Distinct_values_equal_by_ef_comparer_have_equal_fingerprints()
    {
        using var context = CreateContext();
        var entity = new FingerprintEntity { Id = 1, Token = new MutableToken(0) };
        context.Add(entity);
        context.SaveChanges();
        entity.Token = new MutableToken(1);
        var first = Capture(context);

        entity.Token = new MutableToken(1);
        var second = Capture(context);

        Assert.True(first.Equals(second));
    }

    private static EfMutationFingerprint Capture(FingerprintContext context)
    {
        var changes = Assert.IsType<ChangeSet>(
            ChangeTrackerAdapter.CreateChangeSet(context.ChangeTracker));
        return EfMutationFingerprint.Create(context.ChangeTracker, changes.Changes);
    }

    private static FingerprintContext CreateContext() => new(
        new DbContextOptionsBuilder<FingerprintContext>()
            .UseInMemoryDatabase($"fingerprint-{Guid.NewGuid()}").Options);

    private sealed class FingerprintContext(DbContextOptions<FingerprintContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<FingerprintEntity>().Property(value => value.Id).ValueGeneratedNever();
            var bytesComparer = new ValueComparer<byte[]>(
                (left, right) => left!.SequenceEqual(right!),
                value => value.Aggregate(0, (hash, item) => HashCode.Combine(hash, item)),
                value => value.ToArray());
            model.Entity<FingerprintEntity>().Property(value => value.Bytes)
                .Metadata.SetValueComparer(bytesComparer);
            var comparer = new ValueComparer<MutableToken>(
                (left, right) => left!.Value == right!.Value,
                value => value.Value,
                value => new MutableToken(value.Value));
            model.Entity<FingerprintEntity>().Property(value => value.Token)
                .HasConversion(value => value.Value, value => new MutableToken(value))
                .Metadata.SetValueComparer(comparer);
        }
    }

    private sealed class FingerprintEntity
    {
        public int Id { get; set; }
        public byte[] Bytes { get; set; } = [];
        public MutableToken Token { get; set; } = new(0);
    }

    private sealed class MutableToken(int value)
    {
        public int Value { get; set; } = value;
    }
}
