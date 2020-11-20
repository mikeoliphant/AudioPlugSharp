#include "pluginterfaces/base/ibstream.h"
#include "base/source/fstreamer.h"

#include "AudioPlugSharp.h"
#include "AudioPlugSharpController.h"

FUID AudioPlugSharpController::AudioPlugSharpControllerUID;

AudioPlugSharpController::AudioPlugSharpController(void)
{
}

AudioPlugSharpController::~AudioPlugSharpController(void)
{
}

tresult PLUGIN_API AudioPlugSharpController::initialize(FUnknown* context)
{
	tresult result = EditController::initialize(context);

	if (result != kResultOk)
	{
		return result;
	}

	parameters.addParameter(STR16("Gain"), nullptr, 0, 0.5, ParameterInfo::kCanAutomate, kGainId);

	return result;
}

tresult PLUGIN_API AudioPlugSharpController::terminate()
{
	return EditController::terminate();
}

tresult PLUGIN_API AudioPlugSharpController::setComponentState(IBStream* state)
{
	if (!state)
		return kResultFalse;

	IBStreamer streamer(state, kLittleEndian);

	float savedGain = 0.f;
	if (streamer.readFloat(savedGain) == false)
		return kResultFalse;

	setParamNormalized(kGainId, savedGain);

	return kResultOk;
}
