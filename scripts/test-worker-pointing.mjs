import fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const workerPath = path.join(repoRoot, "worker", "clickyclone-worker.js");
const workerSource = await fs.readFile(workerPath, "utf8");
const workerFactorySource = `${workerSource.replace("export default", "const workerDefault =")}\nreturn workerDefault;`;
const worker = new Function(workerFactorySource)();

let capturedOpenAIRequest = null;
const openAIOutputs = [
  JSON.stringify({ spokenText: "Use the marker on the browser's new tab control.", targetId: "C07", needsZoom: false }),
  JSON.stringify({ spokenText: "I could not find a reliable matching marker.", targetId: null, needsZoom: false }),
  "This should be rejected. [BOX:1160,52,80,28:Print Preview:screen1:0.94]",
];
const originalFetch = globalThis.fetch;
globalThis.fetch = async (url, init) => {
  const target = String(url);
  if (target === "https://api.openai.com/v1/responses") {
    capturedOpenAIRequest = JSON.parse(String(init?.body || "{}"));
    return new Response(JSON.stringify({
      output_text: openAIOutputs.shift(),
    }), {
      status: 200,
      headers: { "content-type": "application/json" },
    });
  }

  throw new Error(`Unexpected fetch in worker pointing test: ${target}`);
};

try {
  const selfTestResponse = await worker.fetch(new Request("https://example.test/pointing-self-test"), {});
  const selfTestJson = await selfTestResponse.json();
  assertEqual(200, selfTestResponse.status, "selfTest.status");
  assertEqual(true, selfTestJson.ok, "selfTest.ok");
  assertEqual("visual-target", selfTestJson.point.source, "selfTest.point.source");
  assertEqual(321.5, selfTestJson.point.x, "selfTest.point.x");
  assertEqual(22.25, selfTestJson.point.y, "selfTest.point.y");

  const request = new Request("https://example.test/chat", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({
      transcript: "Point to the new tab button.",
      images: [
        {
          label: "screen 1",
          mediaType: "image/png",
          screenNumber: 1,
          screenshotWidthInPixels: 1920,
          screenshotHeightInPixels: 1080,
          data: "aGVsbG8=",
          visualAtlasData: "aGVsbG8=",
          visualTargets: [
            { id: "C01", kind: "visual-candidate", x: 40, y: 104, width: 120, height: 32, centerX: 100, centerY: 120, confidence: 0.7 },
          ],
        },
        {
          label: "screen 2",
          mediaType: "image/png",
          screenNumber: 2,
          screenshotWidthInPixels: 1920,
          screenshotHeightInPixels: 1080,
          data: "aGVsbG8=",
          visualAtlasData: "aGVsbG8=",
          visualTargets: [
            { id: "C07", kind: "visual-candidate", x: 1398.4, y: 1.8, width: 28, height: 28, centerX: 1412.4, centerY: 15.8, confidence: 0.9, labelHint: "New tab" },
          ],
        },
      ],
      conversationHistory: [],
    }),
  });

  const response = await worker.fetch(request, { OPENAI_API_KEY: "test-openai-key" });
  const json = await response.json();

  assertEqual(200, response.status, "response.status");
  assertEqual("Use the marker on the browser's new tab control.", json.spokenText, "spokenText");
  assertEqual(1412.4, json.point.x, "point.x");
  assertEqual(15.8, json.point.y, "point.y");
  assertEqual("New tab", json.point.label, "point.label");
  assertEqual(2, json.point.screenNumber, "point.screenNumber");
  assertEqual("visual-target", json.point.source, "point.source");
  assertEqual("C07", json.point.targetId, "point.targetId");
  assertEqual(1398.4, json.point.bounds.x, "point.bounds.x");
  assertEqual(1.8, json.point.bounds.y, "point.bounds.y");
  assertEqual(28, json.point.bounds.width, "point.bounds.width");
  assertEqual(28, json.point.bounds.height, "point.bounds.height");

  const promptText = capturedOpenAIRequest?.input?.[0]?.content?.find((item) => item.type === "input_text")?.text || "";
  assertIncludes(promptText, "response schema", "system prompt");
  assertIncludes(promptText, "never return coordinates", "system prompt");
  const catalogText = capturedOpenAIRequest?.input?.[0]?.content?.find((item) =>
    item.type === "input_text" && String(item.text || "").includes("C07 | visual-candidate"));
  if (!catalogText) {
    throw new Error("visual target manifest was not included in OpenAI request");
  }

  const noneResponse = await worker.fetch(new Request("https://example.test/chat", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({
      transcript: "Point to print preview.",
      images: [
        {
          label: "screen 1",
          mediaType: "image/png",
          data: "aGVsbG8=",
          screenNumber: 1,
          screenshotWidthInPixels: 1920,
          screenshotHeightInPixels: 1080,
          visualTargets: [],
        },
      ],
      conversationHistory: [],
    }),
  }), { OPENAI_API_KEY: "test-openai-key" });
  const noneJson = await noneResponse.json();

  assertEqual(200, noneResponse.status, "none.response.status");
  assertEqual(null, noneJson.point, "none.point");

  const rejectedBoxResponse = await worker.fetch(new Request("https://example.test/chat", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({
      transcript: "Point to the edge button.",
      images: [
        {
          label: "screen 1",
          mediaType: "image/png",
          data: "aGVsbG8=",
          screenNumber: 1,
          screenshotWidthInPixels: 100,
          screenshotHeightInPixels: 100,
          visualTargets: [],
        },
      ],
      conversationHistory: [],
    }),
  }), { OPENAI_API_KEY: "test-openai-key" });
  const rejectedBoxJson = await rejectedBoxResponse.json();

  assertEqual(200, rejectedBoxResponse.status, "rejectedBox.response.status");
  assertEqual(null, rejectedBoxJson.point, "rejectedBox.point");

  console.log("worker visual reticle pointing: ok");
} finally {
  globalThis.fetch = originalFetch;
}

function assertEqual(expected, actual, label) {
  if (expected !== actual) {
    throw new Error(`${label}: expected ${expected}, got ${actual}`);
  }
}

function assertIncludes(haystack, needle, label) {
  if (!String(haystack).includes(needle)) {
    throw new Error(`${label}: expected to include ${needle}`);
  }
}
