import { defineConfig, type ProxyOptions } from "vite";

// LABCOAT_URL: the Labcoat host the dev server proxies /api and /ws to (MSTEST_EXPLORER_URL: the old name, still read)
const backend = (process.env.LABCOAT_URL ?? process.env.MSTEST_EXPLORER_URL ?? "http://localhost:4300").replace(/\/+$/, "");
const backendOrigin = new URL(backend).origin;

// The host refuses cross-origin /ws and non-GET /api/* (docs/contract.md "Round 4 → Security"). The dev server is a
// different origin, so the proxy presents the backend's own origin (changeOrigin fixes Host; this fixes Origin).
const sameOrigin: ProxyOptions["configure"] = (proxy) => {
  proxy.on("proxyReq", (req) => req.setHeader("origin", backendOrigin));
  proxy.on("proxyReqWs", (req) => req.setHeader("origin", backendOrigin));
};

export default defineConfig({
  base: "./",
  build: {
    outDir: "../src/Labcoat/wwwroot",
    emptyOutDir: true,
    chunkSizeWarningLimit: 800,
  },
  server: {
    port: 5174,
    strictPort: true,
    proxy: {
      "/api": { target: backend, changeOrigin: true, configure: sameOrigin },
      "/ws": { target: backend.replace(/^http/, "ws"), ws: true, changeOrigin: true, configure: sameOrigin },
    },
  },
});
