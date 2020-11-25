#include "AudioPlugSharpFactory.h"
#include "AssemblyResolver.h"

#include "public.sdk/source/main/pluginfactory.h"

using namespace Steinberg;

IPluginFactory* PLUGIN_API GetPluginFactory()
{
	AssemblyResolver::RegisterResolver();

	AudioPlugSharp::Logger::Log("GetPluginFactory");

	//if (!gPluginFactory)
	//{
		try
		{
			gPluginFactory = new AudioPlugSharpFactory();
		}
		catch (Exception^ ex)
		{
			Logger::Log("Error creating plugin factory: " + ex->ToString());
		}
	//}

	return gPluginFactory;
}
