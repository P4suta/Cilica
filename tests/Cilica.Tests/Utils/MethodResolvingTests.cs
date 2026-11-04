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
