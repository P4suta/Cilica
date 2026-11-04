namespace Cilica.Tests.Passes;

using System.Reflection;
using Cilica.Analysis;
using Cilica.AsmIO;
using Cilica.CodeGen.Cil;
using Cilica.IR;
using Cilica.IR.Utils;
using Cilica.Passes;
using Cilica.Util;
using MethodBody = Cilica.IR.MethodBody;

[Collection("ModuleResolver")]
public class ValueNumberingTests(ModuleResolverFixture fixture) : PassTestBase(fixture)
{
    [Fact]
    public void Test_ValueNumbering() => CheckIR("ValueNumbering.ethil", new ValueNumbering());

    [Fact]
    public void MemoryBarriersSurviveDceAndClobberAvailableLoads()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var field = body.Definition.DeclaringType.CreateField("Value", PrimType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        var ib = new IRBuilder(body.CreateBlock());
        var address = ib.CreateFieldAddr(field);
        var first = ib.CreateLoad(address);
        var acquire = ib.CreateLoad(address, flags: PointerFlags.Volatile);
        var second = ib.CreateLoad(address);
        ib.CreateReturn(ib.CreateAdd(first, second));
        Run(body, new ValueNumbering(), new DeadCodeElim());

        Assert.NotNull(acquire.Block);
        Assert.NotNull(second.Block);
        Assert.True(acquire.IsMemoryBarrier);
        Assert.False(acquire.MayWriteToMemory);
    }

    [Fact]
    public void ValueNumberingPreservesVirtualCalls()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var type = body.Definition.DeclaringType;
        var method = type.CreateMethod("Read", PrimType.Int32, [new ParamDef(type, "this")], MethodAttributes.Public | MethodAttributes.Virtual);
        method.Body = new MethodBody(method);
        method.Body.CreateBlock().SetBranch(new ReturnInst(ConstInt.CreateI(0)));
        var ib = new IRBuilder(body.CreateBlock());
        var first = ib.CreateCallVirt(method, ConstNull.Create());
        var second = ib.CreateCallVirt(method, ConstNull.Create());
        ib.CreateReturn(ib.CreateSub(second, first));
        Run(body, new ValueNumbering());

        Assert.NotNull(second.Block);
        Assert.Equal(2, body.Instructions().OfType<CallInst>().Count());
    }

    [Fact]
    public void ValueNumberingClobbersFieldsWhenTheirStructElementIsStored()
    {
        var cell = _modResolver.Import(typeof(ValueTuple<int>));
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32, cell.CreateArray(), cell);
        var ib = new IRBuilder(body.CreateBlock());
        var element = ib.CreateArrayAddr(body.Args[0], ConstInt.CreateI(0));
        var field = ib.CreateFieldAddr(cell.FindField("Item1"), element);
        var first = ib.CreateLoad(field);
        ib.CreateStore(element, body.Args[1]);
        var second = ib.CreateLoad(field);
        ib.CreateReturn(ib.CreateAdd(first, second));
        Run(body, new ValueNumbering());

        Assert.Equal(7, Utils.Compile<Func<ValueTuple<int>[], ValueTuple<int>, int>>(body)([new(5)], new(2)));
    }

    [Fact]
    public void ValueNumberingClobbersCovariantInterfaceArrays()
    {
        var comparable = _modResolver.Import(typeof(IComparable));
        var convertible = _modResolver.Import(typeof(IConvertible));
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Bool, comparable.CreateArray(), convertible.CreateArray(), PrimType.String);
        var ib = new IRBuilder(body.CreateBlock());
        var first = ib.CreateArrayLoad(body.Args[0], ConstInt.CreateI(0));
        ib.CreateArrayStore(body.Args[1], ConstInt.CreateI(0), body.Args[2]);
        var second = ib.CreateArrayLoad(body.Args[0], ConstInt.CreateI(0));
        ib.CreateReturn(ib.CreateEq(first, second));
        Run(body, new ValueNumbering());
        string[] array = ["first"];

        Assert.False(Utils.Compile<Func<IComparable[], IConvertible[], string, bool>>(body)(array, array, "second"));
    }

    [Fact]
    public void ValueNumberingPreservesAmbientCultureAndStringAllocation()
    {
        var culture = _modResolver.Import(typeof(System.Globalization.CultureInfo));
        var textType = _modResolver.Import(typeof(string));
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Bool, culture);
        var builder = new IRBuilder(body.CreateBlock());
        var upper = textType.FindMethod("ToUpper", new MethodSig(PrimType.String, []));
        var setter = culture.FindMethod("set_CurrentCulture", new MethodSig(PrimType.Void, [culture]));
        var equal = textType.FindMethod("op_Equality", new MethodSig(PrimType.Bool, [PrimType.String, PrimType.String]));
        var first = builder.CreateCallVirt(upper, ConstString.Create("i"));
        builder.CreateCall(setter, body.Args[0]);
        var second = builder.CreateCallVirt(upper, first.Args[0]);
        builder.CreateReturn(builder.CreateCall(equal, first, second));
        Run(body, new ValueNumbering());

        Assert.NotNull(second.Block);

        if (!(AppContext.TryGetSwitch("System.Globalization.Invariant", out bool invariant) && invariant)) {
            var saved = System.Globalization.CultureInfo.CurrentCulture;

            try {
                System.Globalization.CultureInfo.CurrentCulture = new("en-US");

                Assert.False(Utils.Compile<Func<System.Globalization.CultureInfo, bool>>(body)(new("tr-TR")));
            } finally {
                System.Globalization.CultureInfo.CurrentCulture = saved;
            }
        }

        var effects = new Compilation(body.Definition.Module, new VoidLogger(), new()).GetAnalysis<GlobalFunctionEffects>();

        Assert.True(effects.GetEffects(textType.FindMethod("ToUpperInvariant", new MethodSig(PrimType.String, []))).MayAllocate);
    }

    [Fact]
    public void ValueNumberingChecksWritesAfterTheUseOnBackedges()
    {
        var body = Utils.CreateTestMethod(_modResolver, PrimType.Int32);
        var field = body.Definition.DeclaringType.CreateField("Value", PrimType.Int32, FieldAttributes.Static | FieldAttributes.Public);
        var entry = body.CreateBlock();
        var header = body.CreateBlock();
        var items = body.CreateBlock();
        var exit = body.CreateBlock();
        var start = new IRBuilder(entry);
        var address = start.CreateFieldAddr(field);
        start.CreateStore(address, ConstInt.CreateI(0));
        var first = start.CreateLoad(address);
        start.SetBranch(header);
        var index = header.InsertPhi(new PhiInst(PrimType.Int32, (entry, ConstInt.CreateI(0))));
        var result = header.InsertPhi(new PhiInst(PrimType.Int32, (entry, ConstInt.CreateI(0))));
        var head = new IRBuilder(header, InsertionDir.After);
        head.SetBranch(head.CreateSlt(index, ConstInt.CreateI(2)), items, exit);
        var builder = new IRBuilder(items);
        var second = builder.CreateLoad(address);
        var next = builder.CreateAdd(index, ConstInt.CreateI(1));
        builder.CreateStore(address, next);
        builder.SetBranch(header);
        index.AddArg(items, next);
        result.AddArg(items, second);
        var finish = new IRBuilder(exit);
        finish.CreateReturn(finish.CreateAdd(first, result));
        Run(body, new ValueNumbering());

        Assert.NotNull(second.Block);
        Assert.Equal(1, Utils.Compile<Func<int>>(body)());
    }
}
