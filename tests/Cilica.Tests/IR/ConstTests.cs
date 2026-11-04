namespace Cilica.Tests.IR;

using Cilica.AsmIO;
using Cilica.IR;
using Cilica.IR.Utils;

[Collection("ModuleResolver")]
public class ConstTests
{
    readonly ModuleResolver _resolver;

    public ConstTests(ModuleResolverFixture fixture) => _resolver = fixture.Resolver;

    [Theory]
    [InlineData(BinaryOp.Shrl, -1, 1, 2147483647)]
    [InlineData(BinaryOp.Shl, 1, 32, 1)]
    [InlineData(BinaryOp.Shra, int.MinValue, 32, int.MinValue)]
    [InlineData(BinaryOp.Shrl, -1, -1, 1)]
    public void FoldShiftsAtStackWidth(BinaryOp op, int value, int shift, int expected)
    {
        var result = Assert.IsType<ConstInt>(ConstFolding.FoldBinary(op, ConstInt.CreateI(value), ConstInt.CreateI(shift)));
        Assert.Equal(expected, result.Value);
    }

    [Theory]
    [InlineData(BinaryOp.SDiv)]
    [InlineData(BinaryOp.SRem)]
    public void ExceptionalDivisionIsNotFolded(BinaryOp op)
    {
        Assert.Null(ConstFolding.FoldBinary(op, ConstInt.CreateI(int.MinValue), ConstInt.CreateI(-1)));
        Assert.Null(ConstFolding.FoldBinary(op, ConstInt.CreateL(long.MinValue), ConstInt.CreateL(-1)));
        Assert.Null(ConstFolding.FoldBinary(op, ConstInt.CreateL(1), ConstInt.CreateL(0)));
        Assert.Equal(2147483648L, Assert.IsType<ConstInt>(ConstFolding.FoldBinary(BinaryOp.SDiv,
            ConstInt.CreateL(int.MinValue), ConstInt.CreateL(-1))).Value);
    }

    [Fact]
    public void IntegerComparisonsUseStackBits()
    {
        var signed = ConstInt.CreateI(-1);
        var unsigned = ConstInt.Create(PrimType.UInt32, uint.MaxValue);
        Assert.True(Fold(CompareOp.Eq, signed, unsigned));
        Assert.True(Fold(CompareOp.Slt, unsigned, ConstInt.CreateI(0)));
        Assert.False(Fold(CompareOp.Ult, signed, ConstInt.CreateI(0)));
        var division = ConstFolding.FoldBinary(BinaryOp.UDiv, ConstInt.CreateI(4), ConstInt.CreateI(2))!;
        Assert.True(Fold(CompareOp.Eq, division, ConstInt.CreateI(2)));
    }

    [Fact]
    public void FloatingComparisonsFollowOrderedAndUnorderedPredicates()
    {
        double[] values = [double.NaN, double.NegativeInfinity, -1, -0.0, 0.0, 1, double.PositiveInfinity];
        foreach (double x in values) foreach (double y in values) {
            bool unordered = double.IsNaN(x) || double.IsNaN(y);
            var cases = new (CompareOp Op, bool Expected)[] {
                (CompareOp.FOeq, x == y), (CompareOp.FOne, !unordered && x != y),
                (CompareOp.FOlt, x < y), (CompareOp.FOle, x <= y),
                (CompareOp.FOgt, x > y), (CompareOp.FOge, x >= y),
                (CompareOp.FUeq, unordered || x == y), (CompareOp.FUne, x != y),
                (CompareOp.FUlt, unordered || x < y), (CompareOp.FUle, unordered || x <= y),
                (CompareOp.FUgt, unordered || x > y), (CompareOp.FUge, unordered || x >= y)
            };
            foreach (var (op, expected) in cases) Assert.Equal(expected, Fold(op, ConstFloat.CreateD(x), ConstFloat.CreateD(y)));
        }
    }

    [Fact]
    public void FloatingIdentityPreservesSignedZero()
    {
        var positive = ConstFloat.CreateD(0.0);
        var negative = ConstFloat.CreateD(-0.0);
        Assert.NotEqual(positive, negative);
        Assert.True(Fold(CompareOp.FOeq, positive, negative));
        Assert.Null(ConstFolding.FoldSelect(new FakeTrackedValue(1), positive, negative));
    }

    [Fact]
    public void ConversionsPreserveOverflowExtensionAndRounding()
    {
        Assert.Null(ConstFolding.FoldConvert(ConstInt.CreateI(-1), PrimType.Int32, true, true));
        Assert.Equal(uint.MaxValue, Assert.IsType<ConstInt>(ConstFolding.FoldConvert(ConstInt.CreateI(-1), PrimType.UInt64, false, false)).Value);
        Assert.Equal(-1, Assert.IsType<ConstInt>(ConstFolding.FoldConvert(ConstInt.Create(PrimType.UInt32, uint.MaxValue), PrimType.Int64, false, false)).Value);
        var narrowed = ConstFolding.FoldConvert(ConstFloat.CreateD(16777217), PrimType.Single, false, false)!;
        Assert.Equal(16777216, Assert.IsType<ConstFloat>(ConstFolding.FoldConvert(narrowed, PrimType.Double, false, false)).Value);
    }

