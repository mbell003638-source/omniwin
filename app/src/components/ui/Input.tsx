import React from "react";

interface InputProps extends React.InputHTMLAttributes<HTMLInputElement> {
    variant?: "default" | "compact";
}

export const Input: React.FC<InputProps> = ({
    className = "",
    variant = "default",
    disabled,
    ...props
}) => {
    // Fluent text box: a quiet edge with a stronger bottom line that turns accent on focus.
    const baseClasses =
        "text-sm bg-surface border border-line border-b-line-strong rounded-md text-start placeholder:text-ink-3 transition-colors duration-150";

    const interactiveClasses = disabled
        ? "opacity-60 cursor-not-allowed"
        : "hover:bg-surface-2 focus:outline-none focus:border-b-logo-primary focus:shadow-[inset_0_-1px_0_var(--color-logo-primary)]";

    const variantClasses = {
        default: "px-3 py-1.5",
        compact: "px-2 py-1",
    } as const;

    return (
        <input
            className={`${baseClasses} ${variantClasses[variant]} ${interactiveClasses} ${className}`}
            disabled={disabled}
            {...props}
        />
    );
};
