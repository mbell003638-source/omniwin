import React from "react";
import { useTranslation } from "react-i18next";
import {
    Cog,
    FlaskConical,
    History,
    Info,
    Sparkles,
    Cpu,
    Terminal,
} from "lucide-react";
import HandyTextLogo from "./icons/HandyTextLogo";
import HandyHand from "./icons/HandyHand";
import { useSettings } from "../hooks/useSettings";
import {
    GeneralSettings,
    AdvancedSettings,
    HistorySettings,
    AboutSettings,
    PostProcessingSettings,
    ModelsSettings,
    OmniWinSettings,
} from "./settings";

export type SidebarSection = keyof typeof SECTIONS_CONFIG;

interface IconProps {
    width?: number | string;
    height?: number | string;
    size?: number | string;
    className?: string;
    [key: string]: any;
}

interface SectionConfig {
    labelKey: string;
    icon: React.ComponentType<IconProps>;
    /** `null` when App renders the section itself (Debug, with its handlers). */
    component: React.ComponentType | null;
    enabled: (settings: any) => boolean;
}

export const SECTIONS_CONFIG = {
    omniwin: {
        labelKey: "sidebar.omniwin",
        icon: Terminal,
        component: OmniWinSettings,
        enabled: () => true,
    },
    general: {
        labelKey: "sidebar.general",
        icon: HandyHand,
        component: GeneralSettings,
        enabled: () => true,
    },
    history: {
        labelKey: "sidebar.history",
        icon: History,
        component: HistorySettings,
        enabled: () => true,
    },
    models: {
        labelKey: "sidebar.models",
        icon: Cpu,
        component: ModelsSettings,
        enabled: () => true,
    },
    advanced: {
        labelKey: "sidebar.advanced",
        icon: Cog,
        component: AdvancedSettings,
        enabled: () => true,
    },
    postprocessing: {
        labelKey: "sidebar.postProcessing",
        icon: Sparkles,
        component: PostProcessingSettings,
        enabled: (settings) => settings?.post_process_enabled ?? false,
    },
    debug: {
        labelKey: "sidebar.debug",
        icon: FlaskConical,
        component: null,
        enabled: (settings) => settings?.debug_mode ?? false,
    },
    about: {
        labelKey: "sidebar.about",
        icon: Info,
        component: AboutSettings,
        enabled: () => true,
    },
} as const satisfies Record<string, SectionConfig>;

interface SidebarProps {
    activeSection: SidebarSection;
    onSectionChange: (section: SidebarSection) => void;
}

export const Sidebar: React.FC<SidebarProps> = ({
    activeSection,
    onSectionChange,
}) => {
    const { t } = useTranslation();
    const { settings } = useSettings();

    const availableSections = Object.entries(SECTIONS_CONFIG)
        .filter(([_, config]) => config.enabled(settings))
        .map(([id, config]) => ({ id: id as SidebarSection, ...config }));

    return (
        <nav className="flex flex-col w-52 h-full items-center px-3">
            <HandyTextLogo width={124} className="mt-5 mb-4" />
            <div className="flex flex-col w-full gap-0.5 pt-2">
                {availableSections.map((section) => {
                    const Icon = section.icon;
                    const isActive = activeSection === section.id;

                    // Windows 11 navigation: a soft fill and an accent bar on the current page.
                    return (
                        <button
                            type="button"
                            key={section.id}
                            aria-current={isActive ? "page" : undefined}
                            className={`relative flex gap-3 items-center px-3 py-2 w-full rounded-md cursor-pointer text-start transition-colors focus:outline-none focus-visible:ring-2 focus-visible:ring-logo-primary/60 ${
                                isActive
                                    ? "bg-wash text-text"
                                    : "text-ink-2 hover:bg-wash hover:text-text"
                            }`}
                            onClick={() => onSectionChange(section.id)}
                        >
                            {isActive && (
                                <span className="absolute start-0 top-1/2 -translate-y-1/2 h-4 w-[3px] rounded-full bg-logo-primary" />
                            )}
                            <Icon width={18} height={18} className="shrink-0" />
                            <span
                                className={`text-sm truncate ${isActive ? "font-semibold" : "font-medium"}`}
                                title={t(section.labelKey)}
                            >
                                {t(section.labelKey)}
                            </span>
                        </button>
                    );
                })}
            </div>
        </nav>
    );
};
