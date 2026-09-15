using Raffinert.Relations.PurchaseOrderSample.Scenarios;

LegacyDependencyScenarios.Run();
GhostMatchingScenarios.Run();
PrecommitGuardScenarios.Run();
Console.WriteLine("All procurement scenarios passed.");
