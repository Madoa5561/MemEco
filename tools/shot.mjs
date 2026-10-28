// 開発用: ヘッドレス Edge を CDP で操作してフルページ / モバイルのスクリーンショットを撮る。
// 使い方:
//   node tools/shot.mjs <url> <out.png> <width> <height> [deviceScaleFactor] [mobile:0|1]
import { spawn } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

const [url, out, widthArg, heightArg, dsfArg = "1", mobileArg = "0", fullArg = "1"] = process.argv.slice(2);
if (!url || !out) {
  console.error("usage: node tools/shot.mjs <url> <out.png> <width> <height> [dsf] [mobile]");
  process.exit(2);
}

const width = Number(widthArg || 1440);
const height = Number(heightArg || 900);
const deviceScaleFactor = Number(dsfArg);
const mobile = mobileArg === "1";
const fullPage = fullArg !== "0";

const EDGE =
  process.env.EDGE ||
  "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe";

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const port = 9400 + Math.floor(Math.random() * 300);
const profile = fs.mkdtempSync(path.join(os.tmpdir(), "edge-cdp-"));
const proc = spawn(
  EDGE,
  [
    "--headless=new",
    "--disable-gpu",
    "--hide-scrollbars",
    "--no-first-run",
    "--no-default-browser-check",
    `--user-data-dir=${profile}`,
    `--remote-debugging-port=${port}`,
    "about:blank",
  ],
  { stdio: "ignore" }
);

async function waitForDevTools() {
  for (let i = 0; i < 100; i++) {
    try {
      const r = await fetch(`http://127.0.0.1:${port}/json/version`);
      if (r.ok) return;
    } catch {
      /* retry */
    }
    await sleep(200);
  }
  throw new Error("DevTools エンドポイントに接続できませんでした");
}

try {
  await waitForDevTools();
  const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  const target = targets.find((t) => t.type === "page");
  if (!target) throw new Error("page target が見つかりません");

  const ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => {
    ws.onopen = resolve;
    ws.onerror = reject;
  });

  let seq = 0;
  const pending = new Map();
  ws.onmessage = (event) => {
    const msg = JSON.parse(event.data);
    if (msg.id && pending.has(msg.id)) {
      const { resolve, reject } = pending.get(msg.id);
      pending.delete(msg.id);
      msg.error ? reject(new Error(msg.error.message)) : resolve(msg.result);
    }
  };
  const send = (method, params = {}) =>
    new Promise((resolve, reject) => {
      const id = ++seq;
      pending.set(id, { resolve, reject });
      ws.send(JSON.stringify({ id, method, params }));
    });

  await send("Page.enable");
  await send("Runtime.enable");
  await send("Emulation.setDeviceMetricsOverride", {
    width,
    height,
    deviceScaleFactor,
    mobile,
    screenWidth: width,
    screenHeight: height,
  });

  const loaded = new Promise((resolve) => {
    const handler = (event) => {
      const msg = JSON.parse(event.data);
      if (msg.method === "Page.loadEventFired") {
        ws.removeEventListener("message", handler);
        resolve();
      }
    };
    ws.addEventListener("message", handler);
  });

  await send("Page.navigate", { url });
  await loaded;
  await sleep(1400);

  // lazy 画像を読み込ませるため少しスクロールする（smooth を無効化して即時移動）
  await send("Runtime.evaluate", {
    expression:
      "document.documentElement.style.scrollBehavior='auto'; window.scrollTo({top: document.body.scrollHeight, behavior:'instant'});",
    awaitPromise: true,
  });
  await sleep(900);
  await send("Runtime.evaluate", {
    expression: "window.scrollTo({top: 0, behavior:'instant'});",
  });
  await sleep(600);

  const metrics = await send("Page.getLayoutMetrics");
  const content = metrics.cssContentSize;
  const params = { format: "png" };
  if (fullPage) {
    params.captureBeyondViewport = true;
    params.clip = {
      x: 0,
      y: 0,
      width: Math.ceil(content.width),
      height: Math.ceil(content.height),
      scale: 1,
    };
  }
  const shot = await send("Page.captureScreenshot", params);

  fs.writeFileSync(out, Buffer.from(shot.data, "base64"));
  console.log(
    `saved ${out} (${fullPage ? `${Math.ceil(content.width)}x${Math.ceil(content.height)}` : `${width}x${height}`})`
  );

  ws.close();
} finally {
  proc.kill();
  await sleep(300);
  try {
    fs.rmSync(profile, { recursive: true, force: true });
  } catch {
    /* ignore */
  }
}
