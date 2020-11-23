#pragma once

#include "public.sdk/source/vst/vsteditcontroller.h"
#include "public.sdk/source/vst/vstaudioeffect.h"
#include "base/source/fstring.h"
#include "pluginterfaces/base/funknown.h"

#include <msclr/gcroot.h>

using namespace msclr;

using namespace Steinberg;
using namespace Steinberg::Vst;

using namespace AudioPlugSharp;

#define RESERVED_PARAMCOUNT 16

class AudioPlugSharpProcessor : public AudioEffect
{
public:
	AudioPlugSharpProcessor(void);

	static FUID AudioPlugSharpProcessorUID;

	static FUnknown* createInstance(void* context)
	{
		return (IAudioProcessor*) new AudioPlugSharpProcessor();
	}

	tresult PLUGIN_API initialize(FUnknown* context);
	tresult PLUGIN_API terminate();
	tresult PLUGIN_API setActive(TBool state);
	tresult PLUGIN_API setupProcessing(ProcessSetup& newSetup);
	tresult PLUGIN_API process(ProcessData& data);
	tresult PLUGIN_API setState(IBStream* state);
	tresult PLUGIN_API getState(IBStream* state);
	tresult PLUGIN_API canProcessSampleSize(int32 symbolicSampleSize) SMTG_OVERRIDE;
	tresult PLUGIN_API setBusArrangements(SpeakerArrangement* inputs, int32 numIns, SpeakerArrangement* outputs, int32 numOuts);
	~AudioPlugSharpProcessor(void);

private:
};
