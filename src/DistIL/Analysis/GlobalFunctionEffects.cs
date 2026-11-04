namespace DistIL.Analysis;

public class GlobalFunctionEffects : IGlobalAnalysis
{
    readonly Dictionary<MethodDef, Entry> _cache = new();
    readonly Dictionary<MethodDef, HashSet<MethodDef>> _callers = new();
    readonly Stack<(MethodDef Method, HashSet<MethodDef> Dependencies)> _building = new();
    readonly HashSet<MethodDef> _active = new();
    readonly HashSet<MethodDef> _checkedMethods = new();
    readonly Stack<MethodDef> _pendingChecks = new();
    readonly Compilation _comp;

    public long Computations { get; private set; }

    public long CacheHits { get; private set; }

    sealed record Entry(FunctionEffects Effects, MethodBody? Body, long Revision, HashSet<MethodDef> Dependencies);

    private GlobalFunctionEffects(Compilation comp) => _comp = comp;

    static IGlobalAnalysis IGlobalAnalysis.Create(Compilation comp)
        => new GlobalFunctionEffects(comp);
    
    public FunctionEffects GetEffects(MethodDesc method)
    {
        if (method is not MethodDefOrSpec def) {
            return FunctionEffects.Unknown;
        }

        var target = def.Definition;

        if (_building.TryPeek(out var parent)) {
            parent.Dependencies.Add(target);
        }

        if (_active.Contains(target)) {
            return FunctionEffects.Unknown;
        }

        if (_cache.TryGetValue(target, out var entry)) {
            if (entry.Body == target.Body && entry.Revision == (target.Body?.Revision ?? 0) &&
                (entry.Dependencies.Count == 0 || IsCurrent(target))) {
                CacheHits++;

                return entry.Effects;
            }

            SetDirty(target);
        }

        var dependencies = new HashSet<MethodDef>();
        _building.Push((target, dependencies));
        _active.Add(target);

        try {
            var effects = ComputeEffects(target);
            _cache.Add(target, new Entry(effects, target.Body, target.Body?.Revision ?? 0, dependencies));

            foreach (var dependency in dependencies) {
                (_callers.GetOrAddRef(dependency) ??= new()).Add(target);
            }

            Computations++;

            return effects;
        } finally {
            _building.Pop();
            _active.Remove(target);
        }
    }

    bool IsCurrent(MethodDef method)
    {
        // Validation does not compute effects or re-enter GetEffects, so queries
        // can share this scratch storage, including queries during graph building.
        _pendingChecks.Push(method);
        try {
            while (_pendingChecks.TryPop(out var current)) {
                if (!_checkedMethods.Add(current)) continue;
                if (!_cache.TryGetValue(current, out var entry)) {
                    // Active recursive ancestors deliberately contributed unknown
                    // effects and have not published their cache entries yet.
                    if (!_active.Contains(current)) return false;
                    continue;
                }
                if (entry.Body != current.Body || entry.Revision != (current.Body?.Revision ?? 0)) return false;
                foreach (var dependency in entry.Dependencies) _pendingChecks.Push(dependency);
            }
            return true;
        } finally {
            _checkedMethods.Clear();
            _pendingChecks.Clear();
        }
    }

    public FunctionEffects GetEffects(CallInst call)
    {
        var method = call.Method;
        if (call.IsVirtual && method.Attribs.HasFlag(System.Reflection.MethodAttributes.Virtual) &&
            !method.Attribs.HasFlag(System.Reflection.MethodAttributes.Final)) {
            return FunctionEffects.Unknown;
        }
        var effects = GetEffects(method);
        if (call.IsVirtual && !call.InBounds) {
            effects = new FunctionEffects(effects.MemEffects, effects.Traits | FunctionTraits.MayThrow);
        }
        return effects;
    }

    public bool SetDirty(MethodDef method)
    {
        bool found = _cache.ContainsKey(method);
        var pending = new DiscreteStack<MethodDef>(method);

        while (pending.TryPop(out var current)) {
            if (_callers.Remove(current, out var callers)) {
                foreach (var caller in callers) {
                    pending.Push(caller);
                }
            }

            if (_cache.Remove(current, out var entry)) {
                foreach (var dependency in entry.Dependencies) {
                    if (_callers.TryGetValue(dependency, out var users)) {
                        users.Remove(current);
                    }
                }
            }
        }

        return found;
    }

    public void SetAllDirty()
    {
        _cache.Clear();
        _callers.Clear();
    }

    void IGlobalAnalysis.Invalidate(MethodDef method) => SetDirty(method);

