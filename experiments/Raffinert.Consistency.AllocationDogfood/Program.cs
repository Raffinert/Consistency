using Raffinert.Consistency.AllocationDogfood;

try
{
    await Scenarios.RunAsync();
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    Environment.ExitCode = 1;
}
