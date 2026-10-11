// Expected/actual extraction from assertion messages + a small LCS diff (lines and characters).
// Client-side for now; if the host ever reports structured expected/actual (MTP "assert.expected"/"assert.actual"),
// feed them to `buildDiff` directly.

export interface ParsedAssertion {
  expected: string;
  actual: string;
  /** message text outside the expected/actual block (assert summary + user message) */
  summary: string;
  /** the framework elided part of a value ("...") so this is only a window around the first difference */
  truncated: boolean;
  /** values were quoted strings (vs. ToString() of objects / numbers) */
  quoted: boolean;
  /** which ends of each value were elided */
  cut?: { expected: Cut; actual: Cut };
}

export interface Cut {
  start: boolean;
  end: boolean;
}

/** MSTest renders control chars as Unicode "control pictures" (␊ = LF, ␍ = CR, ␉ = TAB …). */
const unpicture = (s: string) => s.replace(/[␀-␟]/g, (c) => String.fromCodePoint(c.codePointAt(0)! - 0x2400));

function unquote(raw: string): { value: string; quoted: boolean; truncated: boolean; cut: Cut } {
  let v = raw.trim();
  let quoted = false;
  if (v.length >= 2 && v.startsWith('"') && v.endsWith('"')) {
    v = v.slice(1, -1);
    quoted = true;
  }
  const cut = { start: false, end: false };
  if (quoted && v.startsWith("...")) {
    v = v.slice(3);
    cut.start = true;
  }
  if (quoted && v.endsWith("...")) {
    v = v.slice(0, -3);
    cut.end = true;
  }
  return { value: unpicture(v), quoted, truncated: cut.start || cut.end, cut };
}

/**
 * Recognizes:
 *  - MSTest object form:   `Assert.AreEqual failed. Expected:<10>. Actual:<9>. 'expected' expression: …`
 *    (values may span lines; ToString() of the objects, never truncated)
 *  - MSTest 4 / NUnit string form:  `Expected: "sent"` / `But was:  "queued"` (+ a `---^` caret line)
 *  - xUnit form:           `Expected: 10` / `Actual:   9` (with optional ↓/↑ marker lines)
 */
export function parseAssertion(message: string | undefined): ParsedAssertion | undefined {
  if (!message) return undefined;
  const msg = message.replaceAll("\r\n", "\n");
  return parseObjectForm(msg) ?? parseLineForm(msg) ?? parseLocalizedObjectForm(msg) ?? parseCaretForm(msg);
}

const EXPECTED_EXPRESSION = "'expected' expression: '";
const ACTUAL_EXPRESSION = "', 'actual' expression: '";

/**
 * `text` without MSTest's "'expected' expression: '…', 'actual' expression: '…'" suffix and the whitespace before it.
 * The same as `text.replace(/\s*'expected' expression: '.*?', 'actual' expression: '.*?'\.?/s, "")` (`dot: "optional"`)
 * or with `'\./s` at the end (`dot: "required"`), in linear time.
 */
export function stripExpressions(text: string, dot: "optional" | "required"): string {
  const start = text.indexOf(EXPECTED_EXPRESSION);
  if (start < 0) return text;
  const actual = text.indexOf(ACTUAL_EXPRESSION, start + EXPECTED_EXPRESSION.length);
  if (actual < 0) return text;
  const valueStart = actual + ACTUAL_EXPRESSION.length;
  let end: number;
  if (dot === "required") {
    const close = text.indexOf("'.", valueStart);
    if (close < 0) return text;
    end = close + 2;
  } else {
    const close = text.indexOf("'", valueStart);
    if (close < 0) return text;
    end = text[close + 1] === "." ? close + 2 : close + 1;
  }
  return text.slice(0, start).trimEnd() + text.slice(end);
}

