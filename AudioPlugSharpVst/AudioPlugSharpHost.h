#pragma once

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

	virtual void AudioPlugSharpHost::SendNoteOn(int noteNumber, float velocity)
	{
		Event event;

		event.type = Event::kNoteOnEvent;
		event.noteOn.channel = 1;
		event.noteOn.pitch = noteNumber;
		event.noteOn.velocity = velocity;

		OutputEventList->addEvent(event);
	}

	virtual void AudioPlugSharpHost::SendNoteOff(int noteNumber, float velocity)
	{
		Event event;

		event.type = Event::kNoteOffEvent;
		event.noteOff.channel = 1;
		event.noteOff.pitch = noteNumber;
		event.noteOff.velocity = velocity;

		OutputEventList->addEvent(event);
	}

	virtual void AudioPlugSharpHost::SendPolyPressure(int noteNumber, float pressure)
	{
		Event event;

		event.type = Event::kNoteOnEvent;
		event.polyPressure.channel = 1;
		event.polyPressure.pitch = noteNumber;
		event.polyPressure.pressure = pressure;

		OutputEventList->addEvent(event);
	}
internal:
	IEventList* OutputEventList;
};

