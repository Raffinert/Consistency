using Raffinert.Consistency.FinalApiDogfood;

await Scenarios.RunAsync();
var definitions = DogfoodDefinitions.Create();
Console.WriteLine("Final logical graph:");
Console.WriteLine(definitions.Compiled.LogicalDebugView);
Console.WriteLine("Focused final API v2 PriceRate/UnitRate dogfood passed.");
