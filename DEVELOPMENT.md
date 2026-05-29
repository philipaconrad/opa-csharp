# Development Notes for the OPA C# SDK

## Project structure

The repo is split between two Solution files:
 - `OpenPolicyAgent.Opa.sln` — the shippable SDK (`src/OpenPolicyAgent.Opa`).
 - `test/test.sln` — the test project (`test/SmokeTest.Tests`) plus a project reference back to the SDK.

This is intentional, because it is currently the most convenient way to prevent development/testing dependencies from accidentally being included into our NuGet package builds.
This is caused by [NuGet not supporting the "development dependency" metadata feature](https://github.com/NuGet/Home/issues/4125) for referenced packages, and [requires hacky workarounds](https://github.com/NuGet/Home/wiki/DevelopmentDependency-support-for-PackageReference) for those who want to emulate the desired functionality.
In our case, it was simpler to just cleave the project in half; one side for testing, one side for development of the SDK itself.


## Source layout

Everything under `src/OpenPolicyAgent.Opa/` is hand-written. `Internal/` holds the bespoke HTTP client and Newtonsoft converters; `Serialization/` holds the pluggable serializer interface and the two built-in implementations; `Filters/` holds Compile API result and dialect types. The public top-level types are `OpaClient`, `OpaResult<T>`, `OpaBatchEntry<T>`, `OpaError`, `OpaProvenance`, and the `OpaException` hierarchy.


## Project Automation

Custom workflows in `.github/workflows/`:

### `pull-request`

Three jobs run on every PR:
- `build` — `dotnet restore` + `dotnet build`.
- `smoke-test` — `dotnet test`. Spins up real OPA + EOPA containers via Testcontainers.
- `gh-actions-lint` — runs [`zizmor`](https://github.com/zizmorcore/zizmor-action) over the workflow files.

### `pr-release-check`

A separate PR-time workflow. Posts an informational annotation in the GH Actions log when a PR contains a `Release ...` commit, listing the versions it sees in `CHANGELOG.md` and the SDK csproj so reviewers can sanity-check that they agree. If the versions mismatch, it errors, breaking the build.

### `docfx-publish`

Builds the DocFX site and publishes it to GitHub Pages on push to `main`.

### `post-tag` / `manual_publish_nuget_pkg`

Release-side automation: build and pack, push to NuGet, push the GitHub release. `post-tag` is triggered automatically on tags matching `v*`; `manual_publish_nuget_pkg` is a manual `workflow_dispatch` fallback.

## Release scripts

Helpers in `scripts/`, called from the release workflows:

- `get-csproj-version.sh` — extract the `<Version>` element from a csproj.
- `get-release-from-commits.sh` — detect `Release X.Y.Z` commits between two refs.
- `latest-release-notes.sh` — extract the most recent section of `CHANGELOG.md`.
- `github-release.sh` — create the GitHub release.
- `publish-nuget.sh` — push the `.nupkg` to NuGet.
