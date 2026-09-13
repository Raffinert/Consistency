namespace Raffinert.Relations.Tests;

internal sealed class CodeHolder
{
    public Guid Id { get; init; }
    public string Code { get; set; } = "";
    public bool Enabled { get; set; }
}
