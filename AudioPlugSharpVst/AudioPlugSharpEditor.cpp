#include "AudioPlugSharpEditor.h"
#include "AudioPlugSharp.h"
#include "AudioPlugSharpController.h";
#include "AudioPlugSharpFactory.h"

using namespace AudioPlugSharp;

AudioPlugSharpEditor::AudioPlugSharpEditor(AudioPlugSharpController* controller)
	: EditorView(controller, nullptr)
{
	this->controller = controller;

	rect.right = 860;
	rect.bottom = 450;
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

