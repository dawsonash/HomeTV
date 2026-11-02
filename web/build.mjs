// Bundles the launcher and remote into the .NET app's wwwroot.
import * as esbuild from "esbuild";
import { cp, mkdir } from "node:fs/promises";
import { fileURLToPath } from "node:url";

// fileURLToPath, not URL.pathname: pathname gives "/C:/..." on Windows.
const outRoot = fileURLToPath(new URL("../src/HomeTV.App/wwwroot/", import.meta.url));
const pages = ["launcher", "remote"];
const staticFiles = { launcher: ["index.html", "styles.css"], remote: ["index.html", "styles.css", "manifest.webmanifest"] };
const watch = process.argv.includes("--watch");

async function copyStatic() {
  for (const page of pages) {
    await mkdir(`${outRoot}${page}`, { recursive: true });
    for (const file of staticFiles[page]) await cp(new URL(`${page}/${file}`, import.meta.url), `${outRoot}${page}/${file}`);
  }
}

const options = {
  entryPoints: pages.map((page) => ({ in: `${page}/main.ts`, out: `${page}/app` })),
  outdir: outRoot,
  bundle: true,
  format: "esm",
  target: "es2022",
  sourcemap: true,
  minify: !watch,
  logLevel: "info",
  plugins: [{ name: "copy-static", setup: (build) => build.onEnd(copyStatic) }],
};

if (watch) await (await esbuild.context(options)).watch();
else await esbuild.build(options);
