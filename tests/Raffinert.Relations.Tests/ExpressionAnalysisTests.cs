using System.Linq.Expressions;
using Raffinert.Relations.Expressions;

namespace Raffinert.Relations.Tests;

public sealed class ExpressionAnalysisTests
{
    [Fact]
    public void Extracts_single_equality_join()
    {
        Expression<Func<CodeHolder, CodeHolder, bool>> expression = (a, b) => a.Code == b.Code;

        var analysis = RelationExpressionAnalyzer.Analyze(expression);

        var part = Assert.Single(analysis.JoinKeyParts);
        Assert.Equal("CodeHolder.Code", part.Left.DisplayName);
        Assert.Equal("CodeHolder.Code", part.Right.DisplayName);
        Assert.False(analysis.HasResidualPredicate);
    }

    [Fact]
    public void Extracts_composite_equality_join()
    {
        Expression<Func<InvoiceLine, PurchaseOrderLine, bool>> expression =
            (a, b) => a.PurchaseOrderNumber == b.PurchaseOrderNumber && a.ItemNumber == b.ItemNumber;

        var analysis = RelationExpressionAnalyzer.Analyze(expression);

        Assert.Equal(2, analysis.JoinKeyParts.Count);
        Assert.Equal(["PurchaseOrderNumber", "ItemNumber"], analysis.JoinKeyParts.Select(part => part.Left.Members[^1].Name));
    }

    [Fact]
    public void Normalizes_reversed_operands()
    {
        Expression<Func<InvoiceLine, PurchaseOrderLine, bool>> expression =
            (a, b) => b.ItemNumber == a.ItemNumber;

        var part = Assert.Single(RelationExpressionAnalyzer.Analyze(expression).JoinKeyParts);

        Assert.Equal(typeof(InvoiceLine), part.Left.RootType);
        Assert.Equal(typeof(PurchaseOrderLine), part.Right.RootType);
    }

    [Fact]
    public void Identifies_a_residual_predicate()
    {
        Expression<Func<CodeHolder, CodeHolder, bool>> expression =
            (a, b) => a.Code == b.Code && b.Enabled;

        var analysis = RelationExpressionAnalyzer.Analyze(expression);

        Assert.Single(analysis.JoinKeyParts);
        Assert.True(analysis.HasResidualPredicate);
        Assert.True(analysis.IsDependencyAnalysisComplete);
        Assert.Contains(analysis.Dependencies, path => path.DisplayName == "CodeHolder.Enabled");
    }

    [Fact]
    public void Marks_opaque_method_calls_incomplete_without_inventing_a_join()
    {
        Expression<Func<CodeHolder, CodeHolder, bool>> expression = (a, b) => CustomMatch(a, b);

        var analysis = RelationExpressionAnalyzer.Analyze(expression);

        Assert.Empty(analysis.JoinKeyParts);
        Assert.False(analysis.IsDependencyAnalysisComplete);
        Assert.True(analysis.HasResidualPredicate);
    }

    [Fact]
    public void Extracts_a_nested_member_path()
    {
        Expression<Func<InvoiceLine, PurchaseOrderLine, bool>> expression =
            (invoice, line) => invoice.PurchaseOrderNumber == line.PurchaseOrder!.Number;

        var part = Assert.Single(RelationExpressionAnalyzer.Analyze(expression).JoinKeyParts);

        Assert.Equal(["PurchaseOrder", "Number"], part.Right.Members.Select(member => member.Name));
        Assert.Equal("PurchaseOrderLine.PurchaseOrder.Number", part.Right.DisplayName);
        var analysis = RelationExpressionAnalyzer.Analyze(expression);
        Assert.Contains(analysis.Dependencies, dependency => dependency.DisplayName == "PurchaseOrderLine.PurchaseOrder");
        Assert.Contains(analysis.Dependencies, dependency => dependency.DisplayName == "PurchaseOrder.Number");
    }

    private static bool CustomMatch(CodeHolder left, CodeHolder right) => left.Code.StartsWith(right.Code, StringComparison.Ordinal);
}