    [Fact]
    public void NativeIntegersRequireExplicitConversion()
    {
        Assert.Throws<InvalidOperationException>(() => ConstInt.Create(PrimType.IntPtr, 123));
        Assert.Throws<InvalidOperationException>(() => ConstInt.Create(PrimType.UIntPtr, 123));
        var body = Utils.CreateDummyMethodBody(PrimType.IntPtr, PrimType.Int32.CreateByref());
        Assert.Null(ConstFolding.FoldBinary(BinaryOp.Sub, body.Args[0], body.Args[0]));
    }

    [Fact]
    public void FailedCastRetainsItsException()
    {
        var ctor = _resolver.Import(typeof(object)).FindMethod(".ctor");
        var obj = new NewObjInst(ctor, []);
        Assert.Null(ConstFolding.FoldIntrinsic(new CilIntrinsic.CastClass(PrimType.String, obj)));
        Assert.IsType<ConstNull>(ConstFolding.FoldIntrinsic(new CilIntrinsic.AsInstance(PrimType.String, obj)));
    }

    [Fact]
    public void FoldMathUsesTheResolvedOverload()
    {
        var math = _resolver.Import(typeof(Math));
        var min = math.FindMethod("Min", new MethodSig(PrimType.UInt32, [PrimType.UInt32, PrimType.UInt32]));
        Assert.Equal(1, Assert.IsType<ConstInt>(ConstFolding.FoldCall(min, [ConstInt.CreateI(-1), ConstInt.CreateI(1)])).Value);
        var round = math.FindMethod("Round", new MethodSig(PrimType.Double, [PrimType.Double, _resolver.Import(typeof(MidpointRounding))]));
        Assert.Equal(1, Assert.IsType<ConstFloat>(ConstFolding.FoldCall(round, [ConstFloat.CreateD(1.5), ConstInt.CreateI((int)MidpointRounding.ToZero)])).Value);
        foreach (var type in new[] { PrimType.SByte, PrimType.Int16, PrimType.Int32, PrimType.Int64 }) {
            var abs = math.FindMethod("Abs", new MethodSig(type, [type]));
            Assert.Null(ConstFolding.FoldCall(abs, [ConstInt.Create(type, 1L << (type.Kind.BitSize() - 1))]));
        }
        var digits = math.FindMethod("Round", new MethodSig(PrimType.Double, [PrimType.Double, PrimType.Int32]));
        Assert.Null(ConstFolding.FoldCall(digits, [ConstFloat.CreateD(1.5), ConstInt.CreateI(100)]));
    }

    private static bool Fold(CompareOp op, Value left, Value right)
        => Assert.IsType<ConstInt>(ConstFolding.FoldCompare(op, left, right)).Value != 0;

    [Fact]
    public void Test_CreateZero()
    {
        static void Check(Const c) => Assert.Equal(0, ((ConstInt)c).Value);

        Check(ConstInt.CreateZero(PrimType.Byte));
        Check(ConstInt.CreateZero(PrimType.UInt16));
        Check(ConstInt.CreateZero(PrimType.UInt32));
        Check(ConstInt.CreateZero(PrimType.UInt64));
    }

    [Fact]
    public void Test_ConstInt_Normalization()
    {
        static void Check(PrimType type, long min, long max, ulong minU)
        {
            Assert.Equal(min, ConstInt.Create(type, min).Value);
            Assert.Equal(max, ConstInt.Create(type, max).Value);

            Assert.Equal(minU, ConstInt.Create(type, min).UValue);
            Assert.Equal((ulong)max, ConstInt.Create(type, max).UValue);

            var ci = ConstInt.Create(type, max + 1);
            Assert.Equal(ci.IsSigned ? min : 0, ci.Value);
            Assert.Equal(minU, ci.UValue);

            ci = ConstInt.Create(type, min - 1);
            Assert.Equal(max, ci.Value);
            Assert.Equal((ulong)max, ci.UValue);
        }
        unchecked {
            Check(PrimType.Byte, Byte.MinValue, Byte.MaxValue, Byte.MinValue);
            Check(PrimType.SByte, SByte.MinValue, SByte.MaxValue, (byte)SByte.MinValue);
            Check(PrimType.Int16, Int16.MinValue, Int16.MaxValue, (ushort)Int16.MinValue);
            Check(PrimType.UInt16, UInt16.MinValue, UInt16.MaxValue, UInt16.MinValue);
            Check(PrimType.Int32, Int32.MinValue, Int32.MaxValue, (uint)Int32.MinValue);
            Check(PrimType.UInt32, UInt32.MinValue, UInt32.MaxValue, UInt32.MinValue);
            Check(PrimType.Int64, Int64.MinValue, Int64.MaxValue, (ulong)Int64.MinValue);
            Check(PrimType.UInt64, (long)UInt64.MinValue, (long)UInt64.MaxValue, UInt64.MinValue);
        }
    }
}
