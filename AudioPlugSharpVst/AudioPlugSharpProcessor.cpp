#include "public.sdk/source/vst/vstaudioprocessoralgo.h"

#include "pluginterfaces/vst/ivstevents.h"
#include "pluginterfaces/vst/ivstparameterchanges.h"
#include "pluginterfaces/base/ibstream.h"
#include "base/source/fstreamer.h"

#include <sstream>

#include "AudioPlugSharpProcessor.h"
#include "AudioPlugSharpController.h"
#include "AudioPlugSharpFactory.h"

using namespace System;
using namespace System::Collections::Generic;
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

FUnknown* AudioPlugSharpProcessor::createInstance(void* factory)
{
	Logger::Log("Create processor instance");

	AudioPlugSharpProcessor* processor = new AudioPlugSharpProcessor();
	processor->plugin = ((AudioPlugSharpFactory *)factory)->plugin;

	return (IAudioProcessor*)processor;
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
		audioPlugHost = gcnew AudioPlugSharpHost();

		plugin->Host = audioPlugHost;

		plugin->Processor->Initialize();
	}
	catch (Exception^ ex)
	{
		Logger::Log("Unable to initialize managed processor: " + ex->ToString());
	}

	// Add audio inputs
	for each (auto port in plugin->Processor->InputPorts)
	{
		TChar* portName = (TChar*)(void*)Marshal::StringToHGlobalUni(plugin->Company);

		addAudioInput(portName, port->ChannelConfiguration == EAudioChannelConfiguration::Mono ? SpeakerArr::kMono : SpeakerArr::kStereo);

		Marshal::FreeHGlobal((IntPtr)portName);
	}

	// Add audio outputs
	for each (auto port in plugin->Processor->OutputPorts)
	{
		TChar* portName = (TChar*)(void*)Marshal::StringToHGlobalUni(plugin->Company);

		addAudioOutput(portName, port->ChannelConfiguration == EAudioChannelConfiguration::Mono ? SpeakerArr::kMono : SpeakerArr::kStereo);

		Marshal::FreeHGlobal((IntPtr)portName);
	}

	// Set up an event intput
	addEventInput(STR16("Event In"), 1);

	// Set up an event output
	addEventOutput(STR16("Event Out"), 1);

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
		plugin->Processor->Start();
	}
	else
	{
		plugin->Processor->Stop();
	}

	return AudioEffect::setActive(state);
}

tresult PLUGIN_API AudioPlugSharpProcessor::setState(IBStream* state)
{
	Logger::Log("Restore State");

	if (state != nullptr)
	{
		std::stringstream stringStream;

		char readBuf[1024];

		int32 numRead;

		do
		{
			state->read(readBuf, 1024, &numRead);

			stringStream.write(readBuf, numRead);
		} while (numRead == 1024);

		std::string dataString = stringStream.str();

		array<Byte>^ byteArray = gcnew array<Byte>(dataString.size());

		Marshal::Copy((IntPtr)&dataString[0], byteArray, 0, byteArray->Length);

		plugin->Processor->RestoreState(byteArray);
	}

	return kResultOk;
}

tresult PLUGIN_API AudioPlugSharpProcessor::getState(IBStream* state)
{
	Logger::Log("Save State");

	auto data = plugin->Processor->SaveState();

	if (data != nullptr)
	{
		unsigned char* dataChars = new unsigned char[data->Length];

		Marshal::Copy(data, 0, (IntPtr)dataChars, data->Length);

		state->write(dataChars, data->Length, 0);

		delete[] dataChars;
	}

	return kResultOk;
}

tresult PLUGIN_API AudioPlugSharpProcessor::canProcessSampleSize(int32 symbolicSampleSize)
{
	if (symbolicSampleSize == kSample32)
	{
		if ((plugin->Processor->SampleFormatsSupported & EAudioBitsPerSample::Bits32) == EAudioBitsPerSample::Bits32)
		{
			return kResultTrue;
		}
	}

	if (symbolicSampleSize == kSample64)
	{
		if ((plugin->Processor->SampleFormatsSupported & EAudioBitsPerSample::Bits64) == EAudioBitsPerSample::Bits64)
		{
			return kResultTrue;
		}
	}

	return kResultFalse;
}

