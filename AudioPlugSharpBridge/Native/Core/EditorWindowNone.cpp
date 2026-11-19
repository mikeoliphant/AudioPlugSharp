// EditorWindowNone.cpp - aps::EditorWindow on platforms without an implementation yet (X11 is next): no
// editors, so the adapters report no GUI support.
#include "aps/EditorWindow.h"

namespace aps {

std::unique_ptr<EditorWindow> EditorWindow::create(intptr_t, uint32_t, uint32_t, Client&) { return nullptr; }

EditorWindowType EditorWindow::platformWindowType() { return EditorWindowType::None; }

double EditorWindow::platformScale(intptr_t) { return 1.0; }

} // namespace aps
