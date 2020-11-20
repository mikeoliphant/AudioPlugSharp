#include "public.sdk/source/vst/vstaudioprocessoralgo.h"

#include "pluginterfaces/vst/ivstevents.h"
#include "pluginterfaces/vst/ivstparameterchanges.h"
#include "pluginterfaces/base/ibstream.h"
#include "base/source/fstreamer.h"

#include "AudioPlugSharp.h"
#include "AudioPlugSharpController.h"

using namespace AudioPlugSharp;

FUID AudioPlugSharpProcessor::AudioPlugSharpProcessorUID;

AudioPlugSharpProcessor::AudioPlugSharpProcessor(void)
{
	setControllerClass(FUID(AudioPlugSharpController::AudioPlugSharpControllerUID));
}

AudioPlugSharpProcessor::~AudioPlugSharpProcessor(void)
{
}

tresult PLUGIN_API AudioPlugSharpProcessor::initialize(FUnknown* context)
{
	tresult result = AudioEffect::initialize(context);

	if (result != kResultOk)
	{
		return result;
	}

	// Mono in/out
	addAudioInput(STR16("Mono In"), SpeakerArr::kMono);
	addAudioOutput(STR16("Mono Out"), SpeakerArr::kMono);

	// Set up a MIDI event intput as an example, even though we aren't going to use it
	addEventInput(STR16("Event In"), 1);

	return kResultOk;
}

tresult PLUGIN_API AudioPlugSharpProcessor::terminate()
{
	return AudioEffect::terminate();
}

tresult PLUGIN_API AudioPlugSharpProcessor::setActive(TBool state)
{
	return AudioEffect::setActive(state);
}

tresult PLUGIN_API AudioPlugSharpProcessor::setState(IBStream* state)
{
	IBStreamer streamer(state, kLittleEndian);

	float savedGain = 0;
	if (streamer.readFloat(savedGain) == false)
		return kResultFalse;

	gain = savedGain;

	return kResultOk;
}

tresult PLUGIN_API AudioPlugSharpProcessor::getState(IBStream* state)
{
	IBStreamer streamer(state, kLittleEndian);

	streamer.writeFloat(gain);

	return kResultOk;
}

tresult PLUGIN_API AudioPlugSharpProcessor::setBusArrangements(SpeakerArrangement* inputs, int32 numIns, SpeakerArrangement* outputs, int32 numOuts)
{
	// We should be ok with any arrangement

	return kResultOk;
}

tresult PLUGIN_API AudioPlugSharpProcessor::setupProcessing(ProcessSetup& newSetup)
{
	return AudioEffect::setupProcessing(newSetup);
}

tresult PLUGIN_API AudioPlugSharpProcessor::process(ProcessData& data)
{
	IParameterChanges* paramChanges = data.inputParameterChanges;

	// Handle parameter changes
	if (paramChanges)
	{
		int32 numParamsChanged = paramChanges->getParameterCount();

		for (int32 i = 0; i < numParamsChanged; i++)
		{
			IParamValueQueue* paramQueue = paramChanges->getParameterData(i);

			if (paramQueue)
			{
				ParamValue value;
				int32 sampleOffset;
				int32 numPoints = paramQueue->getPointCount();

				switch (paramQueue->getParameterId())
				{
					case (kGainId):
						// There could be multiple changes to the parameter - we only care about the last one
						if (paramQueue->getPoint(numPoints - 1, sampleOffset, value) ==	kResultTrue)
						{
							gain = (float)value;
						}
						break;
				}
			}
		}
	}

	// Handle MIDI events
	IEventList* eventList = data.inputEvents;

	if (eventList)
	{
		int32 numEvent = eventList->getEventCount();
		for (int32 i = 0; i < numEvent; i++)
		{
			Event event;
			if (eventList->getEvent(i, event) == kResultOk)
			{
				switch (event.type)
				{
					// case statements for handling MIDI events go here
				}
			}
		}
	}

	if (data.numInputs == 0 || data.numOutputs == 0)
	{
		return kResultOk;
	}

	// We've said we're mono, but...
	uint32 numChannels = data.inputs[0].numChannels;

	uint32 sampleFramesSize = getSampleFramesSizeInBytes(processSetup, data.numSamples);
	void** in = getChannelBuffersPointer(processSetup, data.inputs[0]);
	void** out = getChannelBuffersPointer(processSetup, data.outputs[0]);

	if (data.inputs[0].silenceFlags != 0)
	{
		// Propogate silence from input to output
		data.outputs[0].silenceFlags = data.inputs[0].silenceFlags;

		for (int32 i = 0; i < numChannels; i++)
		{
			// Clear the output, if it doesn't point to the same buffer as the input
			if (in[i] != out[i])
			{
				memset(out[i], 0, sampleFramesSize);
			}
		}

		return kResultOk;
	}
	
	// We are silent if our gain is zero
	if (gain == 0)
	{
		// Set silence flags
		data.outputs[0].silenceFlags = (uint64(1) << numChannels) - 1;

		// Clear the output
		for (int32 i = 0; i < numChannels; i++)
		{
			memset(out[i], 0, sampleFramesSize);
		}

		return kResultOk;
	}

	data.outputs[0].silenceFlags = 0;

	// Apply the gain
	for (int32 i = 0; i < numChannels; i++)
	{
		if (data.symbolicSampleSize == kSample32)
		{
			Sample32* inSamples = ((Sample32**)in)[i];
			Sample32* outSamples = ((Sample32**)out)[i];

			for (uint32 sample = 0; sample < data.numSamples; sample++)
			{
				outSamples[sample] = inSamples[sample] * gain;
			}
		}
		else
		{
			Sample64* inSamples = ((Sample64**)in)[i];
			Sample64* outSamples = ((Sample64**)out)[i];

			for (uint32 sample = 0; sample < data.numSamples; sample++)
			{
				outSamples[sample] = inSamples[sample] * gain;
			}
		}
	}

	// Handle any output parameter changes (such as volume meter output)
	// We don't have any
	IParameterChanges* outParamChanges = data.outputParameterChanges;

	return kResultOk;
}
