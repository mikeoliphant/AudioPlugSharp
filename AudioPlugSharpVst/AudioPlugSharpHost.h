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
	virtual property unsigned int CurrentAudioBufferSize;
	virtual property AudioPlugSharp::EAudioBitsPerSample BitsPerSample;
	virtual property double BPM;
	virtual property bool IsPlaying;
	virtual property Int64 CurrentProjectSample;

	virtual void AudioPlugSharpHost::ProcessAllEvents()
	{
		int nextSample = 0;

		do
		{
			nextSample = ProcessEvents();
		}
		while (nextSample < processData->numSamples);
	}

	virtual int AudioPlugSharpHost::ProcessEvents()
	{
		int minSample = processData->numSamples;

		// Handle parameter changes
		if (processData->inputParameterChanges != nullptr)
		{
			int32 numParamsChanged = processData->inputParameterChanges->getParameterCount();

			for (int32 i = 0; i < numParamsChanged; i++)
			{
				IParamValueQueue* paramQueue = processData->inputParameterChanges->getParameterData(i);

				if (paramQueue != nullptr)
				{
					ParamValue value;
					int32 sampleOffset;
					int32 numPoints = paramQueue->getPointCount();

					ParamID paramID = paramQueue->getParameterId();
					
					AudioPlugSharp::AudioPluginParameter^ param = plugin->Processor->Parameters[paramID - PLUGIN_PARAMETER_USER_START];

					for (int32 i = startEvent; i < numPoints; i++)
					{
						if (paramQueue->getPoint(i, sampleOffset, value) == kResultTrue)
						{
							if (sampleOffset == 0)
							{
								plugin->Processor->HandleParameterChange(param, value, sampleOffset);
							}
							else if (sampleOffset > eventSampleOffset)
							{
								plugin->Processor->HandleParameterChange(param, value, sampleOffset);
								minSample = sampleOffset + 1;

								startEvent = i + 1;

								break;
							}
						}
					}
				}
			}
		}
			
		// Handle MIDI events
		if (processData->inputEvents != nullptr)
		{
			int32 numEvent = processData->inputEvents->getEventCount();

			for (int32 i = 0; i < numEvent; i++)
			{
				Event event;

				if (processData->inputEvents->getEvent(i, event) == kResultOk)
				{
					if (event.sampleOffset == eventSampleOffset)
					{
						switch (event.type)
						{
							case Event::kNoteOnEvent:
							{
								plugin->Processor->HandleNoteOn(event.noteOn.channel, event.noteOn.pitch, event.noteOn.velocity, event.sampleOffset);

								break;
							}
							case Event::kNoteOffEvent:
							{
								plugin->Processor->HandleNoteOff(event.noteOff.channel, event.noteOff.pitch, event.noteOff.velocity, event.sampleOffset);

								break;
							}
							case Event::kPolyPressureEvent:
								plugin->Processor->HandlePolyPressure(event.polyPressure.channel, event.polyPressure.pitch, event.polyPressure.pressure, event.sampleOffset);

								break;
							}
					}
					else if (event.sampleOffset > eventSampleOffset)
					{
						if (event.sampleOffset < minSample)
							minSample = event.sampleOffset;

						break;
					}
				}
			}
		}

		eventSampleOffset = minSample;

		return eventSampleOffset;
	}

	virtual void AudioPlugSharpHost::SendNoteOn(int channel, int noteNumber, float velocity, int sampleOffset)
	{
		Event event;

		event.type = Event::kNoteOnEvent;
		event.sampleOffset = sampleOffset;
		event.noteOn.channel = channel;
		event.noteOn.pitch = noteNumber;
		event.noteOn.velocity = velocity;

		processData->outputEvents->addEvent(event);
	}

	virtual void AudioPlugSharpHost::SendNoteOff(int channel, int noteNumber, float velocity, int sampleOffset)
	{
		Event event;

		event.type = Event::kNoteOffEvent;
		event.sampleOffset = sampleOffset;
		event.noteOff.channel = channel;
		event.noteOff.pitch = noteNumber;
		event.noteOff.velocity = velocity;

		processData->outputEvents->addEvent(event);
	}

	virtual void AudioPlugSharpHost::SendCC(int channel, int ccNumber, int ccValue, int sampleOffset)
	{
		Event event;

		event.type = Event::kLegacyMIDICCOutEvent;
		event.sampleOffset = sampleOffset;
		event.midiCCOut.channel = channel;
		event.midiCCOut.controlNumber = ccNumber;
		event.midiCCOut.value = ccValue;

		processData->outputEvents->addEvent(event);
	}

	virtual void AudioPlugSharpHost::SendPolyPressure(int channel, int noteNumber, float pressure, int sampleOffset)
	{
		Event event;

		event.type = Event::kNoteOnEvent;
		event.sampleOffset = sampleOffset;
		event.polyPressure.channel = channel;
		event.polyPressure.pitch = noteNumber;
		event.polyPressure.pressure = pressure;

		processData->outputEvents->addEvent(event);
	}

	virtual void AudioPlugSharpHost::BeginEdit(int parameter)
	{
		if (controller != nullptr)
			controller->beginEdit(parameter + PLUGIN_PARAMETER_USER_START);
	}

	virtual void AudioPlugSharpHost::PerformEdit(int parameter, double normalizedValue)
	{
		if (controller != nullptr)
			controller->performEdit(parameter + PLUGIN_PARAMETER_USER_START, (float)normalizedValue);
	}

	virtual void AudioPlugSharpHost::EndEdit(int parameter)
	{
		if (controller != nullptr)
			controller->endEdit(parameter + PLUGIN_PARAMETER_USER_START);
	}

internal:
	ProcessData* processData = nullptr;
	property AudioPlugSharp::IAudioPlugin^ plugin;
	AudioPlugSharpController* controller = nullptr;

	void SetProcessData(ProcessData* data)
	{
		eventSampleOffset = 0;
		startEvent = 0;
		processData = data;
		CurrentAudioBufferSize = data->numSamples;
		BPM = data->processContext->tempo;
		IsPlaying = (data->processContext->state & ProcessContext::kPlaying) != 0;
		CurrentProjectSample = data->processContext->projectTimeSamples;
	}

private:
	int eventSampleOffset = 0;
	int startEvent = 0;
		
};

