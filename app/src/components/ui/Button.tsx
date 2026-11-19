import React from "react";

interface ButtonProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
    variant?:
        | "primary"
        | "primary-soft"
        | "secondary"
        | "outline"
        | "warning"
        | "danger"
        | "danger-ghost"
        | "ghost";
    size?: "sm" | "md" | "lg";
}

export const Button: React.FC<ButtonProps> = ({
    children,
    className = "",
    variant = "primary",
    size = "md",
    ...props
}) => {
    // Fluent-style buttons: 6px corners, a quiet edge, a visible keyboard focus ring.
    const baseClasses =
        "inline-flex items-center justify-center gap-1.5 font-medium rounded-md border focus:outline-none focus-visible:ring-2 focus-visible:ring-logo-primary/60 transition-colors disabled:opacity-50 disabled:cursor-not-allowed cursor-pointer";

    const variantClasses = {
        primary:
            "text-white bg-background-ui border-background-ui hover:bg-background-ui/90 active:bg-background-ui/80 shadow-sm",
        "primary-soft":
            "text-text bg-logo-primary/15 border-transparent hover:bg-logo-primary/25",
        secondary:
            "text-text bg-surface border-line hover:bg-wash active:bg-line shadow-sm",
        // For screens where secondary's grey fill reads as disabled (onboarding)
        outline: "text-text bg-background border-line-strong hover:border-plum",
        // Secondary's neutral resting look, but hover/focus use the semantic
        // --color-warning token (theme.css) instead of the accent — for
        // buttons sitting on warning surfaces like SecureInputWarning
        warning:
            "text-text bg-surface border-line hover:bg-warning-wash hover:border-warning focus-visible:ring-warning",
        danger: "text-white bg-error border-error hover:bg-error/90 active:bg-error/80 focus-visible:ring-error",
        "danger-ghost":
            "text-error border-transparent hover:bg-error-wash focus:bg-error-wash",
        ghost: "text-current border-transparent hover:bg-wash active:bg-line",
    };

    const sizeClasses = {
        sm: "px-2.5 py-1 text-xs",
        md: "px-4 py-1.5 text-sm",
        lg: "px-5 py-2 text-sm",
    };

    return (
        <button
            className={`${baseClasses} ${variantClasses[variant]} ${sizeClasses[size]} ${className}`}
            {...props}
        >
            {children}
        </button>
    );
};
