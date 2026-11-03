namespace DistIL.CodeGen.Cil;

using DistIL.Analysis;
using DistIL.IR.Utils;

/// <summary> Owns instruction selection, legalization, edge preparation, scheduling, and local allocation for one CIL body. </summary>
internal sealed class CilPreparation
{
    public MethodBody Method { get; }

    public ForestAnalysis Forest { get; }

    public RegisterAllocator Registers { get; }

    public LayoutedCFG Layout { get; }

    public CilPreparation(MethodBody source)
    {
        IRVerifier.Verify(source);
        Method = IRCloner.CloneBody(source);
        Legalize(Method);
        PreparePhiEdges(Method);
        PrepareIndexedAccesses(Method);
        Forest = PrepareExpressions(Method);
        IRVerifier.Verify(Method);
        var interference = new InterferenceGraph(Method, new LivenessAnalysis(Method), Forest);
        long revision = Method.Revision;
        Registers = new RegisterAllocator(Method, interference);
        Ensure.That(Method.Revision == revision, "Register allocation must preserve the prepared IR");

        Layout = LayoutedCFG.Compute(Method);
    }

    private static void Legalize(MethodBody method)
    {
        foreach (var inst in method.Instructions().ToArray()) {
            if (inst is not CompareInst { Op: CompareOp.FOne or CompareOp.FUeq } compare) {
                continue;
            }

            var builder = new IRBuilder(compare, InsertionDir.Before).SetNextDebugLocation(compare.DebugLocation);
            var lower = builder.CreateCmp(CompareOp.FOlt, compare.Left, compare.Right);
            var greater = builder.CreateCmp(CompareOp.FOgt, compare.Left, compare.Right);
            var orderedNotEqual = builder.CreateOr(lower, greater);
            compare.ReplaceWith(compare.Op == CompareOp.FOne ? orderedNotEqual : builder.CreateEq(orderedNotEqual, ConstInt.CreateI(0)));
        }
    }

    private static void PreparePhiEdges(MethodBody method)
    {
        foreach (var block in method) {
            if (!block.HasPhis) {
                continue;
            }

            foreach (var predecessor in block.Preds.AsEnumerable().ToArray()) {
                predecessor.SplitCriticalEdge(block);
            }
        }

        var regions = new ProtectedRegionAnalysis(method);

        foreach (var block in method) {
            var phis = block.Phis().ToArray();

            if (phis.Length == 0) {
                continue;
            }

            var values = new Dictionary<BasicBlock, Value[]>();

            for (int i = 0; i < phis.Length; i++) {
                foreach (var (predecessor, value) in phis[i]) {
                    (values.GetOrAddRef(predecessor) ??= new Value[phis.Length])[i] = value;
                }
            }

            var representatives = new Dictionary<PhiEdge, BasicBlock>(PhiEdgeComparer.Instance);

            foreach (var (predecessor, inputs) in values) {
                if (predecessor == method.EntryBlock || predecessor.First is not BranchInst { IsJump: true }) {
                    continue;
                }

                var key = new PhiEdge(regions.GetBlockRegion(predecessor), inputs);

                if (representatives.TryGetValue(key, out var representative)) {
                    block.RedirectPhis(predecessor, null, removeTrivialPhis: false);
                    predecessor.ReplaceUses(representative);
                    predecessor.Remove();
                } else {
                    representatives.Add(key, predecessor);
                }
            }
        }
    }

    private static void PrepareIndexedAccesses(MethodBody method)
    {
        if (!method.Instructions().Any(inst => inst is PtrOffsetInst { Index: PhiInst { ResultType.Kind: TypeKind.Int32 } })) {
            return;
        }

        var dominators = new DominatorTree(method);
        var loops = new LoopAnalysis(method, dominators);

        foreach (var shape in loops.GetShapedLoops()) {
            if (shape is not CountingLoopInfo {
                IsCanonical: true,
                Counter.ResultType.Kind: TypeKind.Int32,
                End.ResultType.Kind: TypeKind.Int32,
                ExitCondition.Op: CompareOp.Slt
            } loop) {
                continue;
            }

            // A signed int32 bound stops unit-step induction before its counter can wrap.
            foreach (var offset in loop.Counter.Users().AsEnumerable().OfType<PtrOffsetInst>().ToArray()) {
                if (offset.Index != loop.Counter || !loop.Contains(offset.Block)) {
                    continue;
                }

                if (TrySelectArrayAddress(loop, offset, dominators)) {
                    continue;
                }

                var builder = new IRBuilder(offset, InsertionDir.Before).SetNextDebugLocation(offset.DebugLocation);
                offset.Index = builder.CreateConvert(loop.Counter, PrimType.UIntPtr);
            }
        }
    }

