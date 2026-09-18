namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class EM04RuntimeOnlyDerived
{
    public static void Run()
    {
        var fixture = ModelDFixture.Create();
        var score = fixture.Runtime.Evaluate(fixture.Model.RiskScore, fixture.Link);
        ModelDAssertions.Require(score == 50m, "EM4 runtime-only Evaluate is valid.");
        var rejected = false;
        try
        {
            _ = fixture.Runtime.Materialize(fixture.Model.RiskScore, fixture.Link);
        }
        catch (InvalidOperationException error)
        {
            rejected = error.Message.Contains("no registered materialization target", StringComparison.Ordinal);
        }
        ModelDAssertions.Require(rejected,
            "EM4 M1 rejects Materialize for a runtime-only definition with actionable guidance.");
    }
}
