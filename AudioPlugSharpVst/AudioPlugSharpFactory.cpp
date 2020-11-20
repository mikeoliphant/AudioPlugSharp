#include "AudioPlugSharp.h"
#include "AudioPlugSharpController.h"
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

			IAudioPluginInfo^ pluginInfo = nullptr;

			try
			{
				pluginInfo = safe_cast<IAudioPluginInfo^>(AssemblyResolver::GetObjectByInterface(pluginAssembly, IAudioPluginInfo::typeid));
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

			gPluginFactory = new CPluginFactory(factoryInfo);

			static const PClassInfo2 componentClass
			(
				AudioPlugSharpProcessor::AudioPlugSharpProcessorUID,
				PClassInfo::kManyInstances,
				kVstAudioEffectClass,
				pluginNameChars,
				Vst::kDistributable,
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

//// This creates the GetPluginFactory entry point that is exposed the the VST host
//BEGIN_FACTORY_DEF("COMPANY",
//	"WEBSITE",
//	"CONTACT")
//
//	//---First plug-in included in this factory-------
//	// its kVstAudioEffectClass component
//	DEF_CLASS2(INLINE_UID_FROM_FUID(AudioPlugSharpProcessorUID),
//		PClassInfo::kManyInstances,	// cardinality
//		kVstAudioEffectClass,	// the component category (do not changed this)
//		PluginName,		// here the plug-in name (to be changed)
//		Vst::kDistributable,	// means that component and controller could be distributed on different computers
//		PluginCategory,		// Subcategory for this plug-in (to be changed)
//		PLUGINVERSION,		// Plug-in version (to be changed)
//		kVstVersionString,		// the VST 3 SDK version (do not changed this, use always this define)
//		AudioPlugSharpProcessor::createInstance)	// function pointer called when this component should be instantiated
//
//	// its kVstComponentControllerClass component
//	DEF_CLASS2(INLINE_UID_FROM_FUID(AudioPlugSharpControllerUID),
//		PClassInfo::kManyInstances, // cardinality
//		kVstComponentControllerClass,// the Controller category (do not changed this)
//		PluginName "Controller",	// controller name (could be the same than component name)
//		0,						// not used here
//		"",						// not used here
//		PLUGINVERSION,		// Plug-in version (to be changed)
//		kVstVersionString,		// the VST 3 SDK version (do not changed this, use always this define)
//		AudioPlugSharpController::createInstance)// function pointer called when this component should be instantiated
//
//
//END_FACTORY