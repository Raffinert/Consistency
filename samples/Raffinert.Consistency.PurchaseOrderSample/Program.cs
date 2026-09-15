using Raffinert.Consistency.PurchaseOrderSample.Scenarios;

LegacyDependencyScenarios.Run();
GhostMatchingScenarios.Run();
PrecommitGuardScenarios.Run();
Console.WriteLine("All procurement scenarios passed.");
