using System.Reflection;

public static unsafe class ReflectionNonPublicCases
{
    private const BindingFlags InstanceFlags = BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags StaticFlags = BindingFlags.NonPublic | BindingFlags.Static;

    public static object Field()
    {
        var target = new ReflectionPrivateTarget();
        var field = typeof(ReflectionPrivateTarget).GetField("_value", InstanceFlags)!;
        field.SetValue(target, 42);
        return field.GetValue(target)!;
    }

    public static object Property()
    {
        var target = new ReflectionPrivateTarget();
        var property = typeof(ReflectionPrivateTarget).GetProperty("Value", InstanceFlags)!;
        property.SetValue(target, 42);
        return property.GetValue(target)!;
    }

    public static object Invoke()
        => typeof(ReflectionPrivateTarget).GetMethod("AddOne", StaticFlags)!.Invoke(null, new object[] { 41 })!;

    public static object StaticDelegate()
        => typeof(ReflectionPrivateTarget).GetMethod("AddOne", StaticFlags)!.CreateDelegate<Func<int, int>>()(41);

    public static object InstanceDelegate()
        => typeof(ReflectionPrivateTarget).GetMethod("Add", InstanceFlags)!.CreateDelegate<Func<int, int>>(new ReflectionPrivateTarget())(2);

    public static object OpenInstanceDelegate()
        => typeof(ReflectionPrivateTarget).GetMethod("Add", InstanceFlags)!.CreateDelegate<Func<ReflectionPrivateTarget, int, int>>()(new ReflectionPrivateTarget(), 2);

    public static object StaticFactory()
        => ((Func<int, int>)Delegate.CreateDelegate(typeof(Func<int, int>), typeof(ReflectionPrivateTarget).GetMethod("AddOne", StaticFlags)!))(41);

    public static object FunctionPointer()
    {
        var pointer = (delegate* managed<int, int>)typeof(ReflectionPrivateTarget).GetMethod("AddOne", StaticFlags)!.MethodHandle.GetFunctionPointer();
        return pointer(41);
    }

    private delegate int PrivateCallback(int value);

    public static object PrivateDelegateType()
        => typeof(ReflectionPrivateTarget).GetMethod("AddOne", StaticFlags)!.CreateDelegate<PrivateCallback>()(41);

    public static object GenericType()
        => typeof(ReflectionPrivateGeneric<int>).GetMethod("Echo", StaticFlags)!.CreateDelegate<Func<int, int>>()(42);

    public static object HiddenType()
        => ReflectionPrivateTarget.GetHiddenType().GetMethod("AddOne", StaticFlags)!.CreateDelegate<Func<int, int>>()(41);
}

public sealed class ReflectionPrivateTarget
{
    private int _value = 40;
    private int Value { get => _value; set => _value = value; }
    private int Add(int value) => _value + value;
    private static int AddOne(int value) => value + 1;

    public static Type GetHiddenType() => typeof(Hidden);

    private static class Hidden
    {
        private static int AddOne(int value) => value + 1;
    }
}

internal static class ReflectionPrivateGeneric<T>
{
    private static T Echo(T value) => value;
}
