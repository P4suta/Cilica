using System.Reflection;
using System.Runtime.CompilerServices;

public static class ReflectionCases
{
    public static object Fields()
    {
        var target = new Target();
        var field = typeof(Target).GetField(nameof(Target.Value))!;
        field.SetValue(target, 42);
        return field.GetValue(target)!;
    }

    public static object StaticFields()
    {
        var field = typeof(Target).GetField(nameof(Target.StaticValue))!;
        field.SetValue(new object(), "updated");
        return field.GetValue(null)!;
    }

    public static object ObjectFieldStoresMissing()
    {
        var target = new Target();
        var field = typeof(Target).GetField(nameof(Target.ObjectValue))!;
        field.SetValue(target, Type.Missing);
        return ReferenceEquals(Type.Missing, field.GetValue(target));
    }

    public static object StructFieldLoad()
    {
        var target = new Target { Counter = new Counter { Value = 42 } };
        return ((Counter)typeof(Target).GetField(nameof(Target.Counter))!.GetValue(target)!).Value;
    }

    public static object FieldBoxIdentity()
    {
        var target = new Target();
        var field = typeof(Target).GetField(nameof(Target.Value))!;
        return !ReferenceEquals(field.GetValue(target), field.GetValue(target));
    }

    public static object Properties()
    {
        var target = new Target();
        var property = typeof(Target).GetProperty(nameof(Target.Property))!;
        property.SetValue(target, 41, null);
        return property.GetValue(target, null)!;
    }

    public static object StaticProperties()
    {
        var property = typeof(Target).GetProperty(nameof(Target.StaticProperty))!;
        property.SetValue(null, 17);
        return property.GetValue(new object())!;
    }

    public static object MethodArguments()
        => typeof(Target).GetMethod(nameof(Target.Add))!.Invoke(new Target(), new object[] { 37, "hello" })!;

    public static object VoidInvocation()
    {
        var target = new Target();
        object? result = typeof(Target).GetMethod(nameof(Target.Set))!.Invoke(target, new object[] { 42 });
        return result == null && target.Value == 42;
    }

    public static object VirtualInvocation()
        => typeof(Base).GetMethod(nameof(Base.Compute))!.Invoke(new Target(), null)!;

    public static object BoxedStructFields()
    {
        object target = new Counter();
        var field = typeof(Counter).GetField(nameof(Counter.Value))!;
        field.SetValue(target, 42);
        return field.GetValue(target)!;
    }

    public static object BoxedStructMethod()
    {
        object target = new Counter();
        typeof(Counter).GetMethod(nameof(Counter.Increment))!.Invoke(target, null);
        return ((Counter)target).Value;
    }

    public static object MutatedBoxArgument()
    {
        object boxed = 1;
        object[] arguments = [boxed];
        Unsafe.Unbox<int>(boxed) = 42;
        return typeof(Target).GetMethod(nameof(Target.Echo))!.Invoke(null, arguments)!;
    }

    public static object ClosedGeneric()
    {
        var target = new GenericTarget<int>();
        typeof(GenericTarget<int>).GetField(nameof(GenericTarget<int>.Value))!.SetValue(target, 20);
        typeof(GenericTarget<int>).GetProperty(nameof(GenericTarget<int>.Property))!.SetValue(target, 22);
        return target.Value + (int)typeof(GenericTarget<int>).GetProperty(nameof(GenericTarget<int>.Property))!.GetValue(target)!;
    }

    public static object ExactGetType()
    {
        var target = new Target();
        return target.GetType().GetField(nameof(Target.Value))!.GetValue(target)!;
    }

    public static object ChangedArguments()
    {
        object[] arguments = [1];
        arguments[0] = 42;
        return typeof(Target).GetMethod(nameof(Target.Echo))!.Invoke(null, arguments)!;
    }

    public static object EmptyArguments()
        => typeof(Target).GetMethod(nameof(Target.Constant))!.Invoke(null, Array.Empty<object>())!;

    public static object ReferenceArguments()
        => typeof(Target).GetMethod(nameof(Target.ObjectEcho))!.Invoke(null, new object[] { "hello" })!;

    public static object NullReferenceArgument()
        => typeof(Target).GetMethod(nameof(Target.ObjectEcho))!.Invoke(null, new object?[] { null }) == null;

    public static object NullableResult()
        => typeof(Target).GetMethod(nameof(Target.NullableConstant))!.Invoke(null, null)!;

    public static object StaticLookupThroughNullableTarget(Target? target)
        => target!.GetType().GetField(nameof(Target.StaticValue))!.GetValue(null)!;

    private static int PrivateValue = 42;

