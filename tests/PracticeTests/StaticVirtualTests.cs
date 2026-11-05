namespace Cilica.PracticeTests;

using System.Runtime.CompilerServices;
using Cilica.Attributes;

[Optimize]
public class StaticVirtualTests
{
    [Fact, CheckCodeGenAfterRun]
    public void DefaultBodiesInlineAfterGenericSpecialization()
    {
        Assert.Equal(11, Read<DefaultValue>());
        Assert.Equal(22, Read<OverrideValue>());
        Assert.Equal(42, Echo<DefaultValue, int>(42));
        Assert.Equal("value", Echo<DefaultValue, string>("value"));
        // CHECK-NOT: ::Read
        // CHECK-NOT: ::Echo
    }

    [Fact, CheckCodeGenAfterRun]
    public void StaticDelegatesInlineAfterGenericSpecialization()
    {
        Assert.Equal(11, InvokeDelegate<DefaultValue>());
        Assert.Equal(22, InvokeDelegate<OverrideValue>());
        // CHECK-NOT: ::Invoke
        // CHECK-NOT: ::Read
        // CHECK-NOT: newobj Func
    }

    [Fact]
    public void OpenStaticDelegatesSelectTheRuntimeImplementation()
    {
        Assert.Equal(11, InvokeOpenDelegate<DefaultValue>());
        Assert.Equal(22, InvokeOpenDelegate<OverrideValue>());
    }

    [Fact]
    public void UnresolvedStaticCallsPreserveImplementationEffects()
    {
        s_Effects = 0;
        Touch<DefaultEffect>();
        Assert.Equal(0, s_Effects);
        Touch<OverrideEffect>();
        Assert.Equal(1, s_Effects);
    }

    [Fact]
    public void StaticCallsPreserveImplementationTypeInitialization()
    {
        Assert.Equal(33, Read<InitializedValue>());
        Assert.Equal(1, s_ValueInitializations);
        Assert.Equal(44, ReadInitializedDefault<InitializedDefaultValue>());
        Assert.Equal(1, s_InterfaceInitializations);
    }

    static int Read<T>() where T : IValue => T.Read();
    static U Echo<T, U>(U value) where T : IValue => T.Echo(value);
    static int InvokeDelegate<T>() where T : IValue => new Func<int>(T.Read)();
    static int ReadInitializedDefault<T>() where T : IInitializedDefault => T.Read();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int InvokeOpenDelegate<T>() where T : IValue => new Func<int>(T.Read)();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Touch<T>() where T : IEffect => T.Touch();

    interface IValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static virtual int Read() => 11;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static virtual U Echo<U>(U value) => value;
    }

    class DefaultValue : IValue { }

    struct OverrideValue : IValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Read() => 22;
    }

    static int s_Effects;
    static int s_ValueInitializations;
    static int s_InterfaceInitializations;

    interface IEffect
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static virtual void Touch() { }
    }

    class DefaultEffect : IEffect { }

    class OverrideEffect : IEffect
    {
        public static void Touch() => s_Effects++;
    }

    class InitializedValue : IValue
    {
        static InitializedValue() => s_ValueInitializations++;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Read() => 33;
    }

    interface IInitializedDefault
    {
        static IInitializedDefault() => s_InterfaceInitializations++;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static virtual int Read() => 44;
    }

    class InitializedDefaultValue : IInitializedDefault { }
}
