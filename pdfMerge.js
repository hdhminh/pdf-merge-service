const fs = require("fs");
const { PDFDocument } = require("pdf-lib");

async function mergePdfs(inputFiles, outputFile) {
  const mergedPdf = await PDFDocument.create();

  for (const file of inputFiles) {
    const bytes = fs.readFileSync(file);
    const pdf = await PDFDocument.load(bytes);
    const pages = await mergedPdf.copyPages(pdf, pdf.getPageIndices());
    pages.forEach((p) => mergedPdf.addPage(p));
  }

  const mergedBytes = await mergedPdf.save();
  fs.writeFileSync(outputFile, mergedBytes);
}

module.exports = { mergePdfs };
