# Cilica
![GitHub](https://img.shields.io/github/license/hez2010/Cilica)
[![Compiler CI](https://github.com/hez2010/Cilica/actions/workflows/ci.yml/badge.svg)](https://github.com/hez2010/Cilica/actions/workflows/ci.yml)

Post-build IL optimizer and intermediate representation for .NET programs (experimental).

Cilica is a continuation of [DistIL](https://github.com/dubiousconst282/DistIL).

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
Cilica.Cli -i input/MyLibrary.dll -o output/MyLibrary.dll --public-sign --strict --verify-each
```

# Usage
The `Cilica.OptimizerTask` NuGet package contains an MSBuild task which automatically invokes the optimizer on the output project assembly when building in _Release_ mode. Build the packages locally and install `Cilica.OptimizerTask` from the `artifacts` directory using a local NuGet source:

```shell
dotnet pack Cilica.sln -c Release -o artifacts
```

By default, only methods and classes annotated with `[Cilica.Attributes.Optimize]` will be transformed, in order to reduce the chances of things breaking unexpectedly. This can be changed by setting the `CilicaAllMethods` project property to `true`. Set `RunCilica` to enable or disable optimization explicitly, and use `CilicaExtraArgs` to pass additional CLI options.
