namespace Cilica.Passes;

using Cilica.Analysis;
using Cilica.IR.Utils;

using AssertFragment = (CompareOp Op, Value Right, BasicBlock ActiveBlock);

/// <summary> A simple dominator-based "assertion propagation" pass. </summary>
public class AssertionProp : IMethodPass
{
    public MethodPassResult Run(MethodTransformContext ctx) => new Worker(ctx).Run();

    sealed class Worker(MethodTransformContext ctx)
    {
        Dictionary<Value, List<AssertFragment>> _activeAsserts = new();
        DominatorTree _domTree = null!;
        int _numChanges = 0;

        public MethodPassResult Run()
        {
            long revision = ctx.Method.Revision;
            _domTree = ctx.GetAnalysis<DominatorTree>();
            _numChanges = 0;

            _domTree.Traverse(preVisit: ProcessBlock);


            return ctx.Method.Revision != revision ? MethodInvalidations.DataFlow : MethodInvalidations.None;
        }

        private void ProcessBlock(BasicBlock block)
        {
            // Add implications from predecessor edge
            if (block.NumPreds == 1 && block.Preds.First() is { Last: BranchInst { Cond: CompareInst predCmp } predBr }) {
                var op = block == predBr.Then ? predCmp.Op : predCmp.Op.GetNegated();
                Imply(op, predCmp.Left, predCmp.Right);

                // Replace existing uses of the predecessor branch condition's
                // that are dominated by this block, to catch cases like:
                //   if (cond) { if (cond) { ... } }
                foreach (var use in predCmp.Uses()) {
                    if (_domTree.Dominates(block, use.Parent.Block)) {
                        use.Operand = ConstInt.CreateI(block == predBr.Then ? 1 : 0);
                        _numChanges++;
                    }
                }
            }

            // Add null asserts for phis
            foreach (var phi in block.Phis()) {
                if (!phi.ResultType.IsPointerOrObject()) continue;

                bool? commonResult = null;

                foreach (var (pred, arg) in phi) {
                    bool? result = null;

                    // Try to derive from pred branch, `if (obj != null)`
                    if (pred.Last is BranchInst { Cond: CompareInst { Op: CompareOp.Ne or CompareOp.Eq, Right: ConstNull } cmp } br && cmp.Left == arg) {
                        result = (br.Then == block) == (cmp.Op == CompareOp.Ne);
                    } else {
                        result = Evaluate(CompareOp.Ne, arg, ConstNull.Create(), pred);
                    }

                    // Abort if we have no info about this argument, or if it conflicts
                    // with a previous one.
                    if (result == null || ((commonResult ??= result) != result)) {
                        commonResult = null;
                        break;
                    }
                }

                if (commonResult != null) {
                    Imply(commonResult.Value ? CompareOp.Ne : CompareOp.Eq, phi, ConstNull.Create());
                }
            }

            // Looking for new implications and fold existing conditions
            foreach (var inst in block.NonPhis()) {
                switch (inst) {
                    // Access to an object implies that it must be non-null afterwards.
                    // FIXME: check if try..catch regions will mess with this
                    case MemoryInst:
                    case CallInst { IsVirtual: true }:
                    case FieldAddrInst { IsInstance: true }:
                    case ArrayAddrInst: {
                        if (inst.Operands[0] is not TrackedValue obj) break;

                        bool? isNonNull = Evaluate(CompareOp.Ne, obj, ConstNull.Create(), block);

                        if (isNonNull is null) {
                            Imply(CompareOp.Ne, obj, ConstNull.Create());
                        } else if (isNonNull is true) {
                            SetInBounds(inst);
                        } // else, inst is unreachable

                        break;
                    }
                    case CompareInst instC: {
                        if (Evaluate(instC.Op, instC.Left, instC.Right, block) is bool cond) {
                            instC.ReplaceUses(ConstInt.CreateI(cond ? 1 : 0));
                            _numChanges++;
                        }
                        break;
                    }
                }
            }

            // Adds an assertion that implies a true condition.
            void Imply(CompareOp op, Value left, Value right)
            {
                Assert(op, left, right, block);
                Assert(op.GetSwapped(), right, left, block);
            }
        }

        private static void SetInBounds(Instruction inst)
        {
            switch (inst) {
                case FieldAddrInst instC: instC.InBounds = true; break;
                case CallInst instC: instC.InBounds = true; break;
            }
        }

        private void Assert(CompareOp op, Value left, Value right, BasicBlock activeBlock)
        {
            // Don't bother tracking asserts related to consts
            if (left is not TrackedValue) return;

            var list = _activeAsserts.GetOrAddRef(left) ??= new();

            // Limit list size to avoid exponential runtime
            if (list.Count >= 16) {
                list.RemoveAt(0);
            }
            list.Add((op, right, activeBlock));
        }

        private bool? Evaluate(CompareOp op, Value left, Value right, BasicBlock block)
        {
            bool? result = null;

            if (result == null && _activeAsserts.TryGetValue(left, out var asserts)) {
                result = EvaluateRelatedAsserts(asserts, (op, right, block));
            }
            if (result == null && _activeAsserts.TryGetValue(right, out asserts)) {
                result = EvaluateRelatedAsserts(asserts, (op.GetSwapped(), left, block));
            }
            return result;
        }

        private bool? EvaluateRelatedAsserts(List<AssertFragment>? relatedAsserts, AssertFragment cond)
        {
            if (relatedAsserts == null) return null;

            // Consider most recent asserts first
            for (int i = relatedAsserts.Count - 1; i >= 0; i--) {
                var assert = relatedAsserts[i];

                if (assert.Right.Equals(cond.Right) && _domTree.Dominates(assert.ActiveBlock, cond.ActiveBlock)) {
                    if (IsImpliedPredicate(assert.Op, cond.Op)) {
                        return true;
                    }
                    if (IsImpliedPredicate(assert.Op, cond.Op.GetNegated())) {
                        return false;
                    }
                }

                // TODO: evaluate relations, eg. x < 10  implies  x < 20
            }
            return null;
        }

        // Checks if predicate `a` implies `b`
        private static bool IsImpliedPredicate(CompareOp a, CompareOp b)
        {
            // x < y  implies  x <= y
            if (a.IsStrict() && !b.IsStrict()) {
                b = b.GetStrict();
            }
            return a == b;
        }

    }
}
