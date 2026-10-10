import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

import { PLUGIN_DOMAIN_DEFINITION } from "../src/tools/domains/pluginDomain.js";

const pluginDirectory = fileURLToPath(new URL("../Civil3D-MCP-Plugin/", import.meta.url));
const source = (name: string) => readFileSync(`${pluginDirectory}/${name}`, "utf8");

// Civil 3D stops running ExecuteInCommandContextAsync callbacks after a COM
// client sends a command (verified live on 2027), so every host operation
// needs a second route. These guards pin that route down; the live runs are
// recorded in TESTING.md.
describe("host route fallback when the command context is wedged", () => {
  const execution = source("CivilExecution.cs");

  it("watches every drawing operation and fails fast while Civil 3D is busy", () => {
    expect(execution.match(/RunOnHostAsync\(/g)?.length).toBeGreaterThanOrEqual(4);
    expect(execution).toContain("CommandContextStartGrace = TimeSpan.FromSeconds(5)");
    expect(execution).toContain("HostBusyTimeout = TimeSpan.FromSeconds(15)");
    expect(execution).toContain('"CIVIL3D.HOST_BUSY"');
    expect(execution).toContain("Interlocked.Exchange(ref claimed, 1)");
  });

  it("reads from Application.Idle with an OpenCloseTransaction, which keeps the undo history", () => {
    expect(execution).toMatch(/route == HostRoute\.Idle\s*\?\s*database\.TransactionManager\.StartOpenCloseTransaction\(\)/);
    expect(execution).toContain("needsCommand: write");
  });

  it("runs writes and command sequences inside the plugin's own command", () => {
    expect(execution.match(/needsCommand: true/g)?.length).toBe(2);
    expect(execution).toContain('Civil3DCompatibility.InvokeComMethod(acadDocument, "SendCommand"');
    const entry = source("PluginEntry.cs");
    expect(entry).toContain("[CommandMethod(CivilExecution.PluginCommandName");
    // async void and awaited, so Editor.CommandAsync can run inside the command.
    expect(entry).toMatch(/public static async void HostHopCommand\(\)[\s\S]*await CivilExecution\.RunPluginCommandWorkAsync\(\)/);
  });

  it("reports the route in the health response", () => {
    expect(source("DrawingCommands.cs")).toContain('["hostRoute"] = CivilExecution.CommandContextWedged ? "fallback" : "commandContext"');
    const health = PLUGIN_DOMAIN_DEFINITION.actions.health.responseSchema!;
    const base = {
      connected: true, drawingLoaded: true, operationInProgress: false, currentOperation: null, queueDepth: 0, queueCapacity: 64,
      currentOperationStartedAtUnixMs: null, currentRequestId: null, currentOperationDurationMs: null, memoryUsageMb: 1,
      logFilePath: "x", fileLoggingHealthy: true, fileLoggingError: null,
      jobs: { total: 0, running: 0, completed: 0, failed: 0, cancelled: 0, capacity: 256, terminalRetentionMinutes: 1440 },
    };
    expect(health.parse({ ...base, hostRoute: "fallback" })).toMatchObject({ hostRoute: "fallback" });
    expect(health.safeParse({ ...base, hostRoute: "elsewhere" }).success).toBe(false);
  });
});
