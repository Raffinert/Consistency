namespace Raffinert.Consistency.Tests;

public sealed class RuntimeEntityTypeRelevanceTests
{
    [Fact]
    public void Object_set_type_relevance_uses_assignability_overlap()
    {
        var exactModel = new ConsistencyModelBuilder();
        _ = exactModel.Objects<BaseEntity>().Key(x => x.Id);
        var exact = exactModel.Build().CreateRuntime();
        Assert.True(exact.HasObjectSetForClrType(typeof(BaseEntity)));
        Assert.True(exact.HasObjectSetForClrType(typeof(DerivedEntity)));
        Assert.False(exact.HasObjectSetForClrType(typeof(UnrelatedEntity)));

        var derivedModel = new ConsistencyModelBuilder();
        _ = derivedModel.Objects<DerivedEntity>().Key(x => x.Id);
        var derived = derivedModel.Build().CreateRuntime();
        Assert.True(derived.HasObjectSetForClrType(typeof(BaseEntity)));
    }

    [Fact]
    public void Multiple_sets_for_same_clr_type_remain_relevant()
    {
        var model = new ConsistencyModelBuilder();
        var first = model.Objects<BaseEntity>().Key(x => x.Id);
        var second = model.Objects<BaseEntity>().Key(x => x.Id);
        var runtime = model.Build().CreateRuntime();

        Assert.True(runtime.OwnsObjectSet(first.Definition));
        Assert.True(runtime.OwnsObjectSet(second.Definition));
        Assert.True(runtime.HasObjectSetForClrType(typeof(BaseEntity)));
    }

    private class BaseEntity { public int Id { get; set; } }
    private sealed class DerivedEntity : BaseEntity { }
    private sealed class UnrelatedEntity { }
}
