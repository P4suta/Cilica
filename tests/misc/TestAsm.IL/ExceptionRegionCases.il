.class public ExceptionRegionCases
{
    .method public static void LoopAtSharedTryEntry(class [System.Runtime]System.Action`1<int32> trace)
    {
        .maxstack 2
        .locals init (int32 remaining)
        ldc.i4.2
        stloc.0
        .try {
            .try {
            Again:
                ldarg.0
                ldloc.0
                callvirt instance void class [System.Runtime]System.Action`1<int32>::Invoke(!0)
                ldloc.0
                ldc.i4.1
                sub
                dup
                stloc.0
                brtrue Again
                leave InnerDone
            } finally {
                ldarg.0
                ldc.i4.3
                callvirt instance void class [System.Runtime]System.Action`1<int32>::Invoke(!0)
                endfinally
            }
        InnerDone:
            leave Done
        } finally {
            ldarg.0
            ldc.i4.4
            callvirt instance void class [System.Runtime]System.Action`1<int32>::Invoke(!0)
            endfinally
        }
    Done:
        ret
    }

    .method public static void LeaveIntoNestedSiblingTry(class [System.Runtime]System.Action`1<int32> trace)
    {
        .maxstack 2
        .try {
            ldarg.0
            ldc.i4.1
            callvirt instance void class [System.Runtime]System.Action`1<int32>::Invoke(!0)
            leave Second
        } finally {
            ldarg.0
            ldc.i4.2
            callvirt instance void class [System.Runtime]System.Action`1<int32>::Invoke(!0)
            endfinally
        }
    Second:
        .try {
            .try {
                ldarg.0
                ldc.i4.3
                callvirt instance void class [System.Runtime]System.Action`1<int32>::Invoke(!0)
                leave InnerDone
            } finally {
                ldarg.0
                ldc.i4.4
                callvirt instance void class [System.Runtime]System.Action`1<int32>::Invoke(!0)
                endfinally
            }
        InnerDone:
            leave Done
        } finally {
            ldarg.0
            ldc.i4.5
            callvirt instance void class [System.Runtime]System.Action`1<int32>::Invoke(!0)
            endfinally
        }
    Done:
        ldarg.0
        ldc.i4.6
        callvirt instance void class [System.Runtime]System.Action`1<int32>::Invoke(!0)
        ret
    }

    .method public static void LeaveWithinTry(class [System.Runtime]System.Action`1<int32> trace)
    {
        .maxstack 2
        .try {
            ldarg.0
            ldc.i4.1
            callvirt instance void class [System.Runtime]System.Action`1<int32>::Invoke(!0)
            leave Next
        Next:
            ldarg.0
            ldc.i4.2
            callvirt instance void class [System.Runtime]System.Action`1<int32>::Invoke(!0)
            leave Done
        } finally {
            ldarg.0
            ldc.i4.3
            callvirt instance void class [System.Runtime]System.Action`1<int32>::Invoke(!0)
            endfinally
        }
    Done:
        ldarg.0
        ldc.i4.4
        callvirt instance void class [System.Runtime]System.Action`1<int32>::Invoke(!0)
        ret
    }

    .method public static void LeaveWithoutExceptionRegions(class [System.Runtime]System.Action`1<int32> trace)
    {
        .maxstack 2
        ldc.i4.s 99
        leave Done
    Done:
        ldarg.0
        ldc.i4.1
        callvirt instance void class [System.Runtime]System.Action`1<int32>::Invoke(!0)
        ret
    }

    .method public static void FinallyStartsWithTry(class [System.Runtime]System.Action`1<int32> trace)
    {
        .maxstack 2
        .try {
            ldarg.0
            ldc.i4.1
            callvirt instance void class [System.Runtime]System.Action`1<int32>::Invoke(!0)
            leave Done
        } finally {
            .try {
                ldarg.0
                ldc.i4.2
                callvirt instance void class [System.Runtime]System.Action`1<int32>::Invoke(!0)
                leave InnerDone
            } finally {
                ldarg.0
                ldc.i4.3
                callvirt instance void class [System.Runtime]System.Action`1<int32>::Invoke(!0)
                endfinally
            }
        InnerDone:
            endfinally
        }
    Done:
        ldarg.0
        ldc.i4.4
        callvirt instance void class [System.Runtime]System.Action`1<int32>::Invoke(!0)
        ret
    }
}
