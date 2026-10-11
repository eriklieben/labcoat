/**
 * Full-page "can't reach the host" state (P2-2): shown instead of the whole shell when the UI never got a connection
 * (no empty panes, no double spinners). A connection lost later keeps the shell with an "Offline" state instead.
 */
import { brandLogo, button, h, kbd, type Child } from "@eriklieben/labcoat-design-system";
import { getStore } from "../store";
import "./never-connected.css";

export function neverConnectedPage(): Child {
  const store = getStore();
  const $ = store.$;
  const host = location.host || "localhost";
  return h(
    "main",
    { class: "never-connected", "aria-labelledby": "nc-title" },
    h(
      "div",
      { class: "nc-card" },
      h("div", { class: "nc-logo" }, brandLogo({ size: 44 })),
      h("h1", { id: "nc-title", class: "nc-title" }, "Can't reach the Labcoat host"),
      h(
        "p",
        { class: "nc-text" },
        "This page talks to the Labcoat host at ",
        h("code", { class: "mono" }, `${location.protocol}//${host}`),
        " (",
        h("code", { class: "mono" }, "/api"),
        ", ",
        h("code", { class: "mono" }, "/ws"),
        "), but nothing answered.",
      ),
      h("p", { class: "nc-status", role: "status" }, h("span", { class: "spinner" }), " ", () => {
        const r = $.retryInMs();
        return r ? `Retrying in ${Math.ceil(r / 1000)} s…` : "Connecting…";
      }),
      h("div", { class: "nc-actions" }, button({ variant: "accent", icon: "arrowSync", onclick: () => store.transport.retryNow?.() }, " Retry now")),
      h(
        "div",
        { class: "nc-help" },
        h("h2", { class: "nc-help-title" }, "Start it"),
        h("p", "From your solution or test project directory:"),
        h("pre", { class: "nc-code mono" }, "labcoat path/to/MyApp.Tests"),
        h(
          "p",
          "Installed with ",
          h("code", { class: "mono" }, "dotnet tool install -g Labcoat"),
          ". Under Aspire, start the AppHost — Labcoat is one of its resources. In development, run ",
          h("code", { class: "mono" }, "dotnet run --project src/Labcoat -- samples/SampleTests"),
          ".",
        ),
        h("p", { class: "nc-hint" }, "This page reconnects by itself once the host is up. ", kbd("Mod+R"), " reloads."),
      ),
    ),
  );
}
