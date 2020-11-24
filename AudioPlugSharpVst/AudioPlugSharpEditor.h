#pragma once

#include "public.sdk/source/vst/vsteditcontroller.h"

class AudioPlugSharpController;

using namespace Steinberg;
using namespace Steinberg::Vst;

using namespace System;

public class AudioPlugSharpEditor : public EditorView
{
public:
	AudioPlugSharpEditor(AudioPlugSharpController* controller);
	~AudioPlugSharpEditor(void);

	tresult PLUGIN_API isPlatformTypeSupported(FIDString type) SMTG_OVERRIDE;
	void attachedToParent() SMTG_OVERRIDE;
private:
	AudioPlugSharpController* controller = nullptr;
};

