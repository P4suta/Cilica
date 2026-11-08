namespace Cilica.Passes.Linq;

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Cilica.IR.Utils;

internal abstract class ConcretizationSink : LinqSink
{
    public ConcretizationSink(CallInst call)
        : base(call) { }

    Value? _container;

    public override void EmitHead(IRBuilder builder, EstimatedSourceLen estimLen)
    {
        _container = AllocContainer(builder, estimLen);
    }
    public override void EmitBody(IRBuilder builder, Value currItem, BodyLoopData loopData)
    {
        AppendItem(builder, _container!, currItem, loopData);
    }
    public override void EmitTail(IRBuilder builder)
    {
        var result = WrapContainer(builder, _container!);
        Result = result;
    }

    protected virtual void AppendItem(IRBuilder builder, Value container, Value currItem, BodyLoopData loopData)
        => builder.CreateCallVirt("Add", container, currItem);
    protected virtual Value WrapContainer(IRBuilder builder, Value container) => container;

    // Allocates a collection with a proven initial capacity when available.
    protected virtual Value AllocContainer(IRBuilder builder, EstimatedSourceLen estimLen)
    {
        var type = SubjectCall.ResultType;
        Debug.Assert(type.Name is "List`1" or "HashSet`1" or "Dictionary`2");

        var args = new List<Value>();
        var sig = new List<TypeSig>();

        if (estimLen.Length != null && !estimLen.IsOverEstimation) {
            args.Add(estimLen.Length);
            sig.Add(PrimType.Int32);
        }
        var lastParType = SubjectCall.Method.ParamSig[^1].Type;

        if (type.Name is "Dictionary`2" or "HashSet`1" && lastParType.Name is "IEqualityComparer`1") {
            args.Add(SubjectCall.Args[^1]);
            sig.Add(lastParType.GetUnboundSpec());
        }
        var ctor = type.FindMethod(".ctor", new MethodSig(PrimType.Void, sig));
        return builder.CreateNewObj(ctor, args.ToArray());
    }
}
internal class ListOrArraySink : ConcretizationSink
{
    public ListOrArraySink(CallInst call)
        : base(call) { }

    LocalSlot? _segments;

    public override bool HasDisposal => _segments != null;

    protected override Value AllocContainer(IRBuilder builder, EstimatedSourceLen estimLen)
    {
        var elemType = SubjectCall.ResultType;
        elemType = (elemType as ArrayType)?.ElemType ?? elemType.GenericParams[0];

        if (!estimLen.IsExact) {
            var definition = (TypeDef)builder.Resolver.Resolve("System.Linq")
                .FindType("System.Collections.Generic", "SegmentedArrayBuilder`1", throwIfNotFound: true);
            var scratchDefinition = definition.FindNestedType("ScratchBuffer")!;
            var scratchType = scratchDefinition.GetSpec([elemType]);
            int scratchLength = (int)scratchDefinition.GetCustomAttribs().Find(typeof(InlineArrayAttribute))!.Args[0]!;
            var scratch = Method.CreateVar(scratchType, "lq_scratch", hardExposed: true);
            builder.CreateInitObj(scratch);
            var start = builder.CreateFieldAddr(scratchType.FindField("_item"), scratch);
            var createSpan = builder.Resolver.Import(typeof(MemoryMarshal)).FindMethod("CreateSpan").GetSpec([elemType]);
            var span = builder.CreateCall(createSpan, start, ConstInt.CreateI(scratchLength));

            var type = definition.GetSpec([elemType]);
            _segments = Method.CreateVar(type, "lq_segments", hardExposed: true);
            builder.CreateCall(type.FindMethod(".ctor"), _segments, span);
            return _segments;
        }

        if (SubjectCall.Method.Name == "ToArray") {
            return builder.CreateNewArray(elemType, estimLen.Length);
        }

        return base.AllocContainer(builder, estimLen);
    }

    protected override void AppendItem(IRBuilder builder, Value container, Value currItem, BodyLoopData loopData)
    {
        if (_segments != null) {
            builder.CreateCall(_segments.Type.FindMethod("Add"), _segments, currItem);
        } else if (container.ResultType is ArrayType) {
            var index = loopData.CreateAccum(ConstInt.CreateI(0), curr => builder.CreateAdd(curr, ConstInt.CreateI(1)));
            builder.CreateArrayStore(container, index, currItem, inBounds: true);
        } else {
            base.AppendItem(builder, container, currItem, loopData);
        }
    }

    protected override Value WrapContainer(IRBuilder builder, Value container)
    {
        if (_segments != null) {
            var materialize = _segments.Type.Methods.Single(method =>
                method.Name == SubjectCall.Method.Name && method.ParamSig.Count == 1);
            return builder.CreateCall(materialize, _segments);
        }
        if (SubjectCall.ResultType is ArrayType array) {
            var empty = builder.Resolver.SysTypes.Array.FindMethod("Empty").GetSpec([array.ElemType]);
            return builder.CreateSelect(builder.CreateEq(builder.CreateArrayLen(container), ConstInt.CreateI(0)),
                builder.CreateCall(empty), container);
        }
        return container;
    }

    public override void EmitEnd(LoopBuilder loop)
    {
        if (_segments == null) return;

        var continuation = ((BranchInst)loop.Exit.Block.Last).Then;
        var exit = Method.CreateBlock(insertAfter: loop.Exit.Block).SetName("LQ_Materialized");
        exit.SetBranch(new LeaveInst(continuation));
        loop.Exit.SetBranch(exit);

        var handler = Method.CreateBlock(insertAfter: exit).SetName("LQ_BufferFinally");
        var builder = new IRBuilder(handler);
        builder.CreateCall(_segments.Type.FindMethod("Dispose"), _segments);
        builder.Emit(new ResumeInst([continuation]));

        // Buffer ownership encloses enumeration so its storage is returned even
        // when acquisition, iteration, or enumerator disposal throws.
        var entry = loop.PreHeader.Block.Split(loop.PreHeader.Block.Last).SetName("LQ_Materialize");
        entry.InsertFirst(new GuardInst(GuardKind.Finally, handler));
    }
}
internal class HashSetSink : ConcretizationSink
{
    public HashSetSink(CallInst call)
        : base(call) { }
}
internal class DictionarySink : ConcretizationSink
{
    public DictionarySink(CallInst call)
        : base(call) { }

    protected override void AppendItem(IRBuilder builder, Value container, Value currItem, BodyLoopData loopData)
    {
        var key = builder.CreateLambdaInvoke(SubjectCall, 1, currItem);
        var value = currItem;
        // Signature: ToDictionary(source, keySelector, [elementSelector], [comparer])
        if (SubjectCall.Method.ParamSig is [_, _, { Type.Name: "Func`2" }, ..]) {
            value = builder.CreateLambdaInvoke(SubjectCall, 2, currItem);
        }
        builder.CreateCallVirt("Add", container, key, value);
    }
}
