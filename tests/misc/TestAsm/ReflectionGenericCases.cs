using System.Reflection;
using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;

public static unsafe class ReflectionGenericCases
{
    public static object MethodDelegate()
        => typeof(Target).GetMethod(nameof(Target.Echo))!.MakeGenericMethod(typeof(int)).CreateDelegate<Func<int, int>>()(42);

    public static object MethodInvoke()
        => typeof(Target).GetMethod(nameof(Target.Echo))!.MakeGenericMethod(typeof(int)).Invoke(null, new object[] { 42 })!;

    public static object MethodPointer()
    {
        var pointer = (delegate* managed<string, string>)typeof(Target).GetMethod(nameof(Target.Echo))!
            .MakeGenericMethod(typeof(string)).MethodHandle.GetFunctionPointer();
        return pointer("hello");
    }

    public static object ConstructedType()
        => typeof(Owner<>).MakeGenericType(typeof(int)).GetMethod(nameof(Owner<int>.Echo))!.CreateDelegate<Func<int, int>>()(42);

    public static Type ConstructedTypeResult() => typeof(Owner<>).MakeGenericType(typeof(int));
    public static Type InlinedTypeResult() => Definition().MakeGenericType(Arguments());

    public static object ConstructedDelegateType()
        => ((Func<int, int>)typeof(Target).GetMethod(nameof(Target.Echo))!.MakeGenericMethod(typeof(int))
            .CreateDelegate(typeof(Func<,>).MakeGenericType(typeof(int), typeof(int))))(42);

    public static object MethodOnGenericOwner()
        => typeof(Owner<string>).GetMethod(nameof(Owner<string>.Describe))!.MakeGenericMethod(typeof(object))
            .CreateDelegate<Func<object, string>>()("hello");

    public static object ClosedStaticGenericReturnBuffer()
    {
        var callback = typeof(Owner<string>).GetMethod(nameof(Owner<string>.Compose))!.MakeGenericMethod(typeof(object))
            .CreateDelegate<Func<string, object, (string, object, string)>>(new object());
        var result = callback("hello", "world");
        return result.Item3 + ":" + result.Item1 + ":" + result.Item2;
    }

    public static T GenericBody<T>(T value)
        => typeof(Target).GetMethod(nameof(Target.Echo))!.MakeGenericMethod(typeof(T)).CreateDelegate<Func<T, T>>()(value);

    public static T GenericOwnerBody<T>(T value)
        => typeof(Owner<T>).GetMethod(nameof(Owner<T>.Echo))!.CreateDelegate<Func<T, T>>()(value);

    public static T GenericConstructedTypeBody<T>(T value)
        => typeof(Owner<>).MakeGenericType(typeof(T)).GetMethod(nameof(Owner<T>.Echo))!.CreateDelegate<Func<T, T>>()(value);

    public static T GenericValueInvoke<T>(T value) where T : struct
        => (T)typeof(Target).GetMethod(nameof(Target.Echo))!.MakeGenericMethod(typeof(T)).Invoke(null, new object[] { value })!;

    public static T GenericField<T>(T value)
        => (T)typeof(Box<T>).GetField(nameof(Box<T>.Value))!.GetValue(new Box<T>(value))!;

    public static T GenericPointer<T>(T value)
    {
        var pointer = (delegate* managed<T, T>)typeof(Target).GetMethod(nameof(Target.Echo))!
            .MakeGenericMethod(typeof(T)).MethodHandle.GetFunctionPointer();
        return pointer(value);
    }

    public static object DerivedOverride()
        => typeof(Derived).GetMethod(nameof(Derived.Read))!.Invoke(new Derived(), null)!;

    public static object InterfaceInvoke()
        => typeof(IValue).GetMethod(nameof(IValue.Read))!.Invoke(new Derived(), null)!;

    public static object ClosedInterfaceDelegate()
        => typeof(IValue).GetMethod(nameof(IValue.Read))!.CreateDelegate<Func<int>>(new Derived())();

    public static object AbstractInvoke()
        => typeof(Abstract).GetMethod(nameof(Abstract.Read))!.Invoke(new Concrete(), null)!;

    public static object ClosedAbstractDelegate()
        => typeof(Abstract).GetMethod(nameof(Abstract.Read))!.CreateDelegate<Func<int>>(new Concrete())();

    public static object BoxedInterfaceInvoke()
    {
        object value = new Counter(40);
        typeof(IValue).GetMethod(nameof(IValue.Add))!.Invoke(value, new object[] { 2 });
        return ((Counter)value).Value;
    }

