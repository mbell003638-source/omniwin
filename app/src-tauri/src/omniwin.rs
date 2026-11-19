//! OmniWin (fork of Handy): the app's own copy of `OmniWin.Server.exe`, spoken to over MCP (JSON-RPC lines on stdin/stdout)
//! exactly as an AI client does. All automation, every safety check and the stop key live in that server; this file only
//! carries requests to it. The app may call only the tools in `ALLOWED_TOOLS`.

use serde_json::{json, Value};
use std::io::{BufRead, BufReader, Write};
use std::path::{Path, PathBuf};
use std::process::{Child, ChildStdin, ChildStdout, Command, Stdio};
use std::sync::Mutex;
use std::time::Duration;
use tauri::{AppHandle, Emitter, Manager};

#[cfg(windows)]
use std::os::windows::process::CommandExt;

/// The OmniWin tools the app uses. Anything else is refused before it reaches the server.
pub const ALLOWED_TOOLS: &[&str] = &[
    "omniwin_do",
    "omniwin_status",
    "task_done",
    "workflow_list",
    "workflow_manage",
    "selection_get",
    "selection_replace",
    "text_insert",
    "polish_prompts",
];

struct Link {
    child: Child,
    stdin: ChildStdin,
    stdout: BufReader<ChildStdout>,
    next_id: u64,
}

impl Drop for Link {
    fn drop(&mut self) {
        // Closing stdin makes the server exit by itself; it is killed only if it does not.
        let _ = self.stdin.flush();
        for _ in 0..50 {
            if let Ok(Some(_)) = self.child.try_wait() {
                return;
            }
            std::thread::sleep(Duration::from_millis(100));
        }
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

#[derive(Default)]
pub struct OmniWin {
    link: Mutex<Option<Link>>,
}

/// Where `OmniWin.Server.exe` is: `OMNIWIN_SERVER` if set; else beside this app; else a `publish` folder above it.
pub fn server_path() -> Option<PathBuf> {
    if let Ok(path) = std::env::var("OMNIWIN_SERVER") {
        let path = PathBuf::from(path);
        if path.is_file() {
            return Some(path);
        }
    }
    let exe = std::env::current_exe().ok()?;
    let beside = exe.parent()?.join("OmniWin.Server.exe");
    if beside.is_file() {
        return Some(beside);
    }
    let mut dir = exe.parent();
    while let Some(d) = dir {
        let published = d.join("publish").join("OmniWin.Server.exe");
        if published.is_file() {
            return Some(published);
        }
        dir = d.parent();
    }
    None
}

fn request(link: &mut Link, method: &str, params: Value) -> Result<Value, String> {
    link.next_id += 1;
    let id = link.next_id;
    let line = json!({ "jsonrpc": "2.0", "id": id, "method": method, "params": params }).to_string();
    writeln!(link.stdin, "{line}").map_err(|e| format!("OmniWin did not accept the request: {e}"))?;
    link.stdin.flush().map_err(|e| format!("OmniWin did not accept the request: {e}"))?;
    let mut buf = String::new();
    loop {
        buf.clear();
        let read = link
            .stdout
            .read_line(&mut buf)
            .map_err(|e| format!("OmniWin stopped answering: {e}"))?;
        if read == 0 {
            return Err("The OmniWin server closed its output.".to_string());
        }
        let Ok(reply) = serde_json::from_str::<Value>(buf.trim()) else {
            continue;
        };
        if reply.get("id").and_then(Value::as_u64) == Some(id) {
            return Ok(reply);
        }
    }
}

fn start() -> Result<Link, String> {
    let exe = server_path().ok_or_else(|| {
        "OmniWin.Server.exe was not found beside the app or in a publish folder above it (or set OMNIWIN_SERVER).".to_string()
    })?;
    let mut command = Command::new(&exe);
    command.stdin(Stdio::piped()).stdout(Stdio::piped()).stderr(Stdio::null());
    #[cfg(windows)]
    command.creation_flags(0x0800_0000); // CREATE_NO_WINDOW
    let mut child = command
        .spawn()
        .map_err(|e| format!("OmniWin.Server.exe could not be started: {e}"))?;
    let stdin = child.stdin.take().ok_or("no stdin")?;
    let stdout = BufReader::new(child.stdout.take().ok_or("no stdout")?);
    let mut link = Link { child, stdin, stdout, next_id: 0 };
    request(
        &mut link,
        "initialize",
        json!({ "protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": { "name": "omniwin-app", "version": env!("CARGO_PKG_VERSION") } }),
    )?;
    let note = json!({ "jsonrpc": "2.0", "method": "notifications/initialized" }).to_string();
    writeln!(link.stdin, "{note}").map_err(|e| e.to_string())?;
    link.stdin.flush().map_err(|e| e.to_string())?;
    Ok(link)
}

impl OmniWin {
    /// Calls one allowed tool and returns its text. Blocking: call it from a blocking task.
    pub fn call(&self, tool: &str, args: Value) -> Result<String, String> {
        if !ALLOWED_TOOLS.contains(&tool) {
            return Err(format!("The app may not call \"{tool}\"."));
        }
        let mut guard = self.link.lock().map_err(|_| "OmniWin link is unavailable".to_string())?;
        if guard.as_mut().map(|l| matches!(l.child.try_wait(), Ok(None))) != Some(true) {
            *guard = None;
            *guard = Some(start()?);
        }
        let link = guard.as_mut().expect("started above");
        match request(link, "tools/call", json!({ "name": tool, "arguments": args })) {
            Ok(reply) => {
                if let Some(error) = reply.get("error") {
                    return Err(format!(
                        "OmniWin refused the call: {}",
                        error.get("message").and_then(Value::as_str).unwrap_or("unknown error")
                    ));
                }
                let mut text = String::new();
                if let Some(blocks) = reply.pointer("/result/content").and_then(Value::as_array) {
                    for block in blocks {
                        if block.get("type").and_then(Value::as_str) == Some("text") {
                            text.push_str(block.get("text").and_then(Value::as_str).unwrap_or_default());
                        }
                    }
                }
                Ok(text)
            }
            Err(e) => {
                *guard = None; // started again on the next call
                Err(e)
            }
        }
    }

    /// Ends the server child (it is started again on the next call).
    pub fn stop(&self) {
        if let Ok(mut guard) = self.link.lock() {
            *guard = None;
        }
    }
}

/// Calls a tool off the async runtime.
pub async fn call_async(app: &AppHandle, tool: &str, args: Value) -> Result<String, String> {
    let app = app.clone();
    let tool = tool.to_string();
    tauri::async_runtime::spawn_blocking(move || app.state::<OmniWin>().call(&tool, args))
        .await
        .map_err(|e| format!("OmniWin call failed: {e}"))?
}

/// The first line's `key=value` token, if any.
pub fn token<'a>(line: &'a str, key: &str) -> Option<&'a str> {
    line.split(' ').find_map(|t| t.strip_prefix(key))
}

