// E22: a slide server that renders any slide on request, so a show can
// start before the whole deck is rendered. It uses the deck's own toolchain
// and the Playwright that comes with it (Playwright for .NET ships no ARM64
// driver for Windows; this runs on the native Node).
//
//   node e22_slide_server.mjs <marp|slidev> <deck dir> <width> <edge> <node_modules>
//
// marp:   marp-cli writes the deck's HTML once (bespoke template, no on-screen
//         controls or transitions); each slide is its #<n>, captured at the
//         design canvas (1280x604, E12) times width/1280.
// slidev: the deck's Slidev dev server; each slide is /<n>?print=clicks&clicks=<k>
//         (as `slidev export` does), captured at the canvas (980 wide) times
//         width/980.
// Edge runs headless in a non-persistent context: a throwaway profile in the
// temp folder (point TEMP at a folder of your own).
//
// stdin: "render <n> <clicks> <out.png>" per line; stdout: "ready <ms>" once,
// then "done <n> <clicks> <ms>" per slide, or "error <message>".
import { spawn } from "node:child_process";
import { createRequire } from "node:module";
import { createInterface } from "node:readline";
import path from "node:path";
import { pathToFileURL } from "node:url";

const [mode, deckDir, widthArg, edge, nodeModules] = process.argv.slice(2);
const width = Number(widthArg);
const require = createRequire(path.join(nodeModules, "noop.js"));
const { chromium } = require("playwright-chromium");
const t0 = performance.now();
const bin = (name) => path.join(nodeModules, ".bin", process.platform === "win32" ? `${name}.cmd` : name);

function run(cmd, args, cwd) {
  return new Promise((resolve, reject) => {
    const p = spawn(cmd, args, { cwd, shell: true, stdio: ["ignore", "ignore", "pipe"] });
    let err = "";
    p.stderr.on("data", (d) => (err += d));
    p.on("exit", (code) => (code ? reject(new Error(err.slice(-400))) : resolve()));
  });
}

let server = null;
let url;
let canvas;
if (mode === "marp") {
  canvas = [1280, 604];
  await run(bin("marp"), ["deck.md", "--html", "--theme-set", "theme.css", "--bespoke.osc", "false",
    "--bespoke.transition", "false", "--bespoke.progress", "false", "-o", "deck.html"], deckDir);
  url = (n) => `${pathToFileURL(path.join(deckDir, "deck.html")).href}#${n}`;
} else {
  canvas = [980, 463];
  const port = 3000 + Math.floor(Math.random() * 1000);
  server = spawn(bin("slidev"), ["slides.md", "--port", String(port), "--open", "false"],
    { cwd: deckDir, shell: true, stdio: ["ignore", "pipe", "pipe"] });
  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("slidev did not start")), 60000);
    const check = async () => {
      try {
        const r = await fetch(`http://localhost:${port}/`);
        if (r.ok) { clearTimeout(timer); return resolve(); }
      } catch { }
      setTimeout(check, 100);
    };
    check();
  });
  url = (n, k) => `http://localhost:${port}/${n}?print=clicks${k ? `&clicks=${k}` : ""}`;
}
const browser = await chromium.launch({ executablePath: edge, headless: true });
const context = await browser.newContext({
  viewport: { width: canvas[0], height: canvas[1] },
  deviceScaleFactor: width / canvas[0],
});
const page = await context.newPage();
console.log(`ready ${Math.round(performance.now() - t0)}`);

const rl = createInterface({ input: process.stdin });
for await (const line of rl) {
  const [cmd, n, k, out] = line.trim().split(" ");
  if (cmd !== "render") break;
  const t = performance.now();
  try {
    await page.goto(url(n, Number(k)), { waitUntil: "load" });
    if (mode === "slidev") {
      await page.locator(`[data-slidev-no="${n}"]`).waitFor();
      await page.evaluate(() => document.fonts.ready);
    } else {
      await page.waitForTimeout(50);  // the hash change shows the slide
    }
    await page.screenshot({ path: out, animations: "disabled" });
    console.log(`done ${n} ${k} ${Math.round(performance.now() - t)}`);
  } catch (e) {
    console.log(`error ${String(e.message).split("\n")[0]}`);
  }
}
await browser.close();
if (server) {
  // the dev server runs under cmd.exe: end its whole tree
  spawn("taskkill", ["/PID", String(server.pid), "/T", "/F"], { stdio: "ignore" });
}
