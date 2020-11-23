#pragma once

#include "AudioPlugSharp.h"
#include "AudioPlugSharpController.h"

#include "public.sdk/source/main/pluginfactory.h"

#include <msclr/gcroot.h>

#define AUDIO_PLUG_SHARP_ID 0xB0FA8B5C
#define AUDIO_PLUG_SHARP_PROCESSOR_ID 0xC4B5BA02
#define AUDIO_PLUG_SHARP_CONTROLLER_ID 0xD0411FA8

using namespace msclr;

extern gcroot<AudioPlugSharp::IAudioPlugin^> plugin;

class AudioPlugSharpFactory : public CPluginFactory
{
public:
	AudioPlugSharpFactory();
};