/** MSTest object form: `Assert.AreEqual failed. Expected:<10>. Actual:<9>. 'expected' expression: …` */
function parseObjectForm(msg: string): ParsedAssertion | undefined {
  const eStart = msg.indexOf("Expected:<");
  if (eStart < 0) return undefined;
  const sep = msg.indexOf(">. Actual:<", eStart);
  if (sep <= 0) return undefined;
  const aStart = sep + ">. Actual:<".length;
  const aEnd = actualEnd(msg, aStart);
  if (aEnd < aStart) return undefined;
  const expected = msg.slice(eStart + "Expected:<".length, sep);
  const actual = msg.slice(aStart, aEnd);
  const rest = stripExpressions(msg.slice(aEnd + 2), "optional");
  const summary = (msg.slice(0, eStart).trim() + " " + rest.trim()).trim();
  return { expected, actual, summary, truncated: false, quoted: false };
}

/** The actual value ends at ">." followed by the expression suffix, the end, or whitespace (user message); -1 if none. */
function actualEnd(msg: string, aStart: number): number {
  const beforeExpression = msg.indexOf(">. 'expected' expression:", aStart);
  if (beforeExpression >= 0) return beforeExpression;
  const m = />\.(?=\s|$)/g;
  m.lastIndex = aStart;
  let last = -1;
  for (let r = m.exec(msg); r; r = m.exec(msg)) last = r.index;
  return last;
}

// `/^\s*(?:But was|Actual):\s*(.*)$/` without the trade between `\s*` and `.*`: the value starts after the whitespace
const ACTUAL_LINE_RE = /^\s*(?:But was|Actual):\s*((?:\S.*)?)$/;

/** Line based: `Expected: X` / (`But was:`|`Actual:`) `Y`, with optional ↓/↑ marker lines and a `---^` caret line. */
function parseLineForm(msg: string): ParsedAssertion | undefined {
  const lines = msg.split("\n");
  for (let i = 0; i < lines.length; i++) {
    const em = /^\s*Expected:\s?(.*)$/.exec(lines[i]);
    if (!em) continue;
    let j = i + 1;
    while (j < lines.length && /^\s*[↓↑]/.test(lines[j])) j++;
    const am = j < lines.length ? ACTUAL_LINE_RE.exec(lines[j]) : null;
    if (!am) continue;
    const e = unquote(em[1]);
    const a = unquote(am[1]);
    let end = j + 1;
    while (end < lines.length && /^\s*(-+\^|[↓↑])/.test(lines[end])) end++;
    const kept = [...lines.slice(0, i), ...lines.slice(end)].filter((l) => !/^\s*[↓↑]/.test(l)).join("\n");
    return {
      expected: e.value,
      actual: a.value,
      summary: stripExpressions(kept, "required").trim(),
      truncated: e.truncated || a.truncated,
      quoted: e.quoted || a.quoted,
      cut: { expected: e.cut, actual: a.cut },
    };
  }
  return undefined;
}

// ---- other UI languages ---------------------------------------------------------------------------
// The tests run in the user's UI language (as under `dotnet test`), so MSTest's labels may be localized:
// `Erwartet:<1>. Tatsächlich:<2>.`, `Oczekiwana:<1>. Rzeczywista:<2>.`, `Erwartet:  "sent"` / `War aber:  "queued"` / `---^`.
// These fallbacks recognize the structure with any short label.

const MAX_LABEL = 40;

