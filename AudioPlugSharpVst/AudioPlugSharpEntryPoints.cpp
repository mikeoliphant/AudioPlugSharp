#include "AudioPlugSharpFactory.h"

#include "public.sdk/source/main/pluginfactory.h"

using namespace Steinberg;

IPluginFactory* PLUGIN_API GetPluginFactory()
{
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
