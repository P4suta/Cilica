namespace Cilica.Analysis;

/// <summary> The contents of a private, straight-line array at one consuming call. </summary>
internal sealed record KnownArray(Value[] Values, Instruction? Allocation, Instruction[] Stores, Instruction[] Addresses)
{
    public static bool TryRead(Value value, CallInst consumer, int count, out KnownArray result,
        TypeDesc? elementType = null, bool allowNull = true)
    {
        result = null!;
        elementType ??= PrimType.Object;
        if (count == 0 && (allowNull && value is ConstNull || value is CallInst { Method.Name: "Empty", NumArgs: 0 } empty &&
            empty.Method.DeclaringType.IsCorelibType(typeof(Array)) && empty.Method.GenericParams is [var element] && element == elementType)) {
            result = new([], value as Instruction, [], []);
            return true;
        }
        if (value is not CilIntrinsic.NewArray { StaticArgs.Length: 0 } allocation || allocation.ElemType != elementType ||
            allocation.Args[0] is not ConstInt length || length.Value != count || allocation.Block != consumer.Block) return false;

        var addresses = new HashSet<Instruction>();
        var stores = new HashSet<Instruction>();
        int consumerUses = 0;
        foreach (var (user, operand) in allocation.Uses()) {
            if (user == consumer) { consumerUses++; continue; }
            if (user is not ArrayAddrInst address || operand != 0 || address.ElemType != elementType ||
                address.Index is not ConstInt index || index.Value < 0 || index.Value >= count) return false;
            addresses.Add(address);
            foreach (var (use, position) in address.Uses()) {
                if (use is not StoreInst store || position != 0 || store.ElemType != elementType || store.Flags != PointerFlags.None) return false;
                stores.Add(store);
            }
        }
        if (consumerUses != 1) return false;

        var values = Enumerable.Repeat<Value>(ConstNull.Create(), count).ToArray();
        var remaining = new HashSet<Instruction>(stores.Concat(addresses));
        for (var instruction = allocation.Next!; instruction != consumer; instruction = instruction.Next!) {
            if (!remaining.Remove(instruction)) continue;
            if (instruction is StoreInst store) values[(int)((ConstInt)((ArrayAddrInst)store.Address).Index).Value] = store.Value;
        }
        if (remaining.Count != 0) return false;
        result = new(values, allocation, stores.ToArray(), addresses.ToArray());
        return true;
    }

    public void Remove()
    {
        foreach (var store in Stores) store.Remove();
        foreach (var address in Addresses) address.Remove();
        if (Allocation?.NumUses == 0) Allocation.Remove();
    }
}
