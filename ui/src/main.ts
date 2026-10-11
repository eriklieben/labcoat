import "@eriklieben/labcoat-design-system/styles.css";
import "./styles/base.css";
import "./styles/shell.css";
import "./styles/shared.css";
import { reactiveStats } from "@eriklieben/labcoat-design-system/core";
import { Store, setStore } from "./store";
import { WsTransport } from "./transport";
import { initLiveOutput } from "./live-output";
import "./app";

const params = new URLSearchParams(location.search);
const mockParam = params.get("mock");
const mockOpts = () => ({
  large: mockParam === "large" || params.has("large"),
  scale: mockParam === "huge" || mockParam === "many-projects" ? (mockParam as "huge" | "many-projects") : undefined, // perf / soak checks
  fast: params.has("fast"),
  coverage: params.has("coverage"),
  allPass: params.has("allpass"),
  aspireDashboardUrl: params.has("noaspire") ? undefined : params.get("aspire") || "http://localhost:18888",
});

const store = new Store(mockParam !== null ? "mock" : "ws");
setStore(store);
initLiveOutput(store); // live console output of running tests (before the transport delivers events)

if (mockParam !== null) {
  // The mock backend is only loaded with ?mock: it stays out of the main chunk (and out of production use).
  const [{ MockTransport }, { withShellMock }] = await Promise.all([import("./mock/mockServer"), import("./mock/shell")]);
  const mock = new MockTransport(mockOpts());
  store.attach(withShellMock(mock));
  (window as unknown as { __labcoatMock: unknown }).__labcoatMock = mock; // soak checks drive watch runs through it
} else {
  // Mock data only when asked for explicitly (?mock); otherwise always talk to the real host and
  // show the reconnect banner when it isn't reachable.
  store.attach(new WsTransport());
}

// Debug hook (leak checks, perf scripts): live effect/owner counts.
(window as unknown as { __labcoat: unknown }).__labcoat = { stats: reactiveStats, store };

document.getElementById("root")!.append(document.createElement("lc-app"));