tresult PLUGIN_API AudioPlugSharpProcessor::setBusArrangements(SpeakerArrangement* inputs, int32 numIns, SpeakerArrangement* outputs, int32 numOuts)
{
	// We should be ok with any arrangement

	return kResultOk;
}

tresult PLUGIN_API AudioPlugSharpProcessor::notify(Vst::IMessage* message)
{
	Logger::Log("Got message from controller");

	if (message != nullptr)
	{
		Steinberg::int64 value = 0;

		if (message->getAttributes()->getInt("AudioPlugSharpControllerPtr", value) == kResultTrue)
		{
			Logger::Log("Got controller pointer");

			controller = (AudioPlugSharpController*)value;

			controller->setProcessor(this, plugin);

			audioPlugHost->Controller = controller;
		}
	}

	return kResultTrue;
}


tresult PLUGIN_API AudioPlugSharpProcessor::setupProcessing(ProcessSetup& newSetup)
{
	Logger::Log("Setup Processing. " + ((newSetup.symbolicSampleSize == kSample32) ? "32bit" : "64bit"));

	tresult result = AudioEffect::setupProcessing(newSetup);

	if (result != kResultOk)
	{
		Logger::Log("Setup processing failed");

		return result;
	}

	audioPlugHost->SampleRate = newSetup.sampleRate;
	audioPlugHost->BitsPerSample = (newSetup.symbolicSampleSize == kSample32) ? EAudioBitsPerSample::Bits32 : EAudioBitsPerSample::Bits64;
	audioPlugHost->MaxAudioBufferSize = newSetup.maxSamplesPerBlock;

	plugin->Processor->InitializeProcessing();

	return kResultOk;
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

				ParamID paramID = paramQueue->getParameterId();

				// Only getting the last value - probably should get them all and pass them on with sample offsets...
				if (paramQueue->getPoint(numPoints - 1, sampleOffset, value) ==	kResultTrue)
				{					
					//Logger::Log("*** processor set param: " + paramID + " to " + value);

					plugin->Processor->Parameters[paramID - PLUGIN_PARAMETER_USER_START]->NormalizedValue = value;
				}
			}
		}
	}

	// Handle MIDI events
	IEventList* eventList = data.inputEvents;
	audioPlugHost->outputEventList = data.outputEvents;

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
					case Event::kNoteOnEvent:
					{
						plugin->Processor->HandleNoteOn(event.noteOn.pitch, event.noteOn.velocity, event.sampleOffset);

						break;
					}
					case Event::kNoteOffEvent:
					{
						plugin->Processor->HandleNoteOff(event.noteOff.pitch, event.noteOff.velocity, event.sampleOffset);

						break;
					}
					case Event::kPolyPressureEvent:
						plugin->Processor->HandlePolyPressure(event.polyPressure.pitch, event.polyPressure.pressure, event.sampleOffset);

						break;
				}
			}
		}
	}

	if ((data.numInputs == 0) && (data.numOutputs == 0))
	{
		// The host is just flushing events without sending audio data
	}
	else
	{
		for (int input = 0; input < plugin->Processor->InputPorts->Length; input++)
		{
			plugin->Processor->InputPorts[input]->SetAudioBufferPtrs((IntPtr)getChannelBuffersPointer(processSetup, data.inputs[input]),
				(data.symbolicSampleSize == kSample32) ? EAudioBitsPerSample::Bits32 : EAudioBitsPerSample::Bits64, data.numSamples);
		}

		for (int output = 0; output < plugin->Processor->OutputPorts->Length; output++)
		{
			plugin->Processor->OutputPorts[output]->SetAudioBufferPtrs((IntPtr)getChannelBuffersPointer(processSetup, data.outputs[output]),
				(data.symbolicSampleSize == kSample32) ? EAudioBitsPerSample::Bits32 : EAudioBitsPerSample::Bits64, data.numSamples);
		}

		plugin->Processor->Process();
	}

	// Handle any output parameter changes (such as volume meter output)
	// We don't have any
	IParameterChanges* outParamChanges = data.outputParameterChanges;

	return kResultOk;
}
