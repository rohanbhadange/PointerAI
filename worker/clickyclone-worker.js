const CORS_HEADERS = {
  "access-control-allow-origin": "*",
  "access-control-allow-methods": "GET,POST,OPTIONS",
  "access-control-allow-headers": "content-type",
};

const OPENAI_MODEL = "gpt-4.1-mini";
const OPENAI_COMPUTER_MODEL = "gpt-5.5";
const ELEVENLABS_MODEL = "eleven_flash_v2_5";
export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (request.method === "OPTIONS") {
      return new Response(null, { status: 204, headers: CORS_HEADERS });
    }

    try {
      if (url.pathname === "/health") {
        return jsonResponse({ ok: true, service: "clickyclone-worker" });
      }

      if (url.pathname === "/pointing-self-test") {
        return handlePointingSelfTest();
      }

      if (url.pathname === "/chat") {
        requireMethod(request, "POST");
        return await handleChat(request, env);
      }

      if (url.pathname === "/locate") {
        requireMethod(request, "POST");
        return await handleLocate(request, env);
      }

      if (url.pathname === "/tts") {
        requireMethod(request, "POST");
        return await handleTextToSpeech(request, env);
      }

      if (url.pathname === "/transcribe") {
        requireMethod(request, "POST");
        return await handleTranscribe(request, env);
      }

      if (url.pathname === "/transcribe-token") {
        requireMethod(request, "POST");
        return await handleTranscribeToken(env);
      }

      return jsonResponse({ error: "Not found" }, 404);
    } catch (error) {
      return jsonResponse({ error: error instanceof Error ? error.message : String(error) }, 500);
    }
  },
};

async function handleLocate(request, env) {
  const body = await request.json();
  const provider = String(body.provider || env.LOCATOR_PROVIDER || "openai-computer-use").trim();
  const goal = String(body.goal || "").trim();
  const screenshotBase64 = String(body.screenshotBase64 || "");
  const mimeType = String(body.mimeType || "image/png");
  const width = Number(body.width);
  const height = Number(body.height);
  const screenNumber = Number.isFinite(Number(body.screenNumber)) ? Number(body.screenNumber) : null;

  if (!goal || !screenshotBase64 || !Number.isFinite(width) || !Number.isFinite(height) || width <= 0 || height <= 0) {
    return jsonResponse({
      ok: false,
      provider,
      error: "Missing goal, screenshotBase64, width, or height",
    }, 400);
  }

  if (provider === "openai-computer-use") {
    return jsonResponse(await locateWithOpenAIComputerUse({
      env,
      provider,
      goal,
      screenshotBase64,
      mimeType,
      width,
      height,
      screenNumber,
    }));
  }

  return jsonResponse({
    ok: false,
    provider,
    error: `Unsupported locator provider: ${provider}`,
  }, 400);
}

