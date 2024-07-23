#include "AudioPlugSharpFactory.h"

using namespace System::IO;
using namespace System::Reflection;
using namespace System::Runtime::InteropServices;
using namespace AudioPlugSharp;

#include <msclr/gcroot.h>

using namespace msclr;

AudioPlugSharpFactory::AudioPlugSharpFactory()
	: CPluginFactory(PFactoryInfo())
{
	Logger::Log("AudioPlugSharpFactory running under .NET version: " + System::Environment::Version);

	System::String^ assemblyName = Path::GetFileNameWithoutExtension(Assembly::GetExecutingAssembly()->Location);

	// Our plugin should be our name but without the 'Bridge' at the end
	assemblyName = assemblyName->Substring(0, assemblyName->Length - 6);

	Logger::Log("Plugin assembly name: " + assemblyName);

	plugin = PluginLoader::LoadPluginFromAssembly(assemblyName);

	char* companyChars = (char*)(void*)Marshal::StringToHGlobalAnsi(plugin->Company);
	char* websiteChars = (char*)(void*)Marshal::StringToHGlobalAnsi(plugin->Website);
	char* contactChars = (char*)(void*)Marshal::StringToHGlobalAnsi(plugin->Contact);
	char* pluginNameChars = (char*)(void*)Marshal::StringToHGlobalAnsi(plugin->PluginName);
	char* pluginCategoryChars = (char*)(void*)Marshal::StringToHGlobalAnsi(plugin->PluginCategory);
	char* pluginVersionChars = (char*)(void*)Marshal::StringToHGlobalAnsi(plugin->PluginVersion);

	AudioPlugSharpProcessorUID = FUID(AUDIO_PLUG_SHARP_ID, AUDIO_PLUG_SHARP_PROCESSOR_ID, plugin->PluginID >> 32, plugin->PluginID & 0x00000000ffffffff);
	AudioPlugSharpControllerUID = FUID(AUDIO_PLUG_SHARP_ID, AUDIO_PLUG_SHARP_CONTROLLER_ID, plugin->PluginID >> 32, plugin->PluginID & 0x00000000ffffffff);

	factoryInfo = PFactoryInfo(companyChars, websiteChars, contactChars, Vst::kDefaultFactoryFlags);

	PClassInfo2 componentClass
	(
		AudioPlugSharpProcessorUID,
		PClassInfo::kManyInstances,
		kVstAudioEffectClass,
		pluginNameChars,
		0,
		pluginCategoryChars,
		companyChars,
		pluginVersionChars,
		kVstVersionString
	);

	registerClass(&componentClass, AudioPlugSharpProcessor::createInstance, this);

	PClassInfo2 controllerClass
	(
		AudioPlugSharpControllerUID,
		PClassInfo::kManyInstances,
		kVstComponentControllerClass,
		pluginNameChars,
		0,
		"",
		companyChars,
		pluginVersionChars,
		kVstVersionString
	);

	Marshal::FreeHGlobal((IntPtr)companyChars);
	Marshal::FreeHGlobal((IntPtr)websiteChars);
	Marshal::FreeHGlobal((IntPtr)contactChars);
	Marshal::FreeHGlobal((IntPtr)pluginNameChars);
	Marshal::FreeHGlobal((IntPtr)pluginCategoryChars);
	Marshal::FreeHGlobal((IntPtr)pluginVersionChars);

	registerClass(&controllerClass, AudioPlugSharpController::createInstance, this);
}
