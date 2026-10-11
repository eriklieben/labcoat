/**
 * Listeners for server events (also types the store doesn't model, e.g. `debuggerWaiting`). Lives in its own
 * dependency-free module so modules the store imports (settings → ui-defaults) can subscribe without an import cycle.
 */
export const serverEventListeners = new Set<(e: { type: string } & Record<string, unknown>) => void>();
