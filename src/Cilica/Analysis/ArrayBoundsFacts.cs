namespace Cilica.Analysis;

/// <summary> Fixed array lengths established by dominating equality guards. </summary>
internal static class ArrayBoundsFacts
{
    public static bool IsLengthBound(Value bound, Value array, BasicBlock block, DominatorTree dominators)
    {
        if (LengthSource(bound) == array) return true;

        for (var current = block; ; ) {
            var parent = dominators.IDom(current);
            if (parent == current) return false;
            // A merge can be reached without taking this branch's edge. Only
            // learn a fact from a successor with that edge as its sole entry.
            if (current.NumPreds == 1 && current.Preds.First() == parent &&
                parent.Last is BranchInst { Cond: CompareInst compare } branch &&
                (branch.Then == current || branch.Else == current)) {
                var op = branch.Then == current ? compare.Op : compare.Op.GetNegated();
                if (op == CompareOp.Eq &&
                    (SameBound(bound, compare.Left) && LengthSource(compare.Right) == array ||
                     SameBound(bound, compare.Right) && LengthSource(compare.Left) == array)) return true;
            }
            current = parent;
        }
    }

    private static bool SameBound(Value left, Value right)
        => left.Equals(right) || LengthSource(left) is { } source && source == LengthSource(right);

    private static Value? LengthSource(Value value)
    {
        if (value is ConvertInst { CheckOverflow: false, ResultType.Kind: TypeKind.Int32 } conversion) value = conversion.Value;
        return value is CilIntrinsic.ArrayLen length ? length.Args[0] : null;
    }
}
