import { withApplicationConnection } from "../build/utils/ConnectionManager.js";
import { getProjectContext } from "../build/orchestration/ProjectContextService.js";

const NO_DRAWING_FAIL_FAST_MS = 5000;

// With zero documents open, a drawing-dependent request must fail fast with
// CIVIL3D.NO_DRAWING instead of hanging until the command timeout. Only
// meaningful when no drawing is loaded; skipped otherwise.
async function verifyNoDrawingFailsFast(health) {
  if (health?.drawingLoaded !== false) {
    return { skipped: true, reason: "a drawing is loaded" };
  }

  const startedAt = Date.now();
  const timeout = new Promise((_, reject) => setTimeout(
    () => reject(new Error(`getDrawingInfo did not fail within ${NO_DRAWING_FAIL_FAST_MS}ms with no document open.`)),
    NO_DRAWING_FAIL_FAST_MS,
  ).unref());
  const info = withApplicationConnection(async (client) => client.sendCommand("getDrawingInfo", {}));

  try {
    await Promise.race([info, timeout]);
  } catch (error) {
    if (error?.code === "CIVIL3D.NO_DRAWING") {
      return { skipped: false, code: error.code, durationMs: Date.now() - startedAt };
    }
    throw error;
  }

  throw new Error("getDrawingInfo succeeded although getCivil3DHealth reported no drawing loaded.");
}

async function main() {
  const health = await withApplicationConnection(async (client) =>
    client.sendCommand("getCivil3DHealth", {}),
  );
  const noDrawing = await verifyNoDrawingFailsFast(health);
  const context = health?.drawingLoaded === false ? null : await getProjectContext();

  process.stdout.write(`${JSON.stringify({ health, noDrawing, context }, null, 2)}\n`);
}

main().catch((error) => {
  const message = error instanceof Error ? error.message : String(error);
  process.stderr.write(`Live Civil 3D plugin validation failed: ${message}\n`);
  process.exitCode = 1;
});
