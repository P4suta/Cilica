namespace Cilica.Analysis;

/// <summary> The contents of a private, straight-line array at one consuming call. </summary>
internal sealed record KnownArray(Value[] Values, Instruction? Allocation, Instruction[] Stores, Instruction[] Addresses)
{
    public static bool TryRead(Value value, CallInst consumer, int count, out KnownArray result,
        TypeDesc? elementType = null, bool allowNull = true)
    {
        result = null!;
        elementType ??= PrimType.Object;
        if (count == 0 && IsEmpty(value, elementType, allowNull)) {
            result = new([], value as Instruction, [], []);
            return true;
        }
        if (value is not CilIntrinsic.NewArray { StaticArgs.Length: 0 } allocation) return false;
        if (allocation.ElemType != elementType || allocation.Block != consumer.Block) return false;
        if (allocation.Args[0] is not ConstInt length || length.Value != count) return false;

        // Every use must either initialize a constant index or pass the array to
        // this consumer. An escaping array or element address is not a known value.
        var addresses = new HashSet<Instruction>();
        var stores = new HashSet<Instruction>();
        int consumerUses = 0;
        foreach (var (user, operand) in allocation.Uses()) {
            if (user == consumer) {
                consumerUses++;
                continue;
            }
            if (user is not ArrayAddrInst address || operand != 0 || address.ElemType != elementType) return false;
            if (address.Index is not ConstInt index || index.Value < 0 || index.Value >= count) return false;
            addresses.Add(address);
            foreach (var (use, position) in address.Uses()) {
                if (use is not StoreInst store || position != 0 || store.ElemType != elementType || store.Flags != PointerFlags.None) return false;
                stores.Add(store);
            }
        }
        if (consumerUses != 1) return false;

        // Read stores in program order so repeated writes select their last value.
        // All initialization must lie between allocation and consumption.
        var values = Enumerable.Repeat<Value>(ConstNull.Create(), count).ToArray();
        var remaining = new HashSet<Instruction>(stores.Concat(addresses));
        for (var instruction = allocation.Next!; instruction != consumer; instruction = instruction.Next!) {
            if (!remaining.Remove(instruction)) continue;
            if (instruction is StoreInst store) {
                var address = (ArrayAddrInst)store.Address;
                int index = (int)((ConstInt)address.Index).Value;
                values[index] = store.Value;
            }
        }
        if (remaining.Count != 0) return false;
        result = new(values, allocation, stores.ToArray(), addresses.ToArray());
        return true;
    }

    private static bool IsEmpty(Value value, TypeDesc elementType, bool allowNull)
    {
        if (value is ConstNull) return allowNull;
        return value is CallInst { Method.Name: "Empty", NumArgs: 0 } call &&
            call.Method.DeclaringType.IsCorelibType(typeof(Array)) &&
            call.Method.GenericParams is [var element] && element == elementType;
    }

    public void Remove()
    {
        foreach (var store in Stores) store.Remove();
        foreach (var address in Addresses) address.Remove();
        if (Allocation?.NumUses == 0) Allocation.Remove();
    }
}
