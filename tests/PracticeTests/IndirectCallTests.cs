namespace DistIL.PracticeTests;

using DistIL.Attributes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[Optimize, CheckCodeGenAfterRun]
public unsafe class IndirectCallTests
{
    [Fact]
    public void ManagedCallsPreserveArgumentsAndUnusedResults()
    {
        int value = 10;
        delegate*<ref int, int, int> update = &Update;
        update(ref value, 2);
        Assert.Equal(15, update(ref value, 3));
        Assert.Equal(15, value);
        // CHECK: CIL::CallIndirect
    }

    [Fact]
    public void VoidIndirectCallsKeepTheirEffects()
    {
        int value = 41;
        delegate*<ref int, void> increment = &Increment;
        increment(ref value);
        Assert.Equal(42, value);
        // CHECK: CIL::CallIndirect
    }

    [Fact]
    public void UnmanagedCallsPreserveTheirCallingConventions()
    {
        delegate* unmanaged[Stdcall]<int, int, int> stdcall = &StdcallTarget;
        delegate* unmanaged[Cdecl]<int, int, int> cdecl = &CdeclTarget;
        Assert.Equal(42, stdcall(2, 4));
        Assert.Equal(24, cdecl(4, 2));
        // CHECK: CIL::CallIndirect
    }

    [Fact]
    public void GenericFunctionPointersSpecializeDuringInlining()
    {
        Assert.Equal(42, Invoke<int>(&Identity, 42));
        // CHECK: CIL::CallIndirect
        // CHECK-NOT: call IndirectCallTests::Invoke
    }

    static T Invoke<T>(delegate*<T, T> function, T value) => function(value);

    [DoNotOptimize]
    static int Update(ref int value, int amount) => value += amount;

    [DoNotOptimize]
    static void Increment(ref int value) => value++;

    [DoNotOptimize]
    static int Identity(int value) => value;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int StdcallTarget(int a, int b) => a + b * 10;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int CdeclTarget(int a, int b) => a + b * 10;
}
