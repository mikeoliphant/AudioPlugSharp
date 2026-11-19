using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace AudioPlugSharpBridge.Interop;

/// <summary>
/// JIT-compiles code ahead of time on the main thread, so the audio thread never hits the JIT.
/// With TieredCompilation disabled, this produces the final optimized code.
/// </summary>
internal static class Precompiler
{
    private const BindingFlags All = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static;

    private const int MaxMethods = 50_000;

    public readonly record struct Stats(int Compiled, int Considered, double Milliseconds);

    /// <summary>Compiles every method in an assembly. Used for the (small) framework assembly.</summary>
    public static Stats PrecompileAll(Assembly assembly)
    {
        var sw = Stopwatch.StartNew();
        int compiled = 0, considered = 0;
        foreach (var type in GetTypes(assembly))
        {
            if (type.ContainsGenericParameters) continue;
            foreach (var m in Methods(type))
            {
                considered++;
                if (CanPrepare(m) && TryPrepare(m)) compiled++;
            }
        }
        return new Stats(compiled, considered, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Compiles the realtime closure of a plugin type: its [Realtime] methods (including overrides of
    /// [Realtime] base methods and implementations of [Realtime] interface methods, e.g.
    /// IAudioPluginProcessor.Process) and everything they call within <paramref name="context"/>.
    /// </summary>
    public static Stats PrecompileRealtime(Type pluginType, AssemblyLoadContext context)
    {
        var sw = Stopwatch.StartNew();
        var walker = new Walker(context);
        for (var t = pluginType; t != null && t != typeof(object); t = t.BaseType)
            foreach (var m in t.GetMethods(All))
                if (IsRealtimeRoot(m)) walker.Enqueue(m);
        // Implementations of [Realtime] interface methods (the most derived override is the map target).
        foreach (var iface in SafeInterfaces(pluginType))
        {
            InterfaceMapping map;
            try { map = pluginType.GetInterfaceMap(iface); }
            catch { continue; }
            for (int i = 0; i < map.InterfaceMethods.Length; i++)
                if (map.InterfaceMethods[i].IsDefined(typeof(RealtimeAttribute), inherit: false))
                    walker.Enqueue(map.TargetMethods[i]);
        }
        // Explicitly marked helpers anywhere in the plugin's own assemblies.
        foreach (var asm in context.Assemblies)
            foreach (var type in GetTypes(asm))
                foreach (var m in Methods(type))
                    if (m.IsDefined(typeof(RealtimeAttribute), inherit: false)) walker.Enqueue(m);

        walker.Run();
        return new Stats(walker.Compiled, walker.Visited, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>Methods considered by the old whole-assembly approach, for comparison in diagnostics.</summary>
    public static int CountMethods(AssemblyLoadContext context) =>
        context.Assemblies.SelectMany(GetTypes).Where(t => !t.ContainsGenericParameters).Sum(t => Methods(t).Count());

    private static bool IsRealtimeRoot(MethodInfo m)
    {
        if (m.IsDefined(typeof(RealtimeAttribute), inherit: false)) return true;
        if (!m.IsVirtual) return false;
        // Overrides of [Realtime] base methods (e.g. AudioPlugin.Process).
        var baseDef = m.GetBaseDefinition();
        return baseDef != m && baseDef.IsDefined(typeof(RealtimeAttribute), inherit: false);
    }

    private sealed class Walker(AssemblyLoadContext context)
    {
        private readonly HashSet<MethodBase> _seen = new();
        private readonly Queue<MethodBase> _queue = new();
        private readonly HashSet<Type> _cctorsRun = new();
        private Dictionary<MethodInfo, List<MethodInfo>>? _overrides;

        public int Compiled { get; private set; }
        public int Visited => _seen.Count;

        public void Enqueue(MethodBase m)
        {
            if (_seen.Count < MaxMethods && _seen.Add(m)) _queue.Enqueue(m);
        }

        public void Run()
        {
            while (_queue.TryDequeue(out var m))
            {
                if (!InScope(m)) continue;
                if (CanPrepare(m))
                {
                    if (TryPrepare(m)) Compiled++;
                    RunClassConstructor(m.DeclaringType);
                }
                foreach (var callee in Callees(m))
                {
                    Enqueue(callee);
                    // A virtual/interface call may dispatch to any override in the plugin.
                    if (callee is MethodInfo mi && (mi.IsVirtual || mi.IsAbstract))
                        foreach (var o in OverridesOf(mi)) Enqueue(o);
                }
            }
        }

        private bool InScope(MethodBase m) =>
            m.DeclaringType is { } t && AssemblyLoadContext.GetLoadContext(t.Assembly) == context;

        private void RunClassConstructor(Type? t)
        {
            if (t == null || t.ContainsGenericParameters || !_cctorsRun.Add(t)) return;
            try { RuntimeHelpers.RunClassConstructor(t.TypeHandle); }
            catch { /* a failing static constructor will fail again (visibly) when the plugin uses the type */ }
        }

        private IEnumerable<MethodInfo> OverridesOf(MethodInfo method)
        {
            _overrides ??= BuildOverrideMap();
            var key = method.GetBaseDefinition();
            return _overrides.TryGetValue(key, out var list) ? list : (IEnumerable<MethodInfo>)[];
        }

        /// <summary>Base definition (or interface method) -> implementing methods in the plugin's context.</summary>
        private Dictionary<MethodInfo, List<MethodInfo>> BuildOverrideMap()
        {
            var map = new Dictionary<MethodInfo, List<MethodInfo>>();
            void Add(MethodInfo key, MethodInfo impl)
            {
                if (!map.TryGetValue(key, out var list)) map[key] = list = new List<MethodInfo>();
                list.Add(impl);
            }
            foreach (var asm in context.Assemblies)
            {
                foreach (var type in GetTypes(asm))
                {
                    if (type.ContainsGenericParameters || type.IsInterface) continue;
                    foreach (var m in type.GetMethods(All))
                        if (m.IsVirtual && m.GetBaseDefinition() is var b && b != m) Add(b, m);
                    foreach (var iface in SafeInterfaces(type))
                    {
                        InterfaceMapping mapping;
                        try { mapping = type.GetInterfaceMap(iface); }
                        catch { continue; }
                        for (int i = 0; i < mapping.InterfaceMethods.Length; i++)
                            Add(mapping.InterfaceMethods[i], mapping.TargetMethods[i]);
                    }
                }
            }
            return map;
        }
    }

    // ---- IL scanning -------------------------------------------------------------

    private static readonly OpCode[] OneByte = new OpCode[256];
    private static readonly OpCode[] TwoByte = new OpCode[256];

    static Precompiler()
    {
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (f.GetValue(null) is not OpCode op) continue;
            ushort v = (ushort)op.Value;
            if (op.Size == 1) OneByte[v] = op;
            else TwoByte[v & 0xFF] = op;
        }
    }

    /// <summary>Methods referenced by call / callvirt / newobj / ldftn / ldvirtftn in a method body.</summary>
    private static IEnumerable<MethodBase> Callees(MethodBase method)
    {
        byte[]? il;
        try { il = method.GetMethodBody()?.GetILAsByteArray(); }
        catch { yield break; }
        if (il == null) yield break;

        var typeArgs = method.DeclaringType is { IsGenericType: true } dt ? dt.GetGenericArguments() : null;
        var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;

        int pos = 0;
        while (pos < il.Length)
        {
            OpCode op;
            byte b = il[pos++];
            if (b == 0xFE)
            {
                if (pos >= il.Length) yield break;
                op = TwoByte[il[pos++]];
            }
            else op = OneByte[b];

            int operandSize = OperandSize(op.OperandType, il, pos);
            if (op.OperandType == OperandType.InlineMethod && pos + 4 <= il.Length)
            {
                int token = BitConverter.ToInt32(il, pos);
                MethodBase? callee = null;
                try { callee = method.Module.ResolveMethod(token, typeArgs, methodArgs); }
                catch { /* unresolvable (e.g. vararg or missing dependency) */ }
                if (callee != null) yield return callee;
            }
            pos += operandSize;
        }
    }

    private static int OperandSize(OperandType t, byte[] il, int pos) => t switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => pos + 4 <= il.Length ? 4 + 4 * BitConverter.ToInt32(il, pos) : il.Length,
        _ => 4,
    };

    // ---- helpers ---------------------------------------------------------------

    private static IEnumerable<MethodBase> Methods(Type type) =>
        type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All));

    private static bool CanPrepare(MethodBase m) =>
        !m.IsAbstract && !m.ContainsGenericParameters && m.DeclaringType is { ContainsGenericParameters: false } &&
        !m.IsDefined(typeof(UnmanagedCallersOnlyAttribute)) &&
        (m.MethodImplementationFlags & MethodImplAttributes.InternalCall) == 0;

    private static bool TryPrepare(MethodBase m)
    {
        try
        {
            RuntimeHelpers.PrepareMethod(m.MethodHandle);
            return true;
        }
        catch
        {
            return false; // some methods (e.g. on ref structs with odd signatures) can't be prepared; warm-up covers them
        }
    }

    private static Type[] GetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray()!; }
    }

    private static Type[] SafeInterfaces(Type type)
    {
        try { return type.GetInterfaces(); }
        catch { return []; }
    }
}
