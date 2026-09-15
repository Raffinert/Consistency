namespace Raffinert.Consistency.Tests;

public sealed class DependencyStateTransitionTests
{
    [Theory]
    [InlineData(DerivedValueState.Fresh, false, DerivedValueState.Dirty)]
    [InlineData(DerivedValueState.Dirty, false, DerivedValueState.Dirty)]
    [InlineData(DerivedValueState.Invalid, false, DerivedValueState.Invalid)]
    [InlineData(DerivedValueState.Fresh, true, DerivedValueState.Invalid)]
    [InlineData(DerivedValueState.Dirty, true, DerivedValueState.Invalid)]
    [InlineData(DerivedValueState.Invalid, true, DerivedValueState.Invalid)]
    public void Derived_state_transition_matrix_is_monotonic(
        DerivedValueState current,
        bool invalid,
        DerivedValueState expected)
    {
        var impact = invalid ? DependencyImpactKind.Invalid : DependencyImpactKind.Dirty;
        Assert.Equal(expected, DependencyStateTransitions.Apply(current, impact));
    }

    [Theory]
    [InlineData(InvariantEvaluationState.Unknown, false, InvariantEvaluationState.Unknown)]
    [InlineData(InvariantEvaluationState.Valid, false, InvariantEvaluationState.Dirty)]
    [InlineData(InvariantEvaluationState.Violated, false, InvariantEvaluationState.Dirty)]
    [InlineData(InvariantEvaluationState.Dirty, false, InvariantEvaluationState.Dirty)]
    [InlineData(InvariantEvaluationState.Invalid, false, InvariantEvaluationState.Invalid)]
    [InlineData(InvariantEvaluationState.Unknown, true, InvariantEvaluationState.Unknown)]
    [InlineData(InvariantEvaluationState.Valid, true, InvariantEvaluationState.Invalid)]
    [InlineData(InvariantEvaluationState.Violated, true, InvariantEvaluationState.Invalid)]
    [InlineData(InvariantEvaluationState.Dirty, true, InvariantEvaluationState.Invalid)]
    [InlineData(InvariantEvaluationState.Invalid, true, InvariantEvaluationState.Invalid)]
    public void Invariant_state_transition_matrix_is_monotonic(
        InvariantEvaluationState current,
        bool invalid,
        InvariantEvaluationState expected)
    {
        var impact = invalid ? DependencyImpactKind.Invalid : DependencyImpactKind.Dirty;
        Assert.Equal(expected, DependencyStateTransitions.Apply(current, impact));
    }

    [Fact]
    public void Later_weaker_impact_does_not_downgrade_invalid_state()
    {
        var derived = DependencyStateTransitions.Apply(DerivedValueState.Fresh, DependencyImpactKind.Dirty);
        derived = DependencyStateTransitions.Apply(derived, DependencyImpactKind.Invalid);
        derived = DependencyStateTransitions.Apply(derived, DependencyImpactKind.Dirty);

        var invariant = DependencyStateTransitions.Apply(
            InvariantEvaluationState.Valid,
            DependencyImpactKind.Dirty);
        invariant = DependencyStateTransitions.Apply(invariant, DependencyImpactKind.Invalid);
        invariant = DependencyStateTransitions.Apply(invariant, DependencyImpactKind.Dirty);

        Assert.Equal(DerivedValueState.Invalid, derived);
        Assert.Equal(InvariantEvaluationState.Invalid, invariant);
    }
}
