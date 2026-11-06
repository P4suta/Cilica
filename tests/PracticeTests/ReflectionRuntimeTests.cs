namespace Cilica.PracticeTests;

using Cilica.Attributes;
using System.Reflection;

[Optimize]
public class ReflectionRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActivationWithoutAnAssemblyNameUsesTheCallingAssembly(bool fullSignature)
    {
        string name = typeof(ActivatedType).FullName!;
        var handle = fullSignature
            ? Activator.CreateInstance(null!, name, false, BindingFlags.Public | BindingFlags.Instance,
                null, [], null, null)
            : Activator.CreateInstance(null!, name);
        Assert.IsType<ActivatedType>(handle!.Unwrap());
    }

    public sealed class ActivatedType { }
}
