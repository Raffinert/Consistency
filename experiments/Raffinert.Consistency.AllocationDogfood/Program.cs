using Raffinert.Consistency.AllocationDogfood;

try
{
    if (args.Contains("--capture-benchmark-only", StringComparer.Ordinal))
        EfMutationCaptureBenchmark.Run();
    else if (args.Contains("--one-to-one-benchmark-only", StringComparer.Ordinal))
        OneToOneCaptureBenchmark.Run();
    else
        await Scenarios.RunAsync();
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    Environment.ExitCode = 1;
}