    public static object BoxedInterfaceDelegate()
    {
        object value = new Counter(40);
        typeof(IValue).GetMethod(nameof(IValue.Add))!.CreateDelegate<Action<int>>(value)(2);
        return ((Counter)value).Value;
    }

    public static int GenericBoxedInterface<T>(T value) where T : struct, IValue
    {
        object boxed = value;
        typeof(IValue).GetMethod(nameof(IValue.Add))!.CreateDelegate<Action<int>>(boxed)(2);
        return ((T)boxed).Read();
    }

    public static Func<object, string> EscapedBoxedGenericVirtualDelegate()
    {
        object value = new GenericCounter<string>(40);
        return typeof(IGeneric).GetMethod(nameof(IGeneric.Describe))!.MakeGenericMethod(typeof(object))
            .CreateDelegate<Func<object, string>>(value);
    }

    public static Func<object, string> EscapedBoxedGenericMethodDelegate()
    {
        object value = new GenericCounter<string>(40);
        return typeof(GenericCounter<string>).GetMethod(nameof(GenericCounter<string>.Describe))!.MakeGenericMethod(typeof(object))
            .CreateDelegate<Func<object, string>>(value);
    }

    public static Func<U, string> GenericBoxedVirtualFactory<T, U>()
    {
        object value = new GenericCounter<T>(40);
        return typeof(IGeneric).GetMethod(nameof(IGeneric.Describe))!.MakeGenericMethod(typeof(U))
            .CreateDelegate<Func<U, string>>(value);
    }

    public static Func<U, string> GenericBoxedMethodFactory<T, U>()
    {
        object value = new GenericCounter<T>(40);
        return typeof(GenericCounter<T>).GetMethod(nameof(GenericCounter<T>.Describe))!.MakeGenericMethod(typeof(U))
            .CreateDelegate<Func<U, string>>(value);
    }

    public delegate ref int RefGetter();

    public static ref int EscapingBoxReference<T>()
    {
        object value = new RefCounter<T> { Value = 40 };
        return ref typeof(RefCounter<T>).GetMethod(nameof(RefCounter<T>.Get))!.CreateDelegate<RefGetter>(value)();
    }

    public static object BoxedDefaultInterfaceIdentity()
    {
        object value = new DefaultValue();
        return typeof(IDefaultIdentity).GetMethod(nameof(IDefaultIdentity.Same))!.Invoke(value, new object[] { value })!;
    }

    public static bool GenericDefaultInterfaceIdentity<T>(T value) where T : struct, IDefaultIdentity
    {
        object boxed = value;
        return (bool)typeof(IDefaultIdentity).GetMethod(nameof(IDefaultIdentity.Same))!.Invoke(boxed, new object[] { boxed })!;
    }

    public static object BoxedGenericVirtualInvoke()
    {
        object value = new GenericCounter<string>(40);
        return typeof(IGeneric).GetMethod(nameof(IGeneric.Describe))!.MakeGenericMethod(typeof(object))
            .Invoke(value, new object[] { "hello" })!;
    }

    public static object GenericVirtualDelegate()
        => typeof(GenericBase<string>).GetMethod(nameof(GenericBase<string>.Describe))!.MakeGenericMethod(typeof(object))
            .CreateDelegate<Func<object, string>>(new GenericDerived<string>())("hello");

    public static object GenericVirtualInvoke()
        => typeof(GenericBase<string>).GetMethod(nameof(GenericBase<string>.Describe))!.MakeGenericMethod(typeof(object))
            .Invoke(new GenericDerived<string>(), new object[] { "hello" })!;

    public static object DefaultInterfaceInvoke()
        => typeof(IDefault).GetMethod(nameof(IDefault.Read))!.Invoke(new DefaultReceiver(), null)!;

    public static object DefaultInterfaceDelegate()
        => typeof(IDefault).GetMethod(nameof(IDefault.Read))!.CreateDelegate<Func<IDefault, int>>()(new DefaultReceiver());

    public static Func<object, string> EscapedGenericDefaultInterface()
        => typeof(IDefaultGeneric<string>).GetMethod(nameof(IDefaultGeneric<string>.Describe))!.MakeGenericMethod(typeof(object))
            .CreateDelegate<Func<object, string>>(new DefaultGenericReceiver<string>());

