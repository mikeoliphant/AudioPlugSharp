#include "AssemblyResolver.h"

using namespace System;
using namespace System::IO;
using namespace System::Reflection;
using namespace System::Runtime::Loader;
using namespace AudioPlugSharp;

void AssemblyResolver::RegisterResolver()
{
	AssemblyLoadContext::GetLoadContext(Reflection::Assembly::GetExecutingAssembly())->Resolving +=
		gcnew System::Func<System::Runtime::Loader::AssemblyLoadContext^, System::Reflection::AssemblyName^, System::Reflection::Assembly^>(&ResolveAssembly);
}

System::Reflection::Assembly^ AssemblyResolver::ResolveAssembly(AssemblyLoadContext^ assemblyLoadContext, AssemblyName^ assemblyName)
{
	return LoadAssembly(assemblyName->Name);
}

System::Reflection::Assembly^ AssemblyResolver::LoadAssembly(System::String^ assemblyName)
{
	try
	{
		Logger::Log("Executing assembly: " + Reflection::Assembly::GetExecutingAssembly()->FullName);

		String^ executingAseemblyPath = Path::GetDirectoryName(Reflection::Assembly::GetExecutingAssembly()->Location);

		Assembly^ assembly = AssemblyLoadContext::GetLoadContext(Reflection::Assembly::GetExecutingAssembly())->LoadFromAssemblyPath(
			Path::Combine(executingAseemblyPath, (gcnew Reflection::AssemblyName(assemblyName))->Name + ".dll"));

		return assembly;
	}
	catch (System::Exception^ ex)
	{
		Logger::Log("Unable to load assembly: " + assemblyName);
	}

	return nullptr;
}

System::Object^ AssemblyResolver::GetObjectByInterface(System::Reflection::Assembly^ assembly, System::Type^ interfaceType)
{
	Logger::Log("Looking for type: " + interfaceType->AssemblyQualifiedName);

	System::Type^ matchedType = nullptr;

	for each (System::Type^ type in assembly->GetTypes())
	{
		Logger::Log("Found type: " + type->Name);

		if (type->IsPublic)
		{
			for each (System::Type^ iType in type->GetInterfaces())
			{
				Logger::Log("Found interface: " + iType->AssemblyQualifiedName);

				if (iType == interfaceType)
				{
					matchedType = type;

					break;
				}
			}
		}
	}

	if (matchedType == nullptr)
	{
		Logger::Log("Unable to find type");

		return nullptr;
	}

	Object^ obj = nullptr;
	
	try
	{
		obj = System::Activator::CreateInstance(matchedType);
	}
	catch (Exception^ ex)
	{
		Logger::Log("Failed to create object: " + ex->ToString());
	}

	return obj;
}
