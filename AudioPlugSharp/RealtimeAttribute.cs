using System;

namespace AudioPlugSharp
{
    /// <summary>
    /// Marks a method that runs on the realtime audio thread. When a plugin instance is loaded, every [Realtime]
    /// method (including implementations and overrides of one), plus everything it calls inside the plugin, is
    /// JIT-compiled up front so the audio thread never waits for the JIT.
    ///
    /// The call-graph walk follows direct calls, virtual overrides and interface implementations. Mark methods
    /// it cannot see (for example delegate targets) explicitly.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = true)]
    public sealed class RealtimeAttribute : Attribute
    {
    }
}