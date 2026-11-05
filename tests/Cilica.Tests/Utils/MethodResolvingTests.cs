namespace Cilica.Tests.Util;

using System.Text;

using Cilica.AsmIO;
using Cilica.IR;

[Collection("ModuleResolver")]
public class MethodResolvingTests
{
    public interface IValue { int Read(); }

    public class Base : IValue { public virtual int Read() => 1; }

    public class Override : Base { public override int Read() => 2; }

    public class Hidden : Base { public new virtual int Read() => 3; }

    public sealed class HiddenOverride : Hidden { public override int Read() => 4; }

    public sealed class Reimplementation : Base, IValue { public new int Read() => 5; }

    public sealed class Explicit : Base, IValue { int IValue.Read() => 6; }

    public class GenericBase<T> { public virtual T Echo(T value) => value; }

    public sealed class GenericOverride : GenericBase<string> { public override string Echo(string value) => value; }

    public interface IDefault { int Read() => 7; }
    public interface ISpecificDefault : IDefault { int IDefault.Read() => 8; }
    public sealed class DefaultConsumer : ISpecificDefault { }
    public abstract class AbstractDefaultConsumer : IDefault { }
    public class CompletedDefaultConsumer : AbstractDefaultConsumer { public virtual int Read() => 12; }
    public sealed class NonVirtualDefaultConsumer : AbstractDefaultConsumer { public int Read() => 12; }
    public class ConcreteDefaultConsumer : IDefault { }
    public sealed class HiddenDefaultConsumer : ConcreteDefaultConsumer { public int Read() => 13; }
    public sealed class OpenConsumer<T> : IValue { public int Read() => 9; }
    public interface IGenericRead<T> { int Read(); }
    public sealed class OpenDistinctConsumer<T> : IGenericRead<int>, IGenericRead<string>
    {
        int IGenericRead<int>.Read() => 1;
        int IGenericRead<string>.Read() => 2;
    }
    public interface IStatic<T> { static abstract int Read(); }
    public sealed class StaticConsumer : IStatic<StaticConsumer> { public static int Read() => 10; }
    public interface IStaticDefault { static virtual int Read() => 11; }
    public sealed class StaticDefaultConsumer : IStaticDefault { }
    public class CovariantBase { public virtual object Make() => new object(); }
    public class CovariantMiddle : CovariantBase { public override string Make() => "middle"; }
    public sealed class CovariantLeaf : CovariantMiddle { public override string Make() => "leaf"; }

    [Theory]
    [InlineData(typeof(IDefault), typeof(DefaultConsumer), typeof(ISpecificDefault))]
    [InlineData(typeof(IDefault), typeof(CompletedDefaultConsumer), typeof(CompletedDefaultConsumer))]
    [InlineData(typeof(IDefault), typeof(NonVirtualDefaultConsumer), typeof(IDefault))]
    [InlineData(typeof(IDefault), typeof(HiddenDefaultConsumer), typeof(IDefault))]
    [InlineData(typeof(IValue), typeof(OpenConsumer<>), typeof(OpenConsumer<>))]
    [InlineData(typeof(IGenericRead<int>), typeof(OpenDistinctConsumer<>), typeof(OpenDistinctConsumer<>))]
    [InlineData(typeof(IGenericRead<string>), typeof(OpenDistinctConsumer<>), typeof(OpenDistinctConsumer<>))]
    [InlineData(typeof(IStatic<StaticConsumer>), typeof(StaticConsumer), typeof(StaticConsumer))]
    [InlineData(typeof(IStaticDefault), typeof(StaticDefaultConsumer), typeof(IStaticDefault))]
    public void ResolvesDefaultStaticAndOpenInterfaceImplementations(Type declaration, Type actual, Type expected)
    {
        var target = _modResolver.Import(declaration).FindMethod("Read");
        var implementation = TypeUtils.ResolveVirtualMethod(target, _modResolver.Import(actual));
        Assert.NotNull(implementation);
        Assert.Equal(((TypeDefOrSpec)_modResolver.Import(expected)).Definition,
            ((TypeDefOrSpec)implementation.DeclaringType).Definition);
        if (!target.IsStatic && !actual.ContainsGenericParameters) {
            var runtimeTarget = Assert.Single(actual.GetInterfaceMap(declaration).TargetMethods);
            Assert.Equal(_modResolver.Import(runtimeTarget.DeclaringType!), implementation.DeclaringType);
            Assert.Equal(runtimeTarget.Name, implementation.Name);
        }
    }

    [Theory]
    [InlineData(typeof(CovariantBase))]
    [InlineData(typeof(CovariantMiddle))]
    public void ResolvesCovariantReturnSlotAliases(Type declaration)
    {
        var target = _modResolver.Import(declaration).FindMethod("Make");
        var implementation = TypeUtils.ResolveVirtualMethod(target, _modResolver.Import(typeof(CovariantLeaf)));
        Assert.Equal(_modResolver.Import(typeof(CovariantLeaf)), implementation!.DeclaringType);
        Assert.Equal(PrimType.String, implementation.ReturnType);
    }

