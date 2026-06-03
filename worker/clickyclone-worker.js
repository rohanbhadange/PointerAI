const CORS_HEADERS = {
  "access-control-allow-origin": "*",
  "access-control-allow-methods": "GET,POST,OPTIONS",
  "access-control-allow-headers": "content-type",
};

const OPENAI_MODEL = "gpt-4.1-mini";
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
      elements: [
        {
          id: "screen1-el42",
          name: "New tab",
          controlType: "Button",
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
  const parsed = parsePointTag("Self-test response. [POINT-ELEMENT:screen1-el42]", buildElementLookup(images));
  return jsonResponse({
    ok: parsed.point?.source === "element" &&
      parsed.point?.x === 321.5 &&
      parsed.point?.y === 22.25 &&
      parsed.point?.screenNumber === 1,
    spokenText: parsed.spokenText,
    point: parsed.point,
  });
}

async function handleChat(request, env) {
  assertSecret(env.OPENAI_API_KEY, "OPENAI_API_KEY");

  const body = await request.json();
  const transcript = String(body.transcript || "").trim();
  const images = Array.isArray(body.images) ? body.images : [];
  const elementLookup = buildElementLookup(images);
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
      text: `${SYSTEM_PROMPT}\n\nRecent conversation:\n${historyText || "none"}\n\nThe user said:\n${transcript}\n\nVisible UIA candidate controls:\n${formatElementCatalog(images, transcript) || "none"}\n\nChoose exactly one candidate id when a listed control clearly matches the user's request. End with exactly one point tag.`,
    },
  ]);

  for (const image of images) {
    currentContent.push({ type: "input_text", text: String(image.label || "user screen") });
  }

  input.push({ role: "user", content: currentContent });

  const openAIResponse = await fetch("https://api.openai.com/v1/responses", {
    method: "POST",
    headers: {
      authorization: `Bearer ${env.OPENAI_API_KEY}`,
      "content-type": "application/json",
    },
    body: JSON.stringify({
      model: env.OPENAI_MODEL || OPENAI_MODEL,
      input,
      max_output_tokens: 700,
    }),
  });

  const responseText = await openAIResponse.text();
  if (!openAIResponse.ok) {
    return jsonResponse({ error: "OpenAI request failed", status: openAIResponse.status, detail: safeParseJSON(responseText) || responseText }, openAIResponse.status);
  }

  const fullText = extractOpenAIText(safeParseJSON(responseText)).trim();
  const point = parsePointTag(fullText, elementLookup);
  return jsonResponse({
    text: fullText,
    spokenText: point.spokenText.trim(),
    point: point.point,
    model: env.OPENAI_MODEL || OPENAI_MODEL,
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
- write for speech. no markdown, no bullets, no code blocks.
- if the user's request is to find a visible software control, select only from the provided UI Automation candidate controls.
- if no listed candidate clearly matches the requested control, say you could not find a reliable matching control and end with [POINT:none].
- if the user's question is not about finding a screen control, answer directly and end with [POINT:none].
- do not read code verbatim. explain what it does or what to change.
- never say "simply" or "just".
- all responses must end with exactly one point tag.

UI Automation pointing:
- the candidate list contains real controls detected locally by Windows UI Automation.
- choose the exact candidate whose name, type, and window context best match the user's words.
- never invent coordinates.
- never use BOX tags.
- never use raw POINT x,y tags.
- when there is a clear match, end with [POINT-ELEMENT:elementId].
- when there is no clear match, end with [POINT:none].

point tag format:
[POINT-ELEMENT:screenN-elM]
[POINT:none]
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

function parsePointTag(text, elementLookup = new Map()) {
  const elementMatch = text.match(/\[POINT-ELEMENT:\s*([a-zA-Z0-9_-]+)\s*\]\s*$/);
  if (elementMatch) {
    const spokenText = text.slice(0, elementMatch.index).trim();
    const element = elementLookup.get(elementMatch[1]);
    if (!element) {
      return { spokenText, point: null };
    }

    return {
      spokenText,
      point: {
        x: element.x,
        y: element.y,
        label: element.label,
        screenNumber: element.screenNumber,
        source: "element",
        bounds: element.bounds,
        elementId: element.id,
      },
    };
  }

  const match = text.match(/\[POINT:none\]\s*$/);
  if (!match) {
    return { spokenText: text, point: null };
  }
  const spokenText = text.slice(0, match.index).trim();
  return { spokenText, point: null };
}

function buildElementLookup(images) {
  const lookup = new Map();
  for (const image of images) {
    const elements = Array.isArray(image?.elements) ? image.elements : [];
    for (const element of elements) {
      const id = String(element?.id || "").trim();
      const x = Number(element?.centerX);
      const y = Number(element?.centerY);
      const left = Number(element?.x);
      const top = Number(element?.y);
      const width = Number(element?.width);
      const height = Number(element?.height);
      if (!id || !Number.isFinite(x) || !Number.isFinite(y)) {
        continue;
      }

      const screenMatch = id.match(/^screen(\d+)-/i);
      lookup.set(id, {
        id,
        x,
        y,
        label: String(element?.name || "").trim() || null,
        screenNumber: screenMatch ? Number(screenMatch[1]) : null,
        bounds: Number.isFinite(left) && Number.isFinite(top) && Number.isFinite(width) && Number.isFinite(height) && width > 0 && height > 0
          ? { x: left, y: top, width, height }
          : null,
      });
    }
  }

  return lookup;
}

function formatElementCatalog(images, transcript) {
  const elements = images.flatMap((image) => Array.isArray(image?.elements) ? image.elements : []);
  if (elements.length === 0) {
    return "";
  }

  const queryWords = extractWords(transcript);
  const rows = elements
    .filter((element) => element && element.name && Number.isFinite(Number(element.centerX)) && Number.isFinite(Number(element.centerY)))
    .map((element) => ({
      element,
      score: scoreElementForTranscript(element, queryWords),
    }))
    .sort((first, second) => second.score - first.score)
    .slice(0, 80)
    .map((element) => {
      const candidate = element.element;
      const id = String(candidate.id || "");
      const type = String(candidate.controlType || "Control").replace(/^ControlType\./, "");
      const name = String(candidate.name).slice(0, 90);
      const windowTitle = String(candidate.windowTitle || "").slice(0, 90);
      const centerX = Math.round(Number(candidate.centerX));
      const centerY = Math.round(Number(candidate.centerY));
      const width = Math.round(Number(candidate.width || 0));
      const height = Math.round(Number(candidate.height || 0));
      const clickable = candidate.isClickable ? "clickable" : "visible";
      return `${id} | ${type} | ${clickable} | "${name}" | window="${windowTitle}" | center=${centerX},${centerY} | size=${width}x${height}`;
    });

  if (rows.length === 0) {
    return "";
  }

  return rows.join("\n");
}

function scoreElementForTranscript(element, queryWords) {
  const nameWords = extractWords(`${element?.name || ""} ${element?.controlType || ""} ${element?.windowTitle || ""}`);
  const overlap = queryWords.filter((word) => nameWords.includes(word)).length;
  const clickableBonus = element?.isClickable ? 35 : 0;
  const localScore = Number(element?.score || 0);
  return overlap * 100 + clickableBonus + Math.min(localScore, 120);
}

function extractWords(text) {
  const stopWords = new Set(["a", "an", "and", "are", "button", "click", "control", "cursor", "find", "for", "go", "i", "icon", "it", "me", "of", "on", "please", "point", "show", "the", "there", "this", "to", "where", "you"]);
  return (String(text || "").toLowerCase().match(/[a-z0-9]+/g) || [])
    .filter((word) => word.length > 1 && !stopWords.has(word));
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
