#pragma once

#include "public.sdk/source/vst/vsteditcontroller.h"

#include "AudioPlugSharpEditor.h"

using namespace Steinberg;
using namespace Steinberg::Vst;

#include <msclr/gcroot.h>
using namespace msclr;

#define PLUGIN_PARAMETER_USER_START 128

class AudioPlugSharpProcessor;

class AudioPlugSharpController : public EditController, public IMidiMapping
{
public:
	AudioPlugSharpController(void);

	static FUnknown* createInstance(void* factory);

	tresult PLUGIN_API initialize(FUnknown* context) SMTG_OVERRIDE;
	tresult PLUGIN_API terminate() SMTG_OVERRIDE;

	tresult PLUGIN_API setComponentState(IBStream* state) SMTG_OVERRIDE;

	tresult PLUGIN_API getParamStringByValue(ParamID tag, ParamValue valueNormalized, String128 string) SMTG_OVERRIDE;
	tresult PLUGIN_API setParamNormalized(ParamID tag, ParamValue value) SMTG_OVERRIDE;
	ParamValue PLUGIN_API getParamNormalized(ParamID tag) SMTG_OVERRIDE;

	// Uncomment to add a GUI
	 IPlugView * PLUGIN_API createView (const char * name) SMTG_OVERRIDE;

	 tresult PLUGIN_API getMidiControllerAssignment(int32 busIndex, int16 channel, CtrlNumber midiControllerNumber, ParamID& tag/*out*/) SMTG_OVERRIDE;

	// Uncomment to override default EditController behavior
	// tresult PLUGIN_API setState(IBStream* state) SMTG_OVERRIDE;
	// tresult PLUGIN_API getState(IBStream* state) SMTG_OVERRIDE;
	// tresult PLUGIN_API getParamValueByString(ParamID tag, TChar* string, ParamValue& valueNormalized) SMTG_OVERRIDE;

	 tresult PLUGIN_API connect(IConnectionPoint* other) SMTG_OVERRIDE;

	 void sendIntMessage(const char* idTag, const Steinberg::int64 value);
	 void setProcessor(AudioPlugSharpProcessor* processor, IAudioPlugin^ plugin);

	~AudioPlugSharpController(void);

	DELEGATE_REFCOUNT(EditController)
		tresult PLUGIN_API queryInterface(const char* iid, void** obj) SMTG_OVERRIDE;

private:
	AudioPlugSharpProcessor* processor = nullptr;
	AudioPlugSharpEditor* editorView = nullptr;
	gcroot<AudioPlugSharp::IAudioPlugin^> plugin;
};

