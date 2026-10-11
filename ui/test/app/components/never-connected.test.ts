import { afterEach, describe, expect, it } from "vitest";
import { h, root, type Disposer } from "@eriklieben/labcoat-design-system";
import { neverConnectedPage } from "../../../src/components/never-connected";
import { flushFrame, makeStore, text } from "../../helpers/app";

let dispose: Disposer | undefined;
afterEach(() => dispose?.());

function page() {
  const { store, transport } = makeStore({ kind: "ws", open: false });
  const el = root((d) => {
    dispose = d;
    return h("div", null, neverConnectedPage());
  });
  document.body.append(el);
  return { el, store, transport };
}

describe("neverConnectedPage", () => {
  it("is a labelled main landmark that names the host it tried", () => {
    const { el } = page();
    const main = el.querySelector("main.never-connected")!;
    expect(main.getAttribute("aria-labelledby")).toBe("nc-title");
    expect(text(main.querySelector("#nc-title"))).toBe("Can't reach the Labcoat host");
    expect(text(main.querySelector(".nc-text"))).toContain(`${location.protocol}//${location.host}`);
    expect(main.querySelector(".nc-logo .lc-logo")).not.toBeNull();
    expect(main.querySelector(".nc-hint kbd")).not.toBeNull();
  });

  it("shows the retry countdown in a status region and retries on demand", async () => {
    const { el, transport } = page();
    const status = el.querySelector(".nc-status")!;
    expect(status.getAttribute("role")).toBe("status");
    expect(text(status)).toBe("Connecting…");
    transport.setStatus("reconnecting", { retryInMs: 2400, attempt: 2 });
    await flushFrame();
    expect(text(status)).toBe("Retrying in 3 s…");
    const retry = [...el.querySelectorAll<HTMLButtonElement>(".nc-actions button")].find((b) => text(b) === "Retry now")!;
    retry.click();
    expect(transport.retryNow).toHaveBeenCalledOnce();
  });
});
