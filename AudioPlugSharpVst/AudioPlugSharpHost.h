#pragma once

using namespace AudioPlugSharp;

public ref class AudioPlugSharpHost : public IAudioHost
{
public:

	// Inherited via IAudioHost
	virtual property double SampleRate;
	virtual property unsigned int MaxAudioBufferSize;
	virtual property AudioPlugSharp::EAudioBitsPerSample BitsPerSample;
	virtual property double BPM;
};

