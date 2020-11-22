#pragma once

#include "AudioPlugSharp.h"
#include "AudioPlugSharpController.h"

#include "public.sdk/source/main/pluginfactory.h"

#include <msclr/gcroot.h>

using namespace msclr;

extern gcroot<AudioPlugSharp::IAudioPlugin^> plugin;

class AudioPlugSharpFactory : public CPluginFactory
{
public:
	AudioPlugSharpFactory();
};

