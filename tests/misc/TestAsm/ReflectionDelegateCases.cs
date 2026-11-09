using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

public static unsafe class ReflectionDelegateCases
{
    public static object GenericStatic()
        => typeof(Target).GetMethod(nameof(Target.Add))!.CreateDelegate<Func<int, int>>()(41);

    public static object UntypedStatic()
        => ((Func<int, int>)typeof(Target).GetMethod(nameof(Target.Add))!.CreateDelegate(typeof(Func<int, int>)))(41);

    public static object GenericClosedInstance()
        => typeof(Target).GetMethod(nameof(Target.AddInstance))!.CreateDelegate<Func<int, int>>(new Target())(2);

    public static object UntypedClosedInstance()
        => ((Func<int, int>)typeof(Target).GetMethod(nameof(Target.AddInstance))!.CreateDelegate(typeof(Func<int, int>), new Target()))(2);

    public static object StaticFactoryOpen()
        => ((Func<int, int>)Delegate.CreateDelegate(typeof(Func<int, int>), typeof(Target).GetMethod(nameof(Target.Add))!))(41);

    public static object StaticFactoryOpenWithFlag()
        => ((Func<int, int>)Delegate.CreateDelegate(typeof(Func<int, int>), typeof(Target).GetMethod(nameof(Target.Add))!, false)!)(41);

    public static object StaticFactoryClosed()
        => ((Func<int, int>)Delegate.CreateDelegate(typeof(Func<int, int>), new Target(), typeof(Target).GetMethod(nameof(Target.AddInstance))!))(2);

    public static object StaticFactoryClosedWithFlag()
        => ((Func<int, int>)Delegate.CreateDelegate(typeof(Func<int, int>), new Target(), typeof(Target).GetMethod(nameof(Target.AddInstance))!, false)!)(2);

    public static object OpenInstance()
        => typeof(Target).GetMethod(nameof(Target.AddInstance))!.CreateDelegate<Func<Target, int, int>>()(new Target(), 2);

    public static object OpenVirtual()
        => typeof(Base).GetMethod(nameof(Base.Read))!.CreateDelegate<Func<Base, int>>()(new Target());

    public static object ClosedVirtual()
        => typeof(Base).GetMethod(nameof(Base.Read))!.CreateDelegate<Func<int>>(new Target())();

    public static object ClosedVirtualIdentity()
    {
        var target = new Target();
        var action = typeof(Base).GetMethod(nameof(Base.Read))!.CreateDelegate<Func<int>>(target);
        Func<int> expected = target.Read;
        return action == expected && action.Method == expected.Method && ReferenceEquals(action.Target, target);
    }

    public static object BoxedVirtual()
        => typeof(object).GetMethod(nameof(ToString))!.CreateDelegate<Func<string>>(new Counter { Value = 42 })();

    public static object ClosedStatic()
        => typeof(Target).GetMethod(nameof(Target.Prefix))!.CreateDelegate<Func<int, int>>("hello")(37);

    public static object ClosedStaticNull()
        => typeof(Target).GetMethod(nameof(Target.Prefix))!.CreateDelegate<Func<int, int>>(null)(42);

    public static object ClosedStaticFactory()
        => ((Func<int, int>)Delegate.CreateDelegate(typeof(Func<int, int>), "hello", typeof(Target).GetMethod(nameof(Target.Prefix))!))(37);

    public static object BoundMissingArgument()
        => typeof(Target).GetMethod(nameof(Target.IsMissing))!.CreateDelegate<Func<bool>>(Type.Missing)();

    public static object OpenStaticExplicitNull()
        => typeof(Target).GetMethod(nameof(Target.Add))!.CreateDelegate<Func<int, int>>(null)(41);

    public static object BoxedStruct()
    {
        object box = new Counter { Value = 40 };
        var action = typeof(Counter).GetMethod(nameof(Counter.Increment))!.CreateDelegate<Func<int, int>>(box);
        action(2);
        return ((Counter)box).Value;
    }

    public delegate int RefCounter(ref Counter receiver, int value);

    public static object OpenStruct()
    {
        var action = typeof(Counter).GetMethod(nameof(Counter.Increment))!.CreateDelegate<RefCounter>();
        var counter = new Counter { Value = 40 };
        return action(ref counter, 2);
    }

    public delegate void RefAction(ref int value);

    public static object RefParameter()
    {
        var action = typeof(Target).GetMethod(nameof(Target.Increment))!.CreateDelegate<RefAction>();
        int value = 41;
        action(ref value);
        return value;
    }

    public delegate ref int RefResult();
    public static int Result;