    private static bool TrySelectArrayAddress(CountingLoopInfo loop, PtrOffsetInst offset, DominatorTree dominators)
    {
        var array = loop.GetSourceArray();

        if (array == null || offset.BasePtr is not CallInst { Method.Name: "GetArrayDataReference", Args: [var source] } data ||
            source != array || !data.Method.DeclaringType.IsCorelibType(typeof(System.Runtime.InteropServices.MemoryMarshal)) ||
            offset.ElemType != array.ResultType.ElemType || offset.Stride != offset.ElemType.Kind.Size() ||
            !dominators.Dominates(((BranchInst)loop.Header.Last).Then, offset.Block) || offset.NumUses == 0) {
            return false;
        }

        bool readOnly = true;

        foreach (var use in offset.Uses()) {
            if (use.OperIndex != 0 || use.Parent is not MemoryInst { Flags: 0 } access || access.ElemType != offset.ElemType) {
                return false;
            }

            readOnly &= access is LoadInst;
        }

        var address = new ArrayAddrInst(array, offset.Index, inBounds: true, readOnly: readOnly) {
            DebugLocation = offset.DebugLocation
        };

        if (address.RequiresVarianceCheck) {
            address.Remove();
            return false;
        }

        offset.ReplaceWith(address, insertIfInst: true);
        return true;
    }

    private static ForestAnalysis PrepareExpressions(MethodBody method)
    {
        var forest = new ForestAnalysis(method);
        long revision = method.Revision;

        foreach (var block in method) {
            if (block.NumSuccs != 1) {
                continue;
            }

            foreach (var phi in block.Succs.First().Phis()) {
                if (phi.GetValue(block) is Instruction { NumUses: 1, HasSideEffects: false, MayReadFromMemory: false } definition &&
                    RootObservableLeaves(definition, forest) != definition) {
                    definition.MoveBefore(definition.Block.Last);
                }
            }
        }

        if (revision != method.Revision) {
            forest = new ForestAnalysis(method);
        }

        foreach (var select in method.Instructions().OfType<SelectInst>()) {
            RootObservableLeaves(select.IfTrue, forest);
            RootObservableLeaves(select.IfFalse, forest);
        }

        return forest;
    }

    private static Instruction? RootObservableLeaves(Value value, ForestAnalysis forest)
    {
        var root = FindObservableLeaf(value);

        if (root != null) {
            forest.SetLeaf(root, markAsLeaf: false);
        }

        return root;

        Instruction? FindObservableLeaf(Value current)
        {
            if (current is not Instruction inst) {
                return null;
            }

            if (inst is PhiInst || (inst.HasSideEffects || inst.MayReadFromMemory) && forest.IsLeaf(inst)) {
                return inst;
            }

            Instruction? first = null;
            int count = 0;

            foreach (var operand in inst.Operands) {
                if (FindObservableLeaf(operand) is { } leaf) {
                    first = leaf;
                    count++;
                }
            }

            return count == 0 ? null : count == 1 ? first : inst;
        }
    }

    readonly record struct PhiEdge(ProtectedRegion Region, Value[] Inputs);

    sealed class PhiEdgeComparer : IEqualityComparer<PhiEdge>
    {
        public static readonly PhiEdgeComparer Instance = new();

        public bool Equals(PhiEdge a, PhiEdge b) => a.Region == b.Region && a.Inputs.AsSpan().SequenceEqual(b.Inputs);

        public int GetHashCode(PhiEdge edge)
        {
            var hash = new HashCode();
            hash.Add(edge.Region);

            foreach (var value in edge.Inputs) {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }
}
