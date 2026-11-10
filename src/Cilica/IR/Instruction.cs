namespace Cilica.IR;

public abstract class Instruction : TrackedValue
{
    public BasicBlock Block { get; internal set; } = null!;

    /// <summary> The previous instruction in the block. </summary>
    public Instruction? Prev => _prev;

    /// <summary> The next instruction in the block. </summary>
    public Instruction? Next => _next;

    internal Instruction? _prev, _next;

    internal Value[] _operands;
    internal UseDef[] _useDefs;
    private int _numOperands;
    internal int NumOperands => _numOperands;
    public ReadOnlySpan<Value> Operands => new ReadOnlySpan<Value>(_operands, 0, _numOperands);

    internal void NotifyChanged() => Block?.Method?.NotifyChanged(IsBranch || this is GuardInst);

    protected void SetProperty<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) {
            return;
        }

        field = value;
        NotifyChanged();
    }

    /// <summary> Location of source code that maps into this instruction. May be null to indicate unknown or compiler-generated code. </summary>
    public DebugSourceLocation? DebugLocation { get; set; }

    public abstract string InstName { get; }

    /// <summary> Whether this instructions modifies global state, affects control flow, or throws exceptions. </summary>
    public virtual bool HasSideEffects => MayThrow || MayWriteToMemory || IsMemoryBarrier || IsBranch;
    /// <summary> Whether this instruction constrains the ordering of visible memory accesses. </summary>
    public virtual bool IsMemoryBarrier => false;
    public virtual bool MayThrow => false;
    /// <summary> Whether this instruction is a branch, return, or affects control flow. </summary>
    public virtual bool IsBranch => false;
    /// <summary> Whether this instruction can be safely removed if it has no uses. </summary>
    public virtual bool SafeToRemove => !HasSideEffects;
    /// <summary> Whether this instruction may write to any memory location. </summary>
    public virtual bool MayWriteToMemory => false;
    /// <summary> Whether this instruction may read from a visible memory location (variable, array, field, pointer). </summary>
    public virtual bool MayReadFromMemory => false; // TODO: should this incur HasSideEffects?

    protected Instruction()
    {
        _operands = [];
        _useDefs = [];
    }
    protected Instruction(params Value[] opers)
    {
        _operands = opers;
        _useDefs = new UseDef[opers.Length];
        _numOperands = opers.Length;

        for (int i = 0; i < opers.Length; i++) {
            _operands[i].AddUse(this, i);
        }
    }

    /// <summary> Inserts this instruction before <paramref name="inst"/>. </summary>
    public void InsertBefore(Instruction inst) => inst.Block.InsertBefore(inst, this);

    /// <summary> Inserts this instruction after <paramref name="inst"/>. </summary>
    public void InsertAfter(Instruction inst) => inst.Block.InsertAfter(inst, this);

    /// <summary> Moves this instruction before <paramref name="inst"/>. </summary>
    public void MoveBefore(Instruction inst) => Block.MoveRange(inst.Block, inst.Prev, this, this);

    /// <summary> 
    /// Removes this instruction from the parent block and replaces its uses with the specified value. <br/>
    /// If <paramref name="newValue"/> is an instruction with no parent block and <c>insertIfInst == true</c>, it will be added 
    /// in the same place as this instruction.
    /// </summary>
    /// <remarks> 
    /// Once this method returns, this instruction should be considered invalid and must not be added in a block again.
    /// The <see cref="Operands"/> array is left unmodified, but uses are removed.
    /// </remarks>
    public void ReplaceWith(Value newValue, bool insertIfInst = false)
    {
        Debug.Assert(newValue != this);
        
        if (insertIfInst && newValue is Instruction newInst && newInst.Block == null) {
            Block.InsertAfter(this, newInst);
        }
        ReplaceUses(newValue);
        Remove();
    }

    /// <summary> Removes this instruction from the parent basic block. </summary>
    /// <remarks> 
    /// Once this method returns, this instruction should be considered invalid and must not be added in a block again.
    /// The <see cref="Operands"/> array is left unmodified, but uses are removed.
    /// </remarks>
    public void Remove()
    {
        // Allow Remove() calls on unattached instructions to allow for proper disposal of value uses.
        if (Block != null) {
            NotifyChanged();
            IIntrusiveList<BasicBlock, Instruction>.RemoveRange<BasicBlock.InstLinkAccessor>(Block, this, this);
            Block = null!;
        }

        RemoveOperandUses();
    }

    internal void RemoveOperandUses()
    {
        if (_useDefs == null) return;

        for (int i = 0; i < _numOperands; i++) {
            _operands[i].RemoveUse(this, i);
        }
        _useDefs = null!;
    }

    /// <summary> Replaces all operands set to <paramref name="oldValue"/> with <paramref name="newValue"/>. </summary>
    public void ReplaceOperand(Value oldValue, Value newValue)
    {
        for (int i = 0; i < _numOperands; i++) {
            if (_operands[i] == oldValue) {
                ReplaceOperand(i, newValue);
            }
        }
    }

    /// <summary> Replaces the operand at <paramref name="operIndex"/> with <paramref name="newValue"/>. </summary>
    public void ReplaceOperand(int operIndex, Value newValue)
    {
        Ensure.IndexValid(operIndex, _numOperands);
        var prevValue = _operands[operIndex];

        if (newValue != prevValue) {
            _operands[operIndex] = newValue;

            prevValue?.RemoveUse(this, operIndex);
            newValue.AddUse(this, operIndex);
            NotifyChanged();
        }
    }

    /// <summary> Returns a reference wrapper to the operand at <paramref name="index"/>. </summary>
    public UseRef GetOperandRef(int index)
    {
        Ensure.IndexValid(index, _numOperands);
        return new(this, index);
    }

    /// <summary> 
    /// Extends the operand list by <paramref name="amount"/> and returns the index of the first new element. <br/>
    /// New operands are null.
    /// Initialize them immediately using <see cref="ReplaceOperand(int, Value)"/>.
    /// </summary>
    protected int GrowOperands(int amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        int oldCount = _numOperands;
        int newCount = checked(oldCount + amount);
        NotifyChanged();
        if (newCount > _operands.Length) {
            GrowOperandBuffers(newCount);
        }
        _numOperands = newCount;
        return oldCount;
    }

    private void GrowOperandBuffers(int required)
    {
        int capacity = (int)Math.Min(Math.Max(4L, _operands.Length * 2L), Array.MaxLength);
        capacity = Math.Max(capacity, required);
        var operands = new Value[capacity];
        var useDefs = new UseDef[capacity];
        Array.Copy(_operands, operands, _numOperands);
        Array.Copy(_useDefs, useDefs, _numOperands);
        _operands = operands;
        _useDefs = useDefs;
    }

    /// <summary> Removes operands in the specified range. </summary>
    protected void RemoveOperands(int startIndex, int count)
    {
        NotifyChanged();
        Debug.Assert(startIndex >= 0 && count >= 0 && startIndex + count <= _numOperands);
        int oldCount = _numOperands;

        if (_operands.GetType() != typeof(Value[])) {
            var operands = new Value[_operands.Length];
            Array.Copy(_operands, operands, _numOperands);
            _operands = operands;
        }

        // Remove uses from middle and postfix, relocate them later
        for (int i = startIndex; i < oldCount; i++) {
            _operands[i].RemoveUse(this, i);
        }

        _numOperands = oldCount - count;
        Array.Copy(_operands, startIndex + count, _operands, startIndex, _numOperands - startIndex);
        Array.Clear(_operands, _numOperands, count);
        Array.Clear(_useDefs, _numOperands, count);

        // Rebuild use links after shifting operands.
        for (int i = startIndex; i < _numOperands; i++) {
            _operands[i].AddUse(this, i);
        }
    }

    public abstract void Accept(InstVisitor visitor);

    public override void PrintAsOperand(PrintContext ctx)
    {
        ctx.Print(ctx.SymTable.GetName(this), PrintToner.VarName);
    }
    public override void Print(PrintContext ctx)
    {
        if (HasResult) {
            PrintAsOperand(ctx);
            ctx.Print(" = ");
            PrintWithoutPrefix(ctx);
            ctx.Print(" -> ");
            ctx.Print(ResultType);
        } else {
            PrintWithoutPrefix(ctx);
        }
    }
    internal void PrintWithoutPrefix(PrintContext ctx)
    {
        ctx.Print(InstName, PrintToner.InstName);
        PrintOperands(ctx);
    }
    protected virtual void PrintOperands(PrintContext ctx)
    {
        for (int i = 0; i < _numOperands; i++) {
            ctx.Print(i == 0 ? " " : ", ");
            ctx.PrintAsOperand(_operands[i]);
        }
    }
    public override SymbolTable? GetSymbolTable()
    {
        return Block?.Method.GetSymbolTable();
    }
}
