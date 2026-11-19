// OmniWin fork: Handy's own logo is not open source, so this draws the OmniWin mark and name instead. The component name is
// kept so the rest of the code needs no change.
/** The product name is a brand, not text to translate. */
const PRODUCT_NAME = "OmniWin";

const HandyTextLogo = ({
    width,
    height,
    className,
}: {
    width?: number;
    height?: number;
    className?: string;
}) => (
    <svg
        width={width}
        height={height}
        className={className}
        viewBox="0 0 1060 328"
        fill="none"
        xmlns="http://www.w3.org/2000/svg"
        role="img"
        aria-label="OmniWin"
    >
        {/* Inline colours: an accent ring and dot with the name in the text colour, in light and dark alike. The name is
        fitted to the box (textLength) so it is never cut off. */}
        <circle
            cx="164"
            cy="164"
            r="118"
            strokeWidth="44"
            style={{ fill: "none", stroke: "var(--color-logo-primary)" }}
        />
        <circle
            cx="164"
            cy="164"
            r="40"
            style={{ fill: "var(--color-logo-primary)" }}
        />
        <text
            x="330"
            y="222"
            fontSize="170"
            fontWeight="600"
            fontFamily="Segoe UI Variable Display, Segoe UI, system-ui, sans-serif"
            textLength="700"
            lengthAdjust="spacingAndGlyphs"
            style={{ fill: "var(--color-text)" }}
        >
            {PRODUCT_NAME}
        </text>
    </svg>
);

export default HandyTextLogo;