async function locateWithOpenAIComputerUse({
  env,
  provider,
  goal,
  screenshotBase64,
  mimeType,
  width,
  height,
  screenNumber,
}) {
  if (!env.OPENAI_API_KEY) {
    return {
      ok: false,
      provider,
      error: "OPENAI_API_KEY is not configured",
    };
  }

  const model = env.OPENAI_COMPUTER_MODEL || OPENAI_COMPUTER_MODEL;
  const prompt = buildOpenAIComputerPrompt(goal, width, height);
  const firstResponse = await fetch("https://api.openai.com/v1/responses", {
    method: "POST",
    headers: {
      authorization: `Bearer ${env.OPENAI_API_KEY}`,
      "content-type": "application/json",
    },
    body: JSON.stringify({
      model,
      tools: [{ type: "computer" }],
      input: [
        {
          role: "user",
          content: [
            { type: "input_text", text: prompt },
            {
              type: "input_image",
              image_url: `data:${mimeType};base64,${screenshotBase64}`,
              detail: "original",
            },
          ],
        },
      ],
    }),
  });

  const firstText = await firstResponse.text();
  const firstData = safeParseJSON(firstText) || firstText;
  if (!firstResponse.ok) {
    return {
      ok: false,
      provider,
      error: firstData,
    };
  }

  const firstPoint = extractOpenAIComputerPoint(firstData);
  if (firstPoint) {
    return normalizeLocatePoint({
      provider,
      goal,
      point: firstPoint,
      width,
      height,
      screenNumber,
    });
  }

  const screenshotCall = findOpenAIComputerCall(firstData, (action) => action.type === "screenshot");
  if (!screenshotCall?.call_id || typeof firstData?.id !== "string") {
    return {
      ok: false,
      provider,
      error: "No OpenAI computer-use coordinate or screenshot request returned",
      raw: firstData,
    };
  }

  const secondResponse = await fetch("https://api.openai.com/v1/responses", {
    method: "POST",
    headers: {
      authorization: `Bearer ${env.OPENAI_API_KEY}`,
      "content-type": "application/json",
    },
    body: JSON.stringify({
      model,
      tools: [{ type: "computer" }],
      previous_response_id: firstData.id,
      input: [
        {
          type: "computer_call_output",
          call_id: screenshotCall.call_id,
          output: {
            type: "computer_screenshot",
            image_url: `data:${mimeType};base64,${screenshotBase64}`,
            detail: "original",
          },
        },
      ],
    }),
  });

  const secondText = await secondResponse.text();
  const secondData = safeParseJSON(secondText) || secondText;
  if (!secondResponse.ok) {
    return {
      ok: false,
      provider,
      error: secondData,
    };
  }

  const secondPoint = extractOpenAIComputerPoint(secondData);
  if (!secondPoint) {
    return {
      ok: false,
      provider,
      error: "No OpenAI computer-use coordinate returned after screenshot",
      raw: secondData,
    };
  }

  return normalizeLocatePoint({
    provider,
    goal,
    point: secondPoint,
    width,
    height,
    screenNumber,
  });
}

async function handleTranscribe(request, env) {
  assertSecret(env.ASSEMBLYAI_API_KEY, "ASSEMBLYAI_API_KEY");

  const body = await request.json();
  const mediaType = String(body.mediaType || "audio/wav");
  const base64Audio = String(body.data || "");
  if (!base64Audio) {
    return jsonResponse({ error: "Missing audio data" }, 400);
  }

  const audioBytes = base64ToUint8Array(base64Audio);
  if (audioBytes.byteLength < 4096) {
    return jsonResponse({ error: "Audio recording was too small", byteLength: audioBytes.byteLength }, 400);
  }

  const uploadResponse = await fetch("https://api.assemblyai.com/v2/upload", {
    method: "POST",
    headers: {
      authorization: env.ASSEMBLYAI_API_KEY,
      "content-type": mediaType,
    },
    body: audioBytes,
  });

  const uploadText = await uploadResponse.text();
  if (!uploadResponse.ok) {
    return jsonResponse({ error: "AssemblyAI upload failed", status: uploadResponse.status, detail: safeParseJSON(uploadText) || uploadText }, uploadResponse.status);
  }

  const uploadJSON = safeParseJSON(uploadText);
  const audioUrl = uploadJSON?.upload_url;
  if (!audioUrl) {
    return jsonResponse({ error: "AssemblyAI upload response did not include upload_url", detail: uploadJSON || uploadText }, 502);
  }

  const transcriptResponse = await fetch("https://api.assemblyai.com/v2/transcript", {
    method: "POST",
    headers: {
      authorization: env.ASSEMBLYAI_API_KEY,
      "content-type": "application/json",
    },
    body: JSON.stringify({
      audio_url: audioUrl,
      speech_models: ["universal-3-pro", "universal-2"],
      language_code: "en_us",
      punctuate: true,
      format_text: true,
    }),
  });

  const transcriptText = await transcriptResponse.text();
  if (!transcriptResponse.ok) {
    return jsonResponse({ error: "AssemblyAI transcript submit failed", status: transcriptResponse.status, detail: safeParseJSON(transcriptText) || transcriptText }, transcriptResponse.status);
  }

  const transcriptJSON = safeParseJSON(transcriptText);
  const transcriptId = transcriptJSON?.id;
  if (!transcriptId) {
    return jsonResponse({ error: "AssemblyAI transcript response did not include id", detail: transcriptJSON || transcriptText }, 502);
  }

  const startedAt = Date.now();
  while (Date.now() - startedAt < 45000) {
    await sleep(1000);
    const pollResponse = await fetch(`https://api.assemblyai.com/v2/transcript/${transcriptId}`, {
      headers: { authorization: env.ASSEMBLYAI_API_KEY },
    });
    const pollText = await pollResponse.text();
    const pollJSON = safeParseJSON(pollText);
    if (!pollResponse.ok) {
      return jsonResponse({ error: "AssemblyAI transcript poll failed", status: pollResponse.status, detail: pollJSON || pollText }, pollResponse.status);
    }

    if (pollJSON?.status === "completed") {
      return jsonResponse({ text: String(pollJSON.text || ""), id: transcriptId });
    }

    if (pollJSON?.status === "error") {
      return jsonResponse({ error: "AssemblyAI transcription failed", detail: pollJSON.error || pollJSON }, 502);
    }
  }

  return jsonResponse({ error: "AssemblyAI transcription timed out", id: transcriptId }, 504);
}

