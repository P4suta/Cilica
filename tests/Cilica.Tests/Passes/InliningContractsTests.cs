namespace Cilica.Tests.Passes;

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cilica.AsmIO;
using Cilica.CodeGen.Cil;
using Cilica.IR;
using Cilica.IR.Utils;
using Cilica.Passes;
using MethodBody = Cilica.IR.MethodBody;

[Collection("ModuleResolver")]
public class InliningContractsTests(ModuleResolverFixture fixture) : PassTestBase(fixture)
{
    public static class Contracts
    {
        [RequiresUnreferencedCode("Uses reflection")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int UnreferencedCode() => 42;

        [RequiresDynamicCode("Generates code")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int DynamicCode() => 42;

        [RequiresAssemblyFiles("Reads assembly files")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int AssemblyFiles() => 42;

        [DynamicDependency(nameof(UnreferencedCode))]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Dependency() => 42;

        [UnconditionalSuppressMessage("Trimming", "IL2026")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Suppressed() => 42;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Parameter([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type) => 42;

        [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Type Return() => typeof(string);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Generic<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>() => 42;

        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)]
        public static Type Property {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => typeof(string);
        }
    }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)]
    public static class AnnotatedType
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Read() => 42;
    }

    public static class AnnotatedGenericType<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Read() => 42;
    }

    [Theory]
    [InlineData(nameof(Contracts.UnreferencedCode), false)]
    [InlineData(nameof(Contracts.DynamicCode), false)]
    [InlineData(nameof(Contracts.AssemblyFiles), false)]
    [InlineData(nameof(Contracts.Suppressed), false)]
    [InlineData(nameof(Contracts.Dependency), true)]
    [InlineData(nameof(Contracts.Parameter), true)]
    [InlineData(nameof(Contracts.Return), true)]
    [InlineData(nameof(Contracts.Generic), true)]
    [InlineData("get_Property", true)]
    public void InliningRetainsTrimmingDependencies(string name, bool hasDependency)
    {
        var target = _modResolver.Import(typeof(Contracts)).FindMethod(name);
        if (target.IsGeneric) target = target.GetSpec([PrimType.String]);
        foreach (bool preserveDependencies in new[] { false, true }) {
            CheckCall(target, hasDependency && preserveDependencies, preserveDependencies);
        }
    }

    [Fact]
    public void CallsRetainTheirDeclaringTypeDependencies()
    {
        foreach (bool preserveDependencies in new[] { false, true }) {
            CheckCall(_modResolver.Import(typeof(AnnotatedType)).FindMethod("Read"), preserveDependencies, preserveDependencies);
            CheckCall(_modResolver.Import(typeof(AnnotatedGenericType<string>)).FindMethod("Read"), preserveDependencies, preserveDependencies);
        }
    }

    private void CheckCall(MethodDesc target, bool retainCall, bool preserveDependencies)
    {
        var body = Utils.CreateTestMethod(_modResolver, target.ReturnType);
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateReturn(builder.CreateCall(target, target.ParamSig.Count == 0 ? [] : [ConstNull.Create()]));
        var compilation = new Compilation(body.Definition.Module, new VoidLogger(), new() {
            VerifyEachPass = true,
            PreserveTrimmingDependencies = preserveDependencies
        });

        Assert.True(CompilationPipeline.CreateDefault(compilation).Run([body.Definition]).Succeeded);

        Assert.Equal(retainCall, body.Definition.ILBody!.Instructions.Any(instruction => instruction.OpCode == ILCode.Call && instruction.Operand == target));
        if (target.ReturnType == PrimType.Int32) {
            Assert.Equal(42, Utils.Compile<Func<int>>(body.Definition.Body!)());
        } else {
            Assert.Equal(typeof(string), Utils.Compile<Func<Type>>(body.Definition.Body!)());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HelpersWithNestedFunctionsCanStillInline(bool functionAddress)
    {
        TypeDesc resultType = functionAddress ? _modResolver.Import(typeof(Func<int>)) : PrimType.Int32;
        var body = Utils.CreateTestMethod(_modResolver, resultType);
        var type = body.Definition.DeclaringType;
        var local = type.CreateMethod(functionAddress ? "<Owner>b__0_0" : "<Owner>g__Local|0_0", PrimType.Int32, [],
            MethodAttributes.Public | MethodAttributes.Static);
        local.ImplAttribs = MethodImplAttributes.NoInlining;
        local.Body = new MethodBody(local);
        local.Body.CreateBlock().SetBranch(new ReturnInst(ConstInt.CreateI(42)));
        var owner = type.CreateMethod("Owner", resultType, [], MethodAttributes.Public | MethodAttributes.Static);
        owner.ImplAttribs = MethodImplAttributes.AggressiveInlining;
        owner.Body = new MethodBody(owner);
        var builder = new IRBuilder(owner.Body.CreateBlock());
        builder.CreateReturn(functionAddress
            ? builder.CreateNewObj(resultType.FindMethod(".ctor"), ConstNull.Create(), builder.Emit(new FuncAddrInst(local)))
            : builder.CreateCall(local));
        builder = new IRBuilder(body.CreateBlock());
        builder.CreateReturn(builder.CreateCall(owner));

        Run(body, new InlineMethods());

        var instructions = ILGenerator.GenerateCode(body).Instructions;
        Assert.DoesNotContain(instructions, instruction => instruction.Operand == owner);
        Assert.Contains(instructions, instruction => instruction.Operand == local);
        Assert.Equal(42, functionAddress ? Utils.Compile<Func<Func<int>>>(body)()() : Utils.Compile<Func<int>>(body)());
    }

    [Fact]
    public void LocalFunctionsWithoutRemainingNestedCodeCanStillInline()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var helper = body.Definition.DeclaringType.CreateMethod("<Run>g__Local|0_0", PrimType.Int32, [],
            MethodAttributes.Public | MethodAttributes.Static);
        helper.Body = new MethodBody(helper);
        helper.Body.CreateBlock().SetBranch(new ReturnInst(ConstInt.CreateI(42)));
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateReturn(builder.CreateCall(helper));

        Run(body, new InlineMethods());

        Assert.DoesNotContain(ILGenerator.GenerateCode(body).Instructions, instruction => instruction.Operand == helper);
        Assert.Equal(42, Utils.Compile<Func<int>>(body)());
    }
}
