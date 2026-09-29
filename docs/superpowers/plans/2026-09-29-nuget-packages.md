# XafStateless as NuGet packages on the private feed (BPG WF-008 step 1, CARD-1869)

BPG's WF-008 adopts XafStateless as the workflow engine of generated apps. Generated apps restore from packages, not
project references (as with XafLayoutBuilder: the private GitHub Packages feed
`https://nuget.pkg.github.com/MBrekhof/index.json`). `docs/integrating.md` still says "there is no NuGet package yet".

## Change (mirrors XafLayoutBuilder's setup)

- `Directory.Build.props`: `DevExpressPackageRange` `[26.1.4,26.2)` (BPGDemo and the demo pin 26.1.4;
  26.2 assemblies must never mix with 26.1); `IsPackable` false by default; `PackageVersion` 0.1.0 (bump on every push —
  XAF records module assembly versions in the application database and refuses a lower one); Authors, MIT licence,
  project/repository URL, tags.
- `XafStateless.Engine`, `.Module`, `.Blazor`: `IsPackable` true and a one-line Description. The Module and Blazor
  DevExpress references use `$(DevExpressPackageRange)`.
- Module: the EF Core InMemory/Proxies/Relational 10.0.10 references **stay** (plan review): DevExpress.ExpressApp.EFCore
  only asks for >= 8.0.28, so they keep a consumer's EF Core family on 10.0.10. `Microsoft.Data.SqlClient` stays
  (LostRace reads SqlException): the module targets SQL Server hosts, stated in integrating.md.
- `docs/integrating.md`: the three package ids, the feed, the version rule; ProjectReference stays the way for this
  repo's demo and tests.
- Pack: `dotnet pack XafStateless.slnx -c Release -o artifacts/packages` (only the three packable projects produce
  packages); push: `dotnet nuget push "artifacts/packages/*.nupkg" --source https://nuget.pkg.github.com/MBrekhof/index.json
  --api-key "$(gh auth token)"` as `MBrekhof` (needs `write:packages`). `artifacts/` stays out of git.

## Verify

- `dotnet test XafStateless.slnx` green before and after (Engine 52, Module 116 per the last handoff).
- The nupkgs list the right dependencies (Engine → Stateless; Module → Engine, DevExpress range, EF Core 10.0.10 (InMemory, Proxies, Relational), SqlClient; Blazor → Module, DevExpress.ExpressApp.Blazor range).
- A scratch console project restores `XafStateless.Blazor` 0.1.0 from the feed (proves the push and the dependency
  graph), then is deleted.
