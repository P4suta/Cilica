using System;

namespace Cilica.Attributes
{
    /// <summary> Specifies that a class or method should be transformed by Cilica. </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
    public class OptimizeAttribute : Attribute { }

    /// <summary> Specifies that a class or method should not be transformed by Cilica. </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
    public class DoNotOptimizeAttribute : Attribute { }
}