    public static object StructConstraint()
        => typeof(Target).GetMethod(nameof(Target.StructEcho))!.MakeGenericMethod(typeof(int)).CreateDelegate<Func<int, int>>()(42);

    public static object ReferenceConstraint()
        => typeof(Target).GetMethod(nameof(Target.ReferenceEcho))!.MakeGenericMethod(typeof(string)).CreateDelegate<Func<string, string>>()("hello");

    public static object ConstructorConstraint()
        => typeof(Target).GetMethod(nameof(Target.Create))!.MakeGenericMethod(typeof(Created)).CreateDelegate<Func<Created>>()().Value;

    public static object InterfaceConstraint()
        => typeof(Target).GetMethod(nameof(Target.ReadValue))!.MakeGenericMethod(typeof(Counter)).CreateDelegate<Func<Counter, int>>()(new Counter(42));

    public static T GenericConstrainedBody<T>(T value) where T : struct
        => typeof(Target).GetMethod(nameof(Target.StructEcho))!.MakeGenericMethod(typeof(T)).CreateDelegate<Func<T, T>>()(value);

    public static object InlinedGenericType()
        => Definition().MakeGenericType(Arguments()).GetMethod(nameof(Owner<int>.Echo))!.CreateDelegate<Func<int, int>>()(42);

    public static object InlinedClosedVirtual()
        => VirtualDefinition().CreateDelegate<Func<int>>(new Derived())();

    private static Type Definition() => typeof(Owner<>);
    private static Type[] Arguments() => [typeof(int)];
    private static MethodInfo VirtualDefinition() => typeof(Base).GetMethod(nameof(Base.Read))!;

    public static object InvalidStructConstraint()
    {
        try { return typeof(Target).GetMethod(nameof(Target.StructEcho))!.MakeGenericMethod(typeof(string)).CreateDelegate<Func<string, string>>()("hello"); }
        catch (ArgumentException error) { return error.GetType().Name; }
    }

    public static object InvalidTypeConstraint()
    {
        try { return typeof(ReferenceOwner<>).MakeGenericType(typeof(int)).GetMethod("Echo")!.CreateDelegate<Func<int, int>>()(42); }
        catch (ArgumentException error) { return error.GetType().Name; }
    }

    public static object InvalidConstructorConstraint()
    {
        try { return typeof(Target).GetMethod(nameof(Target.Create))!.MakeGenericMethod(typeof(string)).CreateDelegate<Func<string>>()(); }
        catch (ArgumentException error) { return error.GetType().Name; }
    }

    public static object InvalidInterfaceConstraint()
    {
        try { return typeof(Target).GetMethod(nameof(Target.ReadValue))!.MakeGenericMethod(typeof(int)).CreateDelegate<Func<int, int>>()(42); }
        catch (ArgumentException error) { return error.GetType().Name; }
    }

    public static object MissingTypeArgument()
    {
        try { return typeof(Target).GetMethod(nameof(Target.Echo))!.MakeGenericMethod(new Type[] { null! }).CreateDelegate<Func<int, int>>()(42); }
        catch (ArgumentNullException error) { return error.GetType().Name; }
    }

    public static object WrongArity()
    {
        try { return typeof(Target).GetMethod(nameof(Target.Echo))!.MakeGenericMethod(typeof(int), typeof(string)).CreateDelegate<Func<int, int>>()(42); }
        catch (ArgumentException error) { return error.GetType().Name; }
    }

    public static object AlreadyConstructed()
    {
        try { return typeof(Target).GetMethod(nameof(Target.Echo))!.MakeGenericMethod(typeof(int)).MakeGenericMethod(typeof(string)).CreateDelegate<Func<int, int>>()(42); }
        catch (InvalidOperationException error) { return error.GetType().Name; }
    }

    public static object OpenInterfaceDelegate()
        => typeof(IValue).GetMethod(nameof(IValue.Read))!.CreateDelegate<Func<IValue, int>>()(new Derived());

    public static object OpenAbstractDelegate()
        => typeof(Abstract).GetMethod(nameof(Abstract.Read))!.CreateDelegate<Func<Abstract, int>>()(new Concrete());

    public static object OpenGenericVirtualDelegate()
    {
        try {
            return typeof(GenericBase<string>).GetMethod(nameof(GenericBase<string>.Describe))!.MakeGenericMethod(typeof(object))
                .CreateDelegate<Func<GenericBase<string>, object, string>>()(new GenericDerived<string>(), "hello");
        } catch (NotSupportedException error) { return error.GetType().Name; }
    }

