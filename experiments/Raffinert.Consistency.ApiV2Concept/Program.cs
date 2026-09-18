using Raffinert.Consistency.ApiV2Concept.Common;

await ScenarioExpectations.RunAllAsync([
    Raffinert.Consistency.ApiV2Concept.Current.CurrentApi.Create(),
    Raffinert.Consistency.ApiV2Concept.VariantA.ProviderPipeline.Models.Create(),
    Raffinert.Consistency.ApiV2Concept.VariantB.TypedDomainPipeline.Models.Create(),
    Raffinert.Consistency.ApiV2Concept.VariantC.HybridBuilderPipeline.Models.Create(),
    Raffinert.Consistency.ApiV2Concept.VariantD.ContextDsl.Models.Create()
]);

Console.WriteLine("Consistency API v2 concept dogfood passed for Current and Variants A-D.");
