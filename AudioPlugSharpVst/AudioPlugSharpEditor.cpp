#include "AudioPlugSharpEditor.h"
#include "AudioPlugSharpProcessor.h"
#include "AudioPlugSharpController.h";
#include "AudioPlugSharpFactory.h"

using namespace AudioPlugSharp;

AudioPlugSharpEditor::AudioPlugSharpEditor(AudioPlugSharpController* controller, IAudioPlugin^ plugin)
	: EditorView(controller, nullptr)
{
	this->controller = controller;
	this->plugin = plugin;

	rect.right = plugin->Editor->EditorWidth;
	rect.bottom = plugin->Editor->EditorHeight;
}

AudioPlugSharpEditor::~AudioPlugSharpEditor(void)
{
}

tresult PLUGIN_API AudioPlugSharpEditor::isPlatformTypeSupported(FIDString type)
{
	Logger::Log("IsPlatformSupported");

	if (strcmp(type, kPlatformTypeHWND) == 0)
	{
		Logger::Log("HWND supported");

		return kResultTrue;
	}

	Logger::Log("Not supported");

	return kResultFalse;
}

void AudioPlugSharpEditor::attachedToParent()
{
	Logger::Log("Attach");

	plugin->Editor->ShowEditor((IntPtr)systemWindow);
}

