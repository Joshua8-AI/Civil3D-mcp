import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

// civil3d_run_code: a C# snippet compiled with Roslyn inside the plugin and run
// on Civil 3D's main thread. It exists for work the fixed tools and COM/LISP
// make too slow (2026-10-11: 96,913 objects sampled into 547,441 points in
// 1.2 s in-process against about 20 minutes in LISP). The code runs with the
// user's full rights, so the action is always approval-gated, even with
// CIVIL3D_APPROVAL_MODE=disabled (approvalPolicy.ts), and the plugin saves
// every script with a summary in its run-code log folder.

export const RUN_CODE_MAX_CODE_CHARS = 200_000;
export const RUN_CODE_MAX_TIMEOUT_SECONDS = 90;

export const RunCodeArgsSchema = z.object({
  action: z.literal("run"),
  code: z.string().min(1).max(RUN_CODE_MAX_CODE_CHARS),
  mode: z.enum(["read", "write"]).optional(),
  timeoutSeconds: z.number().int().min(1).max(RUN_CODE_MAX_TIMEOUT_SECONDS).optional(),
  maxInlineBytes: z.number().int().min(1024).max(1024 * 1024).optional(),
  args: z.record(z.unknown()).optional(),
});

const DiagnosticSchema = z.object({
  line: z.number().int(),
  column: z.number().int(),
  code: z.string(),
  message: z.string(),
});

export const RunCodeResponseSchema = z.object({
  runId: z.string(),
  mode: z.enum(["read", "write"]),
  status: z.enum(["ok", "compile_error", "rejected", "runtime_error", "timeout", "cancelled", "failed"]),
  ok: z.boolean(),
  drawing: z.string().optional(),
  output: z.string().optional(),
  outputFile: z.string().optional(),
  result: z.unknown().optional(),
  resultShape: z.string().optional(),
  resultBytes: z.number().int().optional(),
  resultFile: z.string().optional(),
  resultPreview: z.string().optional(),
  resultError: z.string().optional(),
  files: z.array(z.string()).optional(),
  errors: z.array(DiagnosticSchema).optional(),
  error: z.object({
    type: z.string(),
    message: z.string(),
    line: z.number().int().optional(),
    errorStatus: z.string().optional(),
    inner: z.string().optional(),
    stack: z.string().optional(),
  }).optional(),
  warnings: z.array(z.union([z.string(), DiagnosticSchema])).optional(),
  timing: z.object({
    compileMs: z.number(),
    runMs: z.number(),
    totalMs: z.number(),
  }),
  logFolder: z.string(),
  committed: z.boolean().optional(),
}).passthrough();

export const CODE_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "code",
  actions: {
    run: {
      action: "run",
      inputSchema: RunCodeArgsSchema,
      responseSchema: RunCodeResponseSchema,
      // "manage" + not safe for retry: approval is required (and forced for
      // this tool even when approvals are disabled).
      capabilities: ["query", "analyze", "manage"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["runCode"],
      execute: async (args) => await withApplicationConnection(
        async (appClient) => await appClient.sendCommand("runCode", {
          code: args.code,
          mode: args.mode ?? "read",
          timeoutSeconds: args.timeoutSeconds ?? 30,
          maxInlineBytes: args.maxInlineBytes ?? 65536,
          args: args.args ?? {},
        }),
      ),
    },
  },
  exposures: [
    {
      toolName: "civil3d_run_code",
      displayName: "Civil 3D Run Code",
      description:
        "Runs a C# snippet inside Civil 3D (compiled with Roslyn, run on the main thread with the drawing locked), for work that is too slow or impossible with the other tools or COM/LISP: bulk geometry over thousands of objects, reading other DWGs as side databases, anything the AutoCAD/Civil 3D .NET API reaches. " +
        "Always needs approval: call civil3d_request_approval with toolName civil3d_run_code, action 'run' and the same parameters, then retry with approvalToken. The code runs with the user's full rights. " +
        "The snippet is the body of `object Run()`: statements, local functions and leading `using` lines; `return` a value (JSON: points and vectors as [x,y,z], ObjectIds as handles, tuples as arrays, anonymous objects by property). " +
        "In scope: Doc, Db, Ed, Civil (CivilDocument), Tr (the run's transaction), Args (the 'args' object), Log(x), Check(), Open<T>(id, forWrite), ModelSpace<T>(db) (calls Check for each entity), OpenSide(path) (another DWG as a read-only side database, disposed after the run), TransactionFor(db), WriteFile(name, text or lines) (into the run folder), Session (a dictionary kept across runs until Civil 3D closes; plain data only). " +
        "Namespaces: System, Linq, IO, Text, Text.Json, Autodesk.AutoCAD ApplicationServices/DatabaseServices/EditorInput/Geometry/Colors, Autodesk.Civil, Civil ApplicationServices/DatabaseServices/Styles; aliases Entity (AutoCAD), CivilEntity, Surface (Civil), AcSurface, AcException. " +
        "A script cannot be stopped from outside: call Check() in long loops, which ends it when timeoutSeconds (1-90, default 30) is used up or the request is cancelled; a timeout the script catches still ends the run as a timeout. " +
        "mode 'read' (default): an open-close transaction, never committed; the compiler refuses OpenMode.ForWrite, UpgradeOpen, Erase, AppendEntity, Civil 3D Create/Add/Update..., regular transactions and drawing-setting setters (a guard against mistakes, not a sandbox). " +
        "mode 'write': runs inside a plugin command; committed as one undo step when the snippet returns, rolled back when it throws or runs out of time (only changes made through Tr are rolled back: write mode refuses open-close transactions; database or system variables, files and Session are not rolled back). Both modes refuse Editor prompts, modal dialogs, event subscriptions and Tr.Commit()/Abort()/Dispose() (the run owns Tr: just return). " +
        "status is ok, compile_error or rejected (errors with snippet line numbers), runtime_error, timeout or cancelled (error with line). Output or results larger than maxInlineBytes (default 64 KB) go to outputFile/resultFile with a preview. Every script and its summary are saved in logFolder.",
      inputShape: {
        code: z.string().min(1).max(RUN_CODE_MAX_CODE_CHARS),
        mode: z.enum(["read", "write"]).optional(),
        timeoutSeconds: z.number().int().min(1).max(RUN_CODE_MAX_TIMEOUT_SECONDS).optional(),
        maxInlineBytes: z.number().int().min(1024).max(1024 * 1024).optional(),
        args: z.record(z.unknown()).optional(),
      },
      supportedActions: ["run"],
      resolveAction: (rawArgs) => ({ action: "run", args: { ...rawArgs, action: "run" } }),
    },
  ],
};
