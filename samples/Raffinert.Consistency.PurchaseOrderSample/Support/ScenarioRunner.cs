using Raffinert.Consistency.PurchaseOrderSample.Domain;
namespace Raffinert.Consistency.PurchaseOrderSample.Support;

internal sealed class ScenarioRunner
{
    private int _passed;
    public void Check(string name, RuleEvaluation expected, RuleEvaluation actual, RuntimeApplyResult? impact = null)
    {
        if (expected != actual) throw new InvalidOperationException($"FAIL {name}: expected {expected}, actual {actual}");
        _passed++; Console.WriteLine($"PASS {name,-48} expected={expected,-9} actual={actual}");
        if (impact is not null) Console.WriteLine(RuntimeImpactTraceRenderer.Render(impact));
    }
    public void Complete(string suite) => Console.WriteLine($"{suite}: {_passed} scenarios passed.");
}
