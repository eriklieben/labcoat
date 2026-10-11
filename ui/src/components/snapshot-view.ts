/**
 * Verify snapshot mismatches in the failure detail (contract Round 6): the "Snapshot" section of a failed test with
 * `hasSnapshotMismatch`. Per pending snapshot: the file names, a diff of the verified (−) and received (+) file
 * (text: unified / side by side; images: side by side / onion skin / slider; binary: names only), and Accept (received
 * → verified, then re-run) / Reject (delete received) / Open both in editor. A new snapshot (no verified file) shows the
 * received content with "No verified snapshot yet — accept to create it". Factories, like diff-view.ts.
 *
 *   snapshotPanel(test)        the section (null unless the test has a snapshot mismatch)
 *   snapshotToolbarButton()    "Accept snapshots (N)" for the toolbar, only while tests have pending snapshots
 */
import {
  button,
  codeBlock,
  computed,
  effect,
  h,
  icon,
  keyed,
  onCleanup,
  segmented,
  show,
  signal,
  untracked,
  type Child,
  type ReadSignal,
} from "@eriklieben/labcoat-design-system";
import type { SnapshotFile, SnapshotKind, TestNode, TestSnapshots } from "../contract";
import { lineDiff, type DiffLine } from "../diff";
import { openInEditor, testKey } from "../store";
import { acceptAllSnapshots, acceptSnapshots, fetchSnapshotText, fetchSnapshots, pendingSnapshotTests, rejectSnapshots, snapshotFileUrl } from "../snapshots";
import { fileName } from "../util";
import { lineDiffView } from "./diff-view";
import "./snapshot-view.css";

export type TextMode = "unified" | "split";
export type ImageMode = "split" | "onion" | "slider";

const MODE_KEY = "mste.snapshotModes";
/** Numbers the panel headings, so each panel's aria-labelledby id is unique in the page. */
let headingSeq = 0;
function loadModes(): { text: TextMode; image: ImageMode } {
  try {
    const v = JSON.parse(localStorage.getItem(MODE_KEY) ?? "{}") as { text?: string; image?: string };
    return {
      text: v.text === "split" ? "split" : "unified",
      image: v.image === "onion" || v.image === "slider" ? v.image : "split",
    };
  } catch {
    return { text: "unified", image: "split" };
  }
}
/** The chosen diff views (shared by every snapshot, remembered in this browser). */
export const snapshotModes = signal(loadModes());
function setMode(patch: Partial<{ text: TextMode; image: ImageMode }>) {
  snapshotModes.update((m) => ({ ...m, ...patch }));
  try {
    localStorage.setItem(MODE_KEY, JSON.stringify(snapshotModes.peek()));
  } catch {
    /* storage unavailable: keep it for this page */
  }
}

const plural = (n: number, w: string) => `${n} ${w}${n === 1 ? "" : "s"}`;

/** "Soft" disabled (aria-disabled + click guard): a button keeps the focus while its action runs. */
const soft = (disabled: () => boolean, onclick: () => void) => ({ "aria-disabled": () => disabled(), onclick: () => (disabled() ? undefined : onclick()) });

// ---- side by side ------------------------------------------------------------------------------------------------

export interface SplitRow {
  left?: DiffLine;
  right?: DiffLine;
}
/** Unified diff lines → side-by-side rows: equal lines on both sides, a block's removed lines paired with its added ones. */
export function sideBySideRows(lines: DiffLine[]): SplitRow[] {
  const rows: SplitRow[] = [];
  for (let i = 0; i < lines.length; ) {
    if (lines[i].op === "eq") {
      rows.push({ left: lines[i], right: lines[i] });
      i++;
      continue;
    }
    const dels: DiffLine[] = [];
    const ins: DiffLine[] = [];
    while (i < lines.length && lines[i].op !== "eq") (lines[i].op === "del" ? dels : ins).push(lines[i++]);
    for (let k = 0; k < Math.max(dels.length, ins.length); k++) rows.push({ left: dels[k], right: ins[k] });
  }
  return rows;
}

