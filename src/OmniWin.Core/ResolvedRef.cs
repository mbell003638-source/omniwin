namespace OmniWin.Core;

/// <summary>
/// Result of resolving a saved selector (workflows). <see cref="Generation"/> is the generation of the scan that found the
/// ref; the engine has recorded that scan in its history, so an act with this ref and generation has an observed baseline.
/// </summary>
public sealed record ResolvedRef(string Ref, long Generation);