function handlePointingSelfTest() {
  const images = [
    {
      visualTargets: [
        {
          id: "C01",
          kind: "visual-candidate",
          x: 307.5,
          y: 8.25,
          width: 28,
          height: 28,
          centerX: 321.5,
          centerY: 22.25,
        },
      ],
    },
  ];
  const parsed = parseTargetSelection('{"spokenText":"Self-test response.","targetId":"C01","needsZoom":false}', buildVisualTargetLookup(images));
  return jsonResponse({
    ok: parsed.point?.source === "visual-target" &&
      parsed.point?.x === 321.5 &&
      parsed.point?.y === 22.25 &&
      parsed.point?.screenNumber === null,
    spokenText: parsed.spokenText,
    point: parsed.point,
  });
}

async function handleChat(request, env) {
  assertSecret(env.OPENAI_API_KEY, "OPENAI_API_KEY");

  const body = await request.json();
  const transcript = String(body.transcript || "").trim();
  const images = Array.isArray(body.images) ? body.images : [];
  const targetLookup = buildVisualTargetLookup(images);
  const conversationHistory = Array.isArray(body.conversationHistory) ? body.conversationHistory : [];

  if (!transcript) {
    return jsonResponse({ error: "Missing transcript" }, 400);
  }

  if (images.length === 0) {
    return jsonResponse({ error: "Missing images" }, 400);
  }

  const input = [];
  const historyText = conversationHistory
    .slice(-10)
    .map((turn) => `user: ${turn.userTranscript || ""}\nclicky: ${turn.assistantResponse || ""}`)
    .join("\n\n");

  const currentContent = /** @type {Array<Record<string, unknown>>} */ ([
    {
      type: "input_text",
      text: `${SYSTEM_PROMPT}\n\nRecent conversation:\n${historyText || "none"}\n\nThe user said:\n${transcript}\n\nVisible reticle targets:\n${formatVisualTargetManifest(images) || "none"}\n\nReturn only valid JSON matching the requested schema.`,
    },
  ]);

  for (const image of images) {
    if (image.data) {
      currentContent.push({
        type: "input_image",
        image_url: `data:${image.mediaType || "image/png"};base64,${image.data}`,
        detail: "high",
      });
      currentContent.push({ type: "input_text", text: `${String(image.label || "user screen")} clean screenshot` });
    }

    if (image.visualAtlasData) {
      currentContent.push({
        type: "input_image",
        image_url: `data:${image.mediaType || "image/png"};base64,${image.visualAtlasData}`,
        detail: "high",
      });
      currentContent.push({ type: "input_text", text: `${String(image.label || "user screen")} annotated reticle atlas. Choose one visible marker id from this image.` });
    }
  }

  input.push({ role: "user", content: currentContent });

  const model = env.OPENAI_MODEL || OPENAI_MODEL;
  const openAIRequest = {
    model,
    input,
    max_output_tokens: 4096,
  };
  if (/^gpt-5/i.test(model)) {
    openAIRequest.reasoning = { effort: "low" };
  }

  const openAIResponse = await fetch("https://api.openai.com/v1/responses", {
    method: "POST",
    headers: {
      authorization: `Bearer ${env.OPENAI_API_KEY}`,
      "content-type": "application/json",
    },
    body: JSON.stringify(openAIRequest),
  });

  const responseText = await openAIResponse.text();
  if (!openAIResponse.ok) {
    return jsonResponse({ error: "OpenAI request failed", status: openAIResponse.status, detail: safeParseJSON(responseText) || responseText }, openAIResponse.status);
  }

  const fullText = extractOpenAIText(safeParseJSON(responseText)).trim();
  const point = parseTargetSelection(fullText, targetLookup);
  return jsonResponse({
    text: point.spokenText,
    spokenText: point.spokenText.trim(),
    point: point.point,
    model,
  });
}