/** Collapse unchanged runs longer than 2 × context + 2 rows into a fold. */
export function foldRows(rows: SplitRow[], context = 3): (SplitRow | { fold: number; from: number })[] {
  const changed = (r: SplitRow) => r.left?.op !== "eq" || r.right?.op !== "eq";
  const keep = rows.map(() => false);
  rows.forEach((r, i) => {
    if (changed(r)) for (let k = Math.max(0, i - context); k <= Math.min(rows.length - 1, i + context); k++) keep[k] = true;
  });
  const out: (SplitRow | { fold: number; from: number })[] = [];
  for (let i = 0; i < rows.length; ) {
    if (keep[i]) {
      out.push(rows[i++]);
      continue;
    }
    let j = i;
    while (j < rows.length && !keep[j]) j++;
    if (j - i <= 2) for (let k = i; k < j; k++) out.push(rows[k]);
    else out.push({ fold: j - i, from: i });
    i = j;
  }
  return out;
}

/** A diff segment: changed text marked. */
const segmentEl = (s: { changed?: boolean; text: string }) => h(s.changed ? "mark" : "span", null, s.text);

/** What a screen reader hears before a changed line, per side. */
const CHANGED_LABEL = { exp: "changed, verified: ", act: "changed, received: " } as const;

/** One side's code cell: the line (changed segments marked, announced as changed), or "no line" for screen readers. */
function lineCode(l: DiffLine | undefined, side: "exp" | "act"): Child {
  if (!l) return h("span", { class: "visually-hidden" }, "no line");
  const changed = l.op === "eq" ? null : h("span", { class: "visually-hidden" }, CHANGED_LABEL[side]);
  return [changed, l.segments ? l.segments.map(segmentEl) : l.text || " "];
}

function splitView(lines: DiffLine[]): HTMLElement {
  const rows = sideBySideRows(lines);
  const open = signal<ReadonlySet<number>>(new Set());
  const cell = (l: DiffLine | undefined, side: "exp" | "act") => {
    const cls = l && l.op !== "eq" ? side : "";
    const n = side === "exp" ? l?.a : l?.b;
    return [
      h("td", { class: `snap-ln ${cls}`, "aria-hidden": "true" }, n != null ? String(n) : ""),
      h("td", { class: `snap-code ${cls}` }, lineCode(l, side)),
    ];
  };
  return h(
    "div",
    { class: "snap-split-scroll", tabIndex: 0, role: "region", "aria-label": "Side-by-side diff: verified and received" },
    h(
      "table",
      { class: "snap-split mono" },
      h("caption", { class: "visually-hidden" }, "Verified snapshot (left) and received output (right)"),
      h(
        "thead",
        { class: "visually-hidden" },
        h("tr", null, h("th", { scope: "col" }, "Line"), h("th", { scope: "col" }, "Verified"), h("th", { scope: "col" }, "Line"), h("th", { scope: "col" }, "Received")),
      ),
      h("tbody", null, () =>
        foldRows(rows).flatMap((r) => {
          if ("fold" in r) {
            if (open().has(r.from)) return rows.slice(r.from, r.from + r.fold).map((x) => h("tr", null, cell(x.left, "exp"), cell(x.right, "act")));
            return [
              h(
                "tr",
                { class: "snap-fold-row" },
                h(
                  "td",
                  { colSpan: 4 },
                  h(
                    "button",
                    { class: "diff-fold", onclick: () => open.update((s) => new Set(s).add(r.from)) },
                    icon("chevronDown", { size: 12 }),
                    ` ${r.fold} unchanged line${r.fold === 1 ? "" : "s"}`,
                  ),
                ),
              ),
            ];
          }
          return [h("tr", null, cell(r.left, "exp"), cell(r.right, "act"))];
        }),
      ),
    ),
  );
}

// ---- text / image / binary bodies --------------------------------------------------------------------------------