/** A short label: no markup, quotes, colons or line breaks (`Tatsächlich`, `Se esperaba`, `Réel\u00a0`). */
function isLabel(s: string): boolean {
  const t = s.trim();
  return t.length > 0 && t.length <= MAX_LABEL && !/[<>:"\n.。]/.test(t);
}

/** `<label>:<expected>. <label>:<actual>.` with localized labels (also `>。` as in Japanese / Chinese). */
function parseLocalizedObjectForm(msg: string): ParsedAssertion | undefined {
  const eOpen = msg.indexOf(":<");
  if (eOpen <= 0) return undefined;
  let eStart = eOpen;
  while (eStart > 0 && eOpen - eStart < MAX_LABEL && !".。\n\"".includes(msg[eStart - 1])) eStart--;
  if (!isLabel(msg.slice(eStart, eOpen))) return undefined;
  for (let sep = msg.indexOf(">", eOpen + 2); sep >= 0; sep = msg.indexOf(">", sep + 1)) {
    if (msg[sep + 1] !== "." && msg[sep + 1] !== "。") continue;
    const aOpen = msg.indexOf(":<", sep + 2);
    if (aOpen < 0) return undefined;
    if (!isLabel(msg.slice(sep + 2, aOpen))) continue;
    const aStart = aOpen + 2;
    const aEnd = localizedActualEnd(msg, aStart);
    if (aEnd < aStart) return undefined;
    const summary = (msg.slice(0, eStart).trim() + " " + msg.slice(aEnd + 2).trim()).trim();
    return { expected: msg.slice(eOpen + 2, sep), actual: msg.slice(aStart, aEnd), summary, truncated: false, quoted: false };
  }
  return undefined;
}

/** The actual value ends at the last `>.` / `>。` followed by whitespace or the end; -1 if none. */
function localizedActualEnd(msg: string, aStart: number): number {
  const m = />[.。](?=\s|$)/g;
  m.lastIndex = aStart;
  let last = -1;
  for (let r = m.exec(msg); r; r = m.exec(msg)) last = r.index;
  return last;
}

/** `label: value` (the value after the first colon and its whitespace); undefined when the line is not one. */
function labelled(line: string): string | undefined {
  const colon = line.indexOf(":");
  if (colon <= 0 || !isLabel(line.slice(0, colon))) return undefined;
  const value = line.slice(colon + 1).trim();
  return value.length > 0 ? value : undefined;
}

/** MSTest 4's string form in another language: two `label: "value"` lines followed by its `---^` caret line. */
function parseCaretForm(msg: string): ParsedAssertion | undefined {
  const lines = msg.split("\n");
  for (let i = 0; i + 2 < lines.length; i++) {
    if (!/^\s*-+\^\s*$/.test(lines[i + 2])) continue;
    const ev = labelled(lines[i]);
    const av = labelled(lines[i + 1]);
    if (ev === undefined || av === undefined) continue;
    const e = unquote(ev);
    const a = unquote(av);
    return {
      expected: e.value,
      actual: a.value,
      summary: [...lines.slice(0, i), ...lines.slice(i + 3)].join("\n").trim(),
      truncated: e.truncated || a.truncated,
      quoted: e.quoted || a.quoted,
      cut: { expected: e.cut, actual: a.cut },
    };
  }
  return undefined;
}

// ---- diff ------------------------------------------------------------------------------------

export type Op = "eq" | "del" | "ins";
export interface Edit<T> {
  op: Op;
  value: T;
}

const MAX_CELLS = 4_000_000;

/** LCS diff with common prefix/suffix trimming. Falls back to "replace all" for huge inputs. */
export function diffSeq<T>(a: readonly T[], b: readonly T[], eq: (x: T, y: T) => boolean = (x, y) => x === y): Edit<T>[] {
  let pre = 0;
  while (pre < a.length && pre < b.length && eq(a[pre], b[pre])) pre++;
  let suf = 0;
  while (suf < a.length - pre && suf < b.length - pre && eq(a[a.length - 1 - suf], b[b.length - 1 - suf])) suf++;
  const A = a.slice(pre, a.length - suf);
  const B = b.slice(pre, b.length - suf);
  const out: Edit<T>[] = [];
  for (let i = 0; i < pre; i++) out.push({ op: "eq", value: a[i] });
  if (A.length && B.length && A.length * B.length <= MAX_CELLS) lcsEdits(A, B, eq, out);
  else {
    for (const v of A) out.push({ op: "del", value: v });
    for (const v of B) out.push({ op: "ins", value: v });
  }
  for (let i = a.length - suf; i < a.length; i++) out.push({ op: "eq", value: a[i] });
  return out;
}

/** The LCS edits of two non-empty sequences, appended to `out`. */
function lcsEdits<T>(A: readonly T[], B: readonly T[], eq: (x: T, y: T) => boolean, out: Edit<T>[]) {
  const n = A.length;
  const m = B.length;
  // lcs[i][j] = LCS length of A[i..] and B[j..], flattened.
  const w = m + 1;
  const lcs = new Uint32Array((n + 1) * w);
  for (let i = n - 1; i >= 0; i--)
    for (let j = m - 1; j >= 0; j--)
      lcs[i * w + j] = eq(A[i], B[j]) ? lcs[(i + 1) * w + j + 1] + 1 : Math.max(lcs[(i + 1) * w + j], lcs[i * w + j + 1]);
  let i = 0,
    j = 0;
  while (i < n && j < m) {
    if (eq(A[i], B[j])) {
      out.push({ op: "eq", value: A[i] });
      i++;
      j++;
    } else if (lcs[(i + 1) * w + j] >= lcs[i * w + j + 1]) out.push({ op: "del", value: A[i++] });
    else out.push({ op: "ins", value: B[j++] });
  }
  while (i < n) out.push({ op: "del", value: A[i++] });
  while (j < m) out.push({ op: "ins", value: B[j++] });
}

export interface Segment {
  text: string;
  changed: boolean;
}

/** Words (letters/digits/_), whitespace runs, or single punctuation characters. */
const TOKEN = /[\p{L}\p{N}_]+|\s+|[^\p{L}\p{N}_\s]/gu;
const isSingleToken = (s: string) => (s.match(TOKEN) ?? []).length <= 1;

/**
 * Word (default) or character diff of two strings -> highlighted segments per side. Tiny equal islands (< 3 chars) between changes
 * are folded into the change so "sent"/"queued" reads as one replacement rather than confetti.
 */
export function charDiff(expected: string, actual: string, unit: "word" | "char" = "word"): { expected: Segment[]; actual: Segment[] } {
  const split = (s: string) => (unit === "char" ? Array.from(s) : (s.match(TOKEN) ?? []));
  const edits = diffSeq(split(expected), split(actual));
  // group into runs
  type Run = { op: Op; text: string };
  const runs: Run[] = [];
  for (const e of edits) {
    const last = runs.at(-1);
    if (last?.op === e.op) last.text += e.value;
    else runs.push({ op: e.op, text: e.value });
  }
  for (let i = 1; i < runs.length - 1; i++) {
    const r = runs[i];
    if (r.op === "eq" && Array.from(r.text).length < 3 && runs[i - 1].op !== "eq" && runs[i + 1].op !== "eq") r.op = "del+ins" as Op;
  }
  const ex: Segment[] = [];
  const ac: Segment[] = [];
  const push = (side: Segment[], text: string, changed: boolean) => {
    if (!text) return;
    const last = side.at(-1);
    if (last?.changed === changed) last.text += text;
    else side.push({ text, changed });
  };
  for (const r of runs) {
    if (r.op === "eq") {
      push(ex, r.text, false);
      push(ac, r.text, false);
    } else if (r.op === "del") push(ex, r.text, true);
    else if (r.op === "ins") push(ac, r.text, true);
    else {
      push(ex, r.text, true);
      push(ac, r.text, true);
    }
  }
  return { expected: ex, actual: ac };
}

export interface DiffLine {
  op: Op;
  text: string;
  /** 1-based line numbers in expected / actual */
  a?: number;
  b?: number;
  /** intra-line highlights for a changed line paired with its counterpart */
  segments?: Segment[];
}

export function lineDiff(expected: string, actual: string): DiffLine[] {
  const A = expected.split("\n");
  const B = actual.split("\n");
  const edits = diffSeq(A, B);
  const out: DiffLine[] = [];
  let a = 0,
    b = 0;
  for (let i = 0; i < edits.length; ) {
    if (edits[i].op === "eq") {
      out.push({ op: "eq", text: edits[i].value, a: ++a, b: ++b });
      i++;
      continue;
    }
    // a block of changes: dels then ins (LCS emits them in that order per block, but be lenient)
    const dels: string[] = [];
    const ins: string[] = [];
    while (i < edits.length && edits[i].op !== "eq") {
      if (edits[i].op === "del") dels.push(edits[i].value);
      else ins.push(edits[i].value);
      i++;
    }
    const { delSeg, insSeg } = pairChangedLines(dels, ins);
    dels.forEach((t, k) => out.push({ op: "del", text: t, a: ++a, segments: delSeg[k] }));
    ins.forEach((t, k) => out.push({ op: "ins", text: t, b: ++b, segments: insSeg[k] }));
  }
  return out;
}

/** Pairs removed/added lines by similarity (best pairs first, keeping order) for intra-line highlights. */
function pairChangedLines(dels: string[], ins: string[]) {
  const delSeg: (Segment[] | undefined)[] = [];
  const insSeg: (Segment[] | undefined)[] = [];
  if (dels.length * ins.length > 400) return { delSeg, insSeg };
  const cand: { d: number; k: number; s: number }[] = [];
  dels.forEach((x, d) =>
    ins.forEach((y, k) => {
      const sc = similarity(x, y);
      if (sc >= 0.5) cand.push({ d, k, s: sc });
    }),
  );
  cand.sort((p, q) => q.s - p.s);
  const taken: { d: number; k: number }[] = [];
  for (const c of cand) {
    if (taken.some((t) => t.d === c.d || t.k === c.k || (t.d < c.d) !== (t.k < c.k))) continue;
    taken.push(c);
    const cd = charDiff(dels[c.d], ins[c.k]);
    delSeg[c.d] = cd.expected;
    insSeg[c.k] = cd.actual;
  }
  return { delSeg, insSeg };
}

/** 0..1: share of characters in common (LCS) relative to the longer line. */
function similarity(x: string, y: string) {
  if (!x.length && !y.length) return 1;
  if (x.length * y.length > 40_000) return 0;
  const eqChars = diffSeq(Array.from(x), Array.from(y)).filter((e) => e.op === "eq").length;
  return eqChars / Math.max(x.length, y.length);
}

/** Pretty-print when both sides are JSON objects/arrays; undefined otherwise. */
export function prettyJson(expected: string, actual: string): { expected: string; actual: string } | undefined {
  const parse = (s: string) => {
    const t = s.trim();
    if (!/^[[{]/.test(t)) return undefined;
    try {
      return JSON.stringify(JSON.parse(t), null, 2);
    } catch {
      return undefined;
    }
  };
  const e = parse(expected);
  const a = parse(actual);
  return e !== undefined && a !== undefined ? { expected: e, actual: a } : undefined;
}

export type DiffModel =
  | { kind: "inline"; expected: Segment[]; actual: Segment[] }
  | { kind: "lines"; json: boolean; lines: DiffLine[]; added: number; removed: number };

export function buildDiff(expected: string, actual: string, cut?: { expected: Cut; actual: Cut }): DiffModel {
  const json = prettyJson(expected, actual);
  if (json) {
    const lines = lineDiff(json.expected, json.actual);
    return { kind: "lines", json: true, lines, ...count(lines) };
  }
  const multi = expected.includes("\n") || actual.includes("\n");
  if (!multi && expected.length <= 160 && actual.length <= 160) {
    // Single words/numbers/ids: character level (shows the one differing char of a GUID); otherwise word level.
    const cd = charDiff(expected, actual, isSingleToken(expected) && isSingleToken(actual) ? "char" : "word");
    // A value cut short by the framework: whatever trails the last common run on the other side is unknown, not a change.
    if (cut?.expected.end) unmarkTail(cd.actual);
    if (cut?.actual.end) unmarkTail(cd.expected);
    return { kind: "inline", ...cd };
  }
  const lines = lineDiff(expected, actual);
  return { kind: "lines", json: false, lines, ...count(lines) };
}

function unmarkTail(segs: Segment[]) {
  const last = segs.at(-1)!;
  if (segs.length > 1 && last.changed) last.changed = false;
}

function count(lines: DiffLine[]) {
  let added = 0,
    removed = 0;
  for (const l of lines) {
    if (l.op === "ins") added++;
    else if (l.op === "del") removed++;
  }
  return { added, removed };
}

/** Collapse long unchanged stretches, keeping `context` lines around changes. */
export function foldContext(lines: DiffLine[], context = 3): (DiffLine | { fold: number; from: number })[] {
  const keep = new Array(lines.length).fill(false);
  lines.forEach((l, i) => {
    if (l.op !== "eq") for (let k = Math.max(0, i - context); k <= Math.min(lines.length - 1, i + context); k++) keep[k] = true;
  });
  const out: (DiffLine | { fold: number; from: number })[] = [];
  for (let i = 0; i < lines.length; ) {
    if (keep[i]) {
      out.push(lines[i++]);
      continue;
    }
    let j = i;
    while (j < lines.length && !keep[j]) j++;
    if (j - i <= 2) for (let k = i; k < j; k++) out.push(lines[k]);
    else out.push({ fold: j - i, from: i });
    i = j;
  }
  return out;
}
