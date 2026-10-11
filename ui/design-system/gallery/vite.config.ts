// Gallery dev server: `npm run gallery` (in ui/design-system) → http://localhost:5200/
import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";

const repoRoot = fileURLToPath(new URL("../../..", import.meta.url));

export default defineConfig({
  root: fileURLToPath(new URL(".", import.meta.url)),
  base: "./",
  server: { port: 5200, strictPort: true, fs: { allow: [repoRoot] } },
  build: { outDir: "../dist-gallery", emptyOutDir: true },
});
