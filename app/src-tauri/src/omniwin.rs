//! OmniWin (fork of Handy): the app's own copy of `OmniWin.Server.exe`, spoken to over MCP (JSON-RPC lines on stdin/stdout)
//! exactly as an AI client does. All automation, every safety check and the stop key live in that server; this file only
//! carries requests to it. The app may call only the tools in `ALLOWED_TOOLS`.

use serde_json::{json, Value};
use std::io::{BufRead, BufReader, Write};
use std::path::PathBuf;
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
