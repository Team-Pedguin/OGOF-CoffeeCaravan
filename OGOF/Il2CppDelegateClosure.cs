using System.Reflection;

namespace OGOF;

public class Il2CppDelegateClosure
{
    public readonly MethodInfo Method;
    public readonly object? Target;


    public Il2CppDelegateClosure(MethodInfo mi, object? target)
    {
        Method = mi;
        Target = target;
    }

    public object? Invoke(params object[] args)
    {
        return Method.Invoke(Target, args);
    }
}