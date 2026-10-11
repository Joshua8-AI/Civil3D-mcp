import { describe, expect, it } from "vitest";
import { spawnSync } from "node:child_process";
import { appendFileSync, copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

// The install and launch scripts are Windows-only and run under Windows PowerShell 5.1; pwsh is checked
// too when it is installed, so neither edition corrupts files or the MCP stdio channel.
const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const onWindows = process.platform === "win32";
const hasPwsh = onWindows && spawnSync("where.exe", ["pwsh"], { stdio: "ignore" }).status === 0;
const shells = ["powershell", ...(hasPwsh ? ["pwsh"] : [])];

function runPowerShell(shell: string, script: string, options: { cwd?: string; env?: NodeJS.ProcessEnv } = {}) {
  return spawnSync(shell, ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script], {
    cwd: options.cwd,
    env: options.env,
    encoding: "utf8",
    input: "",
    timeout: 120_000,
    windowsHide: true,
  });
}

// A throwaway repo holding a copy of the launcher, with fake npm.cmd and node.cmd first on PATH.
// The fake npm logs its arguments and behaves like install / run build (prebuild deletes build\, then
// tsc emits build\index.js even when it reports errors); the fake node logs that it was started.
function makeLauncherRepo() {
  const temp = mkdtempSync(path.join(os.tmpdir(), "c3dmcp-launcher-"));
  const repo = path.join(temp, "repo");
  const bin = path.join(temp, "bin");
  const logDir = path.join(temp, "temp");
  for (const dir of [path.join(repo, "scripts"), path.join(repo, "src", "tools"), bin, logDir]) mkdirSync(dir, { recursive: true });
  copyFileSync(path.join(repoRoot, "scripts", "claude-bootstrap-and-run.ps1"), path.join(repo, "scripts", "claude-bootstrap-and-run.ps1"));
  const files: Record<string, string> = {
    "package.json": '{ "name": "fake", "version": "1.0.0" }\n',
    "package-lock.json": '{ "lockfileVersion": 3 }\n',
    "tsconfig.json": "{}\n",
    "autodesk_videos.json": "[]\n",
    "scripts/copy-static-assets.mjs": "// copies assets\n",
    "src/index.ts": "export {};\n",
    "src/tools/a.ts": "export const a = 1;\n",
    "src/tools/b.ts": "export const b = 2;\n",
  };
  for (const [name, text] of Object.entries(files)) writeFileSync(path.join(repo, name), text);

  writeFileSync(path.join(bin, "fake-npm.mjs"), `
import { appendFileSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import path from "node:path";
const args = process.argv.slice(2);
appendFileSync(${JSON.stringify(path.join(bin, "npm-calls.txt"))}, args.join(" ") + "\\n");
const prefix = args[args.indexOf("--prefix") + 1];
if (process.env.FAKE_NPM_STDERR) process.stderr.write("npm notice New major version of npm available!\\n");
if (args[0] === "install") {
  mkdirSync(path.join(prefix, "node_modules"), { recursive: true });
  console.log("added 1 package");
} else if (args[0] === "run" && args[1] === "build") {
  rmSync(path.join(prefix, "build"), { recursive: true, force: true });
  mkdirSync(path.join(prefix, "build"), { recursive: true });
  writeFileSync(path.join(prefix, "build", "index.js"), "// fake build\\n");
  if (process.env.FAKE_NPM_FAIL_BUILD) {
    console.log("src/tools/a.ts(1,1): error TS2304: Cannot find name 'x'.");
    process.exit(2);
  }
  console.log("built");
}
`);
  writeFileSync(path.join(bin, "npm.cmd"), `@"${process.execPath}" "%~dp0fake-npm.mjs" %*\r\n@exit /b %ERRORLEVEL%\r\n`);
  writeFileSync(path.join(bin, "node.cmd"), `@echo off\r\n>>"%~dp0node-calls.txt" echo %*\r\nexit /b 0\r\n`);

  const env: NodeJS.ProcessEnv = {};
  for (const [key, value] of Object.entries(process.env)) {
    if (!["PATH", "TEMP", "TMP", "CIVIL3D_PORT", "FAKE_NPM_STDERR", "FAKE_NPM_FAIL_BUILD"].includes(key.toUpperCase())) env[key] = value;
  }
  const pathKey = Object.keys(process.env).find((key) => key.toUpperCase() === "PATH") ?? "PATH";
  env.Path = `${bin};${process.env[pathKey] ?? ""}`;
  env.TEMP = logDir;
  env.TMP = logDir;

  const take = (file: string) => {
    const full = path.join(bin, file);
    if (!existsSync(full)) return [];
    const lines = readFileSync(full, "utf8").split(/\r?\n/).filter(Boolean);
    rmSync(full);
    return lines;
  };

  return {
    repo,
    logPath: path.join(logDir, "civil3d-mcp-bootstrap.log"),
    launch(shell: string, extraEnv: NodeJS.ProcessEnv = {}) {
      const result = runPowerShell(shell, path.join(repo, "scripts", "claude-bootstrap-and-run.ps1"), { cwd: repo, env: { ...env, ...extraEnv } });
      return { status: result.status, stdout: result.stdout, stderr: result.stderr, npm: take("npm-calls.txt"), node: take("node-calls.txt") };
    },
    cleanup() {
      rmSync(temp, { recursive: true, force: true });
    },
  };
}

