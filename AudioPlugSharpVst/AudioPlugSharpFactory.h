#pragma once

#include "AudioPlugSharp.h"
#include "AudioPlugSharpController.h"

#include "public.sdk/source/main/pluginfactory.h"

class AudioPlugSharpFactory : public CPluginFactory
{
public:
	AudioPlugSharpFactory(PFactoryInfo factoryInfo)
		: CPluginFactory(factoryInfo)
	{
	}
};

