# Cilica
![GitHub](https://img.shields.io/github/license/hez2010/Cilica)
[![Compiler CI](https://github.com/hez2010/Cilica/actions/workflows/ci.yml/badge.svg)](https://github.com/hez2010/Cilica/actions/workflows/ci.yml)
[![Cilica.OptimizerTask on NuGet](https://img.shields.io/nuget/vpre/Cilica.OptimizerTask?label=Cilica.OptimizerTask)](https://www.nuget.org/packages/Cilica.OptimizerTask)
[![Cilica.Core on NuGet](https://img.shields.io/nuget/vpre/Cilica.Core?label=Cilica.Core)](https://www.nuget.org/packages/Cilica.Core)

Post-build IL optimizer and intermediate representation for .NET programs (experimental).

Cilica is a continuation of [DistIL](https://github.com/dubiousconst282/DistIL).

Try [Cilica Lab](https://hez2010.github.io/CilicaLab/) in your browser to explore optimization passes, control-flow graphs, and generated code.

# Notable Features
- [SSA-based Intermediate Representation](docs/api-walkthrough.md)
- Linq Expansion
- Loop Strength Reduction
- Loop Vectorization
- List Pre-sizing
- Devirtualization
- Method Inlining
- Scalar Replacement

See [Optimization Passes](docs/opt-list.md) for a detailed list of optimizations passes available.

```powershell
Cilica.Cli -i input/MyLibrary.dll -o output/MyLibrary.dll
```

# Usage
The `Cilica.OptimizerTask` NuGet package contains an MSBuild task which automatically invokes the optimizer on the output project assembly when building in _Release_ mode. It requires the .NET 10+. Install the preview package with:

```shell
dotnet add package Cilica.OptimizerTask --prerelease
```

By default, only methods and classes annotated with `[Cilica.Attributes.Optimize]` will be transformed. This can be changed by setting the `CilicaAllMethods` project property to `true`:

```xml
<CilicaAllMethods>true</CilicaAllMethods>
```

Set `RunCilica` to enable or disable optimization explicitly, and use `CilicaExtraArgs` to pass additional CLI options.

Use `--skip-module-initializers` to omit explicit module-initialization checks when the runtime initializes modules before use. `Cilica.OptimizerTask` enables this by default with `PublishAot=true`; set `CilicaSkipModuleInitializers=false` to override it.

The optimizer task processes your main assembly by default. To include managed dependencies and framework assemblies in a self-contained build or publish, enable `CilicaAllAssemblies`:

```xml
<SelfContained>true</SelfContained>
<CilicaAllAssemblies>true</CilicaAllAssemblies>
```

Under this mode, assembly signatures and ReadyToRun code will be removed, and `CilicaAllMethods` will be set to `true`; set it to `false` to retain attribute filtering.

There's also a `Cilica.Core` package that exposes the optimizer and intermediate representation APIs for direct use.
