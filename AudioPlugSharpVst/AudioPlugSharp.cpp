#include "public.sdk/source/vst/vstaudioprocessoralgo.h"

#include "pluginterfaces/vst/ivstevents.h"
#include "pluginterfaces/vst/ivstparameterchanges.h"
#include "pluginterfaces/base/ibstream.h"
#include "base/source/fstreamer.h"

#include "AudioPlugSharp.h"
#include "AudioPlugSharpController.h"
#include "AudioPlugSharpFactory.h"

using namespace System;
using namespace System::Runtime::InteropServices;

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

	try
	{
		managedProcessor = plugin->CreateProcessor();
		managedProcessor->Initialize();
	}
	catch (Exception^ ex)
	{
		Logger::Log("Unable to create managed processor: " + ex->ToString());
	}

	// Add audio inputs
	for each (auto port in managedProcessor->InputPorts)
	{
		TChar* portName = (TChar*)(void*)Marshal::StringToHGlobalUni(plugin->Company);

		addAudioInput(portName, port->ChannelConfiguration == EAudioChannelConfiguration::Mono ? SpeakerArr::kMono : SpeakerArr::kStereo);

		Marshal::FreeHGlobal((IntPtr)portName);
	}

	// Add audio outputs
	for each (auto port in managedProcessor->OutputPorts)
	{
		TChar* portName = (TChar*)(void*)Marshal::StringToHGlobalUni(plugin->Company);

		addAudioOutput(portName, port->ChannelConfiguration == EAudioChannelConfiguration::Mono ? SpeakerArr::kMono : SpeakerArr::kStereo);

		Marshal::FreeHGlobal((IntPtr)portName);
	}

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
	if (state)
	{
		managedProcessor->Start();
	}
	else
	{
		managedProcessor->Stop();
	}

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

tresult PLUGIN_API AudioPlugSharpProcessor::canProcessSampleSize(int32 symbolicSampleSize)
{
	return kResultTrue;
}

tresult PLUGIN_API AudioPlugSharpProcessor::setBusArrangements(SpeakerArrangement* inputs, int32 numIns, SpeakerArrangement* outputs, int32 numOuts)
{
	// We should be ok with any arrangement

	return kResultOk;
}

tresult PLUGIN_API AudioPlugSharpProcessor::setupProcessing(ProcessSetup& newSetup)
{
	Logger::Log("Setup Processing. " + (newSetup.symbolicSampleSize == kSample32) ? "32bit" : "64bit");

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

	for (int input = 0; input < managedProcessor->InputPorts->Length; input++)
	{
		managedProcessor->InputPorts[input]->SetAudioBufferPtrs((IntPtr)getChannelBuffersPointer(processSetup, data.inputs[input]),
			(data.symbolicSampleSize == kSample32) ? EAudioBitsPerSample::Bits32 : EAudioBitsPerSample::Bits64, data.numSamples);
	}

	for (int output = 0; output < managedProcessor->OutputPorts->Length; output++)
	{
		managedProcessor->OutputPorts[output]->SetAudioBufferPtrs((IntPtr)getChannelBuffersPointer(processSetup, data.outputs[output]),
			(data.symbolicSampleSize == kSample32) ? EAudioBitsPerSample::Bits32 : EAudioBitsPerSample::Bits64, data.numSamples);
	}

	managedProcessor->Process();

	// Handle any output parameter changes (such as volume meter output)
	// We don't have any
	IParameterChanges* outParamChanges = data.outputParameterChanges;

	return kResultOk;
}
