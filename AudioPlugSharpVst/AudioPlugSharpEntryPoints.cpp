#include "AudioPlugSharp.h"
#include "AudioPlugSharpController.h"
#include "AudioPlugSharpFactory.h"
#include "AssemblyResolver.h"

#include "public.sdk/source/main/pluginfactory.h"

using namespace System::Runtime::InteropServices;
using namespace AudioPlugSharp;

bool InitModule()
{
	AssemblyResolver::RegisterResolver();

	return true;
}

bool DeinitModule()
{
	return true;
}

IPluginFactory* PLUGIN_API GetPluginFactory()
{
	AudioPlugSharp::Logger::Log("GetPluginFactory");

	if (!gPluginFactory)
	{
		try
		{
			Assembly^ pluginAssembly = AssemblyResolver::LoadAssembly("ExamplePlugin");

			IAudioPlugin^ pluginInfo = nullptr;

			try
			{
				pluginInfo = safe_cast<IAudioPlugin^>(AssemblyResolver::GetObjectByInterface(pluginAssembly, IAudioPlugin::typeid));
			}
			catch (Exception^ ex)
			{
				Logger::Log("Failed to cast pluginInfo: " + ex->ToString());

				return nullptr;
			}

			if (pluginInfo == nullptr)
			{
				Logger::Log("plugInfo is null");

				return nullptr;
			}

			Logger::Log("Creating processor and controller");

			char* companyChars = (char*)(void*)Marshal::StringToHGlobalAnsi(pluginInfo->Company);
			char* websiteChars = (char*)(void*)Marshal::StringToHGlobalAnsi(pluginInfo->Website);
			char* contactChars = (char*)(void*)Marshal::StringToHGlobalAnsi(pluginInfo->Contact);
			char* pluginNameChars = (char*)(void*)Marshal::StringToHGlobalAnsi(pluginInfo->PluginName);
			char* pluginCategoryChars = (char*)(void*)Marshal::StringToHGlobalAnsi(pluginInfo->PluginCategory);
			char* pluginVersionChars = (char*)(void*)Marshal::StringToHGlobalAnsi(pluginInfo->PluginVerstion);
			char* processorGuidChars = (char*)(void*)Marshal::StringToHGlobalAnsi(pluginInfo->ProcessorGuid);
			char* controllerGuidChars = (char*)(void*)Marshal::StringToHGlobalAnsi(pluginInfo->ControllerGuid);

			AudioPlugSharpProcessor::AudioPlugSharpProcessorUID.fromString(processorGuidChars);
			AudioPlugSharpController::AudioPlugSharpControllerUID.fromString(controllerGuidChars);

			Logger::Log("here");

			static PFactoryInfo factoryInfo(companyChars, websiteChars, contactChars, Vst::kDefaultFactoryFlags);

			gPluginFactory = new AudioPlugSharpFactory(factoryInfo);

			static const PClassInfo2 componentClass
			(
				AudioPlugSharpProcessor::AudioPlugSharpProcessorUID,
				PClassInfo::kManyInstances,
				kVstAudioEffectClass,
				pluginNameChars,
				0,
				pluginCategoryChars,
				companyChars,
				pluginVersionChars,
				kVstVersionString
			);

			gPluginFactory->registerClass(&componentClass, AudioPlugSharpProcessor::createInstance);

			static const PClassInfo2 controllerClass
			(
				AudioPlugSharpController::AudioPlugSharpControllerUID,
				PClassInfo::kManyInstances,
				kVstComponentControllerClass,
				pluginNameChars,
				0,
				"",
				companyChars,
				pluginVersionChars,
				kVstVersionString
			);

			Logger::Log("here");

			Marshal::FreeHGlobal((IntPtr)companyChars);
			Marshal::FreeHGlobal((IntPtr)websiteChars);
			Marshal::FreeHGlobal((IntPtr)contactChars);
			Marshal::FreeHGlobal((IntPtr)pluginNameChars);
			Marshal::FreeHGlobal((IntPtr)pluginCategoryChars);
			Marshal::FreeHGlobal((IntPtr)pluginVersionChars);
			Marshal::FreeHGlobal((IntPtr)processorGuidChars);
			Marshal::FreeHGlobal((IntPtr)controllerGuidChars);

			Logger::Log("here");

			gPluginFactory->registerClass(&controllerClass, AudioPlugSharpController::createInstance);

			Logger::Log("here");
		}
		catch (Exception^ ex)
		{
			Logger::Log("Error creating plugin factory: " + ex->ToString());
		}
	}

	return gPluginFactory;
}