/// Frontend: call one allowed OmniWin tool. `args` is a JSON object as text ("{}" for none).
#[tauri::command]
#[specta::specta]
pub async fn omniwin_call(app: AppHandle, tool: String, args: String) -> Result<String, String> {
    let parsed: Value = if args.trim().is_empty() {
        json!({})
    } else {
        serde_json::from_str(&args).map_err(|e| format!("args is not JSON: {e}"))?
    };
    if !parsed.is_object() {
        return Err("args must be a JSON object".to_string());
    }
    call_async(&app, &tool, parsed).await
}

/// Frontend: where the server is (or why it is not found).
#[tauri::command]
#[specta::specta]
pub fn omniwin_server_path() -> Result<String, String> {
    server_path()
        .map(|p| p.display().to_string())
        .ok_or_else(|| "not found".to_string())
}

/// Polish the selected text with the selected post-processing prompt and provider, through OmniWin's selection tools.
/// The result (or why nothing changed) is sent to the window as "omniwin-polish-result".
pub async fn polish_selection(app: AppHandle) {
    let message = match polish_inner(&app).await {
        Ok(m) => m,
        Err(m) => format!("Not changed. {m}"),
    };
    log::info!("OmniWin polish: {message}");
    let _ = app.emit("omniwin-polish-result", message);
}

