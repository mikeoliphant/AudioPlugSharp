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

	AppDomain::CurrentDomain->AssemblyResolve +=
		gcnew ResolveEventHandler(ResolveDomainAssembly);
}

System::Reflection::Assembly^ AssemblyResolver::ResolveAssembly(AssemblyLoadContext^ assemblyLoadContext, AssemblyName^ assemblyName)
{
	return LoadAssembly(assemblyName->Name);
}

System::Reflection::Assembly^ AssemblyResolver::ResolveDomainAssembly(Object^ appDomain, System::ResolveEventArgs^ args)
{
	Logger::Log("Resolve domain assembly " + args->Name);

	AppDomain^ domain = (AppDomain^)appDomain;

	for each(auto assembly in domain->GetAssemblies())
	{
		if (assembly->FullName == args->Name)
		{
			Logger::Log("Assembly found");

			return assembly;
		}
	}

	return nullptr;
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
		if (type->IsPublic)
		{
			for each (System::Type^ iType in type->GetInterfaces())
			{
				if (iType == interfaceType)
				{
					Logger::Log("Matched");

					matchedType = type;

					break;
				}
			}
		}

		if (matchedType != nullptr)
			break;
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
