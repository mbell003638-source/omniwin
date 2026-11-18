import React, { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { listen } from "@tauri-apps/api/event";
import { commands } from "@/bindings";
import { SettingsGroup } from "../../ui/SettingsGroup";
import { ShortcutInput } from "../ShortcutInput";
import { explain, type Explained } from "@/lib/omniwinResult";

export const OmniWinSettings: React.FC = () => {
  const { t } = useTranslation();
  const [request, setRequest] = useState("");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<Explained | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const [recipes, setRecipes] = useState<string[]>([]);
  const [server, setServer] = useState<string | null>(null);
  const input = useRef<HTMLInputElement>(null);

  const call = useCallback(async (tool: string, args: object = {}) => {
    const r = await commands.omniwinCall(tool, JSON.stringify(args));
    if (r.status === "error") throw new Error(r.error);
    return r.data;
  }, []);

  const refresh = useCallback(async () => {
    const path = await commands.omniwinServerPath();
    setServer(path.status === "ok" ? path.data : null);
    try {
      const list = await call("workflow_list");
      setRecipes(
        list.split("\n").filter((l) => l.startsWith("learned ")).map((l) => l.slice(8)),
      );
    } catch {
      setRecipes([]);
    }
  }, [call]);

  useEffect(() => {
    void refresh();
    const unlisten = [
      listen<string>("omniwin-open-command-box", (e) => {
        if (e.payload) {
          setRequest(e.payload);
          setNote(t("omniwin.heard"));
        }
        setResult(null);
        setTimeout(() => input.current?.focus(), 50);
      }),
      listen<string>("omniwin-polish-result", (e) => setNote(e.payload)),
    ];
    return () => {
      unlisten.forEach((p) => p.then((f) => f()));
    };
  }, [refresh, t]);

  const run = async () => {
    const text = request.trim();
    if (!text || busy) return;
    setBusy(true);
    setNote(null);
    setResult(null);
    try {
      const answer = await call("omniwin_do", { text });
      const explained = explain(answer);
      if (explained.outcome === "needsAi") {
        // This window is not an AI and will not finish the task: close the task so nothing half-done is learned.
        await call("task_done", { success: false });
      }
      setResult(explained);
    } catch (e) {
      setResult({ outcome: "failed", recipe: null, detail: String(e), suggestion: null });
    } finally {
      setBusy(false);
      void refresh();
    }
  };

  const rate = async (action: "right" | "wrong_pick") => {
    if (!result?.recipe) return;
    try {
      await call("workflow_manage", { name: result.recipe, action });
      setNote(t(action === "right" ? "omniwin.thanksRight" : "omniwin.thanksWrong"));
    } catch (e) {
      setNote(String(e));
    }
    void refresh();
  };

  return (
    <div className="max-w-3xl w-full mx-auto space-y-6">
      <SettingsGroup title={t("omniwin.commandTitle")}>
        <div className="p-3 space-y-3">
          <p className="text-sm opacity-80">{t("omniwin.commandHelp")}</p>
          <div className="flex gap-2">
            <input
              ref={input}
              className="flex-1 px-3 py-2 rounded border border-mid-gray/40 bg-transparent"
              value={request}
              placeholder={t("omniwin.placeholder")}
              onChange={(e) => setRequest(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter") void run();
              }}
            />
            <button
              className="px-4 py-2 rounded bg-logo-primary/80 disabled:opacity-50"
              disabled={busy || !request.trim()}
              onClick={() => void run()}
            >
              {busy ? t("omniwin.working") : t("omniwin.run")}
            </button>
          </div>
          {result && (
            <div className="space-y-2">
              <p className="font-medium">{t(`omniwin.outcome.${result.outcome}`)}</p>
              {result.detail && <p className="text-sm opacity-70">{result.detail}</p>}
              {result.outcome === "needsAi" && result.suggestion && (
                <p className="text-sm opacity-70">
                  {t("omniwin.suggestion", { name: result.suggestion })}
                </p>
              )}
              <div className="flex gap-2">
                {result.outcome === "needsAi" && (
                  <button
                    className="px-3 py-1 rounded border border-mid-gray/40"
                    onClick={() => void navigator.clipboard.writeText(request)}
                  >
                    {t("omniwin.copy")}
                  </button>
                )}
                {result.recipe && (result.outcome === "done" || result.outcome === "unverified") && (
                  <>
                    <button
                      className="px-3 py-1 rounded border border-mid-gray/40"
                      onClick={() => void rate("right")}
                    >
                      {t("omniwin.right")}
                    </button>
                    <button
                      className="px-3 py-1 rounded border border-mid-gray/40"
                      onClick={() => void rate("wrong_pick")}
                    >
                      {t("omniwin.wrong")}
                    </button>
                  </>
                )}
              </div>
            </div>
          )}
          {note && <p className="text-sm">{note}</p>}
        </div>
      </SettingsGroup>

      <SettingsGroup title={t("omniwin.hotkeysTitle")}>
        <ShortcutInput shortcutId="omniwin_command_box" grouped={true} />
        <ShortcutInput shortcutId="omniwin_voice_command" grouped={true} />
        <ShortcutInput shortcutId="omniwin_polish" grouped={true} />
        <p className="p-3 text-sm opacity-70">{t("omniwin.hotkeysHelp")}</p>
      </SettingsGroup>

      <SettingsGroup title={t("omniwin.learnedTitle")}>
        <div className="p-3 space-y-1 text-sm">
          {recipes.length === 0 ? (
            <p className="opacity-70">{t("omniwin.noneLearned")}</p>
          ) : (
            recipes.map((r) => (
              <p key={r} className="font-mono text-xs break-all">
                {r}
              </p>
            ))
          )}
        </div>
      </SettingsGroup>

      <SettingsGroup title={t("omniwin.serverTitle")}>
        <div className="p-3 space-y-1 text-sm">
          <p className="break-all">{server ?? t("omniwin.serverMissing")}</p>
          <p className="opacity-70">{t("omniwin.safety")}</p>
        </div>
      </SettingsGroup>
    </div>
  );
};
