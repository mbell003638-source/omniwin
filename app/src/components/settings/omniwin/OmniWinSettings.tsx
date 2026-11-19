import React, {
    useCallback,
    useEffect,
    useMemo,
    useRef,
    useState,
} from "react";
import { useTranslation } from "react-i18next";
import { listen } from "@tauri-apps/api/event";
import {
    Check,
    Copy,
    CornerDownLeft,
    Eye,
    EyeOff,
    Info,
    Loader2,
    Server,
    ShieldAlert,
    ShieldCheck,
    Sparkles,
    Terminal,
    ThumbsDown,
    ThumbsUp,
    TriangleAlert,
    X,
} from "lucide-react";
import { commands, type OmniWinConfigView } from "@/bindings";
import { SettingsGroup } from "../../ui/SettingsGroup";
import { SettingContainer } from "../../ui/SettingContainer";
import { ToggleSwitch } from "../../ui/ToggleSwitch";
import { Button } from "../../ui/Button";
import { Input } from "../../ui/Input";
import { Dropdown } from "../../ui/Dropdown";
import { ShortcutInput } from "../ShortcutInput";
import { explain, type Explained, type Outcome } from "@/lib/omniwinResult";

/** The settings the page may write; power_tools.* and safety.* are shown only. */
const EDITABLE = [
    "learning_enabled",
    "learning_auto_run",
    "promote_after",
    "router_enabled",
    "engines_enabled",
    "decision_provider",
    "decision_mode",
    "decision_threshold",
    "decision_allow_remote",
    "decision_endpoint",
    "decision_model",
    "decision_api_key",
    "decision_max_latency_ms",
    "decision_timeout_ms",
] as const;

const editable = (c: OmniWinConfigView | null) =>
    c ? JSON.stringify(EDITABLE.map((k) => c[k])) : "";

const OUTCOME_STYLE: Record<Outcome, { box: string; icon: React.ReactNode }> = {
    done: { box: "bg-moss-wash text-moss", icon: <Check size={16} /> },
    unverified: {
        box: "bg-warning-wash text-warning",
        icon: <TriangleAlert size={16} />,
    },
    needsAi: {
        box: "bg-logo-primary/10 text-logo-primary",
        icon: <Sparkles size={16} />,
    },
    refused: { box: "bg-error-wash text-error", icon: <X size={16} /> },
    stopped: {
        box: "bg-warning-wash text-warning",
        icon: <TriangleAlert size={16} />,
    },
    failed: { box: "bg-error-wash text-error", icon: <X size={16} /> },
};

/** A small status pill: "good" (green), "risky" (amber) or "neutral". */
const Pill: React.FC<{
    tone: "good" | "risky" | "neutral";
    children: React.ReactNode;
}> = ({ tone, children }) => (
    <span
        className={`inline-flex items-center gap-1 rounded-full px-2.5 py-0.5 text-xs font-semibold ${
            tone === "good"
                ? "bg-moss-wash text-moss"
                : tone === "risky"
                  ? "bg-warning-wash text-warning"
                  : "bg-wash text-ink-2"
        }`}
    >
        {children}
    </span>
);

