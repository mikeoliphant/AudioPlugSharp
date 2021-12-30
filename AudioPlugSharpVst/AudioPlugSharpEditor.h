#pragma once

#include "public.sdk/source/vst/vsteditcontroller.h"

class AudioPlugSharpController;

using namespace System;

using namespace Steinberg;
using namespace Steinberg::Vst;

#include <msclr/gcroot.h>
using namespace msclr;

using namespace AudioPlugSharp;

public class AudioPlugSharpEditor : public EditorView
{
public:
	AudioPlugSharpEditor(AudioPlugSharpController* controller, IAudioPlugin^ plugin);
	~AudioPlugSharpEditor(void);

	tresult PLUGIN_API isPlatformTypeSupported(FIDString type) SMTG_OVERRIDE;
	tresult PLUGIN_API canResize() SMTG_OVERRIDE { return kResultTrue; }
	tresult PLUGIN_API onSize(ViewRect* newSize) SMTG_OVERRIDE;
	void attachedToParent() SMTG_OVERRIDE;
	void removedFromParent() SMTG_OVERRIDE;
private:
	AudioPlugSharpController* controller = nullptr;
	gcroot<AudioPlugSharp::IAudioPlugin^> plugin;
};

