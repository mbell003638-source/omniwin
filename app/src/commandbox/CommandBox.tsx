// OmniWin: type a request and press Enter; the answer shows on the line below. Escape, or clicking elsewhere, closes it.
import React, { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { listen } from "@tauri-apps/api/event";
import {
  Check,
  CornerDownLeft,
  Loader2,
  Sparkles,
  Terminal,
  TriangleAlert,
  X,
} from "lucide-react";
import { commands, type CommandBoxState } from "@/bindings";
import { explain, type Explained, type Outcome } from "@/lib/omniwinResult";

const OUTCOME_ICON: Record<Outcome, React.ReactNode> = {
  done: <Check size={16} className="text-moss" />,
  unverified: <TriangleAlert size={16} className="text-warning" />,
  needsAi: <Sparkles size={16} className="text-logo-primary" />,
  refused: <X size={16} className="text-error" />,
  stopped: <TriangleAlert size={16} className="text-warning" />,
  failed: <X size={16} className="text-error" />,
};

const hide = () => {
  void commands.omniwinCommandBoxHide().catch(console.error);
};

const CommandBox: React.FC = () => {
  const { t } = useTranslation();
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<Explained | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const busyRef = useRef(false);
  const seen = useRef(-1);
  const input = useRef<HTMLInputElement>(null);

  const setWorking = (working: boolean) => {
    busyRef.current = working;
    setBusy(working);
  };

  const focusInput = () => setTimeout(() => input.current?.focus(), 30);

  // What the app says the box should show (a new request, a spoken one running, or its answer).
  const apply = useCallback(
    (state: CommandBoxState) => {
      if (state.seq === seen.current) return;
      seen.current = state.seq;
      setText(state.text);
      setWorking(state.phase === "running");
      setResult(state.result ? explain(state.result) : null);
      setError(state.error);
      setNote(
        state.phase === "edit" && state.spoken ? t("omniwin.box.heard") : null,
      );
      focusInput();
    },
    [t],
  );

  useEffect(() => {
    void commands.omniwinCommandBoxState().then(apply).catch(console.error);
    const unlisten = listen<CommandBoxState>("omniwin-command-box", (event) =>
      apply(event.payload),
    );
    // Clicking elsewhere closes the box, except while a request runs (the box steps aside then on purpose).
    const onBlur = () => {
      if (!busyRef.current) hide();
    };
    const onFocus = () => focusInput();
    window.addEventListener("blur", onBlur);
    window.addEventListener("focus", onFocus);
    return () => {
      void unlisten.then((f) => f());
      window.removeEventListener("blur", onBlur);
      window.removeEventListener("focus", onFocus);
    };
  }, [apply]);

  const run = async () => {
    const request = text.trim();
    if (!request || busyRef.current) return;
    setWorking(true);
    setResult(null);
    setError(null);
    setNote(null);
    try {
      const r = await commands.omniwinRunRequest(request);
      if (r.status === "error") setError(r.error);
      else setResult(explain(r.data));
    } catch (e) {
      setError(String(e));
    } finally {
      setWorking(false);
      focusInput();
    }
  };

  let line: React.ReactNode;
  if (busy) {
    line = (
      <span className="text-ink-2">{t("omniwin.box.running")}</span>
    );
  } else if (error) {
    line = (
      <>
        <X size={16} className="mt-0.5 shrink-0 text-error" />
        <span className="line-clamp-2 break-words text-error">{error}</span>
      </>
    );
  } else if (result) {
    line = (
      <>
        <span className="mt-0.5 shrink-0">{OUTCOME_ICON[result.outcome]}</span>
        <span className="line-clamp-2 break-words" title={result.detail ?? ""}>
          <span className="font-semibold">
            {t(`omniwin.outcome.${result.outcome}`)}
          </span>
          {result.detail ? ` ${result.detail}` : ""}
        </span>
      </>
    );
  } else {
    line = (
      <span className="line-clamp-2 text-ink-3">
        {note ?? t("omniwin.commandHelp")}
      </span>
    );
  }

  return (
    <div
      className="flex h-screen w-screen flex-col gap-2 overflow-hidden border border-line-strong bg-background p-3 text-text"
      onKeyDown={(e) => {
        if (e.key === "Escape") {
          e.preventDefault();
          hide();
        }
      }}
    >
      <div className="flex items-center gap-2">
        <Terminal size={18} className="shrink-0 text-logo-primary" />
        <input
          ref={input}
          autoFocus
          className="min-w-0 flex-1 rounded-lg border border-line bg-surface px-3 py-2 text-base placeholder:text-ink-3 focus:border-logo-primary focus:outline-none"
          value={text}
          readOnly={busy}
          placeholder={t("omniwin.box.placeholder")}
          aria-label={t("omniwin.commandTitle")}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              e.preventDefault();
              void run();
            }
          }}
        />
        {busy ? (
          <Loader2 size={18} className="shrink-0 animate-spin text-ink-2" />
        ) : (
          <CornerDownLeft size={18} className="shrink-0 text-ink-3" />
        )}
      </div>
      <div className="flex min-h-0 items-start gap-2 text-sm" aria-live="polite">
        {line}
      </div>
    </div>
  );
};

export default CommandBox;
