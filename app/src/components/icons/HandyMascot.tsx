// OmniWin fork: Handy's mascot is not open source, so this draws the plain OmniWin mark instead. The component name is kept.
const HandyMascot = ({ className = "" }: { className?: string }) => (
  <svg viewBox="0 0 151 165" className={className} fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
    <circle cx="75.5" cy="82.5" r="58" stroke="currentColor" strokeWidth="22" />
    <circle cx="75.5" cy="82.5" r="17" fill="currentColor" />
  </svg>
);

export default HandyMascot;
