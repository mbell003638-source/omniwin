// OmniWin fork: replaces Handy's hand logo (not open source) with a plain ring mark. The component name is kept.
const HandyHand = ({
  width,
  height,
  size,
  className,
}: {
  width?: number | string;
  height?: number | string;
  size?: number | string;
  className?: string;
  [key: string]: unknown;
}) => (
  <svg
    width={width ?? size ?? 24}
    height={height ?? size ?? 24}
    className={className}
    viewBox="0 0 24 24"
    fill="none"
    xmlns="http://www.w3.org/2000/svg"
    aria-hidden="true"
  >
    <circle cx="12" cy="12" r="7.5" stroke="currentColor" strokeWidth="2.5" />
    <circle cx="12" cy="12" r="2.2" fill="currentColor" />
  </svg>
);

export default HandyHand;