function textDiff(snap: SnapshotFile): Child {
  const state = signal<{ status: "loading" } | { status: "error"; message: string } | { status: "ready"; verified?: string; received: string }>({ status: "loading" });
  const ctrl = new AbortController();
  onCleanup(() => ctrl.abort());
  Promise.all([snap.isNew ? Promise.resolve(undefined) : fetchSnapshotText(snap.verifiedPath, ctrl.signal), fetchSnapshotText(snap.receivedPath, ctrl.signal)]).then(
    ([verified, received]) => state.set(received === undefined ? { status: "error", message: "The received file is gone (accepted, rejected or re-run elsewhere)." } : { status: "ready", verified, received }),
    (e) => !ctrl.signal.aborted && state.set({ status: "error", message: `Could not load the snapshot files: ${(e as Error).message}` }),
  );
  return keyed(
    () => state().status,
    (status) => {
      const s = untracked(state);
      if (status === "loading") return h("div", { class: "detail-note", role: "status" }, "Loading the snapshot…");
      if (s.status === "error") return h("div", { class: "detail-note snap-error" }, icon("warning", { size: 14 }), " ", s.message);
      if (s.status !== "ready") return null;
      const received = s.received.replaceAll("\r\n", "\n");
      if (s.verified === undefined)
        return codeBlock({ code: received, label: `Received output: ${fileName(snap.receivedPath)}`, lineNumbers: true, wrap: true });
      const lines = lineDiff(s.verified.replaceAll("\r\n", "\n"), received);
      const added = lines.filter((l) => l.op === "ins").length;
      const removed = lines.filter((l) => l.op === "del").length;
      const identical = added === 0 && removed === 0;
      return h(
        "div",
        { class: "diff-block snap-diff" },
        h(
          "div",
          { class: "diff-head" },
          h("span", { class: "diff-legend exp" }, h("span", { class: "diff-sign" }, "−"), " Verified"),
          h("span", { class: "diff-legend act" }, h("span", { class: "diff-sign" }, "+"), " Received"),
          h("span", { class: "diff-stats mono" }, h("span", { class: "exp" }, `−${removed}`), " ", h("span", { class: "act" }, `+${added}`)),
          segmented<TextMode>({
            label: "Diff view",
            size: "small",
            class: "snap-mode",
            value: () => snapshotModes().text,
            onChange: (v) => setMode({ text: v }),
            options: [
              { value: "unified", label: "Unified" },
              { value: "split", label: "Side by side" },
            ],
          }),
        ),
        identical
          ? h("div", { class: "detail-note snap-identical" }, "The files differ only in encoding or whitespace Verify ignores.")
          : keyed(
              () => snapshotModes().text,
              (mode) => {
                if (mode === "split") return splitView(lines);
                const el = lineDiffView(lines, { del: "verified", ins: "received" });
                el.tabIndex = 0;
                return el;
              },
            ),
      );
    },
  );
}

function imageDiff(snap: SnapshotFile): Child {
  const urls = signal<{ verified?: string; received?: string } | undefined>(undefined);
  Promise.all([snap.isNew ? undefined : snapshotFileUrl(snap.verifiedPath), snapshotFileUrl(snap.receivedPath)]).then(
    ([verified, received]) => urls.set({ verified, received }),
    () => urls.set({}),
  );
  const amount = signal(50);
  const imgName = (which: "Verified" | "Received") => (which === "Verified" ? snap.name : fileName(snap.receivedPath));
  const img = (src: string | undefined, which: "Verified" | "Received", cls = "") =>
    src
      ? h("img", { class: `snap-img ${cls}`, src, alt: `${which} snapshot ${imgName(which)}`, draggable: false })
      : h("div", { class: "snap-img-missing" }, `No ${which.toLowerCase()} image`);
  return show(urls, (u) => {
    const { verified, received } = u();
    if (!verified)
      return h("figure", { class: "snap-figure" }, img(received, "Received"), h("figcaption", null, "Received"));
    return h(
      "div",
      { class: "snap-image-diff" },
      h(
        "div",
        { class: "diff-head" },
        h("span", { class: "diff-legend exp" }, "Verified"),
        h("span", { class: "diff-legend act" }, "Received"),
        segmented<ImageMode>({
          label: "Image comparison",
          size: "small",
          class: "snap-mode",
          value: () => snapshotModes().image,
          onChange: (v) => setMode({ image: v }),
          options: [
            { value: "split", label: "Side by side" },
            { value: "onion", label: "Onion skin" },
            { value: "slider", label: "Slider" },
          ],
        }),
      ),
      keyed(
        () => snapshotModes().image,
        (mode) => {
          if (mode === "split")
            return h(
              "div",
              { class: "snap-images" },
              h("figure", { class: "snap-figure" }, img(verified, "Verified"), h("figcaption", null, "Verified")),
              h("figure", { class: "snap-figure" }, img(received, "Received"), h("figcaption", null, "Received")),
            );
          const onion = mode === "onion";
          return h(
            "div",
            { class: "snap-overlay-wrap" },
            h(
              "div",
              { class: `snap-overlay ${mode}`, style: { "--amount": () => `${amount()}%`, "--opacity": () => String(amount() / 100) } },
              img(verified, "Verified", "base"),
              img(received, "Received", "top"),
              onion ? null : h("div", { class: "snap-divider", "aria-hidden": "true" }),
            ),
            h(
              "label",
              { class: "snap-range" },
              h("span", { "aria-hidden": "true" }, "Verified"),
              h("input", {
                type: "range",
                min: "0",
                max: "100",
                value: String(amount.peek()),
                "aria-label": onion ? "Opacity of the received image" : "Divider position: verified on the left, received on the right",
                "aria-valuetext": () => `${amount()}% received`,
                oninput: (e: Event) => amount.set(Number((e.target as HTMLInputElement).value)),
              }),
              h("span", { "aria-hidden": "true" }, "Received"),
            ),
          );
        },
      ),
    );
  });
}

