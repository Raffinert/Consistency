using Raffinert.Relations;

var repairQueue = new List<Guid>();
var model = new RelationModelBuilder();
var lines = model.Objects<PurchaseOrderLine>().Named("po-lines").Key(line => line.Id);
var receipts = model.Objects<GoodsReceipt>().Named("goods-receipts").Key(receipt => receipt.Id);
var matchingReceipts = model.Relation(lines, receipts)
    .Where((line, receipt) => line.OrderNumber == receipt.OrderNumber &&
        line.ItemNumber == receipt.ItemNumber && !receipt.Cancelled)
    .Named("matching-receipts");

var receivedQuantity = model.Derived(lines).Using(matchingReceipts)
    .Impact(policy => policy
        .MembershipAdded(DependencySeverity.Dirty)
        .MembershipRemoved(DependencySeverity.Invalid)
        .ItemChanged(DependencySeverity.Invalid))
    .Incrementally()
    .Compute((_, matches) => matches.Sum(receipt => receipt.Quantity))
    .Named("received-quantity");

var availableQuantity = model.Derived(lines).Using(receivedQuantity)
    .Impact(policy => policy
        .SourceChanged(DependencySeverity.Dirty)
        .SourceMemberChanged(
            line => line.OrderedQuantity,
            (oldValue, newValue) => newValue < oldValue
                ? DependencySeverity.Invalid
                : DependencySeverity.Dirty))
    .Compute((line, received) => line.OrderedQuantity - received)
    .Named("available-quantity");

var unitRate = model.Derived(lines)
    .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
    .Compute(line => line.PriceRate)
    .Named("unit-rate");

var linkValidity = model.Derived(lines).Using(availableQuantity, unitRate)
    .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
    .Compute((line, available, rate) =>
        line.ReservedQuantity <= available && line.CapturedRate == rate)
    .Named("link-validity");

var linkInvariant = model.Invariant(lines).Using(linkValidity)
    .Must((_, valid) => valid)
    .Named("link-validity-invariant")
    .ScheduleRepairWith(line => repairQueue.Add(line.Id));

var runtime = model.Build().CreateRuntime();
var line = new PurchaseOrderLine
{
    OrderNumber = "PO-100",
    ItemNumber = 1,
    OrderedQuantity = 10,
    PriceRate = 25,
    ReservedQuantity = 4,
    CapturedRate = 25
};
var receipt = new GoodsReceipt
{
    OrderNumber = line.OrderNumber,
    ItemNumber = line.ItemNumber,
    Quantity = 5
};
runtime.Apply(MutationSet.Create(Change.Add(lines, line), Change.Add(receipts, receipt)));
_ = runtime.Get(linkValidity, line);
_ = runtime.Evaluate(linkInvariant, line);

// Additive capacity is explicitly deferrable.
line.OrderedQuantity = 12;
runtime.Apply(Change.Property(lines, line, value => value.OrderedQuantity, 10m, 12m));
Require(runtime.GetState(availableQuantity, line) == DerivedValueState.Dirty, "increase should be dirty");
_ = runtime.Get(linkValidity, line);
_ = runtime.Evaluate(linkInvariant, line);

// Subtractive capacity invalidates the persisted decision.
line.OrderedQuantity = 8;
var decrease = runtime.ApplyDetailed(MutationSet.Create(Change.Property(
    lines, line, value => value.OrderedQuantity, 12m, 8m)), RuntimeImpactDetailLevel.Causal);
Require(runtime.GetState(availableQuantity, line) == DerivedValueState.Invalid, "decrease should be invalid");
Require(decrease.Result.RepairRequests.Count == 1, "decrease should request repair");
Console.WriteLine(RuntimeImpactTraceRenderer.Render(decrease.Result));
decrease.Dispatch.Invoke();

// Cancellation removes relation membership and flows through the whole graph.
_ = runtime.Get(linkValidity, line);
_ = runtime.Evaluate(linkInvariant, line);
repairQueue.Clear();
receipt.Cancelled = true;
runtime.Apply(Change.Property(receipts, receipt, value => value.Cancelled, false, true));
Require(runtime.GetState(receivedQuantity, line) == DerivedValueState.Invalid, "cancellation should invalidate received quantity");
Require(repairQueue.SequenceEqual([line.Id]), "cancellation should schedule repair");

// A rate change invalidates unit rate, link validity, and the invariant without caller orchestration.
_ = runtime.Get(linkValidity, line);
_ = runtime.Evaluate(linkInvariant, line);
repairQueue.Clear();
line.PriceRate = 27;
runtime.Apply(Change.Property(lines, line, value => value.PriceRate, 25m, 27m));
Require(runtime.GetState(unitRate, line) == DerivedValueState.Invalid, "rate should be invalid");
Require(runtime.GetState(linkValidity, line) == DerivedValueState.Invalid, "link should be invalid");
Require(repairQueue.SequenceEqual([line.Id]), "rate change should schedule repair");

Console.WriteLine("Procurement dependency scenarios passed.");

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

internal sealed class PurchaseOrderLine
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string OrderNumber { get; init; } = "";
    public int ItemNumber { get; init; }
    public decimal OrderedQuantity { get; set; }
    public decimal PriceRate { get; set; }
    public decimal ReservedQuantity { get; init; }
    public decimal CapturedRate { get; init; }
}

internal sealed class GoodsReceipt
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string OrderNumber { get; init; } = "";
    public int ItemNumber { get; init; }
    public decimal Quantity { get; set; }
    public bool Cancelled { get; set; }
}
