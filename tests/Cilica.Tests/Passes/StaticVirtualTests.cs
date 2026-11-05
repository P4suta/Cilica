namespace Cilica.Tests.Passes;

using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cilica.Analysis;
using Cilica.AsmIO;
using Cilica.CodeGen.Cil;
using Cilica.Frontend;
using Cilica.IR;
using Cilica.IR.Utils;
using Cilica.Passes;
using MethodBody = Cilica.IR.MethodBody;

[Collection("ModuleResolver")]
public class StaticVirtualTests(ModuleResolverFixture fixture) : PassTestBase(fixture)
{
    public interface IValue
    {
        static abstract int Read();
    }

    public class Value : IValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Read() => 42;
    }

    public class HiddenValue : Value
    {
        public new static int Read() => 99;
    }

    public class ReimplementedValue : Value, IValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public new static int Read() => 44;
    }

    public struct ExplicitValue : IValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int IValue.Read() => 43;
    }

    public interface IDefault
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static virtual int Read() => 11;
    }

    public interface ISpecific : IDefault
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int IDefault.Read() => 22;
    }

    public class DefaultValue : IDefault { }
    public struct DefaultStruct : IDefault { }
    public class SpecificValue : ISpecific { }

    public class OverrideDefault : IDefault
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Read() => 33;
    }

    public interface IGeneric<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static virtual U Echo<U>(T ignored, U value) => value;
    }

    public class GenericDefault<T> : IGeneric<T> { }

    [Theory]
    [InlineData(typeof(IValue), typeof(Value), 42)]
    [InlineData(typeof(IValue), typeof(HiddenValue), 42)]
    [InlineData(typeof(IValue), typeof(ReimplementedValue), 44)]
    [InlineData(typeof(IValue), typeof(ExplicitValue), 43)]
    [InlineData(typeof(IDefault), typeof(DefaultValue), 11)]
    [InlineData(typeof(IDefault), typeof(DefaultStruct), 11)]
    [InlineData(typeof(IDefault), typeof(SpecificValue), 22)]
    [InlineData(typeof(IDefault), typeof(OverrideDefault), 33)]
    public void ResolvedStaticCallsCanBeEmittedAndInlined(Type declaration, Type implementation, int expected)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var builder = new IRBuilder(body.CreateBlock());
        var method = _modResolver.Import(declaration).FindMethod("Read");
        var call = builder.Emit(new CallInst(method, [], constraint: _modResolver.Import(implementation)));
        builder.CreateReturn(call);

        var compilation = new Compilation(body.Definition.Module, new VoidLogger(), new() { VerifyEachPass = true });
        PassRunner.Run(new Devirtualize(), new MethodTransformContext(compilation, body));
        compilation.EnsureReferencedMembersAccessible(body);
        Assert.Null(call.Constraint);
        Assert.False(call.IsVirtual);
        Assert.Equal(expected, Utils.Compile<Func<int>>(body)());

        Run(body, new InlineMethods());
        Assert.Empty(body.Instructions().OfType<CallInst>());
        Assert.Equal(expected, Utils.Compile<Func<int>>(body)());
    }

    [Fact]
    public void InliningSpecializesStaticDispatchAndGenericDefaultBodies()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32);
        var helper = (MethodDefOrSpec)_modResolver.Import(typeof(StaticVirtualTests)).FindMethod(nameof(Echo));
        var specialized = helper.GetSpec([_modResolver.Import(typeof(GenericDefault<string>)), PrimType.String, PrimType.Int32]);
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateReturn(builder.CreateCall(specialized, ConstString.Create("ignored"), body.Args[0]));

        Run(body, new InlineMethods());
        Assert.Empty(body.Instructions().OfType<CallInst>());
        Assert.Equal(42, Utils.Compile<Func<int, int>>(body)(42));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static U Echo<TSelf, T, U>(T ignored, U value) where TSelf : IGeneric<T> => TSelf.Echo(ignored, value);

    [Theory]
    [InlineData(typeof(DefaultValue), 11)]
    [InlineData(typeof(OverrideDefault), 33)]
    public void OpenStaticDispatchRetainsItsRuntimeImplementation(Type implementation, int expected)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var definition = body.Definition.DeclaringType.CreateMethod("Read", PrimType.Int32, [],
            MethodAttributes.Public | MethodAttributes.Static,
            genericPars: [new GenericParamType(0, true, constraints: [new GenericParamConstraint(_modResolver.Import(typeof(IDefault)), [])])]);
        definition.Body = new MethodBody(definition);
        var builder = new IRBuilder(definition.Body.CreateBlock());
        var call = builder.Emit(new CallInst(_modResolver.Import(typeof(IDefault)).FindMethod("Read"), [],
            constraint: definition.GenericParams[0]));
        builder.CreateReturn(call);

        Run(definition.Body, new InlineMethods(), new DeadCodeElim());
        Assert.Contains(call, definition.Body.Instructions());
        Assert.Equal(definition.GenericParams[0], call.Constraint);
        var root = new IRBuilder(body.CreateBlock());
        root.CreateReturn(root.CreateCall(definition.GetSpec([_modResolver.Import(implementation)])));
        Assert.Equal(expected, Utils.Compile<Func<int>>(body)());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaticFunctionPointersRoundTripAndResolvePrimitiveImplementations(bool devirtualize)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32, PrimType.Int32);
        var method = _modResolver.Import(typeof(IAdditionOperators<int, int, int>)).FindMethod("op_Addition");
        var builder = new IRBuilder(body.CreateBlock());
        var address = builder.Emit(new FuncAddrInst(method, constraint: PrimType.Int32));
        var signature = new FuncPtrType(new MethodSig(PrimType.Int32, [PrimType.Int32, PrimType.Int32], isInstance: false));
        builder.CreateReturn(builder.Emit(new CilIntrinsic.CallIndirect(signature, address, [body.Args[0], body.Args[1]])));
        body.Definition.ILBody = ILGenerator.GenerateCode(body);
        body.Definition.Body = body = ILImporter.ParseCode(body.Definition);
        IRVerifier.Verify(body);
        Assert.Equal(PrimType.Int32, Assert.Single(body.Instructions().OfType<FuncAddrInst>()).Constraint);

        if (devirtualize) {
            var compilation = new Compilation(body.Definition.Module, new VoidLogger(), new() { VerifyEachPass = true });
            PassRunner.Run(new Devirtualize(), new MethodTransformContext(compilation, body));
            compilation.EnsureReferencedMembersAccessible(body);
            Assert.Null(Assert.Single(body.Instructions().OfType<FuncAddrInst>()).Constraint);
        }
        Assert.Equal(42, Utils.Compile<Func<int, int, int>>(body)(20, 22));
    }
}