    public static object RefReturn()
    {
        var action = typeof(Target).GetMethod(nameof(Target.GetResult))!.CreateDelegate<RefResult>();
        action() = 42;
        return Result;
    }

    public static object LargeStructReturn()
        => typeof(Target).GetMethod(nameof(Target.MakeTuple))!.CreateDelegate<Func<int, (long, long, long)>>("hello")(37).Item1;

    public static object DelegateIdentity()
    {
        var target = new Target();
        var method = typeof(Target).GetMethod(nameof(Target.AddInstance))!;
        var action = method.CreateDelegate<Func<int, int>>(target);
        Func<int, int> expected = target.AddInstance;
        return ReferenceEquals(action.Target, target) && action.Method == method && action == expected && action.GetInvocationList().Length == 1;
    }

    public static object ClosedGenericType()
        => typeof(GenericTarget<int>).GetMethod(nameof(GenericTarget<int>.Echo))!.CreateDelegate<Func<int, int>>()(42);

    public static object DelegateInitialization()
    {
        var action = typeof(Initialized).GetMethod(nameof(Initialized.Read))!.CreateDelegate<Func<int>>();
        int before = InitializationEffect;
        return before * 100 + action();
    }

    public static object DelegateTypeInitialization()
    {
        Result = 0;
        var action = typeof(Target).GetMethod(nameof(Target.Increment))!.CreateDelegate<RefAction>();
        GC.KeepAlive(action);
        return Result;
    }

    public static object DelegateException()
    {
        try { typeof(Target).GetMethod(nameof(Target.Fail))!.CreateDelegate<Action>()(); }
        catch (InvalidOperationException error) { return error.Message; }
        return "unexpected";
    }

    public static object DelegateInsideFilter()
    {
        try { throw new InvalidOperationException(); }
        catch (InvalidOperationException) when (typeof(Target).GetMethod(nameof(Target.Add))!.CreateDelegate<Func<int, int>>()(41) == 42) {
            return 42;
        }
    }

    public static object StaticFunctionPointer()
    {
        var pointer = (delegate* managed<int, int>)typeof(Target).GetMethod(nameof(Target.Add))!.MethodHandle.GetFunctionPointer();
        return pointer(41);
    }

    public static object CopiedFunctionPointerHandle()
    {
        var handle = typeof(Target).GetMethod(nameof(Target.Add))!.MethodHandle;
        var copy = handle;
        return ((delegate* managed<int, int>)copy.GetFunctionPointer())(41);
    }

    public static object FunctionPointerIdentity()
    {
        nint actual = typeof(Target).GetMethod(nameof(Target.Add))!.MethodHandle.GetFunctionPointer();
        return actual == (nint)(delegate* managed<int, int>)&Target.Add;
    }

    public static object GenericTypeFunctionPointer()
    {
        var pointer = (delegate* managed<string, string>)typeof(GenericTarget<string>).GetMethod(nameof(GenericTarget<string>.Echo))!.MethodHandle.GetFunctionPointer();
        return pointer("hello");
    }

    public static object UnmanagedFunctionPointer()
    {
        var pointer = (delegate* unmanaged[Cdecl]<int, int>)typeof(Target).GetMethod(nameof(Target.UnmanagedAdd))!.MethodHandle.GetFunctionPointer();
        return pointer(41);
    }

    public static object FunctionPointerInitialization()
    {
        var pointer = (delegate* managed<int>)typeof(Initialized).GetMethod(nameof(Initialized.Read))!.MethodHandle.GetFunctionPointer();
        int before = InitializationEffect;
        return before * 100 + pointer();
    }

    public static object FunctionPointerMetadataObserved()
    {
        var method = typeof(Target).GetMethod(nameof(Target.Add))!;
        var pointer = (delegate* managed<int, int>)method.MethodHandle.GetFunctionPointer();
        return method.Name + ":" + pointer(41);
    }

    public static object NativeImportDelegateCreation()
        => typeof(Target).GetMethod(nameof(Target.NativeImport))!.CreateDelegate<Func<int>>().Target == null;

    public static object NativeImportPointerCreation()
        => typeof(Target).GetMethod(nameof(Target.NativeImport))!.MethodHandle.GetFunctionPointer() != 0;

    public static object ThroughHelper() => Lookup().CreateDelegate<Func<int, int>>()(41);
    private static MethodInfo Lookup() => typeof(Target).GetMethod(nameof(Target.Add))!;

    public static object ClosedNullInstance()
    {
        var action = typeof(Target).GetMethod(nameof(Target.WithoutThis))!.CreateDelegate<Func<int>>(null);
        return action.Target == null && action() == 42;
    }

