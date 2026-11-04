namespace DistIL.Tests.Analysis;

using System.Reflection;
using DistIL.Analysis;
using DistIL.AsmIO;
using DistIL.CodeGen.Cil;
using DistIL.IR;
using DistIL.IR.Utils;
using DistIL.Passes;
using DistIL.Util;
using MethodBody = DistIL.IR.MethodBody;

[Collection("ModuleResolver")]
public class GlobalFunctionEffectsTests(ModuleResolverFixture fixture)
{
    readonly ModuleResolver _modResolver = fixture.Resolver;

    [Fact]
    public void WarmDependencyValidationDoesNotAllocateAndDetectsDirectMutations()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Void);
        var type = body.Definition.DeclaringType;
        var methods = Enumerable.Range(0, 64).Select(i => type.CreateMethod("Node" + i, PrimType.Void, [],
            MethodAttributes.Public | MethodAttributes.Static)).ToArray();
        for (int i = 0; i < methods.Length; i++) {
            methods[i].Body = new MethodBody(methods[i]);
            var builder = new IRBuilder(methods[i].Body!.CreateBlock());
            if (i + 1 < methods.Length) builder.CreateCall(methods[i + 1]);
            builder.CreateReturn();
        }
        var effects = new Compilation(body.Definition.Module, new VoidLogger(), new()).GetAnalysis<GlobalFunctionEffects>();
        for (int i = 0; i < 100; i++) Assert.True(effects.GetEffects(methods[0]).IsPure);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) effects.GetEffects(methods[0]);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal(0, allocated);
        Assert.Equal(methods.Length, effects.Computations);

        // No explicit invalidation: the root must notice a changed leaf revision.
        var leaf = methods[^1];
        var loop = leaf.Body!.CreateBlock();
        loop.SetBranch(loop);
        leaf.Body.EntryBlock.SetBranch(loop);
        Assert.True(effects.GetEffects(methods[0]).MayDiverge);

        // Body identity is also part of validation, even with a fresh revision.
        leaf.Body = new MethodBody(leaf);
        leaf.Body.CreateBlock().SetBranch(new ReturnInst());
        Assert.True(effects.GetEffects(methods[0]).IsPure);
        Assert.Equal(methods.Length * 3, effects.Computations);
    }

    [Fact]
    public void RecursiveDependencyDiamondsReuseEffectsWhileAnAncestorIsBuilding()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Void);
        var type = body.Definition.DeclaringType;
        var methods = Enumerable.Range(0, 8).Select(i => type.CreateMethod("Node" + i, PrimType.Void, [],
            MethodAttributes.Public | MethodAttributes.Static)).ToArray();
        for (int i = 0; i < methods.Length; i++) {
            methods[i].Body = new MethodBody(methods[i]);
            var builder = new IRBuilder(methods[i].Body!.CreateBlock());
            builder.CreateCall(methods[0]);
            for (int j = i + 1; j < methods.Length; j++) builder.CreateCall(methods[j]);
            builder.CreateReturn();
        }
        var effects = new Compilation(body.Definition.Module, new VoidLogger(), new()).GetAnalysis<GlobalFunctionEffects>();
        Assert.True(effects.GetEffects(methods[0]).MayDiverge);
        Assert.Equal(methods.Length, effects.Computations);
        Assert.True(effects.GetEffects(methods[^1]).MayWriteMem);
        Assert.Equal(methods.Length, effects.Computations);
    }

    [Fact]
    public void RecursiveFunctionEffectsAreConservative()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Void);
        var type = body.Definition.DeclaringType;
        var other = type.CreateMethod("Other", PrimType.Void, [], MethodAttributes.Public | MethodAttributes.Static);
        other.Body = new MethodBody(other);
        var ib = new IRBuilder(body.CreateBlock());
        ib.CreateCall(other);
        var field = type.CreateField("Effect", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        ib.CreateFieldStore(field, null, ConstInt.CreateI(1));
        ib.CreateReturn();
        var ob = new IRBuilder(other.Body.CreateBlock());
        ob.CreateCall(body.Definition);
        ob.CreateReturn();
        var comp = new Compilation(body.Definition.Module, new VoidLogger(), new CompilationSettings());
        var effects = comp.GetAnalysis<GlobalFunctionEffects>();

        Assert.True(effects.GetEffects(body.Definition).MayWriteMem);
        Assert.True(effects.GetEffects(other).MayWriteMem);
    }

    [Fact]
    public void DictionaryLookupEffectsIncludeUserComparers()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Void);
        body.CreateBlock().SetBranch(new ReturnInst());
        var comp = new Compilation(body.Definition.Module, new VoidLogger(), new CompilationSettings());
        var effects = comp.GetAnalysis<GlobalFunctionEffects>();
        var dict = _modResolver.Import(typeof(Dictionary<int, int>));

        Assert.True(effects.GetEffects(dict.FindMethod("get_Item")).MayWriteMem);
        Assert.True(effects.GetEffects(dict.FindMethod("ContainsKey")).MayWriteMem);
        Assert.False(effects.GetEffects(dict.FindMethod("get_Count")).MayWriteMem);
    }
}
