import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const workerBaseUrl = "https://nudge.rohanbhadange18.workers.dev";
const artifactDir = join(process.cwd(), "artifacts", "diagnostics");
const screenshotPath = join(artifactDir, "primary-screen.jpg");

mkdirSync(artifactDir, { recursive: true });

console.log("Nudge Worker diagnostics");
console.log(`Worker: ${workerBaseUrl}`);

await checkHealth();
await checkTranscribeToken();
await checkTextToSpeech();
await checkChatWithScreenshot();

console.log("All Worker diagnostics passed.");

async function checkHealth() {
  const response = await fetch(`${workerBaseUrl}/health`);
  await ensureOk(response, "health");
  console.log("health: ok");
}

async function checkTranscribeToken() {
  const response = await fetch(`${workerBaseUrl}/transcribe-token`, { method: "POST" });
  const body = await ensureOk(response, "transcribe-token");
  const json = JSON.parse(body);
  if (!json.token) {
    throw new Error("transcribe-token: missing token");
  }
  console.log("transcribe-token: ok");
}

async function checkTextToSpeech() {
  const response = await fetch(`${workerBaseUrl}/tts`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ text: "diagnostics test" }),
  });
  await ensureOk(response, "tts");
  const bytes = Buffer.from(await response.arrayBuffer());
  if (bytes.length < 1024) {
    throw new Error(`tts: expected audio bytes, got ${bytes.length}`);
  }
  writeFileSync(join(artifactDir, "tts-sample.mp3"), bytes);
  console.log(`tts: ok (${bytes.length} bytes)`);
}

async function checkChatWithScreenshot() {
  const screenshot = capturePrimaryScreen();
  const response = await fetch(`${workerBaseUrl}/chat`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({
      transcript: "This is a diagnostics check. Briefly describe the screen and do not point unless useful.",
      images: [
        {
          label: `user's screen (cursor is here) (image dimensions: ${screenshot.width}x${screenshot.height} pixels)`,
          mediaType: "image/jpeg",
          data: screenshot.base64,
          screenNumber: 1,
          screenshotWidthInPixels: screenshot.width,
          screenshotHeightInPixels: screenshot.height,
        },
      ],
      conversationHistory: [],
    }),
  });
  const body = await ensureOk(response, "chat");
  const json = JSON.parse(body);
  if (!json.text || !/\[(?:POINT|POINT-ELEMENT|BOX):/i.test(json.text)) {
    throw new Error(`chat: missing response text or point tag: ${body}`);
  }
  if (!json.spokenText) {
    throw new Error(`chat: missing spokenText: ${body}`);
  }
  console.log("chat: ok");
  console.log(`chat spokenText: ${json.spokenText}`);
}

function capturePrimaryScreen() {
  try {
    const script = `
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$screen = [System.Windows.Forms.Screen]::PrimaryScreen
$bounds = $screen.Bounds
$bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bounds.Size)
$bitmap.Save('${screenshotPath.replaceAll("\\", "\\\\")}', [System.Drawing.Imaging.ImageFormat]::Jpeg)
$graphics.Dispose()
$bitmap.Dispose()
Write-Output "$($bounds.Width)x$($bounds.Height)"
`;
    const output = execFileSync("powershell.exe", ["-NoProfile", "-Command", script], {
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
    }).trim();
    const [width, height] = output.split(/\r?\n/).at(-1).split("x").map(Number);
    if (existsSync(screenshotPath) && width > 0 && height > 0) {
      return {
        width,
        height,
        base64: readFileSync(screenshotPath).toString("base64"),
      };
    }
  } catch (error) {
    console.warn(`screen capture failed, using synthetic jpeg: ${error.message}`);
  }

  return {
    width: 1,
    height: 1,
    base64: "/9j/4AAQSkZJRgABAQAAAQABAAD/2w==",
  };
}

async function ensureOk(response, label) {
  const body = await response.text();
  if (!response.ok) {
    throw new Error(`${label}: HTTP ${response.status}: ${body}`);
  }
  return body;
}
