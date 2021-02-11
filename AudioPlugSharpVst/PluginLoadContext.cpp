#include "PluginLoadContext.h"

using namespace System;
using namespace System::IO;
using namespace System::Reflection;
using namespace System::Runtime::Loader;
using namespace AudioPlugSharp;

PluginLoadContext::PluginLoadContext(String^ pluginPath)
{
	resolver = gcnew AssemblyDependencyResolver(pluginPath);
}

Assembly^ PluginLoadContext::Load(AssemblyName^ assemblyName)
{
    if (assemblyName->Name == "AudioPlugSharp")
    {
        Logger::Log("Skipping AudioPlugSharp assembly load");

        return AssemblyLoadContext::GetLoadContext(Reflection::Assembly::GetExecutingAssembly())->LoadFromAssemblyName(assemblyName);

        //return nullptr;
    }

    String^ assemblyPath = resolver->ResolveAssemblyToPath(assemblyName);

    Logger::Log("PluginLoadContext load [" + assemblyName + "] from: " + assemblyPath);

    if (assemblyPath != nullptr)
    {
        return LoadFromAssemblyPath(assemblyPath);
    }

    return nullptr;
}

IntPtr PluginLoadContext::LoadUnmanagedDll(String^ unmanagedDllName)
{
    String^ libraryPath = resolver->ResolveUnmanagedDllToPath(unmanagedDllName);

    if (libraryPath != nullptr)
    {
        return LoadUnmanagedDllFromPath(libraryPath);
    }

    return IntPtr::Zero;
}
