// Runs the SonarQube Cloud analysis of CI's sonar job from this machine, as an analysis of the current branch's pull
// request, and lists the issues the pull request adds: the TypeScript, JavaScript, CSS and shell findings before a push
// (the C# ones already show in every build, src/sonar.globalconfig). Same settings as CI (eng/SonarQube.Analysis.xml);
// no coverage is imported. A pull request, because the project's plan shows no results of other branches than main.
//
//   node eng/sonar.mjs                  exit code 1 when the pull request adds issues
//
// The token (one that may analyse the project): $SONAR_TOKEN, else the file ~/.config/sonarcloud-token. The pull
// request (a draft will do) comes from `gh pr view`; CI analyses it again on every push.
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const api = "https://sonarcloud.io/api";
const organization = process.env.SONAR_ORGANIZATION ?? "eriklieben";
const project = process.env.SONAR_PROJECT_KEY ?? "eriklieben_labcoat";

const tokenFile = path.join(os.homedir(), ".config", "sonarcloud-token");
const token = process.env.SONAR_TOKEN ?? (existsSync(tokenFile) ? readFileSync(tokenFile, "utf8").trim() : "");
if (!token) fail(`No token: set SONAR_TOKEN or put one in ${tokenFile} (chmod 600).`);
let pr;
try {
  pr = JSON.parse(execFileSync("gh", ["pr", "view", "--json", "number,headRefName,baseRefName"], { cwd: root, encoding: "utf8" }));
} catch {
  fail("This branch has no pull request: open one (gh pr create --draft), then run this again.");
}

const run = (args, shown) => {
  console.log(`> ${shown ?? args.join(" ")}`);
  execFileSync("dotnet", args, { cwd: root, stdio: "inherit" });
};
run(["tool", "restore"]);
run(["dotnet-sonarscanner", "begin", `/k:${project}`, `/o:${organization}`, `/s:${path.join(root, "eng", "SonarQube.Analysis.xml")}`,
  `/d:sonar.pullrequest.key=${pr.number}`, `/d:sonar.pullrequest.branch=${pr.headRefName}`, `/d:sonar.pullrequest.base=${pr.baseRefName}`,
  `/d:sonar.token=${token}`], `dotnet-sonarscanner begin (pull request #${pr.number})`);
run(["build", "Labcoat.slnx", "-c", "Release"]);
run(["dotnet-sonarscanner", "end", `/d:sonar.token=${token}`], "dotnet-sonarscanner end");

async function get(url) {
  const response = await fetch(api + url, { headers: { Authorization: "Basic " + Buffer.from(token + ":").toString("base64") } });
  if (!response.ok) throw new Error(`${url}: ${response.status} ${response.statusText}`);
  return response.json();
}

// The upload is processed in the background: wait for it, or the issues would be the previous analysis's.
const report = readFileSync(path.join(root, ".sonarqube", "out", ".sonar", "report-task.txt"), "utf8");
const taskId = /^ceTaskId=(.+)$/m.exec(report)?.[1];
if (!taskId) fail("The scanner wrote no ceTaskId (.sonarqube/out/.sonar/report-task.txt).");
let status;
for (let waited = 0; waited < 600; waited += 5) {
  ({ task: { status } } = await get(`/ce/task?id=${taskId}`));
  if (!["PENDING", "IN_PROGRESS"].includes(status)) break;
  await new Promise((resolve) => setTimeout(resolve, 5000));
}
if (status !== "SUCCESS") fail(`The analysis ended with ${status} (task ${taskId}).`);

const issues = [];
for (let page = 1; ; page++) {
  const result = await get(`/issues/search?componentKeys=${project}&pullRequest=${pr.number}&issueStatuses=OPEN,CONFIRMED&ps=500&p=${page}`);
  issues.push(...result.issues);
  if (page * 500 >= result.total) break;
}
issues.sort((a, b) => a.component.localeCompare(b.component) || (a.line ?? 0) - (b.line ?? 0));
for (const issue of issues)
  console.log(`${issue.component.replace(`${project}:`, "")}:${issue.line ?? ""}  ${issue.rule}  ${issue.message}`);
console.log(`\n${issues.length} new issue${issues.length === 1 ? "" : "s"} in pull request #${pr.number}: ` +
  `https://sonarcloud.io/project/issues?id=${project}&pullRequest=${pr.number}&issueStatuses=OPEN,CONFIRMED`);
process.exitCode = issues.length > 0 ? 1 : 0;

function fail(message) {
  console.error(message);
  process.exit(2);
}