async function handleTextToSpeech(request, env) {
  assertSecret(env.ELEVENLABS_API_KEY, "ELEVENLABS_API_KEY");
  assertSecret(env.ELEVENLABS_VOICE_ID, "ELEVENLABS_VOICE_ID");

  const body = await request.json();
  const text = String(body.text || "").trim();
  if (!text) {
    return jsonResponse({ error: "Missing text" }, 400);
  }

  const elevenLabsResponse = await fetch(`https://api.elevenlabs.io/v1/text-to-speech/${env.ELEVENLABS_VOICE_ID}`, {
    method: "POST",
    headers: {
      "xi-api-key": env.ELEVENLABS_API_KEY,
      "content-type": "application/json",
      accept: "audio/mpeg",
    },
    body: JSON.stringify({
      text,
      model_id: env.ELEVENLABS_MODEL || ELEVENLABS_MODEL,
      voice_settings: { stability: 0.5, similarity_boost: 0.75 },
    }),
  });

  if (!elevenLabsResponse.ok) {
    const errorBody = await elevenLabsResponse.text();
    return jsonResponse({ error: "ElevenLabs request failed", status: elevenLabsResponse.status, detail: safeParseJSON(errorBody) || errorBody }, elevenLabsResponse.status);
  }

  return new Response(elevenLabsResponse.body, {
    status: 200,
    headers: {
      ...CORS_HEADERS,
      "content-type": elevenLabsResponse.headers.get("content-type") || "audio/mpeg",
      "cache-control": "no-store",
    },
  });
}

async function handleTranscribeToken(env) {
  assertSecret(env.ASSEMBLYAI_API_KEY, "ASSEMBLYAI_API_KEY");

  const assemblyResponse = await fetch("https://streaming.assemblyai.com/v3/token?expires_in_seconds=480", {
    method: "GET",
    headers: { authorization: env.ASSEMBLYAI_API_KEY },
  });

  const responseText = await assemblyResponse.text();
  if (!assemblyResponse.ok) {
    return jsonResponse({ error: "AssemblyAI token request failed", status: assemblyResponse.status, detail: safeParseJSON(responseText) || responseText }, assemblyResponse.status);
  }

  return new Response(responseText, {
    status: 200,
    headers: { ...CORS_HEADERS, "content-type": "application/json", "cache-control": "no-store" },
  });
}

