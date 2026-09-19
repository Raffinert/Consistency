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

    [Fact]
    public void Tracked_member_usage_distinguishes_mapped_irrelevant_and_nested_relevant_members()
    {
        var model = new ConsistencyModelBuilder();
        _ = model.Objects<MemberItem>().Key(value => value.Id);
        var links = model.Objects<MemberLink>().Key(value => value.Id);
        _ = model.Derived(links)
            .DependsOn(value => value.Left.Value)
            .Select(value => value.Left.Value);
        var runtime = model.Build().CreateRuntime();

        var comment = typeof(MemberLink).GetProperty(nameof(MemberLink.Comment))!;
        var left = typeof(MemberLink).GetProperty(nameof(MemberLink.Left))!;
        var value = typeof(MemberItem).GetProperty(nameof(MemberItem.Value))!;
        var id = typeof(MemberLink).GetProperty(nameof(MemberLink.Id))!;

        Assert.Equal(
            ConsistencyRuntime.ModelMemberUsageKind.None,
            runtime.GetTrackedMemberUsage(links.Definition, comment));
        Assert.True(runtime.GetTrackedMemberUsage(links.Definition, left)
            .HasFlag(ConsistencyRuntime.ModelMemberUsageKind.DerivedDependency));
        Assert.True(runtime.GetTrackedMemberUsage(null, value)
            .HasFlag(ConsistencyRuntime.ModelMemberUsageKind.DerivedDependency));
        Assert.True(runtime.GetTrackedMemberUsage(links.Definition, id)
            .HasFlag(ConsistencyRuntime.ModelMemberUsageKind.ObjectSetKey));
    }

    private class BaseEntity { public int Id { get; set; } }
    private sealed class DerivedEntity : BaseEntity { }
    private sealed class UnrelatedEntity { }
    private sealed class MemberItem
    {
        public int Id { get; set; }
        public int Value { get; set; }
    }
    private sealed class MemberLink
    {
        public int Id { get; set; }
        public MemberItem Left { get; set; } = null!;
        public string Comment { get; set; } = "";
    }
}
