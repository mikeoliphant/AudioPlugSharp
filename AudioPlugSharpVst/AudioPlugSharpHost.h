#pragma once

#include "AudioPlugSharpController.h"
#include "public.sdk/source/vst/vstaudioprocessoralgo.h"
#include "public.sdk/source/vst/vsteditcontroller.h"
#include "public.sdk/source/vst/vstaudioeffect.h"

using namespace Steinberg;
using namespace Steinberg::Vst;

using namespace AudioPlugSharp;

public ref class AudioPlugSharpHost : public IAudioHost
{
public:
	// Inherited via IAudioHost
	virtual property double SampleRate;
	virtual property unsigned int MaxAudioBufferSize;
	virtual property AudioPlugSharp::EAudioBitsPerSample BitsPerSample;
	virtual property double BPM;

	AudioPlugSharpController* Controller = nullptr;

	virtual void AudioPlugSharpHost::SendNoteOn(int noteNumber, float velocity)
	{
		Event event;

		event.type = Event::kNoteOnEvent;
		event.noteOn.channel = 1;
		event.noteOn.pitch = noteNumber;
		event.noteOn.velocity = velocity;

		outputEventList->addEvent(event);
	}

	virtual void AudioPlugSharpHost::SendNoteOff(int noteNumber, float velocity)
	{
		Event event;

		event.type = Event::kNoteOffEvent;
		event.noteOff.channel = 1;
		event.noteOff.pitch = noteNumber;
		event.noteOff.velocity = velocity;

		outputEventList->addEvent(event);
	}

	virtual void AudioPlugSharpHost::SendPolyPressure(int noteNumber, float pressure)
	{
		Event event;

		event.type = Event::kNoteOnEvent;
		event.polyPressure.channel = 1;
		event.polyPressure.pitch = noteNumber;
		event.polyPressure.pressure = pressure;

		outputEventList->addEvent(event);
	}

	virtual void AudioPlugSharpHost::BeginEdit(int parameter)
	{
		//if (Controller == nullptr)
		//{
		//	Logger::Log("*** Controller is null!");
		//}

		Controller->beginEdit(parameter + PLUGIN_PARAMETER_USER_START);
	}

	virtual void AudioPlugSharpHost::PerformEdit(int parameter, double normalizedValue)
	{
		Controller->performEdit(parameter + PLUGIN_PARAMETER_USER_START, (float)normalizedValue);
	}

	virtual void AudioPlugSharpHost::EndEdit(int parameter)
	{
		Controller->endEdit(parameter + PLUGIN_PARAMETER_USER_START);
	}

internal:
	IEventList* outputEventList;
};