    [Theory]
    [InlineData(typeof(Base), typeof(Override), typeof(Override))]
    [InlineData(typeof(Base), typeof(HiddenOverride), typeof(Base))]
    [InlineData(typeof(Hidden), typeof(HiddenOverride), typeof(HiddenOverride))]
    [InlineData(typeof(IValue), typeof(Override), typeof(Override))]
    [InlineData(typeof(IValue), typeof(HiddenOverride), typeof(Base))]
    [InlineData(typeof(IValue), typeof(Reimplementation), typeof(Reimplementation))]
    [InlineData(typeof(IValue), typeof(Explicit), typeof(Explicit))]
    public void VirtualDispatchRespectsSlotsAndInterfaceReimplementation(Type declaration, Type actual, Type expected)
    {
        var target = _modResolver.Import(declaration).FindMethod("Read");
        var implementation = TypeUtils.ResolveVirtualMethod(target, _modResolver.Import(actual));

        Assert.NotNull(implementation);
        Assert.Equal(_modResolver.Import(expected), implementation.DeclaringType);
    }

    [Fact]
    public void VirtualDispatchBindsGenericBaseSignatures()
    {
        var target = _modResolver.Import(typeof(GenericBase<string>)).Methods.Single(m => m.Name == "Echo");
        var implementation = TypeUtils.ResolveVirtualMethod(target, _modResolver.Import(typeof(GenericOverride)));

        Assert.Equal(_modResolver.Import(typeof(GenericOverride)), implementation!.DeclaringType);
    }

    readonly ModuleResolver _modResolver;

    public MethodResolvingTests(ModuleResolverFixture mrf)
    {
        _modResolver = mrf.Resolver;

        _modResolver.Import(typeof(StringBuilder));
        _modResolver.Import(typeof(Console));
    }

    [Fact]
    public void Test_MethodResolving()
    {
        var selector = "System.Text.StringBuilder::AppendLine(this, string)";
        var method = _modResolver.FindMethod(selector);

        Assert.NotNull(method);
        Assert.Equal("System.Text", method.DeclaringType.Namespace);
        Assert.Equal("StringBuilder", method.DeclaringType.Name);
        Assert.Equal("AppendLine", method.Name);
        Assert.Equal("StringBuilder", method.ReturnType.Name);
        Assert.Equal(2, method.ParamSig.Count);
        Assert.Equal("StringBuilder", method.ParamSig[0].Type.Name);
        Assert.Equal("String", method.ParamSig[1].Type.Name);
    }

    [Fact]
    public void Test_MethodResolvingReturnType()
    {
        var selector = "System.Text.StringBuilder::AppendLine(this, string) -> this";
        var method = _modResolver.FindMethod(selector);

        Assert.NotNull(method);
        Assert.Equal("System.Text", method.DeclaringType.Namespace);
        Assert.Equal("StringBuilder", method.DeclaringType.Name);
        Assert.Equal("AppendLine", method.Name);
        Assert.Equal("StringBuilder", method.ReturnType.Name);
        Assert.Equal(2, method.ParamSig.Count);
        Assert.Equal("StringBuilder", method.ParamSig[0].Type.Name);
        Assert.Equal("StringBuilder", method.ReturnType.Name);
        Assert.Equal("String", method.ParamSig[1].Type.Name);
    }

    [Fact]
    public void Test_MethodResolvingByValues()
    {
        var selector = "System.Console::WriteLine";
        var method = _modResolver.FindMethod(selector, [ConstString.Create("Hello World")]);

        Assert.NotNull(method);
        Assert.Equal("System", method.DeclaringType.Namespace);
        Assert.Equal("Console", method.DeclaringType.Name);
        Assert.Equal("WriteLine", method.Name);
        Assert.Equal("Void", method.ReturnType.Name);
        Assert.Equal(1, method.ParamSig.Count);
        Assert.Equal("String", method.ParamSig[0].Type.Name);
    }

    [Fact]
    public void Test_MethodResolvingByValuesReturnType()
    {
        var selector = "System.Console::WriteLine() -> void";
        var method = _modResolver.FindMethod(selector, [ConstString.Create("Hello World")]);

        Assert.NotNull(method);
        Assert.Equal("System", method.DeclaringType.Namespace);
        Assert.Equal("Console", method.DeclaringType.Name);
        Assert.Equal("WriteLine", method.Name);
        Assert.Equal("Void", method.ReturnType.Name);
        Assert.Equal(1, method.ParamSig.Count);
        Assert.Equal("String", method.ParamSig[0].Type.Name);
    }
}
