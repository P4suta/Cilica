namespace Cilica.AsmIO;

using System.Collections.ObjectModel;

public sealed class MethodDefCollection : Collection<MethodDef>
{
    internal MethodDefCollection(List<MethodDef> methods) : base(methods)
    {
    }

    private bool? _hasStaticConstructor;

    internal bool HasStaticConstructor => _hasStaticConstructor ??= Items.Any(method => method.Name == ".cctor");

    internal void InvalidateStaticConstructor() => _hasStaticConstructor = null;

    internal int RemoveAll(Predicate<MethodDef> match)
    {
        int removed = ((List<MethodDef>)Items).RemoveAll(match);
        InvalidateStaticConstructor();
        return removed;
    }

    protected override void InsertItem(int index, MethodDef item)
    {
        base.InsertItem(index, item);
        InvalidateStaticConstructor();
    }

    protected override void RemoveItem(int index)
    {
        base.RemoveItem(index);
        InvalidateStaticConstructor();
    }

    protected override void SetItem(int index, MethodDef item)
    {
        base.SetItem(index, item);
        InvalidateStaticConstructor();
    }

    protected override void ClearItems()
    {
        base.ClearItems();
        InvalidateStaticConstructor();
    }
}
