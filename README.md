# DistIL
![GitHub](https://img.shields.io/github/license/dubiousconst282/DistIL)
[![Nuget](https://img.shields.io/nuget/v/DistIL.OptimizerTask)](https://www.nuget.org/packages/DistIL.OptimizerTask)

Post-build IL optimizer and intermediate representation for .NET programs (experimental).

# Notable Features
- [SSA-based Intermediate Representation](./docs/api-walkthrough.md)
- Linq Expansion
- Loop Strength Reduction
- Loop Vectorization
- List Pre-sizing
- Lambda Devirtualization
- Method Inlining
- Scalar Replacement

See [Optimization Passes](./docs/opt-list.md) for a detailed list of optimizations passes available.

The CLI can rewrite strong-named assemblies for .NET Core / .NET 5+ with the explicit `--public-sign` option. It preserves the public key and assembly identity, reserves a zero-filled signature, and sets the strong-name-signed flag. This does **not** retain the publisher's cryptographic signature; see [.NET public signing](https://github.com/dotnet/runtime/blob/main/docs/project/public-signing.md). Authenticode-signed images remain unsupported. Use a separate output path to keep the original:

```powershell
DistIL.Cli -i input/MyLibrary.dll -o output/MyLibrary.dll --public-sign --strict --verify-each
```

# Usage
Preview versions of the optimizer can be used by installing the [DistIL.OptimizerTask](https://www.nuget.org/packages/DistIL.OptimizerTask) NuGet package. It contains a MSBuild task which will automatically invoke the optimizer on the output project assembly when building in _Release_ mode.

By default, only methods and classes annotated with `[Optimize]` will be transformed, in order to reduce the chances of things breaking unexpectedly. This can be changed by setting the `DistilAllMethods` project property to `true`.