for (const shell of shells) {
  describe.skipIf(!onWindows)(`PowerShell scripts under ${shell}`, () => {
    it("install-trusted.ps1 passes its offline checks", () => {
      const result = runPowerShell(shell, path.join(repoRoot, "tests", "powershell", "install-trusted.check.ps1"));
      expect(result.status, `${result.stdout}\n${result.stderr}`).toBe(0);
      expect(result.stdout).toContain("0 failure(s)");
    }, 120_000);

    it("launcher installs, builds once and starts node, keeping stdout for node only", () => {
      const fixture = makeLauncherRepo();
      try {
        const first = fixture.launch(shell);
        expect(first.status, first.stderr).toBe(0);
        expect(first.stdout).toBe("");
        expect(first.npm.map((call) => call.split(" ").slice(0, 2).join(" "))).toEqual(["install --no-fund", "run build"]);
        expect(first.node).toHaveLength(1);
        expect(first.node[0]).toMatch(/[\\/]build[\\/]index\.js$/);
        expect(readFileSync(fixture.logPath, "utf8")).toContain("built");

        const second = fixture.launch(shell);
        expect(second.status, second.stderr).toBe(0);
        expect(second.stdout).toBe("");
        expect(second.npm).toEqual([]);
        expect(second.node).toHaveLength(1);
      } finally {
        fixture.cleanup();
      }
    }, 120_000);

    it("launcher reinstalls before rebuilding when package-lock.json or package.json changes", () => {
      const fixture = makeLauncherRepo();
      try {
        expect(fixture.launch(shell).status).toBe(0);
        appendFileSync(path.join(fixture.repo, "package-lock.json"), " \n");
        const afterLock = fixture.launch(shell);
        expect(afterLock.status, afterLock.stderr).toBe(0);
        expect(afterLock.stdout).toBe("");
        expect(afterLock.npm.map((call) => call.split(" ")[0])).toEqual(["install", "run"]);

        writeFileSync(path.join(fixture.repo, "package.json"), '{ "name": "fake", "version": "1.0.1" }\n');
        const afterPackage = fixture.launch(shell);
        expect(afterPackage.npm.map((call) => call.split(" ")[0])).toEqual(["install", "run"]);
        expect(fixture.launch(shell).npm).toEqual([]);
      } finally {
        fixture.cleanup();
      }
    }, 120_000);

    it("launcher rebuilds when a source file is deleted or added, or a copied asset changes", () => {
      const fixture = makeLauncherRepo();
      try {
        expect(fixture.launch(shell).status).toBe(0);
        rmSync(path.join(fixture.repo, "src", "tools", "b.ts"));
        expect(fixture.launch(shell).npm).toEqual([`run build --prefix ${fixture.repo}`]);
        writeFileSync(path.join(fixture.repo, "src", "tools", "c.ts"), "export const c = 3;\n");
        expect(fixture.launch(shell).npm).toEqual([`run build --prefix ${fixture.repo}`]);
        writeFileSync(path.join(fixture.repo, "autodesk_videos.json"), '[{ "id": 1 }]\n');
        expect(fixture.launch(shell).npm).toEqual([`run build --prefix ${fixture.repo}`]);
        expect(fixture.launch(shell).npm).toEqual([]);
      } finally {
        fixture.cleanup();
      }
    }, 120_000);

    it("launcher does not stop when npm writes to stderr", () => {
      const fixture = makeLauncherRepo();
      try {
        const result = fixture.launch(shell, { FAKE_NPM_STDERR: "1" });
        expect(result.status, result.stderr).toBe(0);
        expect(result.stdout).toBe("");
        expect(result.node).toHaveLength(1);
        expect(readFileSync(fixture.logPath, "utf8")).toContain("npm notice New major version");
      } finally {
        fixture.cleanup();
      }
    }, 120_000);

    it("launcher reports a failed build on stderr and builds again on the next launch", () => {
      const fixture = makeLauncherRepo();
      try {
        const failed = fixture.launch(shell, { FAKE_NPM_FAIL_BUILD: "1" });
        expect(failed.status).toBe(1);
        expect(failed.stdout).toBe("");
        expect(failed.node).toEqual([]);
        expect(failed.stderr).toContain("Failed to build civil3d-mcp");
        expect(failed.stderr).toContain(fixture.logPath);
        expect(failed.stderr).toContain("error TS2304");
        // tsc still emitted build\index.js: only the missing stamp makes the next launch build again.
        expect(existsSync(path.join(fixture.repo, "build", "index.js"))).toBe(true);

        const retried = fixture.launch(shell);
        expect(retried.status, retried.stderr).toBe(0);
        expect(retried.npm).toEqual([`run build --prefix ${fixture.repo}`]);
        expect(retried.node).toHaveLength(1);
      } finally {
        fixture.cleanup();
      }
    }, 120_000);
  });
}
