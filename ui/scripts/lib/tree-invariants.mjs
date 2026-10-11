// In-page invariant checker for <lc-tree-pane> (used by tree-stress.mjs): `install(page)` starts a per-frame check of
// the tree's rows against a full build of the published store state and of the rendered rows against the virtual
// window; `settledCheck(page)` adds the layout checks (boxes, visibility, hit testing) once no row animates.
// Violations collect in window.__ts.violations (with the last actions from window.__ts.actions).

/** Installed in the page: the checker (runs every frame) and helpers the actions call. */
export async function install(page) {
  await page.evaluate(async () => {
    // tree.ts is pure (any module instance will do). The pane's state is read from the element itself, not from an
    // import of tree-pane.ts: after a hot update Vite serves the app's copy with a ?t= query, and a plain import would
    // be a second instance with its own filter / sort / group signals.
    const T = await import("/src/tree.ts");
    const rowId = (key) => `row-${key.replace(/[^a-zA-Z0-9_-]/g, "_")}`; // tree-pane.ts rowId
    const GROUPS = { namespace: "namespace", project: "project", trait: "trait", outcome: "outcome" };
    /** the grouping, from the group menu button's label ("Group tests by trait") */
    const groupOf = () => GROUPS[/Group tests by (\w+)/.exec(document.querySelector('[aria-label^="Group tests by"]')?.getAttribute("aria-label") ?? "")?.[1]] ?? "namespace";
    const ROW_H = 28;
    const OVERSCAN = 8;
    const st = (window.__ts = { violations: [], frames: 0, checks: 0, staleWindow: 0, actions: [], T });
    const report = (kind, detail) => {
      if (st.violations.length < 40) st.violations.push({ kind, detail, frame: st.frames, actions: st.actions.slice(-8) });
    };
    const rowSig = (r) =>
      `${r.node.key}|${r.index}|${r.depth}|${r.expanded}|${r.hasChildren}|${r.pos}/${r.size}|${r.matched ?? ""}|${r.flat ? 1 : 0}|${r.node.status}|${!!r.node.pending}|${!!r.node.outdated}|${r.node.counts.total}:${r.node.counts.failed}:${r.node.counts.passed}:${r.node.counts.pending}:${r.node.counts.running}:${r.node.counts.queued}`;

    st.check = (settled) => {
      const tp = document.querySelector("lc-tree-pane");
      if (!tp?.isConnected) return;
      const store = window.__labcoat.store;
      const s = store.state$.peek();
      if (s.viewing) return;
      st.checks++;
      // 1. rows == a full build of the published state
      const rows = tp.rows.peek();
      const q = s.quarantined?.length ? new Set(s.quarantined) : undefined;
      const full = T.regroup(T.buildTree(s.projects, s.tests, s.pending, q), groupOf());
      const rk = tp.ranks.peek();
      const exp = T.flatten(full, tp.isExpanded, tp.text.peek(), tp.status.peek(), tp.flat.peek(), { newKeys: tp.newKeys.peek(), rank: rk?.rank, flatRank: rk?.flatRank });
      if (exp.length !== rows.length || exp.some((r, i) => rowSig(r) !== rowSig(rows[i]))) {
        const i = exp.findIndex((r, j) => !rows[j] || rowSig(r) !== rowSig(rows[j]));
        const parentKey = (exp[i] ?? rows[i])?.node.parent?.key;
        const kids = (roots) => (parentKey ? T.findNode(roots, parentKey)?.children.map((c) => c.key.split(/[.|:]/).pop()) : undefined);
        report("rows differ from a full build", {
          at: i,
          expected: exp[i] && rowSig(exp[i]),
          actual: rows[i] && rowSig(rows[i]),
          expLen: exp.length,
          len: rows.length,
          group: groupOf(),
          ranked: !!rk,
          fullSiblings: kids(full),
          treeSiblings: kids(tp.props.roots.peek()),
          testOrder: parentKey ? [...s.tests.keys()].filter((k) => k.includes(parentKey.split(":").pop())).map((k) => `${k.split(/[.|]/).pop()} ${s.tests.get(k).line} ${s.tests.get(k).filePath?.slice(-22)}`) : undefined,
        });
      }
      // 2. the window: start / end are exactly the formula on the pane's scroll position and height; it follows the
      //    real scrollTop (scroll events) and always covers the visible part of the scroller. The pane's height comes
      //    from a ResizeObserver (a frame behind a resize; the overscan covers that), so coverage is checked, not equality.
      const scroller = tp.querySelector(".tree-scroller");
      const top = scroller.scrollTop;
      const vh = scroller.clientHeight;
      const pos = tp.scrollPos.peek();
      const fs = Math.max(0, Math.floor(pos / ROW_H) - OVERSCAN);
      const fe = Math.min(rows.length, Math.ceil((pos + tp.viewH.peek()) / ROW_H) + OVERSCAN);
      if (tp.start.peek() !== fs || tp.end.peek() !== fe) report("window != its formula (stale computed)", { start: tp.start.peek(), end: tp.end.peek(), expected: [fs, fe], scrollPos: pos, viewH: tp.viewH.peek() });
      const firstVisible = Math.floor(top / ROW_H);
      const lastVisible = Math.min(rows.length, Math.ceil((top + vh) / ROW_H));
      const covers = tp.start.peek() <= firstVisible && tp.end.peek() >= lastVisible;
      if (Math.abs(pos - top) > 0.5 || !covers) {
        if (++st.staleWindow > 2) report(covers ? "scroll position stale" : "window doesn't cover the viewport", { start: tp.start.peek(), end: tp.end.peek(), visible: [firstVisible, lastVisible], scrollTop: top, scrollPos: pos, vh, viewH: tp.viewH.peek() });
      } else st.staleWindow = 0;
      // 3. rendered rows == the window (+ the pinned active row)
      const start = tp.start.peek();
      const end = tp.end.peek();
      const want = rows.slice(start, end);
      const sel = tp.props.selectedKey.peek();
      const si = rows.findIndex((r) => r.node.key === sel);
      if (si >= 0 && (si < start || si >= end)) want.push(rows[si]);
      const els = [...scroller.querySelectorAll(".tree-row")];
      const ids = els.map((e) => e.id);
      const wantIds = want.map((r) => rowId(r.node.key));
      if (new Set(ids).size !== ids.length) report("duplicate rows", { ids: ids.filter((x, i) => ids.indexOf(x) !== i) });
      if (ids.join() !== wantIds.join()) {
        const missing = wantIds.filter((x) => !ids.includes(x));
        const extra = ids.filter((x) => !wantIds.includes(x));
        report(missing.length || extra.length ? "rendered rows != window" : "rendered row order != window order", { missing, extra, start, end, n: rows.length });
      }
      const byId = new Map(want.map((r) => [rowId(r.node.key), r]));
      const body = scroller.firstElementChild;
      if (rows.length && body && Math.abs(parseFloat(body.style.height) - rows.length * ROW_H) > 0.5) report("container height", { height: body.style.height, rows: rows.length });
      const bodyTop = body?.getBoundingClientRect().top ?? 0;
      const sb = scroller.getBoundingClientRect();
      const menuOpen = !!document.querySelector(".tree-context-menu:popover-open, dialog[open]");
      for (const el of els) {
        const r = byId.get(el.id);
        if (!r) continue;
        const ty = parseFloat((el.style.translate || "0px 0px").split(" ")[1] ?? "0");
        if (Math.abs(ty - r.index * ROW_H) > 0.5) report("row translate != index", { id: el.id, translate: el.style.translate, index: r.index });
        if (!settled) continue;
        const anims = el.getAnimations({ subtree: false });
        if (anims.length) {
          report("animation left on a settled row", { id: el.id, anims: anims.map((a) => `${a.animationName ?? a.constructor.name} ${a.playState} t=${Math.round(a.currentTime ?? -1)}`) });
          continue;
        }
        const b = el.getBoundingClientRect();
        if (Math.abs(b.top - bodyTop - r.index * ROW_H) > 0.5) report("row box not at index", { id: el.id, top: b.top - bodyTop, index: r.index, transform: getComputedStyle(el).transform, translate: getComputedStyle(el).translate });
        const cs = getComputedStyle(el);
        if (cs.display === "none" || cs.visibility !== "visible" || Number(cs.opacity) < 0.99) report("row not visible", { id: el.id, display: cs.display, visibility: cs.visibility, opacity: cs.opacity, cls: el.className, style: el.getAttribute("style") });
        if (!menuOpen && b.top >= sb.top && b.bottom <= sb.bottom && b.height > 0) {
          const hit = document.elementFromPoint(b.left + 40, b.top + b.height / 2);
          if (hit && scroller.contains(hit) && hit.closest(".tree-row") !== el) report("row covered", { id: el.id, hitRow: hit.closest(".tree-row")?.id, hit: hit.className });
        }
      }
      const ad = scroller.getAttribute("aria-activedescendant");
      if (ad && !document.getElementById(ad)) report("aria-activedescendant dangles", { ad });
    };
    // every frame (after the store's publish of this frame if it was scheduled first; consistent either way)
    const loop = () => {
      st.frames++;
      try {
        st.check(false);
      } catch (e) {
        report("checker threw", String(e?.stack ?? e));
      }
      st.raf = requestAnimationFrame(loop);
    };
    st.raf = requestAnimationFrame(loop);
  });
}

/** Wait until rows have no running animations (or 1.5 s), then run the full (settled) check. */
export async function settledCheck(page) {
  await page.evaluate(async () => {
    const frame = () => new Promise((r) => requestAnimationFrame(() => r()));
    await frame();
    await frame();
    const t0 = performance.now();
    while (performance.now() - t0 < 1500 && [...document.querySelectorAll(".tree-row")].some((e) => e.getAnimations().length)) await frame();
    window.__ts.check(true);
  });
}