    public delegate string OpenGenericCounter(ref GenericCounter<string> receiver, object value);

    public static object OpenGenericStructDelegate()
    {
        var callback = typeof(GenericCounter<string>).GetMethod(nameof(GenericCounter<string>.Describe))!.MakeGenericMethod(typeof(object))
            .CreateDelegate<OpenGenericCounter>();
        var value = new GenericCounter<string>(40);
        return callback(ref value, "hello");
    }

    public static object OpaqueGenericInvoke<T>(T value)
    {
        try { return typeof(Target).GetMethod(nameof(Target.Echo))!.MakeGenericMethod(typeof(T)).Invoke(null, new object?[] { value })!; }
        catch (ArgumentException error) { return error.GetType().Name; }
    }

    public static object GenericUnknownConstraint<T>(T value)
    {
        try { return typeof(Target).GetMethod(nameof(Target.StructEcho))!.MakeGenericMethod(typeof(T)).Invoke(null, new object?[] { value })!; }
        catch (ArgumentException error) { return error.GetType().Name; }
    }

    public static object IndirectReferenceConstraint<T, U>(T value) where T : U where U : class
    {
        try { return typeof(Target).GetMethod(nameof(Target.ReferenceEcho))!.MakeGenericMethod(typeof(T)).CreateDelegate<Func<T, T>>()(value)!; }
        catch (ArgumentException error) { return error.GetType().Name; }
    }

    public static object ChangedTypeArguments()
    {
        Type[] arguments = [typeof(string)];
        Change(arguments);
        return typeof(Target).GetMethod(nameof(Target.Echo))!.MakeGenericMethod(arguments).CreateDelegate<Func<int, int>>()(42);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Change(Type[] arguments) => arguments[0] = typeof(int);

    public static class Target
    {
        public static T Echo<T>(T value) => value;
        public static T StructEcho<T>(T value) where T : struct => value;
        public static T ReferenceEcho<T>(T value) where T : class => value;
        public static T Create<T>() where T : new() => new();
        public static int ReadValue<T>(T value) where T : IValue => value.Read();
    }

    public class Owner<T>
    {
        public static T Echo(T value) => value;
        public static string Describe<U>(U value) => typeof(T).Name + ":" + typeof(U).Name + ":" + value;
        public static (T, U, string) Compose<U>(object state, T left, U right) => (left, right, typeof(T).Name + ":" + typeof(U).Name);
    }

    public class ReferenceOwner<T> where T : class { public static T Echo(T value) => value; }
    public class Box<T>(T value) { public T Value = value; }
    public class Created { public int Value = 42; }
    public interface IValue { int Read(); void Add(int amount); }
    public class Base : IValue { public virtual int Read() => 1; public void Add(int amount) { } }
    public sealed class Derived : Base { public override int Read() => 42; }
    public abstract class Abstract { public abstract int Read(); }
    public sealed class Concrete : Abstract { public override int Read() => 42; }
    public struct Counter(int value) : IValue { public int Value = value; public int Read() => Value; public void Add(int amount) => Value += amount; }
    public interface IGeneric { string Describe<U>(U value); }
    public struct GenericCounter<T>(int value) : IGeneric
    {
        public int Value = value;
        public string Describe<U>(U value) => typeof(T).Name + ":" + typeof(U).Name + ":" + value + ":" + ++Value;
    }
    public struct RefCounter<T>
    {
        public T? Tag;
        public int Value;

        [UnscopedRef, MethodImpl(MethodImplOptions.NoInlining)]
        public ref int Get()
        {
            Value += typeof(T).Name.Length;
            return ref Value;
        }
    }
    public class GenericBase<T> { public virtual string Describe<U>(U value) => "base"; }
    public sealed class GenericDerived<T> : GenericBase<T>
    {
        public override string Describe<U>(U value) => typeof(T).Name + ":" + typeof(U).Name + ":" + value;
    }
    public interface IDefault { int Read() => 42; }
    public sealed class DefaultReceiver : IDefault { }
    public interface IDefaultGeneric<T> { string Describe<U>(U value) => typeof(T).Name + ":" + typeof(U).Name + ":" + value; }
    public struct DefaultGenericReceiver<T> : IDefaultGeneric<T> { }
    public interface IDefaultIdentity { bool Same(object other) => ReferenceEquals(this, other); }
    public struct DefaultValue : IDefaultIdentity { }
}
