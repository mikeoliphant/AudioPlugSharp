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

	double scale = plugin->Editor->GetDpiScale();

	rect.right = plugin->Editor->EditorWidth * scale;
	rect.bottom = plugin->Editor->EditorHeight * scale;
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

tresult PLUGIN_API AudioPlugSharpEditor::onSize(ViewRect* newSize)
{
	if (newSize)
		rect = *newSize;

	double scale = plugin->Editor->GetDpiScale();

	plugin->Editor->ResizeEditor(newSize->getWidth() / scale, newSize->getHeight() / scale);

	return kResultTrue;
}

void AudioPlugSharpEditor::attachedToParent()
{
	Logger::Log("Attach");

	plugin->Editor->ShowEditor((IntPtr)systemWindow);
}

