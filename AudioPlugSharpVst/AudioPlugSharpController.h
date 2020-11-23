#pragma once

#include "public.sdk/source/vst/vsteditcontroller.h"

using namespace Steinberg;
using namespace Steinberg::Vst;

class AudioPlugSharpController : public EditController
{
public:
	AudioPlugSharpController(void);

	static FUID AudioPlugSharpControllerUID;

	static FUnknown* createInstance(void* context)
	{
		return (IEditController*) new AudioPlugSharpController;
	}

	tresult PLUGIN_API initialize(FUnknown* context);
	tresult PLUGIN_API terminate();

	tresult PLUGIN_API setComponentState(IBStream* state);

	tresult PLUGIN_API getParamStringByValue(ParamID tag, ParamValue valueNormalized, String128 string);

	// Uncomment to add a GUI
	// IPlugView * PLUGIN_API createView (const char * name);

	// Uncomment to override default EditController behavior
	// tresult PLUGIN_API setState(IBStream* state);
	// tresult PLUGIN_API getState(IBStream* state);
	// tresult PLUGIN_API setParamNormalized(ParamID tag, ParamValue value);
	// tresult PLUGIN_API getParamValueByString(ParamID tag, TChar* string, ParamValue& valueNormalized);

	~AudioPlugSharpController(void);

private:

};