    public static object InvalidDelegateType()
    {
        try { return typeof(Target).GetMethod(nameof(Target.Add))!.CreateDelegate(typeof(string)); }
        catch (ArgumentException error) { return error.GetType().Name; }
    }

    public static object SignatureMismatch()
    {
        try { return typeof(Target).GetMethod(nameof(Target.Add))!.CreateDelegate<Func<string, int>>(); }
        catch (ArgumentException error) { return error.GetType().Name; }
    }

    public static object FailureReturnsNull()
        => Delegate.CreateDelegate(typeof(Func<string, int>), typeof(Target).GetMethod(nameof(Target.Add))!, false) == null;

    public static object CovariantSignature()
        => typeof(Target).GetMethod(nameof(Target.Text))!.CreateDelegate<Func<object>>()();

    public static object OpenWithNonNullTarget()
    {
        try { return typeof(Target).GetMethod(nameof(Target.AddInstance))!.CreateDelegate<Func<Target, int, int>>(new Target()); }
        catch (ArgumentException error) { return error.GetType().Name; }
    }

    public static object BoundValueArgument()
    {
        try { return typeof(Target).GetMethod(nameof(Target.Sum))!.CreateDelegate<Func<int, int>>(40)(2); }
        catch (ArgumentException error) { return error.GetType().Name; }
    }

    public static object OpenDelegateType()
    {
        try { return typeof(Target).GetMethod(nameof(Target.Add))!.CreateDelegate(typeof(Func<,>)); }
        catch (ArgumentException error) { return error.GetType().Name; }
    }

    public static object GenericMethodDefinition()
    {
        try { return typeof(Target).GetMethod(nameof(Target.Generic))!.CreateDelegate<Func<int, int>>(); }
        catch (ArgumentException error) { return error.GetType().Name; }
    }

    public static object NameBasedFactory()
        => ((Func<int, int>)Delegate.CreateDelegate(typeof(Func<int, int>), typeof(Target), nameof(Target.Add)))(41);

    public static object DefaultFunctionPointerHandle()
    {
        try { return default(RuntimeMethodHandle).GetFunctionPointer(); }
        catch (ArgumentNullException error) { return error.GetType().Name; }
    }

    public static object MutatedFunctionPointerHandle()
    {
        var handle = typeof(Target).GetMethod(nameof(Target.Add))!.MethodHandle;
        ChangeHandle(ref handle);
        return ((delegate* managed<int, int>)handle.GetFunctionPointer())(40);
    }

    public static object OverwrittenFunctionPointerHandle()
    {
        var handle = typeof(Target).GetMethod(nameof(Target.Add))!.MethodHandle;
        handle = typeof(Target).GetMethod(nameof(Target.AddTwo))!.MethodHandle;
        return ((delegate* managed<int, int>)handle.GetFunctionPointer())(40);
    }

    public static object InstanceFunctionPointer()
        => typeof(Target).GetMethod(nameof(Target.AddInstance))!.MethodHandle.GetFunctionPointer() != 0;

    public static object RuntimeNamedDelegate(string name)
        => typeof(Target).GetMethod(name)!.CreateDelegate<Func<int, int>>()(41);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ChangeHandle(ref RuntimeMethodHandle handle)
        => handle = typeof(Target).GetMethod(nameof(Target.AddTwo))!.MethodHandle;

    public class Base { public virtual int Read() => 1; }

    public sealed class Target : Base
    {
        public int Value = 40;
        public int AddInstance(int value) => Value + value;
        public int WithoutThis() => 42;
        public override int Read() => 42;
        public static int Add(int value) => value + 1;
        public static int AddTwo(int value) => value + 2;
        public static int Sum(int first, int second) => first + second;
        public static int Prefix(string? text, int value) => (text?.Length ?? 0) + value;
        public static bool IsMissing(object value) => ReferenceEquals(value, Type.Missing);
        public static void Fail() => throw new InvalidOperationException("failure");
        public static string Text() => "hello";
        public static T Generic<T>(T value) => value;
        public static void Increment(ref int value) => value++;
        public static ref int GetResult() => ref Result;
        public static (long, long, long) MakeTuple(string text, int value) => (text.Length + value, 2, 3);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static int UnmanagedAdd(int value) => value + 1;

        [DllImport("Cilica.Nonexistent.ReflectionLibrary")]
        public static extern int NativeImport();
    }

    public struct Counter
    {
        public int Value;
        public int Increment(int value) => Value += value;
        public override string ToString() => Value.ToString();
    }

    public class GenericTarget<T> { public static T Echo(T value) => value; }

    public static int InitializationEffect;
    public class Initialized
    {
        static Initialized() => InitializationEffect = 10;
        public static int Read() => 42;
    }
}
