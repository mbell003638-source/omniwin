import React from "react";

interface SettingsGroupProps {
    title?: string;
    description?: string;
    children: React.ReactNode;
}

/** A titled card of setting rows (Windows 11 settings style). */
export const SettingsGroup: React.FC<SettingsGroupProps> = ({
    title,
    description,
    children,
}) => {
    return (
        <section className="space-y-2">
            {title && (
                <div className="px-1">
                    <h2 className="text-[13px] font-semibold text-text">
                        {title}
                    </h2>
                    {description && (
                        <p className="text-xs text-ink-2 mt-0.5">
                            {description}
                        </p>
                    )}
                </div>
            )}
            <div className="bg-surface border border-card-edge rounded-xl shadow-card overflow-visible">
                <div className="divide-y divide-line">{children}</div>
            </div>
        </section>
    );
};
