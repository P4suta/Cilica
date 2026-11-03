namespace DistIL.Tests.Passes;

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
public class SimplifyInstsTests(ModuleResolverFixture fixture) : PassTestBase(fixture)
{
    [Fact]
    public void Test_SimplifyInsts() => CheckIR("SimplifyInsts.ethil", new SimplifyInsts(_modResolver));

    [Theory]
    [InlineData(UnaryOp.Neg)]
    [InlineData(UnaryOp.Not)]
    [InlineData(UnaryOp.FNeg)]
    public void SimplifyUnaryCancelsBothOperations(UnaryOp op)
    {
        var type = op == UnaryOp.FNeg ? PrimType.Double : PrimType.Int32;
        var body = Utils.CreateTestMethod(_modResolver, type, type);
        var ib = new IRBuilder(body.CreateBlock());
        var inner = ib.Emit(new UnaryInst(op, body.Args[0]));
        var outer = ib.Emit(new UnaryInst(op, inner));
        var ret = ib.CreateReturn(outer);
        Run(body, new SimplifyInsts(_modResolver));

        Assert.Same(body.Args[0], ret.Value);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void DictionaryLookupCoalescingRequiresProvenComparerAndEdge(bool bypassEdge, bool subclass)
    {
        var dictionary = _modResolver.Import(typeof(Dictionary<string, int>));
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Bool, PrimType.String);
        var entry = body.CreateBlock();
        var check = body.CreateBlock();
        var read = body.CreateBlock();
        var missing = body.CreateBlock();
        var ib = new IRBuilder(entry);
        MethodDesc ctor = dictionary.FindMethod(".ctor", new MethodSig(PrimType.Void, []));

        if (subclass) {
            var derived = body.Definition.Module.CreateType(null, "DerivedDictionary", baseType: dictionary);
            var constructor = derived.CreateMethod(".ctor", PrimType.Void, [new ParamDef(derived, "this")],
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
            constructor.Body = new MethodBody(constructor);
            var init = new IRBuilder(constructor.Body.CreateBlock());
            init.CreateCall(ctor, constructor.Body.Args[0]);
            init.CreateReturn();
            ctor = constructor;
        }

        var dict = ib.CreateNewObj(ctor);
        ib.CreateCallVirt("Add", dict, body.Args[1], ConstInt.CreateI(7));

        if (bypassEdge) {
            ib.SetBranch(body.Args[0], read, check);
        } else {
            ib.SetBranch(check);
        }

        var cb = new IRBuilder(check);
        cb.SetBranch(cb.CreateCallVirt("ContainsKey", dict, body.Args[1]), read, missing);
        var rb = new IRBuilder(read);
        rb.CreateReturn(rb.CreateCallVirt("get_Item", dict, body.Args[1]));
        missing.SetBranch(new ReturnInst(ConstInt.CreateI(-1)));
        Run(body, new SimplifyInsts(_modResolver));

        Assert.Equal(!bypassEdge && !subclass, body.Instructions().OfType<CallInst>().Any(c => c.Method.Name == "TryGetValue"));

        var run = Utils.Compile<Func<bool, string, int>>(body);

        Assert.Equal(7, run(true, "key"));
        Assert.Equal(7, run(false, "key"));
    }
}