const SYSTEM_PROMPT = `
you are clicky, a friendly always-on windows desktop companion. the user speaks to you with push-to-talk, and you can see screenshots of their monitors.

rules:
- default to one or two sentences. be direct and useful.
- write spokenText for speech. no markdown, no bullets, no code blocks.
- if the user's request is to find a visible thing, choose only from the visible reticle marker ids in the annotated image.
- if no marker clearly matches the requested thing, set targetId to null.
- if the user's question is not about finding a screen target, answer directly and set targetId to null.
- do not read code verbatim. explain what it does or what to change.
- never say "simply" or "just".
- never mention marker ids, screen ids, coordinates, JSON, or targeting syntax in spokenText.

reticle targeting:
- choose the visible marker whose ring is on or closest to the requested target.
- prefer C markers for controls and R markers for large rendered regions.
- never invent marker ids.
- never return coordinates.
- never return bounding boxes.

response schema:
{"spokenText":"short user-facing response","targetId":"C01 or R02 or null","needsZoom":false}
`.trim();

function extractOpenAIText(responseJSON) {
  if (!responseJSON) {
    return "";
  }
  if (typeof responseJSON.output_text === "string") {
    return responseJSON.output_text;
  }
  const chunks = [];
  for (const outputItem of responseJSON.output || []) {
    for (const contentItem of outputItem.content || []) {
      if (typeof contentItem.text === "string") {
        chunks.push(contentItem.text);
      }
    }
  }
  return chunks.join("");
}

function parseTargetSelection(text, targetLookup = new Map()) {
  const parsed = safeParseJSON(extractJsonObject(text));
  const spokenText = sanitizeSpokenText(String(parsed?.spokenText || ""));
  const targetId = typeof parsed?.targetId === "string" ? parsed.targetId.trim() : null;
  if (!targetId) {
    return { spokenText, point: null };
  }

  const target = targetLookup.get(targetId);
  if (!target) {
    return { spokenText, point: null };
  }

  return {
    spokenText,
    point: {
      x: target.x,
      y: target.y,
      label: target.label,
      screenNumber: target.screenNumber,
      source: "visual-target",
      bounds: target.bounds,
      targetId: target.id,
    },
  };
}

function buildOpenAIComputerPrompt(goal, width, height) {
  return `
You are a precise GUI locator for a Windows desktop application.

The screenshot is exactly ${Math.round(width)} pixels wide and ${Math.round(height)} pixels tall.

Coordinate system:
- origin is the top-left corner of the screenshot
- x increases right
- y increases down
- coordinates must be screenshot image pixels

Goal:
"${goal}"

Use the computer tool to return the UI action you would use to click the visual center of the single UI control that best satisfies the goal.

Rules:
- Return one click target only.
- Prefer the actual clickable control, not nearby explanatory text.
- If the target is a labeled button, click the center of the button area.
- If the target is a tree item, click the center of the row text.
- If the target is an icon-only control, click the visual center of the icon's clickable area.
- If no relevant target is visible, do not return a click action.
- Do not explain.
`.trim();
}

function normalizeLocatePoint({ provider, goal, point, width, height, screenNumber }) {
  return {
    ok: true,
    provider,
    x: clamp(Math.round(point.x), 0, Math.round(width)),
    y: clamp(Math.round(point.y), 0, Math.round(height)),
    label: goal,
    screenNumber,
    coordinateSpace: "screenshot_pixels_top_left",
    rawAction: point.rawAction,
  };
}

function extractOpenAIComputerPoint(data) {
  const call = findOpenAIComputerCall(data, (action) =>
    ["click", "double_click", "move"].includes(String(action?.type || "")));
  if (!call || hasOpenAISafetyChecks(call.item)) {
    return null;
  }

  const x = Number(call.action?.x);
  const y = Number(call.action?.y);
  if (!Number.isFinite(x) || !Number.isFinite(y)) {
    return null;
  }

  return {
    x,
    y,
    rawAction: call.action,
  };
}

function findOpenAIComputerCall(data, predicate) {
  const output = Array.isArray(data?.output) ? data.output : [];
  for (const item of output) {
    if (item?.type !== "computer_call") {
      continue;
    }

    const actions = Array.isArray(item.actions)
      ? item.actions
      : item.action
        ? [item.action]
        : [];

    for (const action of actions) {
      if (predicate(action)) {
        return { item, action, call_id: item.call_id };
      }
    }
  }

  return null;
}