async fn polish_inner(app: &AppHandle) -> Result<String, String> {
    let settings = crate::settings::get_settings(app);
    let provider = settings
        .active_post_process_provider()
        .cloned()
        .ok_or("No post-processing provider is selected (Post Process page).")?;
    let model = settings.post_process_models.get(&provider.id).cloned().unwrap_or_default();
    if model.trim().is_empty() {
        return Err("The post-processing provider has no model set (Post Process page).".into());
    }
    let prompt_id = settings
        .post_process_selected_prompt_id
        .clone()
        .ok_or("No post-processing prompt is selected (Post Process page).")?;
    let prompt = settings
        .post_process_prompts
        .iter()
        .find(|p| p.id == prompt_id)
        .map(|p| p.prompt.clone())
        .ok_or("The selected prompt was not found.")?;
    let api_key = settings.post_process_api_keys.get(&provider.id).cloned().unwrap_or_default();

    let got = call_async(app, "selection_get", json!({})).await?;
    let mut lines = got.lines();
    let first = lines.next().unwrap_or_default();
    if token(first, "status=") != Some("confirmed") {
        let detail = got.lines().find_map(|l| l.strip_prefix("detail: ")).unwrap_or(first);
        return Err(format!("The selection could not be read: {detail}"));
    }
    let info = lines.next().unwrap_or_default();
    if info.contains(" truncated=true") {
        return Err("The selection is too long to polish in one go.".into());
    }
    let sel = token(info, "sel=").ok_or("The selection could not be read.")?.to_string();
    let _header = lines.next();
    let original: String = lines.collect::<Vec<_>>().join("\n");
    if original.trim().is_empty() {
        return Err("Nothing is selected.".into());
    }

    let full = if prompt.contains("${output}") {
        prompt.replace("${output}", &original)
    } else {
        format!("{prompt}\n\n<text>\n{original}\n</text>")
    };
    let disable_reasoning = matches!(provider.id.as_str(), "custom" | "openrouter");
    let rewritten = crate::llm_client::send_chat_completion(&provider, api_key, &model, full, disable_reasoning)
        .await?
        .ok_or("The model gave an empty answer.")?;
    let rewritten = rewritten.trim().to_string();
    if rewritten.is_empty() || rewritten == original {
        return Ok("Nothing to change.".into());
    }

    let replaced = call_async(app, "selection_replace", json!({ "sel": sel, "text": rewritten })).await?;
    let first = replaced.lines().next().unwrap_or_default();
    match token(first, "status=") {
        Some("confirmed") => Ok("Polished. Press Ctrl+Z in the app to undo.".into()),
        Some("unverified") => Ok("Pasted, but OmniWin could not read it back. Please look.".into()),
        _ => Err(replaced
            .lines()
            .find_map(|l| l.strip_prefix("detail: "))
            .unwrap_or(first)
            .to_string()),
    }
}

/// Where OmniWin keeps config.json: `%LOCALAPPDATA%\OmniWin\config.json` (the server's default data folder).
pub fn config_path() -> Result<PathBuf, String> {
    let base = std::env::var_os("LOCALAPPDATA")
        .ok_or("LOCALAPPDATA is not set, so config.json could not be found.")?;
    Ok(PathBuf::from(base).join("OmniWin").join("config.json"))
}

/// The OmniWin settings the app edits (learning, quick commands, engines, decision model), plus the safety switches it only
/// shows. `power_tools.*` and `safety.*` are read for display and are never written from the app.
#[derive(Debug, Clone, PartialEq, serde::Serialize, serde::Deserialize, specta::Type)]
pub struct OmniWinConfigView {
    pub learning_enabled: bool,
    pub learning_auto_run: bool,
    pub promote_after: u32,
    pub router_enabled: bool,
    pub engines_enabled: bool,
    pub decision_provider: String,
    pub decision_mode: String,
    pub decision_threshold: f64,
    pub decision_allow_remote: bool,
    /// Base URL of an OpenAI-style service (`decision.endpoint`); empty means not set.
    pub decision_endpoint: String,
    /// The model name the service expects (`decision.model`); empty means not set.
    pub decision_model: String,
    /// The pasted API key (`decision.api_key`, plain text in config.json); empty means not set.
    pub decision_api_key: String,
    /// `decision.api_key_env`, shown only: when that variable is set it wins over the pasted key.
    pub decision_api_key_env: String,
    pub decision_max_latency_ms: u32,
    pub decision_timeout_ms: u32,
    pub power_tools_enabled: bool,
    pub power_tools_allow_delete: bool,
    pub safety_allow_delete: bool,
    pub config_path: String,
}

type JsonMap = serde_json::Map<String, Value>;

