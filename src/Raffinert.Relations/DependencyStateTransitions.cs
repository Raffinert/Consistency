namespace Raffinert.Relations;

internal static class DependencyStateTransitions
{
    public static DerivedValueState Apply(
        DerivedValueState current,
        DependencyImpactKind impact) => impact switch
        {
            DependencyImpactKind.Invalid => DerivedValueState.Invalid,
            DependencyImpactKind.Dirty when current == DerivedValueState.Invalid => DerivedValueState.Invalid,
            DependencyImpactKind.Dirty => DerivedValueState.Dirty,
            _ => throw new ArgumentOutOfRangeException(nameof(impact), impact, null)
        };

    public static InvariantEvaluationState Apply(
        InvariantEvaluationState current,
        DependencyImpactKind impact) => current switch
        {
            InvariantEvaluationState.Unknown => InvariantEvaluationState.Unknown,
            InvariantEvaluationState.Invalid => InvariantEvaluationState.Invalid,
            _ when impact == DependencyImpactKind.Invalid => InvariantEvaluationState.Invalid,
            _ when impact == DependencyImpactKind.Dirty => InvariantEvaluationState.Dirty,
            _ => throw new ArgumentOutOfRangeException(nameof(impact), impact, null)
        };
}