    public static object DeclaredNonPublicField()
        => typeof(ReflectionCases).GetField(nameof(PrivateValue), BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.GetValue(null)!;

    public static object DeclaredOverride()
        => typeof(Target).GetMethod(nameof(Target.Compute), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!.Invoke(new Target(), null)!;

    public static object ReflectionExceptionFilter()
    {
        int result = 0;
        try {
            try { typeof(Target).GetMethod(nameof(Target.Fail))!.Invoke(null, null); }
            finally { result += 10; }
        } catch (Exception exception) when (RecordException(exception, ref result)) {
            result += 100;
        }
        return result;
    }

    private static bool RecordException(Exception exception, ref int result)
    {
        result += exception is TargetInvocationException ? 1 : 2;
        return true;
    }

    public static object WrappedException()
    {
        try {
            return typeof(Target).GetMethod(nameof(Target.Fail))!.Invoke(null, null)!;
        } catch (TargetInvocationException exception) {
            return exception.InnerException!.GetType().Name;
        }
    }

    public static object UnwrappedException()
    {
        try {
            return typeof(Target).GetMethod(nameof(Target.Fail))!.Invoke(null, BindingFlags.DoNotWrapExceptions, null, null, null)!;
        } catch (InvalidOperationException exception) {
            return exception.Message;
        }
    }

    public static object PropertyException()
    {
        try {
            return typeof(Target).GetProperty(nameof(Target.FailingProperty))!.GetValue(null)!;
        } catch (TargetInvocationException exception) {
            return exception.InnerException!.GetType().Name;
        }
    }

    public static object InvocationInsideFinally()
    {
        int result = 0;
        try { result = 10; }
        finally { result += (int)typeof(Target).GetMethod(nameof(Target.Constant))!.Invoke(null, null)!; }
        return result;
    }

    public static object InvocationInsideFilter()
    {
        int value = (int)typeof(Target).GetMethod(nameof(Target.Constant))!.Invoke(null, null)!;
        try { throw new InvalidOperationException(); }
        catch (InvalidOperationException) when ((int)typeof(Target).GetMethod(nameof(Target.Constant))!.Invoke(null, null)! == value) {
            return value;
        }
    }

    public static object UnwrappedInvocationInsideFilter()
    {
        try { throw new InvalidOperationException(); }
        catch (InvalidOperationException) when ((int)typeof(Target).GetMethod(nameof(Target.Constant))!
            .Invoke(null, BindingFlags.DoNotWrapExceptions, null, null, null)! == 42) {
            return 42;
        }
    }

    public static object PropertyInsideFilter()
    {
        try { throw new InvalidOperationException(); }
        catch (InvalidOperationException) when ((int)typeof(Target).GetProperty(nameof(Target.FailingProperty))!.GetValue(null)! == 42) {
            return "unexpected";
        }
        catch (InvalidOperationException) { return 42; }
    }

    public static object EscapedArguments()
    {
        object[] arguments = [1];
        Change(arguments);
        return typeof(Target).GetMethod(nameof(Target.Echo))!.Invoke(null, arguments)!;
    }

    public static object ArgumentsUsedAfterInvocation()
    {
        object[] arguments = [1];
        object result = typeof(Target).GetMethod(nameof(Target.Echo))!.Invoke(null, arguments)!;
        arguments[0] = 42;
        return (int)result + (int)arguments[0];
    }

    public static object PrimitiveConversion()
        => typeof(Target).GetMethod(nameof(Target.LongEcho))!.Invoke(null, new object[] { 42 })!;

    public static object NullValueArgument()
        => typeof(Target).GetMethod(nameof(Target.Echo))!.Invoke(null, new object?[] { null })!;

    public static object WrongArgumentCount()
    {
        try { return typeof(Target).GetMethod(nameof(Target.Constant))!.Invoke(null, new object[] { 1 })!; }
        catch (TargetParameterCountException exception) { return exception.GetType().Name; }
    }

    public static object InvalidFieldValue()
    {
        try { typeof(Target).GetField(nameof(Target.Value))!.SetValue(new Target(), "hello"); }
        catch (ArgumentException exception) { return exception.GetType().Name; }
        return "unexpected";
    }

    public static object IndexedProperty()
        => typeof(Target).GetProperty("Item")!.GetValue(new Target(), new object[] { 35 })!;

    public static object GenericMethodDefinition()
    {
        try { return typeof(Target).GetMethod(nameof(Target.Generic))!.Invoke(null, null)!; }
        catch (InvalidOperationException exception) { return exception.GetType().Name; }
    }

    public static object InaccessibleDirectField()
        => typeof(Target).GetField("Secret", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(new Target())!;

    public static object RefArgument()
    {
        object[] arguments = [1];
        typeof(Target).GetMethod(nameof(Target.ByRef))!.Invoke(null, arguments);
        return arguments[0];
    }

    public static object BinderArgument()
        => typeof(Target).GetMethod(nameof(Target.Echo))!.Invoke(null, BindingFlags.Default, Type.DefaultBinder, new object[] { 42 }, null)!;

    public static object MissingOptionalArgument()
        => typeof(Target).GetMethod(nameof(Target.OptionalObject))!.Invoke(null, new object[] { Type.Missing })!;

    public static object OpaqueObjectArgument(object? value)
    {
        try { return typeof(Target).GetMethod(nameof(Target.ObjectEcho))!.Invoke(null, new object?[] { value }) ?? "null"; }
        catch (ArgumentException exception) { return exception.GetType().Name; }
    }

    public static object WideFieldStore()
    {
        var target = new Target();
        typeof(Target).GetField(nameof(Target.WideValue))!.SetValue(target, 42L);
        return target.WideValue;
    }

    public static object InheritedMember()
        => typeof(Target).GetField(nameof(Base.Inherited))!.GetValue(new Target())!;

    public static object IgnoreCaseLookup()
        => typeof(Target).GetField("value", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)!.GetValue(new Target())!;

    public static object AmbiguousLookup()
    {
        try { return typeof(Target).GetMethod(nameof(Target.Overload))!.Invoke(null, null)!; }
        catch (AmbiguousMatchException exception) { return exception.GetType().Name; }
    }

    public static object MissingMember()
    {
        try { return typeof(Target).GetField("Missing")!.GetValue(null)!; }
        catch (NullReferenceException exception) { return exception.GetType().Name; }
    }

    public static object InvalidReceiver(object? target)
    {
        try { return typeof(Target).GetField(nameof(Target.Value))!.GetValue(target)!; }
        catch (Exception exception) { return exception.GetType().Name; }
    }

    public static object ReadonlyStore()
    {
        try {
            typeof(Target).GetField(nameof(Target.Readonly))!.SetValue(null, 100);
            return Target.Readonly;
        } catch (FieldAccessException exception) { return exception.GetType().Name; }
    }

    public static object OpenGeneric()
    {
        try { return typeof(GenericTarget<>).GetField(nameof(GenericTarget<int>.StaticValue))!.GetValue(null)!; }
        catch (InvalidOperationException exception) { return exception.GetType().Name; }
    }

    public static object LookupStillUsed()
    {
        var field = typeof(Target).GetField(nameof(Target.Value))!;
        return field.Name + ":" + field.GetValue(new Target());
    }

    public static object InitializedField()
        => typeof(Initialized).GetField(nameof(Initialized.Value))!.GetValue(null)!;

    public static object InitializedMethod()
        => typeof(Initialized).GetMethod(nameof(Initialized.Read))!.Invoke(null, null)!;

    public static object ThrowingInitializer()
    {
        try { return typeof(FailedInitialization).GetMethod(nameof(FailedInitialization.Read))!.Invoke(null, null)!; }
        catch (TargetInvocationException exception) { return exception.InnerException!.GetType().Name; }
    }

    public static object? InitializationBox;

    public static object FieldInitializationMutatesArgument()
    {
        object value = 1;
        InitializationBox = value;
        typeof(MutatingFieldInitialization).GetField(nameof(MutatingFieldInitialization.Value))!.SetValue(null, value);
        return MutatingFieldInitialization.Value;
    }

    public static object MethodInitializationMutatesArgument()
    {
        object value = 1;
        InitializationBox = value;
        return typeof(MutatingMethodInitialization).GetMethod(nameof(MutatingMethodInitialization.Echo))!.Invoke(null, new object[] { value })!;
    }

    public static object ThroughHelper() => LookupValue().GetValue(new Target())!;

    public static FieldInfo LookupValue() => typeof(Target).GetField(nameof(Target.Value))!;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Change(object[] arguments) => arguments[0] = 42;

    public class Base
    {
        public int Inherited = 9;
        public virtual int Compute() => 1;
    }

    public sealed class Target : Base
    {
        public int Value = 7;
        public long WideValue;
        public object? ObjectValue;
        public Counter Counter;
#pragma warning disable CS0414 // Accessed through reflection.
        private int Secret = 42;
#pragma warning restore CS0414
        public static string StaticValue = "initial";
        public static readonly int Readonly = 7;
        public int Property { get => Value + 1; set => Value = value; }
        public int this[int index] => Value + index;
        public static int StaticProperty { get; set; }
        public static int FailingProperty => throw new InvalidOperationException("failure");
        public int Add(int value, string text) => value + text.Length;
        public void Set(int value) => Value = value;
        public override int Compute() => 42;
        public static int Echo(int value) => value;
        public static object? ObjectEcho(object? value) => value;
        public static object OptionalObject(object? value = null) => value ?? "default";
        public static long LongEcho(long value) => value;
        public static int Constant() => 42;
        public static int? NullableConstant() => 42;
        public static T? Generic<T>() => default;
        public static int Fail() => throw new InvalidOperationException("failure");
        public static void ByRef(ref int value) => value = 42;
        public static int Overload() => 1;
        public static int Overload(int value) => value;
    }

    public struct Counter
    {
        public int Value;
        public void Increment() => Value++;
    }

    public class GenericTarget<T>
    {
        public T? Value;
        public T? Property { get; set; }
        public static T? StaticValue;
    }

    public class Initialized
    {
        public static int Value;
        static Initialized() => Value = 42;
        public static int Read() => Value;
    }

    public class FailedInitialization
    {
        static FailedInitialization() => throw new InvalidOperationException();
        public static int Read() => 42;
    }

    public class MutatingFieldInitialization
    {
        static MutatingFieldInitialization() => Unsafe.Unbox<int>(InitializationBox!) = 42;
        public static int Value;
    }

    public class MutatingMethodInitialization
    {
        static MutatingMethodInitialization() => Unsafe.Unbox<int>(InitializationBox!) = 42;
        public static int Echo(int value) => value;
    }
}