fn read_config(path: &Path) -> Result<JsonMap, String> {
    if !path.is_file() {
        return Ok(JsonMap::new());
    }
    let text = std::fs::read_to_string(path).map_err(|e| format!("config.json could not be read: {e}"))?;
    let text = text.trim_start_matches('\u{feff}');
    if text.trim().is_empty() {
        return Ok(JsonMap::new());
    }
    match serde_json::from_str::<Value>(text) {
        Ok(Value::Object(map)) => Ok(map),
        Ok(_) => Err("config.json is not a JSON object.".to_string()),
        Err(e) => Err(format!("config.json is not valid JSON: {e}")),
    }
}

fn config_value<'a>(root: &'a JsonMap, section: &str, key: &str) -> Option<&'a Value> {
    root.get(section)?.get(key)
}

fn config_bool(root: &JsonMap, section: &str, key: &str, fallback: bool) -> bool {
    config_value(root, section, key).and_then(Value::as_bool).unwrap_or(fallback)
}

fn config_text(root: &JsonMap, section: &str, key: &str) -> String {
    config_value(root, section, key).and_then(Value::as_str).unwrap_or_default().to_string()
}

/// The settings as the app shows them; anything missing gets the server's default.
pub fn view_of(root: &JsonMap, path: &Path) -> OmniWinConfigView {
    OmniWinConfigView {
        learning_enabled: config_bool(root, "learning", "enabled", true),
        learning_auto_run: config_bool(root, "learning", "auto_run", true),
        promote_after: config_value(root, "learning", "promote_after")
            .and_then(Value::as_u64)
            .map(|n| n.clamp(1, 20) as u32)
            .unwrap_or(3),
        router_enabled: config_bool(root, "router", "enabled", true),
        engines_enabled: config_bool(root, "engines", "enabled", true),
        decision_provider: config_value(root, "decision", "provider")
            .and_then(Value::as_str)
            .unwrap_or("none")
            .to_string(),
        decision_mode: config_value(root, "decision", "mode")
            .and_then(Value::as_str)
            .unwrap_or("off")
            .to_string(),
        decision_threshold: config_value(root, "decision", "threshold")
            .and_then(Value::as_f64)
            .unwrap_or(0.95),
        decision_allow_remote: config_bool(root, "decision", "allow_remote", false),
        decision_endpoint: config_text(root, "decision", "endpoint"),
        decision_model: config_text(root, "decision", "model"),
        decision_api_key: config_text(root, "decision", "api_key"),
        decision_api_key_env: config_text(root, "decision", "api_key_env"),
        decision_max_latency_ms: config_value(root, "decision", "max_latency_ms")
            .and_then(Value::as_u64)
            .map(|n| n.min(600_000) as u32)
            .unwrap_or(400),
        decision_timeout_ms: config_value(root, "decision", "timeout_ms")
            .and_then(Value::as_u64)
            .map(|n| n.min(600_000) as u32)
            .unwrap_or(1500),
        power_tools_enabled: config_bool(root, "power_tools", "enabled", false),
        power_tools_allow_delete: config_bool(root, "power_tools", "allow_delete", false),
        safety_allow_delete: config_bool(root, "safety", "allow_delete", false),
        config_path: path.display().to_string(),
    }
}

fn config_set(root: &mut JsonMap, section: &str, key: &str, value: Value) {
    let entry = root
        .entry(section.to_string())
        .or_insert_with(|| Value::Object(JsonMap::new()));
    if !entry.is_object() {
        *entry = Value::Object(JsonMap::new());
    }
    if let Value::Object(obj) = entry {
        obj.insert(key.to_string(), value);
    }
}

fn config_remove(root: &mut JsonMap, section: &str, key: &str) {
    if let Some(Value::Object(obj)) = root.get_mut(section) {
        obj.remove(key);
    }
}

/// Writes `value` trimmed, or removes the key when it is empty (so an untouched field adds nothing to config.json).
fn config_set_text(root: &mut JsonMap, section: &str, key: &str, value: &str) {
    let value = value.trim();
    if value.is_empty() {
        config_remove(root, section, key);
    } else {
        config_set(root, section, key, json!(value));
    }
}

