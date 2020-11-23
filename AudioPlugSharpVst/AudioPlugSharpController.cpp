#include "pluginterfaces/base/ibstream.h"
#include "base/source/fstreamer.h"

#include "AudioPlugSharp.h"
#include "AudioPlugSharpController.h"
#include "AudioPlugSharpFactory.h"

FUID AudioPlugSharpController::AudioPlugSharpControllerUID;

using namespace System;
using namespace System::Runtime::InteropServices;

AudioPlugSharpController::AudioPlugSharpController(void)
{
}

AudioPlugSharpController::~AudioPlugSharpController(void)
{
}

tresult PLUGIN_API AudioPlugSharpController::initialize(FUnknown* context)
{
	tresult result = EditController::initialize(context);

	if (result != kResultOk)
	{
		return result;
	}

	try
	{
		plugin->Editor->InitializeEditor();
	}
	catch (Exception^ ex)
	{
		Logger::Log("Unable to initialize managed editor: " + ex->ToString());
	}

	uint16 paramID = RESERVED_PARAMCOUNT;

	for each (auto parameter in plugin->Processor->Parameters)
	{
		Logger::Log("Registering parameter: " + parameter->Name);

		TChar* paramName = (TChar*)(void*)Marshal::StringToHGlobalUni(parameter->Name);

		parameters.addParameter(paramName, nullptr, 0, parameter->GetValueNormalized(parameter->DefaultValue), ParameterInfo::kCanAutomate, paramID);

		Marshal::FreeHGlobal((IntPtr)paramName);

		paramID++;
	}

	//parameters.addParameter(STR16("Gain"), nullptr, 0, 0.5, ParameterInfo::kCanAutomate, kGainId);

	return result;
}

tresult PLUGIN_API AudioPlugSharpController::terminate()
{
	return EditController::terminate();
}

tresult PLUGIN_API AudioPlugSharpController::getParamStringByValue(ParamID tag, ParamValue valueNormalized, String128 string)
{
	TChar* paramStr = (TChar*)(void*)Marshal::StringToHGlobalUni(plugin->Processor->Parameters[tag - RESERVED_PARAMCOUNT]->DisplayValue);

	strcpy16(string, paramStr);

	Marshal::FreeHGlobal((IntPtr)paramStr);

	return kResultOk;
}

tresult PLUGIN_API AudioPlugSharpController::setComponentState(IBStream* state)
{
	if (!state)
		return kResultFalse;

	//IBStreamer streamer(state, kLittleEndian);

	//float savedGain = 0.f;
	//if (streamer.readFloat(savedGain) == false)
	//	return kResultFalse;

	//setParamNormalized(kGainId, savedGain);

	return kResultOk;
}
