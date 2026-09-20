using System.Diagnostics;

namespace Raffinert.Consistency.EntityFrameworkCore;

internal sealed class EfFingerprintDiagnostics
{
    internal int TrackedEntries { get; set; }
    internal long ReferenceNavigationsVisited { get; set; }
    internal long CollectionNavigationsVisited { get; set; }
    internal long ReferenceCandidateChecks { get; set; }
    internal long CollectionCandidateChecks { get; set; }
    internal long ReferenceIndexLookups { get; set; }
    internal long CollectionIndexLookups { get; set; }
    internal int CapturedPropertyMutations { get; set; }
    internal int CapturedNavigationMutations { get; set; }
    internal long GeneratedFixupPrincipalLookups { get; set; }
    internal long GeneratedFixupTrackedEntryScans { get; set; }
    internal long PrincipalReferenceEvidenceCaptured { get; set; }
    internal long PrincipalReferenceCleanupScans { get; set; }
    internal long PrincipalReferenceChangesEmitted { get; set; }
    internal int ExternalDiscoveryResolverInvocations { get; set; }
    internal int ExternalDiscoveryRows { get; set; }
    internal long DetectChangesTicks { get; set; }
    internal long PolicyValidationTicks { get; set; }
    internal long NavigationCaptureTicks { get; set; }
    internal long ScalarCaptureTicks { get; set; }
    internal long ExternalDiscoveryTicks { get; set; }
    internal long FingerprintConstructionTicks { get; set; }

    internal double DetectChangesMilliseconds => Milliseconds(DetectChangesTicks);
    internal double PolicyValidationMilliseconds => Milliseconds(PolicyValidationTicks);
    internal double NavigationCaptureMilliseconds => Milliseconds(NavigationCaptureTicks);
    internal double ScalarCaptureMilliseconds => Milliseconds(ScalarCaptureTicks);
    internal double ExternalDiscoveryMilliseconds => Milliseconds(ExternalDiscoveryTicks);
    internal double FingerprintConstructionMilliseconds => Milliseconds(FingerprintConstructionTicks);

    private static double Milliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;
}
