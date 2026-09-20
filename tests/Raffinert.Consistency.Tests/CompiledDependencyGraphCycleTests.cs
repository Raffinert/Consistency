using System.Linq.Expressions;
using Raffinert.Consistency.Expressions;

namespace Raffinert.Consistency.Tests;

public sealed class CompiledDependencyGraphCycleTests
{
    [Fact]
    public void Compiled_graph_rejects_self_cycle_with_actual_cycle_path()
    {
        var self = Definition("self");
        self.DependsOn(self);

        var error = Assert.Throws<InvalidOperationException>(() => Compile(self));

        Assert.Equal("Dependency cycle detected: self -> self.", error.Message);
    }

    [Fact]
    public void Compiled_graph_rejects_two_node_cycle_with_actual_cycle_path()
    {
        var first = Definition("first");
        var second = Definition("second");
        first.DependsOn(second);
        second.DependsOn(first);

        var error = Assert.Throws<InvalidOperationException>(() => Compile(first, second));

        Assert.Equal("Dependency cycle detected: first -> second -> first.", error.Message);
    }

    [Fact]
    public void Compiled_graph_reports_one_real_cycle_when_tail_nodes_depend_on_cycle()
    {
        var a = Definition("A");
        var b = Definition("B");
        var c = Definition("C");
        var d = Definition("D");
        var e = Definition("E");
        b.DependsOn(a);
        c.DependsOn(b);
        a.DependsOn(c);
        d.DependsOn(c);
        e.DependsOn(d);

        var error = Assert.Throws<InvalidOperationException>(() => Compile(a, b, c, d, e));

        Assert.Equal("Dependency cycle detected: A -> B -> C -> A.", error.Message);
        Assert.DoesNotContain(" -> D", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(" -> E", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compiled_graph_cycle_message_is_deterministic()
    {
        var a = Definition("A");
        var b = Definition("B");
        var c = Definition("C");
        var d = Definition("D");
        a.DependsOn(b);
        b.DependsOn(a);
        c.DependsOn(d);
        d.DependsOn(c);

        var first = Assert.Throws<InvalidOperationException>(() => Compile(a, b, c, d)).Message;
        var second = Assert.Throws<InvalidOperationException>(() => Compile(a, b, c, d)).Message;

        Assert.Equal(first, second);
        Assert.Equal("Dependency cycle detected: A -> B -> A.", first);
    }

    [Fact]
    public void Compiled_graph_cycle_message_prefers_definition_keys()
    {
        var first = Definition("orders-total");
        var second = Definition("available-capacity");
        first.DependsOn(second);
        second.DependsOn(first);

        var error = Assert.Throws<InvalidOperationException>(() => Compile(first, second));

        Assert.Contains("orders-total -> available-capacity -> orders-total", error.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("source.Value", error.Message, StringComparison.Ordinal);
    }

    private static FakeDerivedDefinition Definition(string key) => new(key);

    private static void Compile(params FakeDerivedDefinition[] definitions) =>
        CompiledDependencyGraph.Compile(definitions, []);

    private sealed class FakeDerivedDefinition(string key) : IDerivedDefinition
    {
        private static readonly ObjectSetDefinition<CycleSource> Set = new(0);
        private static readonly Expression<Func<CycleSource, int>> Expression = source => source.Value;

        public string? DefinitionKey { get; set; } = key;
        public IObjectSetDefinition SourceSet => Set;
        public IReadOnlyList<DerivedInput> Inputs { get; private set; } = [];
        public LambdaExpression ComputationExpression => Expression;
        public ExpressionDependencyAnalysis Analysis => throw new NotSupportedException();
        public DerivedImpactPolicy ImpactPolicy => throw new NotSupportedException();
        public string ComputationPlanName => "cycle-test";
        public bool RequiresExactPropagation => false;
        public bool PrefersConservativePropagation => false;
        public bool AllowIncompleteDependencies { get; set; }

        public void DependsOn(params FakeDerivedDefinition[] upstreams) =>
            Inputs = upstreams.Select(upstream => (DerivedInput)new UpstreamDerivedInput(upstream)).ToArray();

        public IDerivedRuntimeState CreateState(
            IReadOnlyDictionary<IRelationDefinition, IRelationQueryState> relations,
            Func<IDerivedDefinition, IDerivedRuntimeState> resolveDerived) =>
            throw new NotSupportedException();

        public IInvariantDefinition CreateInvariant(LambdaExpression predicate, Delegate compiledPredicate) =>
            throw new NotSupportedException();
    }

    private sealed class CycleSource
    {
        public int Value { get; set; }
    }
}