// ---- the section -------------------------------------------------------------------------------------------------

const KIND_LABELS: Record<SnapshotKind, string> = { text: "Text", image: "Image", binary: "Binary" };

/** The comparison: a text diff, the images side by side, or a note for a binary snapshot. */
function snapshotDiff(snap: SnapshotFile): Child {
  if (snap.kind === "text") return textDiff(snap);
  if (snap.kind === "image") return imageDiff(snap);
  return h("div", { class: "detail-note" }, "Binary snapshot: it can't be shown here. Open both files in the editor to compare them.");
}

function snapshotItem(t: () => TestNode, snap: SnapshotFile, busy: ReturnType<typeof signal<boolean>>, run: (f: () => Promise<unknown>) => void): HTMLElement {
  const target = () => ({ projectId: t().projectId, id: t().id, names: [snap.name] });
  return h(
    "article",
    { class: "snapshot-item", "aria-label": `Snapshot ${snap.name}` },
    h(
      "header",
      { class: "snapshot-head" },
      icon(snap.kind === "image" ? "dataPie" : "document", { size: 16, class: "snap-icon" }),
      h(
        "div",
        { class: "snapshot-names" },
        h("div", { class: "snap-name" }, h("span", { class: "snap-label" }, "Verified"), h("code", { title: snap.verifiedPath }, snap.name)),
        h("div", { class: "snap-name" }, h("span", { class: "snap-label" }, "Received"), h("code", { title: snap.receivedPath }, fileName(snap.receivedPath))),
      ),
      snap.isNew ? h("span", { class: "badge snap-badge-new" }, "New") : null,
      snap.kind === "text" ? null : h("span", { class: "badge snap-badge-kind" }, KIND_LABELS[snap.kind]),
    ),
    snap.isNew
      ? h("div", { class: "messagebar mb-info snap-new", role: "note" }, icon("info", { size: 16, class: "mb-icon" }), h("div", { class: "mb-body" }, "No verified snapshot yet — accept to create it."))
      : null,
    snapshotDiff(snap),
    h(
      "div",
      { class: "snapshot-actions" },
      button(
        {
          variant: "accent",
          icon: "checkmark",
          title: snap.isNew ? "Create the verified snapshot from the received output, then re-run the test" : "Replace the verified snapshot with the received output, then re-run the test",
          ...soft(busy, () => run(() => acceptSnapshots(target()))),
        },
        " Accept",
      ),
      button(
        { icon: "dismiss", title: "Delete the received file and keep the verified snapshot", ...soft(busy, () => run(() => rejectSnapshots(target()))) },
        " Reject",
      ),
      button(
        {
          icon: "code",
          title: snap.isNew ? "Open the received file in the editor" : "Open the verified and the received file in the editor",
          onclick: () => {
            if (!snap.isNew) void openInEditor(snap.verifiedPath);
            void openInEditor(snap.receivedPath);
          },
        },
        snap.isNew ? " Open in editor" : " Open both in editor",
      ),
    ),
  );
}

