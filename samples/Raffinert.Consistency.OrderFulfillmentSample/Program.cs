using Raffinert.Consistency.OrderFulfillmentSample.Scenarios;

DependencyPropagationScenarios.Run();
AllocationIntegrityScenarios.Run();
PrecommitValidationScenarios.Run();
Console.WriteLine("All order-fulfillment scenarios passed.");
