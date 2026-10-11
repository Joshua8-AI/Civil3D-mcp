import { afterEach, describe, expect, it } from "vitest";
import {
  ApprovalPolicyService,
  ApprovalRequiredError,
  isApprovalRequired,
} from "../src/tools/approvalPolicy.js";
import { buildExposureAnnotations } from "../src/tools/domainRuntime.js";
import {
  CODE_DOMAIN_DEFINITION,
  RunCodeArgsSchema,
  RunCodeResponseSchema,
} from "../src/tools/domains/codeDomain.js";
import { GENERATED_TOOL_CATALOG_ENTRIES, findManifestAction } from "../src/tools/toolManifest.js";

const runTarget = {
  toolName: "civil3d_run_code",
  action: "run",
  capabilities: CODE_DOMAIN_DEFINITION.actions.run.capabilities,
  safeForRetry: CODE_DOMAIN_DEFINITION.actions.run.safeForRetry,
  requiresActiveDrawing: true,
};

describe("civil3d_run_code domain", () => {
  const savedApprovalMode = process.env.CIVIL3D_APPROVAL_MODE;
  afterEach(() => {
    if (savedApprovalMode === undefined) delete process.env.CIVIL3D_APPROVAL_MODE;
    else process.env.CIVIL3D_APPROVAL_MODE = savedApprovalMode;
  });

  it("is registered as one tool that calls the plugin's runCode", () => {
    const entry = GENERATED_TOOL_CATALOG_ENTRIES.find((item) => item.toolName === "civil3d_run_code");
    expect(entry).toBeDefined();
    expect(entry!.domain).toBe("code");
    expect(entry!.pluginMethods).toEqual(["runCode"]);
    expect(entry!.safeForRetry).toBe(false);
    expect(findManifestAction("civil3d_run_code", "run")).toBeDefined();
  });

  it("always requires approval, also when approvals are disabled", () => {
    expect(isApprovalRequired(runTarget)).toBe(true);
    process.env.CIVIL3D_APPROVAL_MODE = "disabled";
    expect(isApprovalRequired(runTarget)).toBe(true);
    // Other tools still honour the switch.
    expect(isApprovalRequired({ ...runTarget, toolName: "civil3d_xref", action: "detach" })).toBe(false);
  });

  it("refuses to run without a token, also when approvals are disabled", async () => {
    process.env.CIVIL3D_APPROVAL_MODE = "disabled";
    const policy = new ApprovalPolicyService(async () => "drawing-a");
    const parameters = { code: "return 1;" };
    await expect(policy.enforce(runTarget, parameters)).rejects.toBeInstanceOf(ApprovalRequiredError);

    const receipt = await policy.requestApproval(runTarget, parameters);
    await expect(policy.enforce(runTarget, { ...parameters, approvalToken: receipt.approvalToken })).resolves.toBeUndefined();
  });

  it("binds the approval to the exact code", async () => {
    const policy = new ApprovalPolicyService(async () => "drawing-a");
    const receipt = await policy.requestApproval(runTarget, { code: "return 1;" });
    await expect(policy.enforce(runTarget, { code: "return 2;", approvalToken: receipt.approvalToken }))
      .rejects.toThrow(/does not match/);
  });

  it("is marked destructive and not read-only for MCP clients", () => {
    const annotations = buildExposureAnnotations(CODE_DOMAIN_DEFINITION, CODE_DOMAIN_DEFINITION.exposures[0]);
    expect(annotations.readOnlyHint).toBe(false);
    expect(annotations.destructiveHint).toBe(true);
    expect(annotations.idempotentHint).toBe(false);
  });

  it("validates the arguments", () => {
    const valid = { action: "run", code: "return Db.Filename;" };
    expect(RunCodeArgsSchema.safeParse(valid).success).toBe(true);
    expect(RunCodeArgsSchema.safeParse({ ...valid, mode: "write", timeoutSeconds: 90, args: { dwg: "C:/bg.dwg" } }).success).toBe(true);
    expect(RunCodeArgsSchema.safeParse({ ...valid, code: "" }).success).toBe(false);
    expect(RunCodeArgsSchema.safeParse({ ...valid, mode: "admin" }).success).toBe(false);
    expect(RunCodeArgsSchema.safeParse({ ...valid, timeoutSeconds: 0 }).success).toBe(false);
    expect(RunCodeArgsSchema.safeParse({ ...valid, timeoutSeconds: 91 }).success).toBe(false);
    expect(RunCodeArgsSchema.safeParse({ ...valid, maxInlineBytes: 100 }).success).toBe(false);
    expect(RunCodeArgsSchema.safeParse({ ...valid, args: ["not", "an", "object"] }).success).toBe(false);
    expect(RunCodeArgsSchema.safeParse({ ...valid, code: "x".repeat(200_001) }).success).toBe(false);
  });

  it("resolves calls without an action to 'run'", () => {
    const resolved = CODE_DOMAIN_DEFINITION.exposures[0].resolveAction({ code: "return 1;", mode: "read" });
    expect(resolved.action).toBe("run");
    expect(RunCodeArgsSchema.parse(resolved.args).code).toBe("return 1;");
  });

  it("accepts the plugin's response shapes", () => {
    const base = { runId: "20261011-120000-abcd1234", mode: "read", logFolder: "C:/log/run-code/20261011-120000-abcd1234", timing: { compileMs: 180, runMs: 1200, totalMs: 1450 } };
    expect(RunCodeResponseSchema.safeParse({
      ...base, status: "ok", ok: true, drawing: "Job.dwg", output: "96913 objects\n",
      result: { objects: 96913, points: 547441 }, resultShape: "<>f__AnonymousType0`2", resultBytes: 37,
    }).success).toBe(true);
    expect(RunCodeResponseSchema.safeParse({
      ...base, status: "ok", ok: true, result: null, resultFile: "C:/log/run-code/x/result.json",
      resultBytes: 8_400_000, resultPreview: "[[1.00,2.00],...", resultShape: "array of 547441", files: ["C:/log/run-code/x/points.txt"],
    }).success).toBe(true);
    expect(RunCodeResponseSchema.safeParse({
      ...base, status: "rejected", ok: false,
      errors: [{ line: 3, column: 40, code: "C3DMCP001", message: "Read mode opens objects for read only" }],
      warnings: [{ line: 1, column: 1, code: "CS0105", message: "duplicate using" }, "ran past its budget"],
    }).success).toBe(true);
    expect(RunCodeResponseSchema.safeParse({
      ...base, status: "timeout", ok: false,
      error: { type: "Civil3DMcpPlugin.RunCodeTimeoutException", message: "budget used up", line: 12, stack: "at Run() in script:line 12" },
    }).success).toBe(true);
    expect(RunCodeResponseSchema.safeParse({ ...base, status: "exploded", ok: false }).success).toBe(false);
  });
});