/// Writes only the editable settings into `root`. `power_tools.*` and `safety.*` are never touched.
pub fn apply_view(root: &mut JsonMap, view: &OmniWinConfigView) -> Result<(), String> {
    let mode = view.decision_mode.trim();
    if !matches!(mode, "off" | "shadow" | "act") {
        return Err(format!("The decision mode must be off, shadow or act (not \"{mode}\")."));
    }
    if !(1..=20).contains(&view.promote_after) {
        return Err("\"Trust a task after\" must be 1 to 20 good runs.".to_string());
    }
    if !(0.5..=1.0).contains(&view.decision_threshold) {
        return Err("\"How sure it must be\" must be 0.50 to 1.00.".to_string());
    }
    let endpoint = view.decision_endpoint.trim();
    if !endpoint.is_empty() && !(endpoint.starts_with("http://") || endpoint.starts_with("https://")) {
        return Err("The base URL must start with http:// or https:// (for example https://api.example.com/v1).".to_string());
    }
    if view.decision_api_key.contains(['\r', '\n']) {
        return Err("The API key must be on one line.".to_string());
    }
    if view.decision_max_latency_ms < 1 || view.decision_timeout_ms < 100 {
        return Err("\"Fast enough\" must be at least 1 ms and \"Give up after\" at least 100 ms.".to_string());
    }
    let provider = view.decision_provider.trim();
    let provider = if provider.is_empty() { "none" } else { provider };
    config_set(root, "learning", "enabled", json!(view.learning_enabled));
    config_set(root, "learning", "auto_run", json!(view.learning_auto_run));
    config_set(root, "learning", "promote_after", json!(view.promote_after));
    config_set(root, "router", "enabled", json!(view.router_enabled));
    config_set(root, "engines", "enabled", json!(view.engines_enabled));
    config_set(root, "decision", "provider", json!(provider));
    config_set(root, "decision", "mode", json!(mode));
    config_set(root, "decision", "threshold", json!(view.decision_threshold));
    config_set(root, "decision", "allow_remote", json!(view.decision_allow_remote));
    config_set_text(root, "decision", "endpoint", &view.decision_endpoint);
    config_set_text(root, "decision", "model", &view.decision_model);
    config_set_text(root, "decision", "api_key", &view.decision_api_key);
    if view.decision_max_latency_ms != 400 || config_value(root, "decision", "max_latency_ms").is_some() {
        config_set(root, "decision", "max_latency_ms", json!(view.decision_max_latency_ms));
    }
    if view.decision_timeout_ms != 1500 || config_value(root, "decision", "timeout_ms").is_some() {
        config_set(root, "decision", "timeout_ms", json!(view.decision_timeout_ms));
    }
    Ok(())
}

/// Asks the server whether a config file is usable (`--validate-config`, the server's own rules).
fn validate_config(server: &Path, file: &Path) -> Result<(), String> {
    let mut command = Command::new(server);
    command
        .arg("--validate-config")
        .arg(file)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::null());
    #[cfg(windows)]
    command.creation_flags(0x0800_0000); // CREATE_NO_WINDOW
    let output = command
        .output()
        .map_err(|e| format!("OmniWin.Server.exe could not be started to check the settings: {e}"))?;
    let text = String::from_utf8_lossy(&output.stdout);
    let last = text.lines().rev().find(|l| !l.trim().is_empty()).unwrap_or_default();
    let reply: Value = serde_json::from_str(last.trim())
        .map_err(|_| "The server's answer could not be read; nothing was changed.".to_string())?;
    if reply.get("ok").and_then(Value::as_bool) == Some(true) {
        return Ok(());
    }
    let problems: Vec<String> = reply
        .get("errors")
        .and_then(Value::as_array)
        .map(|a| a.iter().map(|e| e.as_str().unwrap_or("unknown problem").to_string()).collect())
        .unwrap_or_default();
    Err(if problems.is_empty() {
        "The server did not accept the settings; nothing was changed.".to_string()
    } else {
        problems.join(" ")
    })
}

/// Saves the editable settings: a candidate file is checked by the server and only a file that passes replaces config.json,
/// by one rename. Nothing else in config.json changes.
pub fn save_config(path: &Path, server: &Path, view: &OmniWinConfigView) -> Result<(), String> {
    let mut root = read_config(path).map_err(|e| format!("{e} Nothing was changed."))?;
    apply_view(&mut root, view)?;
    if let Some(dir) = path.parent() {
        std::fs::create_dir_all(dir).map_err(|e| format!("The settings could not be saved: {e}"))?;
    }
    let candidate = PathBuf::from(format!("{}.new", path.display()));
    let text = serde_json::to_string_pretty(&Value::Object(root)).map_err(|e| e.to_string())?;
    std::fs::write(&candidate, text).map_err(|e| format!("The settings could not be saved: {e}"))?;
    if let Err(problems) = validate_config(server, &candidate) {
        let _ = std::fs::remove_file(&candidate); // the app's own scratch file
        return Err(problems);
    }
    std::fs::rename(&candidate, path).map_err(|e| format!("The settings could not be saved: {e}"))
}

