# Release process

Package publication is manual for the alpha series.

## Versioning policy

- Use Semantic Versioning identifiers.
- Before `1.0.0`, increment the minor version for deliberate public API breaks or substantial features.
- Increment the patch version for backward-compatible fixes.
- Use ordered prerelease labels such as `alpha.1`, `alpha.2`, `beta.1`, and `rc.1`.
- The first published version establishes each package's initial `PublicAPI.Shipped.txt` baseline.
- After that baseline is established, approve new APIs in `PublicAPI.Unshipped.txt` until the next release.
- Removing or changing a shipped API requires an explicit compatibility and versioning review; do not
  silently rewrite the shipped baseline.
- Record user-visible changes in `CHANGELOG.md` before tagging.

## Manual checklist

1. Set the same package version in both package projects and update `CHANGELOG.md`.
2. Review public API approval changes and move the release surface to the shipped baselines. For the first
   release, verify the complete approved surface establishes a non-empty initial shipped baseline.
3. Run `dotnet restore Raffinert.Relations.sln`.
4. Run the Release build, tests, formatting verification, and pack commands used by CI.
5. Inspect both `.nupkg` files and `.snupkg` symbol packages, including target frameworks, README,
   changelog, license, repository URL/commit metadata, and SourceLink information.
   Run `./eng/VerifyReleaseCandidate.ps1 -PackageDirectory artifacts/packages -RequireEmptyUnshipped`
   to enforce the current alpha.1 version, initial API-baseline, package metadata, target assets, and
   core/EF dependency alignment checks locally.
6. Create and push a signed/versioned tag only from the reviewed release commit.
7. Manually push packages with `dotnet nuget push` using a scoped NuGet API key.
8. Verify the packages on NuGet.org before announcing the release.

The main-branch CI workflow only builds and uploads artifacts; it never publishes packages.
