import React from "react";

interface SwitchProps {
    checked: boolean;
    onChange: (checked: boolean) => void;
    disabled?: boolean;
    /** Names the switch for assistive tech when no visible label points at it. */
    ariaLabel?: string;
    /** The id of a visible label that names the switch. */
    ariaLabelledBy?: string;
}

/** The on/off switch on its own; ToggleSwitch puts it in a settings row. */
export const Switch: React.FC<SwitchProps> = ({
    checked,
    onChange,
    disabled = false,
    ariaLabel,
    ariaLabelledBy,
}) => {
    return (
        <label
            className={`flex items-center ${disabled ? "cursor-not-allowed" : "cursor-pointer"}`}
        >
            <input
                type="checkbox"
                aria-label={ariaLabel}
                aria-labelledby={ariaLabelledBy}
                value=""
                className="sr-only peer"
                checked={checked}
                disabled={disabled}
                onChange={(e) => onChange(e.target.checked)}
            />
            <div className="relative w-10 h-5 rounded-full border border-ink-2 bg-transparent transition-colors peer-focus:outline-none peer-focus-visible:ring-2 peer-focus-visible:ring-logo-primary/60 peer-checked:bg-background-ui peer-checked:border-background-ui peer-checked:after:translate-x-5 rtl:peer-checked:after:-translate-x-5 peer-checked:after:bg-white after:content-[''] after:absolute after:top-[3px] after:start-[3px] after:bg-ink-2 after:rounded-full after:h-3 after:w-3 after:transition-all peer-disabled:opacity-50"></div>
        </label>
    );
};
