#pragma once

using namespace System;
using namespace System::IO;
using namespace System::Reflection;
using namespace System::Runtime::Loader;
using namespace AudioPlugSharp;


public ref class PluginLoadContext : public AssemblyLoadContext
{
public:
	PluginLoadContext(String^ pluginPath);
protected:
	virtual Assembly^ Load(AssemblyName^ assemblyName) override;
	virtual IntPtr LoadUnmanagedDll(String^ unmanagedDllName) override;

private:
	AssemblyDependencyResolver^ resolver;
};