function hasOpenAISafetyChecks(computerCall) {
  return Array.isArray(computerCall?.pending_safety_checks) &&
    computerCall.pending_safety_checks.length > 0;
}

function buildVisualTargetLookup(images) {
  const lookup = new Map();
  for (const image of images) {
    const targets = Array.isArray(image?.visualTargets) ? image.visualTargets : [];
    for (const target of targets) {
      const id = String(target?.id || "").trim();
      const x = Number(target?.centerX);
      const y = Number(target?.centerY);
      const left = Number(target?.x);
      const top = Number(target?.y);
      const width = Number(target?.width);
      const height = Number(target?.height);
      if (!id || !Number.isFinite(x) || !Number.isFinite(y)) {
        continue;
      }

      lookup.set(id, {
        id,
        x,
        y,
        label: String(target?.labelHint || target?.kind || id).trim() || id,
        screenNumber: Number.isFinite(Number(image?.screenNumber)) ? Number(image.screenNumber) : null,
        bounds: Number.isFinite(left) && Number.isFinite(top) && Number.isFinite(width) && Number.isFinite(height) && width > 0 && height > 0
          ? { x: left, y: top, width, height }
          : null,
      });
    }
  }

  return lookup;
}

function formatVisualTargetManifest(images) {
  const targets = images.flatMap((image) => {
    const screenNumber = Number(image?.screenNumber);
    return (Array.isArray(image?.visualTargets) ? image.visualTargets : []).map((target) => ({ target, screenNumber }));
  });
  if (targets.length === 0) {
    return "";
  }

  const rows = targets
    .filter(({ target }) => target && target.id && Number.isFinite(Number(target.centerX)) && Number.isFinite(Number(target.centerY)))
    .slice(0, 80)
    .map(({ target, screenNumber }) => {
      const id = String(target.id || "");
      const kind = String(target.kind || "visual-target");
      const centerX = Math.round(Number(target.centerX));
      const centerY = Math.round(Number(target.centerY));
      const width = Math.round(Number(target.width || 0));
      const height = Math.round(Number(target.height || 0));
      const confidence = Number(target.confidence || 0).toFixed(2);
      return `${id} | ${kind} | screen=${screenNumber || "unknown"} | center=${centerX},${centerY} | size=${width}x${height} | confidence=${confidence}`;
    });

  if (rows.length === 0) {
    return "";
  }

  return rows.join("\n");
}

function extractJsonObject(text) {
  const trimmed = String(text || "").trim();
  const fenced = trimmed.match(/```(?:json)?\s*([\s\S]*?)```/i);
  if (fenced) {
    return fenced[1].trim();
  }

  const start = trimmed.indexOf("{");
  const end = trimmed.lastIndexOf("}");
  return start >= 0 && end > start ? trimmed.slice(start, end + 1) : trimmed;
}

function sanitizeSpokenText(text) {
  return String(text || "")
    .replace(/\b[CR]\d{2}\b/g, "that spot")
    .replace(/\bscreen\d+\b/gi, "the screen")
    .replace(/\[(?:POINT|POINT-ELEMENT|BOX|TARGET):[^\]]+\]/gi, "")
    .trim();
}

function requireMethod(request, expectedMethod) {
  if (request.method !== expectedMethod) {
    throw new Error(`Method not allowed. Expected ${expectedMethod}.`);
  }
}

function assertSecret(value, name) {
  if (!value) {
    throw new Error(`Missing Worker secret: ${name}`);
  }
}

function jsonResponse(value, status = 200) {
  return new Response(JSON.stringify(value, null, 2), {
    status,
    headers: { ...CORS_HEADERS, "content-type": "application/json", "cache-control": "no-store" },
  });
}

function safeParseJSON(text) {
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}

function base64ToUint8Array(base64) {
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index++) {
    bytes[index] = binary.charCodeAt(index);
  }
  return bytes;
}

function sleep(milliseconds) {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}

function clamp(value, min, max) {
  return Math.max(min, Math.min(max, value));
}
