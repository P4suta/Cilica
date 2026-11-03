namespace DistIL.Tests.Passes;

using System.Reflection;
using System.Runtime.CompilerServices;
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
    [InlineData(false)]
    [InlineData(true)]
    public void NullChecksCoalesceWithFieldDereferences(bool duplicate)
    {
        var type = _modResolver.Import(typeof(StrongBox<int>));
        var field = type.FindField("Value");
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, type, PrimType.Int32);
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateNullCheck(body.Args[0]);
        var increment = builder.CreateAdd(body.Args[1], ConstInt.CreateI(1));

        if (duplicate) {
            builder.CreateNullCheck(body.Args[0]);
        }

        builder.CreateReturn(builder.CreateAdd(builder.CreateFieldLoad(field, body.Args[0]), increment));
        Run(body, new SimplifyInsts(_modResolver), new DeadCodeElim());

        Assert.Empty(body.Instructions().OfType<CilIntrinsic.NullCheck>());
        var run = Utils.Compile<Func<StrongBox<int>, int, int>>(body);

        Assert.Equal(10, run(new(7), 2));
        Assert.Throws<NullReferenceException>(() => run(null!, 2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullChecksStayBeforeObservableOperations(bool throwingOperation)
    {
        var type = _modResolver.Import(typeof(StrongBox<int>));
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, type, PrimType.Int32);
        var effect = body.Definition.DeclaringType.CreateField("Effect", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateNullCheck(body.Args[0]);

        if (throwingOperation) {
            builder.Emit(new BinaryInst(BinaryOp.SDiv, ConstInt.CreateI(10), body.Args[1]));
        } else {
            builder.CreateFieldStore(effect, null, ConstInt.CreateI(1));
        }

        builder.CreateReturn(builder.CreateFieldLoad(type.FindField("Value"), body.Args[0]));
        Run(body, new SimplifyInsts(_modResolver), new DeadCodeElim());

        Assert.Single(body.Instructions().OfType<CilIntrinsic.NullCheck>());
        var run = Utils.Compile<Func<StrongBox<int>, int, int>>(body);

        Assert.Throws<NullReferenceException>(() => run(null!, 0));
        Assert.Equal(0, run.Method.DeclaringType!.GetField("Effect")!.GetValue(null));

        if (throwingOperation) {
            Assert.Throws<DivideByZeroException>(() => run(new(7), 0));
        } else {
            Assert.Equal(7, run(new(7), 0));
            Assert.Equal(1, run.Method.DeclaringType!.GetField("Effect")!.GetValue(null));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullChecksCoalesceWithArrayAccesses(bool length)
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32.CreateArray(), PrimType.Int32);
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateNullCheck(body.Args[0]);
        builder.CreateReturn(length ? builder.CreateConvert(builder.CreateArrayLen(body.Args[0]), PrimType.Int32)
            : builder.CreateArrayLoad(body.Args[0], body.Args[1]));
        Run(body, new SimplifyInsts(_modResolver), new DeadCodeElim());

        Assert.Empty(body.Instructions().OfType<CilIntrinsic.NullCheck>());
        var run = Utils.Compile<Func<int[], int, int>>(body);

        Assert.Equal(length ? 1 : 7, run([7], 0));
        Assert.Throws<NullReferenceException>(() => run(null!, -1));

        if (!length) {
            Assert.Throws<IndexOutOfRangeException>(() => run([7], -1));
        }
    }

    [Fact]
    public void NullChecksCoalesceWithVirtualCalls()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.String);
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateNullCheck(body.Args[0]);
        builder.CreateReturn(builder.CreateCallVirt("get_Length", body.Args[0]));
        Run(body, new SimplifyInsts(_modResolver), new DeadCodeElim());

        Assert.Empty(body.Instructions().OfType<CilIntrinsic.NullCheck>());
        var run = Utils.Compile<Func<string, int>>(body);

        Assert.Equal(3, run("abc"));
        Assert.Throws<NullReferenceException>(() => run(null!));
    }

    [Fact]
    public void NullChecksRetainTheCheckedReceiver()
    {
        var type = _modResolver.Import(typeof(StrongBox<int>));
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, type, type);
        var builder = new IRBuilder(body.CreateBlock());
        builder.CreateNullCheck(body.Args[0]);
        builder.CreateReturn(builder.CreateFieldLoad(type.FindField("Value"), body.Args[1]));
        Run(body, new SimplifyInsts(_modResolver), new DeadCodeElim());

        Assert.Single(body.Instructions().OfType<CilIntrinsic.NullCheck>());
        var run = Utils.Compile<Func<StrongBox<int>, StrongBox<int>, int>>(body);

        Assert.Throws<NullReferenceException>(() => run(null!, new(7)));
        Assert.Equal(7, run(new(1), new(7)));
    }

    [Fact]
    public void NullChecksDoNotMoveAcrossConditionalDereferences()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, PrimType.Int32.CreateArray(), PrimType.Bool);
        var entry = body.CreateBlock();
        var read = body.CreateBlock();
        var skip = body.CreateBlock();
        var builder = new IRBuilder(entry);
        builder.CreateNullCheck(body.Args[0]);
        builder.SetBranch(body.Args[1], read, skip);
        var reader = new IRBuilder(read);
        reader.CreateReturn(reader.CreateConvert(reader.CreateArrayLen(body.Args[0]), PrimType.Int32));
        skip.SetBranch(new ReturnInst(ConstInt.CreateI(0)));
        Run(body, new SimplifyInsts(_modResolver), new DeadCodeElim());

        Assert.Single(body.Instructions().OfType<CilIntrinsic.NullCheck>());
        var run = Utils.Compile<Func<int[], bool, int>>(body);

        Assert.Throws<NullReferenceException>(() => run(null!, false));
        Assert.Throws<NullReferenceException>(() => run(null!, true));
        Assert.Equal(0, run([1, 2], false));
        Assert.Equal(2, run([1, 2], true));
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
