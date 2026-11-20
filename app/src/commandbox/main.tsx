// OmniWin: the small command box window (its hotkey, the tray item and the voice command open it).
import React from "react";
import ReactDOM from "react-dom/client";
import { listen } from "@tauri-apps/api/event";
import { platform } from "@tauri-apps/plugin-os";
import CommandBox from "./CommandBox";
import {
  applyTheme,
  getStoredTheme,
  syncThemeFromSettings,
} from "@/lib/utils/theme";
import type { Theme } from "@/bindings";
import "@/i18n";
import "../App.css";

// A separate webview from the settings window: it sets its own theme and follows changes.
document.documentElement.dataset.platform = platform();
applyTheme(getStoredTheme());
syncThemeFromSettings();
listen<Theme>("theme-changed", (event) => applyTheme(event.payload));

if (import.meta.env.PROD) {
  window.addEventListener("contextmenu", (e) => e.preventDefault());
}

ReactDOM.createRoot(document.getElementById("root") as HTMLElement).render(
  <React.StrictMode>
    <CommandBox />
  </React.StrictMode>,
);
