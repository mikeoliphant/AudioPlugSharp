#include "AssemblyResolver.h"
#include "PluginLoadContext.h"

using namespace System;
using namespace System::IO;
using namespace System::Reflection;
using namespace System::Runtime::Loader;
using namespace AudioPlugSharp;

void AssemblyResolver::RegisterResolver(String^ pluginPath)
{
	// loadContext = AssemblyLoadContext::GetLoadContext(Reflection::Assembly::GetExecutingAssembly());
	//loadContext = gcnew AssemblyLoadContext("Hello", true);

	Logger::Log("Assembly resolution path is: " + pluginPath);

	if (loadContext == nullptr)
		loadContext = gcnew PluginLoadContext(pluginPath);

	//AssemblyLoadContext::GetLoadContext(Reflection::Assembly::GetExecutingAssembly())->Resolving +=
	//	gcnew System::Func<System::Runtime::Loader::AssemblyLoadContext^, System::Reflection::AssemblyName^, System::Reflection::Assembly^>(&ResolveAssembly);

	//loadContext->Resolving +=
	//	gcnew System::Func<System::Runtime::Loader::AssemblyLoadContext^, System::Reflection::AssemblyName^, System::Reflection::Assembly^>(&ResolveAssembly);

	//AppDomain::CurrentDomain->AssemblyResolve +=
	//	gcnew ResolveEventHandler(ResolveDomainAssembly);
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
		Logger::Log("Load assembly: " + assemblyName);

		String^ executingAseemblyPath = Path::GetDirectoryName(Reflection::Assembly::GetExecutingAssembly()->Location);

		//Assembly^ assembly = AssemblyLoadContext::GetLoadContext(Reflection::Assembly::GetExecutingAssembly())->LoadFromAssemblyPath(
		//	Path::Combine(executingAseemblyPath, (gcnew Reflection::AssemblyName(assemblyName))->Name + ".dll"));

		AssemblyName^ actualAssemblyName = gcnew AssemblyName(assemblyName);

		Logger::Log("Actual assembly name is " + actualAssemblyName);

		Assembly^ assembly = loadContext->LoadFromAssemblyName(actualAssemblyName);

		//Assembly^ assembly = loadContext->LoadFromAssemblyPath(
		//	Path::Combine(executingAseemblyPath, (gcnew Reflection::AssemblyName(assemblyName))->Name + ".dll"));

		return assembly;
	}
	catch (System::Exception^ ex)
	{
		Logger::Log("Unable to load assembly: " + assemblyName + " with: " + ex->ToString());
	}

	return nullptr;
}

System::Object^ AssemblyResolver::GetObjectByInterface(System::Reflection::Assembly^ assembly, System::Type^ interfaceType)
{
	Logger::Log("Looking for type: " + interfaceType->AssemblyQualifiedName + " in assembly " + assembly->FullName);

	Logger::Log("FullyQualifiedName: " + (IAudioPlugin::typeid)->Module->FullyQualifiedName);


	System::Type^ matchedType = nullptr;

	try
	{
		Logger::Log("Assembly types: " + assembly->GetTypes()->Length);


		for each (System::Type ^ type in assembly->GetTypes())
		{
			if (type->IsPublic)
			{
				Logger::Log("Checking type: " + type->Name + " -- " + type->Module->FullyQualifiedName);

				if (interfaceType->IsAssignableFrom(type))
				{
					Logger::Log("Matched");

					matchedType = type;

					break;
				}

				//for each (System::Type^ iType in type->GetInterfaces())
				//{
				//	Logger::Log("Checking interface type: " + iType->Name + " -- " + iType->Module->FullyQualifiedName);

				//	if (iType == interfaceType)
				//	{
				//		Logger::Log("Matched");

				//		matchedType = type;

				//		break;
				//	}
				//}
			}

			if (matchedType != nullptr)
				break;
		}
	}
	catch (Exception^ ex)
	{
		Logger::Log("Type scanning failed with: " + ex->ToString());
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
