using System.Linq.Expressions;
using Raffinert.Consistency.Expressions;

namespace Raffinert.Consistency.Tests;

public sealed class ExpressionAnalysisTests
{
    [Fact]
    public void Planner_selects_hash_join_for_recognized_equality()
    {
        Expression<Func<CodeHolder, CodeHolder, bool>> expression = (a, b) => a.Code == b.Code;

        var plan = RelationPlanner.Plan(RelationExpressionAnalyzer.Analyze(expression));

        var hash = Assert.IsType<HashJoinAccessPlan>(plan);
        Assert.Single(hash.JoinKeyParts);
    }

    [Fact]
    public void Planner_selects_scan_when_no_safe_join_is_available()
    {
        Expression<Func<CodeHolder, CodeHolder, bool>> expression = (a, b) => CustomMatch(a, b);

        var plan = RelationPlanner.Plan(RelationExpressionAnalyzer.Analyze(expression));

        Assert.Same(ScanAccessPlan.Instance, plan);
    }

    [Theory]
    [InlineData(StringComparison.Ordinal)]
    [InlineData(StringComparison.OrdinalIgnoreCase)]
    public void Recognizes_well_known_string_equality(StringComparison comparison)
    {
        Expression<Func<CodeHolder, CodeHolder, bool>> expression = comparison == StringComparison.Ordinal
            ? (a, b) => string.Equals(a.Code, b.Code, StringComparison.Ordinal)
            : (a, b) => string.Equals(a.Code, b.Code, StringComparison.OrdinalIgnoreCase);

        var analysis = RelationExpressionAnalyzer.Analyze(expression);

        var key = Assert.Single(analysis.JoinKeyParts);
        Assert.Equal(comparison.ToString(), key.EqualitySemantics);
        Assert.Equal(DependencyAnalysisFlags.Complete, analysis.DependencyAnalysis);
        Assert.False(analysis.HasResidualPredicate);
    }

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
        Expression<Func<RequestLine, OrderLine, bool>> expression =
            (a, b) => a.OrderNumber == b.OrderNumber && a.ItemNumber == b.ItemNumber;

        var analysis = RelationExpressionAnalyzer.Analyze(expression);

