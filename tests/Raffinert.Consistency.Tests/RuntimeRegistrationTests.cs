namespace Raffinert.Consistency.Tests;

public sealed class RuntimeRegistrationTests
{
    [Fact]
    public void Derived_and_invariant_state_operations_reject_unregistered_sources_without_creating_state()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<Source>().Key(source => source.Id);
        var amount = model.Derived(sources).Compute(source => source.Amount);
        var invariant = model.Invariant(sources).Using(amount).Must((_, value) => value >= 0);
        var runtime = model.Build().CreateRuntime();
        var source = new Source { Amount = 1 };

        Assert.Throws<InvalidOperationException>(() => runtime.GetState(amount, source));
        Assert.Throws<InvalidOperationException>(() => runtime.Evaluate(invariant, source));
        Assert.Throws<InvalidOperationException>(() => runtime.GetState(invariant, source));
        Assert.Equal(0, runtime.DerivedStateEntryCount);
        Assert.Equal(0, runtime.InvariantStateEntryCount);
    }

    [Fact]
    public void Removed_and_wrong_set_sources_are_rejected_but_registered_sources_remain_valid()
    {
        var model = new ConsistencyModelBuilder();
        var first = model.Objects<Source>().Key(source => source.Id);
        var second = model.Objects<Source>().Key(source => source.Id);
        var amount = model.Derived(first).Compute(source => source.Amount);
        var invariant = model.Invariant(first).Using(amount).Must((_, value) => value >= 0);
        var runtime = model.Build().CreateRuntime();
        var valid = new Source { Amount = 1 };
        var wrongSet = new Source { Amount = 2 };
        runtime.Add(first, valid);
        runtime.Add(second, wrongSet);

        Assert.Equal(1, runtime.Get(amount, valid));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(amount, valid));
        Assert.True(runtime.Evaluate(invariant, valid));
        Assert.Equal(InvariantEvaluationState.Valid, runtime.GetState(invariant, valid));
        Assert.Throws<InvalidOperationException>(() => runtime.GetState(amount, wrongSet));
        Assert.Throws<InvalidOperationException>(() => runtime.Evaluate(invariant, wrongSet));

        Assert.True(runtime.Remove(first, valid));
        Assert.Throws<InvalidOperationException>(() => runtime.GetState(amount, valid));
        Assert.Throws<InvalidOperationException>(() => runtime.Evaluate(invariant, valid));
        Assert.Throws<InvalidOperationException>(() => runtime.GetState(invariant, valid));
    }

    private sealed class Source
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public int Amount { get; set; }
    }
}