export const OmniWinSettings: React.FC = () => {
    const { t } = useTranslation();
    const [request, setRequest] = useState("");
    const [busy, setBusy] = useState(false);
    const [result, setResult] = useState<Explained | null>(null);
    const [note, setNote] = useState<string | null>(null);
    const [recipes, setRecipes] = useState<string[]>([]);
    const [server, setServer] = useState<string | null>(null);
    const [saved, setSaved] = useState<OmniWinConfigView | null>(null);
    const [draft, setDraft] = useState<OmniWinConfigView | null>(null);
    const [configError, setConfigError] = useState<string | null>(null);
    const [saving, setSaving] = useState(false);
    const [showKey, setShowKey] = useState(false);
    const [saveNote, setSaveNote] = useState<{
        ok: boolean;
        text: string;
    } | null>(null);
    const input = useRef<HTMLInputElement>(null);

    const call = useCallback(async (tool: string, args: object = {}) => {
        const r = await commands.omniwinCall(tool, JSON.stringify(args));
        if (r.status === "error") throw new Error(r.error);
        return r.data;
    }, []);

    const loadConfig = useCallback(async () => {
        const r = await commands.omniwinConfigLoad();
        if (r.status === "ok") {
            setSaved(r.data);
            setDraft(r.data);
            setConfigError(null);
        } else {
            setConfigError(r.error);
        }
    }, []);

    const refresh = useCallback(async () => {
        const path = await commands.omniwinServerPath();
        setServer(path.status === "ok" ? path.data : null);
        try {
            const list = await call("workflow_list");
            setRecipes(
                list
                    .split("\n")
                    .filter((l) => l.startsWith("learned "))
                    .map((l) => l.slice(8)),
            );
        } catch {
            setRecipes([]);
        }
    }, [call]);

    useEffect(() => {
        void refresh();
        void loadConfig();
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
    }, [refresh, loadConfig, t]);

    const run = async () => {
        const text = request.trim();
        if (!text || busy) return;
        setBusy(true);
        setNote(null);
        setResult(null);
        try {
            // The window steps aside while the request runs, so it acts on the window you were using.
            const r = await commands.omniwinRunRequest(text);
            if (r.status === "error") throw new Error(r.error);
            const explained = explain(r.data);
            if (explained.outcome === "needsAi") {
                // This window is not an AI and will not finish the task: close the task so nothing half-done is learned.
                await call("task_done", { success: false });
            }
            setResult(explained);
        } catch (e) {
            setResult({
                outcome: "failed",
                recipe: null,
                detail: String(e),
                suggestion: null,
            });
        } finally {
            setBusy(false);
            void refresh();
        }
    };

    const rate = async (action: "right" | "wrong_pick") => {
        if (!result?.recipe) return;
        try {
            await call("workflow_manage", { name: result.recipe, action });
            setNote(
                t(
                    action === "right"
                        ? "omniwin.thanksRight"
                        : "omniwin.thanksWrong",
                ),
            );
        } catch (e) {
            setNote(String(e));
        }
        void refresh();
    };

    const change = <K extends keyof OmniWinConfigView>(
        key: K,
        value: OmniWinConfigView[K],
    ) => {
        setSaveNote(null);
        setDraft((d) => (d ? { ...d, [key]: value } : d));
    };

    const dirty = useMemo(
        () => editable(draft) !== editable(saved),
        [draft, saved],
    );

    const save = async () => {
        if (!draft || saving) return;
        setSaving(true);
        setSaveNote(null);
        const r = await commands.omniwinConfigSave(draft);
        if (r.status === "ok") {
            setSaved(r.data);
            setDraft(r.data);
            setSaveNote({ ok: true, text: t("omniwin.config.saved") });
        } else {
            setSaveNote({ ok: false, text: r.error });
        }
        setSaving(false);
    };

    const deleting = useMemo(() => {
        if (!saved) return null;
        const desktop = saved.safety_allow_delete;
        const power = saved.power_tools_allow_delete;
        if (!desktop && !power)
            return {
                tone: "good" as const,
                text: t("omniwin.safety.deleteRefused"),
            };
        if (desktop && power)
            return {
                tone: "risky" as const,
                text: t("omniwin.safety.deleteAllowedBoth"),
            };
        if (desktop)
            return {
                tone: "risky" as const,
                text: t("omniwin.safety.deleteAllowedDesktop"),
            };
        return {
            tone: "risky" as const,
            text: saved.power_tools_enabled
                ? t("omniwin.safety.deleteAllowedPower")
                : t("omniwin.safety.deleteAllowedPowerOff"),
        };
    }, [saved, t]);

    const outcome = result ? OUTCOME_STYLE[result.outcome] : null;

    return (
        <div className="max-w-3xl w-full mx-auto space-y-8">
            {/* Command box */}
            <section className="rounded-2xl border border-card-edge bg-surface shadow-card p-6 space-y-4">
                <div className="flex items-start gap-3">
                    <div className="grid size-10 shrink-0 place-items-center rounded-xl bg-logo-primary/10 text-logo-primary">
                        <Terminal size={20} />
                    </div>
                    <div>
                        <h1 className="text-xl font-semibold leading-tight">
                            {t("omniwin.commandTitle")}
                        </h1>
                        <p className="text-sm text-ink-2 mt-0.5">
                            {t("omniwin.commandHelp")}
                        </p>
                    </div>
                </div>
                <div className="flex gap-2">
                    <div className="relative flex-1">
                        <input
                            ref={input}
                            className="w-full rounded-lg border border-line border-b-line-strong bg-surface py-2.5 ps-4 pe-10 text-base placeholder:text-ink-3 transition-colors hover:bg-surface-2 focus:outline-none focus:border-b-logo-primary focus:shadow-[inset_0_-1px_0_var(--color-logo-primary)]"
                            value={request}
                            placeholder={t("omniwin.placeholder")}
                            aria-label={t("omniwin.commandTitle")}
                            onChange={(e) => setRequest(e.target.value)}
                            onKeyDown={(e) => {
                                if (e.key === "Enter") void run();
                            }}
                        />
                        <CornerDownLeft
                            size={16}
                            className="pointer-events-none absolute end-3 top-1/2 -translate-y-1/2 text-ink-3"
                        />
                    </div>
                    <Button
                        variant="primary"
                        size="lg"
                        className="min-w-24"
                        disabled={busy || !request.trim()}
                        onClick={() => void run()}
                    >
                        {busy && <Loader2 size={16} className="animate-spin" />}
                        {busy ? t("omniwin.working") : t("omniwin.run")}
                    </Button>
                </div>
                <p className="text-xs text-ink-3">{t("omniwin.stepAside")}</p>

                {result && outcome && (
                    <div className="rounded-xl border border-card-edge bg-surface-2 p-4 space-y-3 animate-rise">
                        <div className="flex items-center gap-2">
                            <span
                                className={`grid size-7 place-items-center rounded-full ${outcome.box}`}
                            >
                                {outcome.icon}
                            </span>
                            <p className="font-semibold text-sm">
                                {t(`omniwin.outcome.${result.outcome}`)}
                            </p>
                        </div>
                        {result.detail && (
                            <p className="text-sm text-ink-2 break-words">
                                {result.detail}
                            </p>
                        )}
                        {result.outcome === "needsAi" && result.suggestion && (
                            <p className="text-sm text-ink-2">
                                {t("omniwin.suggestion", {
                                    name: result.suggestion,
                                })}
                            </p>
                        )}
                        <div className="flex flex-wrap gap-2">
                            {result.outcome === "needsAi" && (
                                <Button
                                    variant="secondary"
                                    size="sm"
                                    onClick={() =>
                                        void navigator.clipboard.writeText(
                                            request,
                                        )
                                    }
                                >
                                    <Copy size={14} />
                                    {t("omniwin.copy")}
                                </Button>
                            )}
                            {result.recipe &&
                                (result.outcome === "done" ||
                                    result.outcome === "unverified") && (
                                    <>
                                        <Button
                                            variant="secondary"
                                            size="sm"
                                            onClick={() => void rate("right")}
                                        >
                                            <ThumbsUp size={14} />
                                            {t("omniwin.right")}
                                        </Button>
                                        <Button
                                            variant="secondary"
                                            size="sm"
                                            onClick={() =>
                                                void rate("wrong_pick")
                                            }
                                        >
                                            <ThumbsDown size={14} />
                                            {t("omniwin.wrong")}
                                        </Button>
                                    </>
                                )}
                        </div>
                    </div>
                )}
                {note && (
                    <p className="flex items-start gap-2 text-sm text-ink-2">
                        <Info size={16} className="mt-0.5 shrink-0" />
                        {note}
                    </p>
                )}
            </section>

            <SettingsGroup
                title={t("omniwin.hotkeysTitle")}
                description={t("omniwin.hotkeysHelp")}
            >
                <ShortcutInput
                    shortcutId="omniwin_command_box"
                    grouped={true}
                />
                <ShortcutInput
                    shortcutId="omniwin_voice_command"
                    grouped={true}
                />
                <ShortcutInput shortcutId="omniwin_polish" grouped={true} />
            </SettingsGroup>

            {configError && (
                <p className="rounded-xl bg-error-wash px-4 py-3 text-sm text-error">
                    {configError}
                </p>
            )}

            {draft && (
                <>
                    <SettingsGroup
                        title={t("omniwin.config.learningTitle")}
                        description={t("omniwin.config.learningHelp")}
                    >
                        <ToggleSwitch
                            grouped
                            descriptionMode="inline"
                            label={t("omniwin.config.learning")}
                            description={t("omniwin.config.learningDesc")}
                            checked={draft.learning_enabled}
                            onChange={(v) => change("learning_enabled", v)}
                        />
                        <ToggleSwitch
                            grouped
                            descriptionMode="inline"
                            label={t("omniwin.config.autoRun")}
                            description={t("omniwin.config.autoRunDesc")}
                            checked={draft.learning_auto_run}
                            disabled={!draft.learning_enabled}
                            onChange={(v) => change("learning_auto_run", v)}
                        />
                        <SettingContainer
                            grouped
                            descriptionMode="inline"
                            title={t("omniwin.config.promoteAfter")}
                            description={t("omniwin.config.promoteAfterDesc")}
                            disabled={!draft.learning_enabled}
                        >
                            <Input
                                type="number"
                                min={1}
                                max={20}
                                className="w-20 text-center"
                                disabled={!draft.learning_enabled}
                                value={draft.promote_after}
                                aria-label={t("omniwin.config.promoteAfter")}
                                onChange={(e) => {
                                    const n = Math.round(
                                        Number(e.target.value),
                                    );
                                    if (Number.isFinite(n))
                                        change(
                                            "promote_after",
                                            Math.min(20, Math.max(1, n)),
                                        );
                                }}
                            />
                        </SettingContainer>
                        <ToggleSwitch
                            grouped
                            descriptionMode="inline"
                            label={t("omniwin.config.router")}
                            description={t("omniwin.config.routerDesc")}
                            checked={draft.router_enabled}
                            onChange={(v) => change("router_enabled", v)}
                        />
                        <ToggleSwitch
                            grouped
                            descriptionMode="inline"
                            label={t("omniwin.config.engines")}
                            description={t("omniwin.config.enginesDesc")}
                            checked={draft.engines_enabled}
                            onChange={(v) => change("engines_enabled", v)}
                        />
                    </SettingsGroup>

                    <SettingsGroup
                        title={t("omniwin.config.decisionTitle")}
                        description={t("omniwin.config.decisionHelp")}
                    >
                        <SettingContainer
                            grouped
                            descriptionMode="inline"
                            title={t("omniwin.config.provider")}
                            description={t("omniwin.config.providerDesc")}
                        >
                            <Input
                                className="w-48"
                                value={draft.decision_provider}
                                aria-label={t("omniwin.config.provider")}
                                onChange={(e) =>
                                    change("decision_provider", e.target.value)
                                }
                            />
                        </SettingContainer>
                        <SettingContainer
                            grouped
                            descriptionMode="inline"
                            title={t("omniwin.config.endpoint")}
                            description={t("omniwin.config.endpointDesc")}
                        >
                            <Input
                                type="url"
                                className="w-72"
                                spellCheck={false}
                                autoComplete="off"
                                placeholder="https://api.example.com/v1"
                                value={draft.decision_endpoint}
                                aria-label={t("omniwin.config.endpoint")}
                                onChange={(e) =>
                                    change("decision_endpoint", e.target.value)
                                }
                            />
                        </SettingContainer>
                        <SettingContainer
                            grouped
                            descriptionMode="inline"
                            title={t("omniwin.config.modelName")}
                            description={t("omniwin.config.modelNameDesc")}
                        >
                            <Input
                                className="w-72"
                                spellCheck={false}
                                autoComplete="off"
                                placeholder="jev-1"
                                value={draft.decision_model}
                                aria-label={t("omniwin.config.modelName")}
                                onChange={(e) =>
                                    change("decision_model", e.target.value)
                                }
                            />
                        </SettingContainer>
                        <SettingContainer
                            grouped
                            descriptionMode="inline"
                            title={t("omniwin.config.apiKey")}
                            description={
                                draft.decision_api_key_env
                                    ? `${t("omniwin.config.apiKeyDesc")} ${t("omniwin.config.apiKeyEnvNote", { name: draft.decision_api_key_env })}`
                                    : t("omniwin.config.apiKeyDesc")
                            }
                        >
                            <div className="flex w-72 items-center gap-1">
                                <Input
                                    type={showKey ? "text" : "password"}
                                    className="min-w-0 flex-1 font-mono"
                                    spellCheck={false}
                                    autoComplete="off"
                                    placeholder="sk-..."
                                    value={draft.decision_api_key}
                                    aria-label={t("omniwin.config.apiKey")}
                                    onChange={(e) =>
                                        change(
                                            "decision_api_key",
                                            e.target.value,
                                        )
                                    }
                                />
                                <button
                                    type="button"
                                    className="grid size-8 shrink-0 place-items-center rounded-md text-ink-2 transition-colors hover:bg-wash hover:text-text focus-visible:outline-2 focus-visible:outline-logo-primary"
                                    title={
                                        showKey
                                            ? t("omniwin.config.hideKey")
                                            : t("omniwin.config.showKey")
                                    }
                                    aria-label={
                                        showKey
                                            ? t("omniwin.config.hideKey")
                                            : t("omniwin.config.showKey")
                                    }
                                    aria-pressed={showKey}
                                    onClick={() => setShowKey((s) => !s)}
                                >
                                    {showKey ? (
                                        <EyeOff size={16} />
                                    ) : (
                                        <Eye size={16} />
                                    )}
                                </button>
                            </div>
                        </SettingContainer>
                        <SettingContainer
                            grouped
                            descriptionMode="inline"
                            title={t("omniwin.config.mode")}
                            description={t("omniwin.config.modeDesc")}
                        >
                            <Dropdown
                                className="w-48"
                                selectedValue={draft.decision_mode}
                                onSelect={(v) => change("decision_mode", v)}
                                options={[
                                    {
                                        value: "off",
                                        label: t("omniwin.config.modeOff"),
                                    },
                                    {
                                        value: "shadow",
                                        label: t("omniwin.config.modeShadow"),
                                    },
                                    {
                                        value: "act",
                                        label: t("omniwin.config.modeAct"),
                                    },
                                ]}
                            />
                        </SettingContainer>
                        <SettingContainer
                            grouped
                            descriptionMode="inline"
                            title={t("omniwin.config.threshold")}
                            description={t("omniwin.config.thresholdDesc")}
                        >
                            <Input
                                type="number"
                                min={0.5}
                                max={1}
                                step={0.01}
                                className="w-24 text-center"
                                value={draft.decision_threshold}
                                aria-label={t("omniwin.config.threshold")}
                                onChange={(e) => {
                                    const n = Number(e.target.value);
                                    if (Number.isFinite(n))
                                        change(
                                            "decision_threshold",
                                            Math.min(
                                                1,
                                                Math.max(
                                                    0.5,
                                                    Math.round(n * 100) / 100,
                                                ),
                                            ),
                                        );
                                }}
                            />
                        </SettingContainer>
                        <SettingContainer
                            grouped
                            descriptionMode="inline"
                            title={t("omniwin.config.maxLatency")}
                            description={t("omniwin.config.maxLatencyDesc")}
                        >
                            <Input
                                type="number"
                                min={1}
                                max={600000}
                                step={100}
                                className="w-28 text-center"
                                value={draft.decision_max_latency_ms}
                                aria-label={t("omniwin.config.maxLatency")}
                                onChange={(e) => {
                                    const n = Math.round(
                                        Number(e.target.value),
                                    );
                                    if (Number.isFinite(n))
                                        change(
                                            "decision_max_latency_ms",
                                            Math.min(600000, Math.max(1, n)),
                                        );
                                }}
                            />
                        </SettingContainer>
                        <SettingContainer
                            grouped
                            descriptionMode="inline"
                            title={t("omniwin.config.timeout")}
                            description={t("omniwin.config.timeoutDesc")}
                        >
                            <Input
                                type="number"
                                min={100}
                                max={600000}
                                step={100}
                                className="w-28 text-center"
                                value={draft.decision_timeout_ms}
                                aria-label={t("omniwin.config.timeout")}
                                onChange={(e) => {
                                    const n = Math.round(
                                        Number(e.target.value),
                                    );
                                    if (Number.isFinite(n))
                                        change(
                                            "decision_timeout_ms",
                                            Math.min(600000, Math.max(100, n)),
                                        );
                                }}
                            />
                        </SettingContainer>
                        <ToggleSwitch
                            grouped
                            descriptionMode="inline"
                            label={t("omniwin.config.allowRemote")}
                            description={t("omniwin.config.allowRemoteDesc")}
                            checked={draft.decision_allow_remote}
                            onChange={(v) => change("decision_allow_remote", v)}
                        />
                    </SettingsGroup>

                    <div className="flex flex-wrap items-center gap-3 px-1">
                        <Button
                            variant="primary"
                            disabled={!dirty || saving}
                            onClick={() => void save()}
                        >
                            {saving && (
                                <Loader2 size={14} className="animate-spin" />
                            )}
                            {t("omniwin.config.save")}
                        </Button>
                        <Button
                            variant="secondary"
                            disabled={!dirty || saving}
                            onClick={() => {
                                setDraft(saved);
                                setSaveNote(null);
                            }}
                        >
                            {t("omniwin.config.revert")}
                        </Button>
                        {saveNote ? (
                            <p
                                className={`text-sm ${saveNote.ok ? "text-moss" : "text-error"}`}
                            >
                                {saveNote.text}
                            </p>
                        ) : (
                            <p className="text-xs text-ink-3">
                                {t("omniwin.config.saveHelp")}
                            </p>
                        )}
                    </div>
                </>
            )}

            {saved && deleting && (
                <SettingsGroup
                    title={t("omniwin.safety.title")}
                    description={t("omniwin.safety.help")}
                >
                    <div className="flex items-start gap-3 px-5 py-4">
                        <span
                            className={`grid size-9 shrink-0 place-items-center rounded-xl ${
                                deleting.tone === "good"
                                    ? "bg-moss-wash text-moss"
                                    : "bg-warning-wash text-warning"
                            }`}
                        >
                            {deleting.tone === "good" ? (
                                <ShieldCheck size={18} />
                            ) : (
                                <ShieldAlert size={18} />
                            )}
                        </span>
                        <p className="text-sm font-medium pt-1.5">
                            {deleting.text}
                        </p>
                    </div>
                    <SettingContainer
                        grouped
                        descriptionMode="inline"
                        title={t("omniwin.safety.desktopDelete")}
                        description={t("omniwin.safety.desktopDeleteDesc")}
                    >
                        <Pill
                            tone={saved.safety_allow_delete ? "risky" : "good"}
                        >
                            {saved.safety_allow_delete
                                ? t("omniwin.safety.allowed")
                                : t("omniwin.safety.refused")}
                        </Pill>
                    </SettingContainer>
                    <SettingContainer
                        grouped
                        descriptionMode="inline"
                        title={t("omniwin.safety.powerTools")}
                        description={t("omniwin.safety.powerToolsDesc")}
                    >
                        <Pill
                            tone={saved.power_tools_enabled ? "risky" : "good"}
                        >
                            {saved.power_tools_enabled
                                ? t("omniwin.safety.on")
                                : t("omniwin.safety.off")}
                        </Pill>
                    </SettingContainer>
                    <SettingContainer
                        grouped
                        descriptionMode="inline"
                        title={t("omniwin.safety.powerDelete")}
                        description={t("omniwin.safety.powerDeleteDesc")}
                    >
                        <Pill
                            tone={
                                saved.power_tools_allow_delete
                                    ? "risky"
                                    : "good"
                            }
                        >
                            {saved.power_tools_allow_delete
                                ? t("omniwin.safety.allowed")
                                : t("omniwin.safety.refused")}
                        </Pill>
                    </SettingContainer>
                    <SettingContainer
                        grouped
                        descriptionMode="inline"
                        title={t("omniwin.safety.stopKey")}
                        description={t("omniwin.safety.stopKeyDesc")}
                    >
                        <Pill tone="neutral">
                            {t("omniwin.safety.stopKeyValue")}
                        </Pill>
                    </SettingContainer>
                    <p className="px-5 py-3 text-xs text-ink-3 break-all">
                        {t("omniwin.safety.handEdit", {
                            path: saved.config_path,
                        })}
                    </p>
                </SettingsGroup>
            )}

            <SettingsGroup title={t("omniwin.learnedTitle")}>
                <div className="px-5 py-3">
                    {recipes.length === 0 ? (
                        <p className="text-sm text-ink-2 py-1">
                            {t("omniwin.noneLearned")}
                        </p>
                    ) : (
                        <ul className="flex flex-wrap gap-2 py-1">
                            {recipes.map((r) => (
                                <li
                                    key={r}
                                    className="rounded-md border border-card-edge bg-surface-2 px-2.5 py-1 font-mono text-xs break-all"
                                >
                                    {r}
                                </li>
                            ))}
                        </ul>
                    )}
                </div>
            </SettingsGroup>

            <SettingsGroup title={t("omniwin.serverTitle")}>
                <div className="flex items-start gap-3 px-5 py-4">
                    <span
                        className={`grid size-9 shrink-0 place-items-center rounded-xl ${
                            server
                                ? "bg-moss-wash text-moss"
                                : "bg-error-wash text-error"
                        }`}
                    >
                        <Server size={18} />
                    </span>
                    <div className="min-w-0">
                        <p className="text-sm font-medium">
                            {server
                                ? t("omniwin.serverFound")
                                : t("omniwin.serverMissing")}
                        </p>
                        {server && (
                            <p className="text-xs text-ink-2 break-all mt-0.5">
                                {server}
                            </p>
                        )}
                    </div>
                </div>
            </SettingsGroup>
        </div>
    );
};