        Assert.Equal(2, analysis.JoinKeyParts.Count);
        Assert.Equal(["OrderNumber", "ItemNumber"], analysis.JoinKeyParts.Select(part => part.Left.Members[^1].Name));
    }

    [Fact]
    public void Normalizes_reversed_operands()
    {
        Expression<Func<RequestLine, OrderLine, bool>> expression =
            (a, b) => b.ItemNumber == a.ItemNumber;

        var part = Assert.Single(RelationExpressionAnalyzer.Analyze(expression).JoinKeyParts);

        Assert.Equal(typeof(RequestLine), part.Left.RootType);
        Assert.Equal(typeof(OrderLine), part.Right.RootType);
    }

    [Fact]
    public void Identifies_a_residual_predicate()
    {
        Expression<Func<CodeHolder, CodeHolder, bool>> expression =
            (a, b) => a.Code == b.Code && b.Enabled;

        var analysis = RelationExpressionAnalyzer.Analyze(expression);

        Assert.Single(analysis.JoinKeyParts);
        Assert.True(analysis.HasResidualPredicate);
        Assert.Equal(DependencyAnalysisFlags.Complete, analysis.DependencyAnalysis);
        Assert.Contains(analysis.Dependencies, path => path.DisplayName == "CodeHolder.Enabled");
    }

    [Fact]
    public void Marks_opaque_method_calls_incomplete_without_inventing_a_join()
    {
        Expression<Func<CodeHolder, CodeHolder, bool>> expression = (a, b) => CustomMatch(a, b);

        var analysis = RelationExpressionAnalyzer.Analyze(expression);

        Assert.Empty(analysis.JoinKeyParts);
        Assert.True(analysis.DependencyAnalysis.HasFlag(DependencyAnalysisFlags.ContainsOpaqueCode));
        Assert.True(analysis.HasResidualPredicate);
    }

    [Fact]
    public void Distinguishes_captured_state_from_opaque_code()
    {
        var prefix = "A";
        Expression<Func<CodeHolder, CodeHolder, bool>> expression =
            (a, b) => a.Code == b.Code && b.Code.StartsWith(prefix);

        var analysis = RelationExpressionAnalyzer.Analyze(expression);

        Assert.True(analysis.DependencyAnalysis.HasFlag(DependencyAnalysisFlags.ContainsExternalState));
        Assert.True(analysis.DependencyAnalysis.HasFlag(DependencyAnalysisFlags.ContainsOpaqueCode));
    }

    [Fact]
    public void Marks_static_state_as_external_without_calling_it_opaque()
    {
        Expression<Func<CodeHolder, CodeHolder, bool>> expression =
            (a, b) => a.Code == b.Code && b.Code == ExternalValues.Code;

        var analysis = RelationExpressionAnalyzer.Analyze(expression);

        Assert.Equal(DependencyAnalysisFlags.ContainsExternalState, analysis.DependencyAnalysis);
    }

    [Fact]
    public void Extracts_a_nested_member_path()
    {
        Expression<Func<RequestLine, OrderLine, bool>> expression =
            (invoice, line) => invoice.OrderNumber == line.Order!.Number;

        var part = Assert.Single(RelationExpressionAnalyzer.Analyze(expression).JoinKeyParts);

        Assert.Equal(["Order", "Number"], part.Right.Members.Select(member => member.Name));
        Assert.Equal("OrderLine.Order.Number", part.Right.DisplayName);
        var analysis = RelationExpressionAnalyzer.Analyze(expression);
        var completePath = Assert.Single(analysis.DependencyPaths, dependency => dependency.Segments.Count == 2);
        Assert.Equal(1, completePath.RootParameterIndex);
        Assert.Equal(["Order", "Number"], completePath.Segments.Select(segment => segment.Member.Name));
        Assert.Contains(analysis.Dependencies, dependency => dependency.DisplayName == "OrderLine.Order");
        Assert.Contains(analysis.Dependencies, dependency => dependency.DisplayName == "Order.Number");
    }

    [Fact]
    public void Recognizes_constant_and_range_residual_metadata_without_optimizing_it()
    {
        Expression<Func<DateRangeHolder, DateRangeHolder, bool>> expression = (a, b) =>
            a.Date >= b.ValidFrom && a.Date < b.ValidTo && b.Enabled;

        var analysis = RelationExpressionAnalyzer.Analyze(expression);

        Assert.Empty(analysis.JoinKeyParts);
        Assert.True(analysis.HasResidualPredicate);
        Assert.Equal(3, analysis.RecognizedResiduals.Count);
        Assert.Contains(analysis.RecognizedResiduals, value => value.StartsWith("Range:"));
        Assert.Contains(analysis.RecognizedResiduals, value => value.StartsWith("Boolean filter:"));
    }

    [Fact]
    public void Expanded_linq_operators_have_explicit_dependency_semantics()
    {
        Expression<Func<DerivedSourceRecord, IReadOnlyList<DerivedItemRecord>, decimal>> selection =
            (_, items) => items.FirstOrDefault(item => item.Enabled)!.Quantity;
        Expression<Func<DerivedSourceRecord, IReadOnlyList<DerivedItemRecord>, int>> paging =
            (_, items) => items.Skip(1).Take(2).Distinct().Count();
        Expression<Func<DerivedSourceRecord, IReadOnlyList<DerivedItemRecord>, bool>> cardinality =
            (_, items) => items.SingleOrDefault(item => item.Enabled) != null;
        Expression<Func<DerivedSourceRecord, IReadOnlyList<DerivedItemRecord>, bool>> contains =
            (_, items) => items.Contains(items.LastOrDefault()!);

        var selectionAnalysis = ExpressionDependencyAnalyzer.AnalyzeDerived(selection);
        var pagingAnalysis = ExpressionDependencyAnalyzer.AnalyzeDerived(paging);
        var cardinalityAnalysis = ExpressionDependencyAnalyzer.AnalyzeDerived(cardinality);
        var containsAnalysis = ExpressionDependencyAnalyzer.AnalyzeDerived(contains);

        Assert.True(selectionAnalysis.HasRelationMembershipDependency);
        Assert.True(selectionAnalysis.LinqSemantics.HasFlag(LinqDependencySemantics.Item));
        Assert.True(selectionAnalysis.LinqSemantics.HasFlag(LinqDependencySemantics.Ordering));
        Assert.Contains(selectionAnalysis.Dependencies, dependency =>
            dependency.Role == ExpressionParameterRole.RelationItem &&
            dependency.Path.DisplayName == "DerivedItemRecord.Quantity");
        Assert.Equal(
            LinqDependencySemantics.Membership | LinqDependencySemantics.Ordering,
            pagingAnalysis.LinqSemantics);
        Assert.Equal(
            LinqDependencySemantics.Membership | LinqDependencySemantics.Item,
            cardinalityAnalysis.LinqSemantics);
        Assert.True(containsAnalysis.LinqSemantics.HasFlag(LinqDependencySemantics.Membership));
        Assert.True(containsAnalysis.LinqSemantics.HasFlag(LinqDependencySemantics.Ordering));
    }

    private static bool CustomMatch(CodeHolder left, CodeHolder right) => left.Code.StartsWith(right.Code, StringComparison.Ordinal);

    private static class ExternalValues
    {
        public static string Code { get; set; } = "A";
    }
}
