// Checks the relative links and images in the repository's Markdown files: the target file must exist and a #anchor
// must match a heading (GitHub's slug rules) in the target file. Images that are listed in docs/images/SHOTLIST.md but
// not taken yet are reported as pending, not as errors.
//
//   node eng/check-links.mjs            exit code 1 on broken links
import { readFileSync, existsSync, readdirSync, statSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const skip = new Set(["node_modules", "bin", "obj", ".git", "artifacts", "wwwroot", "TestResults"]);

function* markdownFiles(dir) {
  for (const name of readdirSync(dir)) {
    if (skip.has(name)) continue;
    const full = path.join(dir, name);
    if (statSync(full).isDirectory()) yield* markdownFiles(full);
    else if (name.endsWith(".md")) yield full;
  }
}

const slug = (heading) =>
  heading.trim().toLowerCase().replace(/<[^>]+>/g, "").replace(/[^\p{L}\p{N}\s_-]/gu, "").replace(/\s/g, "-");

const anchorCache = new Map();
function anchors(file) {
  if (!anchorCache.has(file)) {
    const set = new Set();
    const counts = new Map();
    let fenced = false;
    for (const line of readFileSync(file, "utf8").split("\n")) {
      if (/^\s*(```|~~~)/.test(line)) fenced = !fenced;
      const m = !fenced && /^#{1,6}\s+(.*?)\s*#*\s*$/.exec(line);
      if (!m) continue;
      const base = slug(m[1]);
      const n = counts.get(base) ?? 0;
      counts.set(base, n + 1);
      set.add(n ? `${base}-${n}` : base);
    }
    anchorCache.set(file, set);
  }
  return anchorCache.get(file);
}

const shotlist = path.join(root, "docs/images/SHOTLIST.md");
const planned = existsSync(shotlist) ? readFileSync(shotlist, "utf8") : "";
const isPlanned = (target) => {
  const rel = path.relative(path.join(root, "docs/images"), target).split(path.sep).join("/");
  if (rel.startsWith("..")) return false;
  if (planned.includes(`\`${rel}\``)) return true;
  const m = /^themes\/([a-z]+)-(light|dark)\.png$/.exec(rel);
  return !!m && planned.includes("themes/<family>-<mode>.png") && planned.includes(`\`${m[1]}\``);
};

let broken = 0;
const pending = new Set();
for (const file of markdownFiles(root)) {
  const text = readFileSync(file, "utf8").replace(/```[\s\S]*?```/g, "");
  const links = [
    ...[...text.matchAll(/!?\[[^\]]*\]\(\s*<?([^)\s>]+)>?(?:\s+"[^"]*")?\s*\)/g)].map((m) => m[1]),
    ...[...text.matchAll(/\b(?:src|srcset|href)="([^"]+)"/g)].map((m) => m[1]),
    ...[...text.matchAll(/^\[[^\]]+\]:\s*(\S+)/gm)].map((m) => m[1]),
  ];
  for (const link of links) {
    if (/^(https?:|mailto:|data:)/.test(link)) continue;
    const [p, hash] = link.split("#");
    const target = p ? path.resolve(path.dirname(file), decodeURI(p)) : file;
    const where = path.relative(root, file);
    if (!existsSync(target)) {
      if (isPlanned(target)) pending.add(path.relative(root, target));
      else {
        broken++;
        console.log(`BROKEN  ${where}: ${link}`);
      }
      continue;
    }
    if (hash && target.endsWith(".md") && !anchors(target).has(hash.toLowerCase())) {
      broken++;
      console.log(`ANCHOR  ${where}: ${link}`);
    }
  }
}
if (pending.size) console.log(`pending screenshots (docs/images/SHOTLIST.md): ${[...pending].sort().join(", ")}`);
console.log(broken ? `${broken} broken link(s)` : "all links resolve");
process.exit(broken ? 1 : 0);
