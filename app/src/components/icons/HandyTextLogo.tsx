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
    viewBox="0 0 930 328"
    fill="none"
    xmlns="http://www.w3.org/2000/svg"
    role="img"
    aria-label="OmniWin"
  >
    <circle cx="164" cy="164" r="118" className="logo-stroke" strokeWidth="44" stroke="currentColor" />
    <circle cx="164" cy="164" r="34" className="logo-primary" fill="currentColor" />
    <text x="330" y="222" fontSize="170" fontWeight="700" fontFamily="Segoe UI, system-ui, sans-serif" fill="currentColor" className="logo-primary">
      {PRODUCT_NAME}
    </text>
  </svg>
);

export default HandyTextLogo;
