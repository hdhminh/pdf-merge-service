const fs = require("fs");
const path = require("path");
const { performance } = require("perf_hooks");
const { stampPdf } = require("../pdfStamp");

function readJson(filePath) {
  try {
    const raw = fs.readFileSync(filePath, "utf8");
    return JSON.parse(raw);
  } catch (err) {
    throw new Error(`Cannot read manifest '${filePath}': ${err.message}`);
  }
}

function resolveCasePath(manifestPath, inputPath) {
  if (!inputPath || typeof inputPath !== "string") {
    return "";
  }

  if (path.isAbsolute(inputPath)) {
    return inputPath;
  }

  return path.resolve(path.dirname(manifestPath), inputPath);
}

function buildDefaultStampOptions() {
  return {
    certificationNumber: "4680",
    certificationBookNumber: "02",
    certificationDate: "2026-03-09",
    certificationText:
      "CH\u1ee8NG TH\u1ef0C B\u1ea2N SAO \u0110\u00daNG V\u1edaI B\u1ea2N CH\u00cdNH",
    notaryTitle: "C\u00d4NG CH\u1ee8NG VI\u00caN",
    logTiming: true,
  };
}

function ensureDirForFile(filePath) {
  const dir = path.dirname(filePath);
  if (!fs.existsSync(dir)) {
    fs.mkdirSync(dir, { recursive: true });
  }
}

async function run() {
  const manifestPath = path.resolve(
    process.argv[2] || path.join(__dirname, "regression-cases.sample.json"),
  );
  const manifest = readJson(manifestPath);
  const cases = Array.isArray(manifest.cases) ? manifest.cases : [];

  if (cases.length === 0) {
    console.error("[regression] No cases in manifest.");
    process.exit(2);
  }

  let failed = 0;
  const summary = [];
  for (const entry of cases) {
    const name = String(entry?.name || "unnamed_case");
    const inputPath = resolveCasePath(manifestPath, entry?.input);
    const outputPath = resolveCasePath(
      manifestPath,
      entry?.output ||
        path.join(
          path.dirname(inputPath || "."),
          `${path.parse(inputPath || name).name}__regression_stamped.pdf`,
        ),
    );

    if (!inputPath || !fs.existsSync(inputPath)) {
      failed += 1;
      summary.push({ name, status: "FAIL", reason: `missing_input:${inputPath}` });
      continue;
    }

    try {
      const sourceBytes = fs.readFileSync(inputPath);
      const options = {
        ...buildDefaultStampOptions(),
        ...(entry?.options || {}),
      };
      if (entry?.profileName && !options.profileName) {
        options.profileName = String(entry.profileName);
      }

      const start = performance.now();
      const outputBytes = await stampPdf(sourceBytes, options);
      const elapsedMs = performance.now() - start;
      ensureDirForFile(outputPath);
      fs.writeFileSync(outputPath, Buffer.from(outputBytes));

      summary.push({
        name,
        status: "PASS",
        ms: Math.round(elapsedMs),
        output: outputPath,
      });
    } catch (err) {
      failed += 1;
      summary.push({
        name,
        status: "FAIL",
        reason: String(err?.message || err),
      });
    }
  }

  for (const item of summary) {
    console.log(`[regression] ${JSON.stringify(item)}`);
  }

  if (failed > 0) {
    process.exit(1);
  }
}

run().catch((err) => {
  console.error(`[regression] fatal: ${err?.message || err}`);
  process.exit(1);
});
