using System.Reflection;
using Raffinert.Consistency.Expressions;

namespace Raffinert.Consistency;

/// <summary>Internal metadata describing a direct navigation consumer that an EF host may resolve.</summary>
internal sealed record ExternalConsumerDescriptor(
    IObjectSetDefinition RootSet,
    MemberInfo Navigation,
    Type TargetType,
    MemberInfo TargetMember,
    object Definition);

internal sealed record ExternalConsumerAnalysis(
    IReadOnlyList<ExternalConsumerDescriptor> Descriptors,
    bool HasUnsupportedNavigationDependency);