    private FunctionEffects ComputeEffects(MethodDef method)
    {
        if (method.IsStatic && method.DeclaringType.RequiresTypeInitialization()) {
            return FunctionEffects.Unknown;
        }

        if (_comp.Runtime.GetKnownEffects(method) is { } effects) {
            return effects;
        }

        if (method.Body != null && (method.Module == _comp.Module || _comp.Settings.AllowCrossAssemblyIPO)) {
            return ComputeEffectsFromBody(method.Body);
        }
        // We can't deduce anything about this method, assume the worst.
        return FunctionEffects.Unknown;
    }

    private FunctionEffects ComputeEffectsFromBody(MethodBody body)
    {
        var memEffects = MemoryEffects.None;
        var traits = ControlFlowFacts.HasCycle(body) ? FunctionTraits.MayDiverge : FunctionTraits.None;
        int numReturns = 0, numThrows = 0;

        foreach (var block in body) {
            foreach (var inst in block.NonPhis()) {
                switch (inst) {
                    case CilIntrinsic.CallIndirect: {
                        memEffects |= FunctionEffects.Unknown.MemEffects;
                        traits |= FunctionTraits.CallTransferMask;
                        break;
                    }
                    case CallInst call: {
                        var callEffects = GetEffects(call);
                        memEffects |= callEffects.MemEffects;
                        traits |= callEffects.Traits & FunctionTraits.CallTransferMask;
                        if (call.Method is MethodDefOrSpec target && target.Definition == body.Definition) {
                            traits |= FunctionTraits.Recursive;
                        }
                        break;
                    }
                    default: {
                        if (inst.MayReadFromMemory) memEffects |= MemoryEffects.Read;
                        if (inst.MayWriteToMemory) memEffects |= MemoryEffects.Write;
                        if (inst.MayThrow) traits |= FunctionTraits.MayThrow;
                        if (inst.IsMemoryBarrier) traits |= FunctionTraits.MemoryBarrier;

                            if (inst is NewObjInst or CilIntrinsic.NewArray or CilIntrinsic.Box or CilIntrinsic.Alloca) {
                                traits |= FunctionTraits.Allocates;
                            }

                            break;
                    }
                }
            }

            switch (block.Last) {
                case ReturnInst: numReturns++; break;
                case ThrowInst: numThrows++; break;
            }
        }

        if (numReturns == 0 && numThrows > 0) {
            traits |= FunctionTraits.DoesNotReturn;
        }
        
        return new FunctionEffects() {
            MemEffects = memEffects,
            Traits = traits,
        };
    }


}

public readonly struct FunctionEffects
{
    public static FunctionEffects Unknown => new(MemoryEffects.ReadWrite, FunctionTraits.Unknown);

    public MemoryEffects MemEffects { get; init; }
    public FunctionTraits Traits { get; init; }

    public bool IsUnknown => Traits == FunctionTraits.Unknown;
    public bool IsPure => Traits == FunctionTraits.None && MemEffects == MemoryEffects.None;

    public bool MayDiverge => (Traits & FunctionTraits.MayDiverge) != 0;

    public bool MayAllocate => (Traits & FunctionTraits.Allocates) != 0;

    public bool MayThrow => (Traits & FunctionTraits.MayThrow) != 0;
    public bool HasMemoryBarrier => (Traits & FunctionTraits.MemoryBarrier) != 0;
    public bool MayReadMem => (MemEffects & MemoryEffects.Read) != 0;
    public bool MayWriteMem => (MemEffects & MemoryEffects.Write) != 0;
    public bool MayOnlyThrowOrReadMem => (Traits & ~FunctionTraits.MayThrow) == 0 && (MemEffects & ~MemoryEffects.Read) == 0;

    public FunctionEffects(MemoryEffects memEffects, FunctionTraits traits)
    {
        MemEffects = memEffects;
        Traits = traits;
    }

    public override string ToString() => $"Mem={MemEffects}, Traits={Traits}";
}
[Flags]
public enum FunctionTraits
{

    None = 0,

    Unknown         = ~0,

    CallTransferMask = MayThrow | MemoryBarrier | MayDiverge | Allocates,


    MayThrow        = 1 << 0,

    Recursive       = 1 << 1,

    DoesNotReturn   = 1 << 3, // e.g. ThrowHelper

    MemoryBarrier   = 1 << 4,

    MayDiverge = 1 << 5,

    Allocates = 1 << 6,



}
public enum MemoryEffects : byte
{
    /// <summary> Memory is neither read nor written. </summary>
    None = 0,

    /// <summary> Memory may be read. </summary>
    Read = 1 << 0,

    /// <summary> Memory may be written. </summary>
    Write = 1 << 1,

    /// <summary> Memory may be both read and written. </summary>
    ReadWrite = Read | Write,
}
