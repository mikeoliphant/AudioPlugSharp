# AbiGen

AbiGen generates the ABI between the native format adapters and the managed bridge. A C# contract (**Contract/Contract.cs**) describes the ABI, and AbiGen turns it into:

- **abi.h**: the C ABI, with function-pointer tables, blittable structs, enums, and layout checks (`static_assert` on sizes and offsets)
- **abi.hpp**: C++ wrappers for the adapters. They convert results and strings, use the two-call pattern for buffers, and check for missing members.
- **Generated/*.g.cs**: the C# side. These are the table structs, the `[UnmanagedCallersOnly]` thunks for the interface implemented in C#, proxies for the interface implemented natively, and a layout check that runs when the bridge starts up.

The headers go in **Native/Include/aps/generated/**.

# Why

The ABI is a flat C ABI so that it works the same everywhere: no COM and no C++/CLI. Written by hand, that means keeping a C header, C++ wrappers and C# interop in step, with layouts that must match byte for byte. AbiGen makes the contract the single source of truth, so the two sides can't drift apart, and it enforces the rules that keep the boundary safe.

It's a standalone tool rather than a Roslyn source generator, because it also writes C and C++ headers. It reads the compiled contract assembly as metadata only. The contract never ships.

# The Contract

An interface marked **[ManagedApi]** is implemented in C# and called from native code (the plugin). One marked **[NativeApi]** is implemented natively and called from C# (the host). Every method is either **[MainThread]** or **[AudioThread]**. **[AbiStruct]** types are passed as they are; **[AbiRecord]** types are main-thread values that may hold fixed-size strings, and the generator converts them.

From these, AbiGen derives the native signatures: every function returns a result code, values come back through out parameters, spans become a pointer plus a count, and returned arrays use the two-call pattern (ask for the size, then fill a buffer). The thunks catch every exception and turn it into a result code, so no exception ever crosses into native code.

# Rules

Anything outside the rules is a build error:

- **Struct fields are blittable:** primitives (no `bool`), enums with an explicit underlying type, pointers, other structs.
- **No allocation on the audio thread:** **[AudioThread]** members can't take or return strings, arrays or records.
- **Append-only:** within an ABI version, structs and enums are frozen and interface members can only be added at the end. Added members must be **[OptionalMember]**, because an older binary won't provide them; the C++ wrappers then check for null. Every table starts with its size, so either side can tell which members the other has.

AbiGen checks the append-only rule against the committed **abi-manifest.json**, which records the previous state of the ABI. To make a breaking change, bump **AbiConfig.Version** in the contract. The adapters and the bridge always ship together, so a version bump has no cost for plugins.

# Changing the ABI

Edit the contract and build the bridge: the bridge project runs AbiGen on every build, and AbiGen only rewrites files that changed. Commit the generated files and the manifest along with the contract change, so ABI changes show up in review. CI fails if they're out of date. `--check` does the same check without writing anything.

Not generated: the bootstrap entry points that the adapters look up by name (**NativeEntry.CreatePlugin** and the diagnostics).

**Tests/AbiGenTests** compile small test contracts and check the rules, the compatibility checks and the output.
