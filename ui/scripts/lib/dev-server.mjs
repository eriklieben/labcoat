// Starts a Vite dev server for the check scripts, so `npm run check:*` works without a server running already.
//   const app = await startApp();        // the Labcoat UI (open it with ?mock for the in-browser mock backend)
//   const gallery = await startGallery(); // the design-system gallery
//   ... app.url ...; await app.close();
// Port: $LABCOAT_CHECK_PORT (default 5300); the next free port is used when it is taken.
import { fileURLToPath } from "node:url";
import { createServer } from "vite";

const ui = fileURLToPath(new URL("../..", import.meta.url));
const port = Number(process.env.LABCOAT_CHECK_PORT ?? 5300);

async function start(configFile, root, { frozen = false } = {}) {
  // frozen: no HMR and no file watching, so edits made while a check runs don't reload its pages
  const live = frozen ? { hmr: false, watch: null } : {};
  const server = await createServer({ configFile, root, logLevel: "error", server: { port, strictPort: false, open: false, ...live } });
  await server.listen();
  const url = server.resolvedUrls?.local?.[0] ?? `http://localhost:${server.config.server.port}/`;
  return { url: url.endsWith("/") ? url : url + "/", close: () => server.close() };
}

export const startApp = (opts) => start(fileURLToPath(new URL("../../vite.config.ts", import.meta.url)), ui, opts);
export const startGallery = (opts) =>
  start(fileURLToPath(new URL("../../design-system/gallery/vite.config.ts", import.meta.url)), fileURLToPath(new URL("../../design-system/gallery", import.meta.url)), opts);

/** Use the base URL given on the command line, or start a dev server with `starter` (closed by the returned stop()). */
export async function baseUrlOr(arg, starter) {
  if (arg && /^\d+$/.test(arg)) arg = `http://localhost:${arg}/`; // a port of a server that is already running
  if (arg && /^https?:/.test(arg)) return { base: arg.endsWith("/") ? arg : arg + "/", stop: async () => {} };
  const server = await starter();
  return { base: server.url, stop: server.close };
}
