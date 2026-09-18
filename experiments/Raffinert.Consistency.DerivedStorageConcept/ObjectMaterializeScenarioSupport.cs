namespace Raffinert.Consistency.DerivedStorageConcept;

internal static class ObjectMaterializeScenarioSupport
{
    public static void EvaluateAllLinkTargets(ModelDFixture fixture)
    {
        _ = fixture.Runtime.Evaluate(fixture.Model.PriceRate, fixture.Link);
        _ = fixture.Runtime.Evaluate(fixture.Model.UnitRate, fixture.Link);
        _ = fixture.Runtime.Evaluate(fixture.Model.UnitFromRuntimePrice, fixture.Link);
        _ = fixture.Runtime.Evaluate(fixture.Model.NullablePriceRate, fixture.Link);
        _ = fixture.Runtime.Evaluate(fixture.Model.RateToken, fixture.Link);
    }

    public static bool Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }
}
