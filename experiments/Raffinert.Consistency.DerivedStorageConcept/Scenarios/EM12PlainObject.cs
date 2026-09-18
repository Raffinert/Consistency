namespace Raffinert.Consistency.DerivedStorageConcept.Scenarios;

internal static class EM12PlainObject
{
    public static void Run()
    {
        var fixture = ModelDFixture.Create();
        fixture.ChangeInvoicePrice(55m);
        var evaluated = fixture.Runtime.Evaluate(fixture.Model.PriceRate, fixture.Link);
        fixture.Link.PriceRate = 999m;
        var afterExternalWrite = fixture.Runtime.Evaluate(fixture.Model.PriceRate, fixture.Link);
        var value = fixture.Runtime.Materialize(fixture.Model.PriceRate, fixture.Link);
        ModelDAssertions.Require(evaluated == 5.5m && afterExternalWrite == 5.5m,
            "EM12 an external mirror write never becomes the logical authority.");
        ModelDAssertions.Require(value == 5.5m && fixture.Link.PriceRate == value,
            "EM12 optional Core-style adapter materializes a detached plain object without EF.");
    }
}
