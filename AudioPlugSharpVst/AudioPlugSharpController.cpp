#include "pluginterfaces/base/ibstream.h"
#include "base/source/fstreamer.h"

#include "AudioPlugSharpProcessor.h"
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

FUnknown* AudioPlugSharpController::createInstance(void* factory)
{
	Logger::Log("Create controller instance");

	AudioPlugSharpController* controller = new AudioPlugSharpController();
	controller->plugin = ((AudioPlugSharpFactory*)factory)->plugin;

	return (IAudioProcessor*)controller;
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

tresult PLUGIN_API AudioPlugSharpController::setParamNormalized(ParamID tag, ParamValue value)
{
	plugin->Processor->Parameters[tag - RESERVED_PARAMCOUNT]->NormalizedValue = value;

	Logger::Log("Host set param");

	return kResultOk;
}

ParamValue PLUGIN_API AudioPlugSharpController::getParamNormalized(ParamID tag)
{
	return plugin->Processor->Parameters[tag - RESERVED_PARAMCOUNT]->NormalizedValue;
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
	Logger::Log("Controller setComponentState");

	if (!state)
		return kResultFalse;

	//IBStreamer streamer(state, kLittleEndian);

	//float savedGain = 0.f;
	//if (streamer.readFloat(savedGain) == false)
	//	return kResultFalse;

	//setParamNormalized(kGainId, savedGain);

	return kResultOk;
}

void AudioPlugSharpController::sendIntMessage(const char* idTag, const Steinberg::int64 value)
{
	if (auto* message = allocateMessage())
	{
		const FReleaser releaser(message);

		message->setMessageID(idTag);
		message->getAttributes()->setInt(idTag, value);

		sendMessage(message);
	}
}

tresult PLUGIN_API AudioPlugSharpController::connect(IConnectionPoint* other)
{
	tresult result = EditController::connect(other);

	Logger::Log("Connect controller to processor");

	sendIntMessage("AudioPlugSharpControllerPtr", (Steinberg::int64)this);

	return result;
}

void AudioPlugSharpController::setProcessor(AudioPlugSharpProcessor* processor, IAudioPlugin^ plugin)
{
	this->processor = processor;
	this->plugin = plugin;
}



IPlugView* PLUGIN_API AudioPlugSharpController::createView(const char* name)
{
	if (!plugin->Editor->HasUserInterface)
	{
		return nullptr;
	}

	editorView = new AudioPlugSharpEditor(this, plugin);

	return editorView;
}