/// Frontend: OmniWin's settings from config.json (with the safety switches, for display).
#[tauri::command]
#[specta::specta]
pub fn omniwin_config_load() -> Result<OmniWinConfigView, String> {
    let path = config_path()?;
    Ok(view_of(&read_config(&path)?, &path))
}

/// Frontend: save the editable settings, checked by the server first. The app's server is restarted so they apply.
#[tauri::command]
#[specta::specta]
pub async fn omniwin_config_save(app: AppHandle, view: OmniWinConfigView) -> Result<OmniWinConfigView, String> {
    tauri::async_runtime::spawn_blocking(move || -> Result<OmniWinConfigView, String> {
        let path = config_path()?;
        let server = server_path().ok_or(
            "OmniWin.Server.exe was not found, so the settings could not be checked. Nothing was changed.",
        )?;
        save_config(&path, &server, &view)?;
        // The server reads config.json when it starts; the next call starts it again with the new settings.
        app.state::<OmniWin>().stop();
        Ok(view_of(&read_config(&path)?, &path))
    })
    .await
    .map_err(|e| format!("Saving failed: {e}"))?
}

/// Frontend: run a typed request, as the old tray command box did. The window steps aside first (hidden for 250 ms) so
/// the request acts on the window the owner was using, then comes back with the answer.
#[tauri::command]
#[specta::specta]
pub async fn omniwin_run_request(app: AppHandle, text: String) -> Result<String, String> {
    let text = text.trim().to_string();
    if text.is_empty() {
        return Err("Type a request first.".to_string());
    }
    let window = app.get_webview_window("main");
    let shown = window.as_ref().map(|w| w.is_visible().unwrap_or(false)).unwrap_or(false);
    if shown {
        if let Some(w) = &window {
            let _ = w.hide();
        }
    }
    let worker = app.clone();
    let result = tauri::async_runtime::spawn_blocking(move || {
        if shown {
            std::thread::sleep(Duration::from_millis(250));
        }
        worker.state::<OmniWin>().call("omniwin_do", json!({ "text": text }))
    })
    .await
    .map_err(|e| format!("OmniWin call failed: {e}"));
    if shown {
        if let Some(w) = &window {
            let _ = w.show();
            let _ = w.set_focus();
        }
    }
    result?
}

