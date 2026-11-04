global using Xunit;

using System.Runtime.CompilerServices;

using Cilica.Attributes;

public static class Utils
{
    [DoNotOptimize, MethodImpl(MethodImplOptions.NoInlining)]
    public static T DoNotOptimize<T>(T value) => value;
}