/** The "Snapshot" section of a failed test's details; null while the test has no pending snapshot mismatch. */
export function snapshotPanel(test: ReadSignal<TestNode>): Child {
  const sig = computed(() => {
    const t = test();
    return t.hasSnapshotMismatch && t.outcome !== "running" && t.outcome !== "queued" ? `${testKey(t)}\u0000${t.lastRunAt ?? ""}` : "";
  });
  return keyed(sig, (s) => (s ? panel(test) : null));
}

function panel(test: ReadSignal<TestNode>): HTMLElement {
  const data = signal<{ status: "loading" } | { status: "error"; message: string } | { status: "ready"; snaps: TestSnapshots }>({ status: "loading" });
  const busy = signal(false);
  const ctrl = new AbortController();
  onCleanup(() => ctrl.abort());
  fetchSnapshots(untracked(test), ctrl.signal).then(
    (snaps) => data.set({ status: "ready", snaps }),
    (e) => !ctrl.signal.aborted && data.set({ status: "error", message: (e as Error).message }),
  );
  const run = (f: () => Promise<unknown>) => {
    busy.set(true);
    void f().finally(() => busy.set(false));
  };
  const others = computed(() => pendingSnapshotTests().length);
  const count = computed(() => {
    const d = data();
    return d.status === "ready" ? d.snaps.snapshots.length : 0;
  });
  const headingId = `snap-h-${++headingSeq}`;
  return h(
    "section",
    { class: "detail-section snapshot-panel", "aria-labelledby": headingId },
    h(
      "div",
      { class: "section-header snapshot-header" },
      h("h3", { id: headingId, class: "snapshot-title" }, icon("document", { size: 16 }), " Snapshot", () => (count() > 1 ? ` (${count()})` : "")),
      h(
        "span",
        { class: "section-extra" },
        () =>
          count() > 1
            ? button({ size: "small", icon: "checkmark", title: "Accept every snapshot of this test, then re-run it", ...soft(busy, () => run(() => acceptSnapshots({ projectId: test.peek().projectId, id: test.peek().id }))) }, ` Accept ${count()}`)
            : null,
        () =>
          others() > 1
            ? button(
                { size: "small", icon: "checkmark", title: "Accept the pending snapshots of every test, then re-run them", ...soft(busy, () => run(acceptAllSnapshots)) },
                ` Accept all snapshots (${plural(others(), "test")})`,
              )
            : null,
      ),
    ),
    keyed(
      () => data().status,
      (status) => {
        const d = untracked(data);
        if (status === "loading") return h("div", { class: "detail-note", role: "status" }, "Looking for snapshot files…");
        if (d.status === "error") return h("div", { class: "detail-note snap-error" }, icon("warning", { size: 14 }), ` Could not load the snapshots: ${d.message}`);
        if (d.status !== "ready") return null;
        if (!d.snaps.snapshots.length)
          return h(
            "div",
            { class: "detail-note" },
            "No received snapshot files are left (they were accepted, rejected or removed). Run the test again to check it.",
          );
        return h(
          "div",
          { class: "snapshot-list" },
          d.snaps.snapshots.map((s) => snapshotItem(test, s, busy, run)),
        );
      },
    ),
  );
}

/** Toolbar: "Accept snapshots (N)" while tests have pending snapshot mismatches (nothing otherwise). */
export function snapshotToolbarButton(disabled: () => boolean = () => false): Child {
  const n = computed(() => pendingSnapshotTests().length);
  const busy = signal(false);
  effect(() => {
    if (!n()) untracked(() => busy.set(false));
  });
  return show(
    () => n() > 0,
    () =>
      button(
        {
          icon: "checkmark",
          class: "snap-toolbar-btn",
          title: () => `Accept the pending Verify snapshots of ${plural(n(), "test")} (received → verified), then re-run them`,
          ...soft(
            () => busy() || disabled(),
            () => {
              busy.set(true);
              void acceptAllSnapshots().finally(() => busy.set(false));
            },
          ),
        },
        " Accept snapshots",
        h("span", { class: "btn-count", "aria-hidden": "true" }, () => n()),
        h("span", { class: "visually-hidden" }, () => ` (${n()})`),
      ),
  );
}