/// Shows the main window on the OmniWin page; with text, puts it in the command box for the owner to check and run.
pub fn open_command_box(app: &AppHandle, text: Option<String>) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.unminimize();
        let _ = window.show();
        let _ = window.set_focus();
    }
    let _ = app.emit("omniwin-open-command-box", text.unwrap_or_default());
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn token_reads_first_line_values() {
        assert_eq!(token("status=confirmed input_sent=false sel=abc", "sel="), Some("abc"));
        assert_eq!(token("status=confirmed", "sel="), None);
    }

    #[test]
    fn a_tool_outside_the_list_is_refused_before_anything_starts() {
        let link = OmniWin::default();
        let error = link.call("desktop_act", json!({})).unwrap_err();
        assert!(error.contains("may not call"));
        assert!(link.link.lock().unwrap().is_none());
    }

    #[test]
    fn missing_settings_get_the_server_defaults_and_both_delete_switches_are_read() {
        let path = Path::new("C:/x/config.json");
        let view = view_of(&JsonMap::new(), path);
        assert!(view.learning_enabled && view.learning_auto_run && view.router_enabled && view.engines_enabled);
        assert_eq!((view.promote_after, view.decision_mode.as_str(), view.decision_provider.as_str()), (3, "off", "none"));
        assert!(!view.power_tools_allow_delete && !view.safety_allow_delete && !view.power_tools_enabled);
        let root: JsonMap = serde_json::from_str(
            r#"{"power_tools":{"enabled":true,"allow_delete":true},"safety":{"allow_delete":true}}"#,
        )
        .unwrap();
        let view = view_of(&root, path);
        assert!(view.power_tools_enabled && view.power_tools_allow_delete && view.safety_allow_delete);
    }

    #[test]
    fn saving_writes_only_the_editable_settings_and_never_the_safety_switches() {
        let mut root: JsonMap =
            serde_json::from_str(r#"{"confirm_mode":"off","power_tools":{"enabled":false}}"#).unwrap();
        let mut view = view_of(&root, Path::new("c.json"));
        view.power_tools_enabled = true;
        view.power_tools_allow_delete = true;
        view.safety_allow_delete = true;
        view.promote_after = 5;
        view.decision_provider = "  ".to_string();
        apply_view(&mut root, &view).unwrap();
        assert_eq!(root["power_tools"], json!({ "enabled": false }));
        assert!(root.get("safety").is_none());
        assert_eq!(root["confirm_mode"], json!("off"));
        assert_eq!(root["learning"]["promote_after"], json!(5));
        assert_eq!(root["decision"]["provider"], json!("none"));
    }

    #[test]
    fn bad_values_are_refused_before_anything_is_written() {
        let mut root = JsonMap::new();
        let mut view = view_of(&root, Path::new("c.json"));
        view.decision_mode = "always".to_string();
        assert!(apply_view(&mut root, &view).is_err());
        view.decision_mode = "off".to_string();
        view.promote_after = 0;
        assert!(apply_view(&mut root, &view).is_err());
        assert!(root.is_empty());
    }

    #[test]
    fn the_model_service_fields_are_read_written_trimmed_and_removed_when_empty() {
        let mut root: JsonMap = serde_json::from_str(
            r#"{"decision":{"provider":"openai","endpoint":"https://old.example/v1","model":"old","api_key":"k-old","api_key_env":"JEV_KEY"}}"#,
        )
        .unwrap();
        let mut view = view_of(&root, Path::new("c.json"));
        assert_eq!(
            (view.decision_endpoint.as_str(), view.decision_model.as_str(), view.decision_api_key.as_str(), view.decision_api_key_env.as_str()),
            ("https://old.example/v1", "old", "k-old", "JEV_KEY")
        );
        assert_eq!((view.decision_max_latency_ms, view.decision_timeout_ms), (400, 1500));
        view.decision_endpoint = " https://api.example.com/v1 ".to_string();
        view.decision_model = " jev-1 ".to_string();
        view.decision_api_key = String::new();
        view.decision_max_latency_ms = 3000;
        view.decision_timeout_ms = 5000;
        apply_view(&mut root, &view).unwrap();
        let d = &root["decision"];
        assert_eq!((d["endpoint"].clone(), d["model"].clone()), (json!("https://api.example.com/v1"), json!("jev-1")));
        assert!(d.get("api_key").is_none());
        assert_eq!(d["api_key_env"], json!("JEV_KEY"));
        assert_eq!((d["max_latency_ms"].clone(), d["timeout_ms"].clone()), (json!(3000), json!(5000)));
    }

    #[test]
    fn untouched_model_fields_add_nothing_and_bad_ones_are_refused() {
        let mut root = JsonMap::new();
        let view = view_of(&root, Path::new("c.json"));
        apply_view(&mut root, &view).unwrap();
        let d = root["decision"].as_object().unwrap();
        for key in ["endpoint", "model", "api_key", "api_key_env", "max_latency_ms", "timeout_ms"] {
            assert!(!d.contains_key(key), "{key} was written");
        }
        let mut bad = view.clone();
        bad.decision_endpoint = "api.example.com".to_string();
        assert!(apply_view(&mut JsonMap::new(), &bad).is_err());
        let mut bad = view.clone();
        bad.decision_api_key = "a\nb".to_string();
        assert!(apply_view(&mut JsonMap::new(), &bad).is_err());
        let mut bad = view;
        bad.decision_timeout_ms = 50;
        assert!(apply_view(&mut JsonMap::new(), &bad).is_err());
    }

    /// Talks to the real server named by OMNIWIN_SERVER (skipped when it is not set). Read-only calls only.
    #[test]
    fn the_app_talks_to_a_real_server_and_starts_it_again_after_a_stop() {
        if std::env::var("OMNIWIN_SERVER").is_err() {
            return;
        }
        let link = OmniWin::default();
        let status = link.call("omniwin_status", json!({})).expect("status");
        assert!(status.contains("contract=2.6"), "{status}");
        let prompts = link.call("polish_prompts", json!({})).expect("prompts");
        assert!(prompts.starts_with("polish_prompts count="), "{prompts}");
        link.stop();
        assert!(link.link.lock().unwrap().is_none());
        assert!(link.call("omniwin_status", json!({})).expect("again").contains("contract=2.6"));
    }
}
