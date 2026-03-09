using System.Text.Json;
using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Colors;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;

var (jobPath, writeStdout, parseError) = ParseArguments(args);
if (!string.IsNullOrWhiteSpace(parseError))
{
    Console.Error.WriteLine(parseError);
    Console.Error.WriteLine("Usage: SignatureFieldTool --job <job.json> [--stdout]");
    return 2;
}

if (!File.Exists(jobPath))
{
    Console.Error.WriteLine($"Job file not found: {jobPath}");
    return 2;
}

SignatureJob? job;
try
{
    var jobJson = File.ReadAllText(jobPath);
    job = JsonSerializer.Deserialize<SignatureJob>(jobJson, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
    });
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to parse job json: {ex.Message}");
    return 2;
}

if (job is null || string.IsNullOrWhiteSpace(job.Input))
{
    Console.Error.WriteLine("Invalid job: input is required.");
    return 2;
}

if (!writeStdout && string.IsNullOrWhiteSpace(job.Output))
{
    Console.Error.WriteLine("Invalid job: output is required unless --stdout is used.");
    return 2;
}

if (!File.Exists(job.Input))
{
    Console.Error.WriteLine($"Input pdf not found: {job.Input}");
    return 2;
}

if (job.Fields is null || job.Fields.Count == 0)
{
    Console.Error.WriteLine("Invalid job: at least one field is required.");
    return 2;
}

try
{
    if (!writeStdout)
    {
        var outputDir = System.IO.Path.GetDirectoryName(job.Output);
        if (!string.IsNullOrWhiteSpace(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        using var reader = new PdfReader(job.Input);
        using var writer = new PdfWriter(job.Output);
        using var pdf = new PdfDocument(reader, writer);
        ApplySignatureFields(pdf, job);
        pdf.Close();
    }
    else
    {
        using var reader = new PdfReader(job.Input);
        using var outputStream = new MemoryStream();
        using (var writer = new PdfWriter(outputStream))
        using (var pdf = new PdfDocument(reader, writer))
        {
            ApplySignatureFields(pdf, job);
            pdf.Close();
        }

        var bytes = outputStream.ToArray();
        using var stdout = Console.OpenStandardOutput();
        stdout.Write(bytes, 0, bytes.Length);
        stdout.Flush();
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Signature field injection failed: {ex}");
    return 1;
}

return 0;

static (string JobPath, bool WriteStdout, string ParseError) ParseArguments(string[] args)
{
    string jobPath = string.Empty;
    var writeStdout = false;

    if (args.Length == 0)
    {
        return (jobPath, writeStdout, "Missing arguments.");
    }

    for (var i = 0; i < args.Length; i += 1)
    {
        var arg = args[i];
        if (string.Equals(arg, "--job", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 >= args.Length)
            {
                return (jobPath, writeStdout, "Missing value for --job.");
            }

            jobPath = args[i + 1];
            i += 1;
            continue;
        }

        if (string.Equals(arg, "--stdout", StringComparison.OrdinalIgnoreCase))
        {
            writeStdout = true;
            continue;
        }

        return (jobPath, writeStdout, $"Unknown argument: {arg}");
    }

    if (string.IsNullOrWhiteSpace(jobPath))
    {
        return (jobPath, writeStdout, "Missing --job argument.");
    }

    return (jobPath, writeStdout, string.Empty);
}

static void ApplySignatureFields(PdfDocument pdf, SignatureJob job)
{
    var form = PdfAcroForm.GetAcroForm(pdf, true);
    form.SetNeedAppearances(false);

    foreach (var field in job.Fields)
    {
        if (field is null || string.IsNullOrWhiteSpace(field.Name))
        {
            continue;
        }

        if (job.ReplaceExisting)
        {
            var existing = form.GetField(field.Name);
            if (existing is not null)
            {
                form.RemoveField(field.Name);
            }
        }

        var pageNumber = field.PageIndex + 1;
        if (pageNumber < 1 || pageNumber > pdf.GetNumberOfPages())
        {
            throw new InvalidOperationException($"Field '{field.Name}' has invalid pageIndex={field.PageIndex}.");
        }

        var page = pdf.GetPage(pageNumber);
        var rect = new Rectangle((float)field.X, (float)field.Y, (float)field.Width, (float)field.Height);

        var sig = new SignatureFormFieldBuilder(pdf, field.Name)
            .SetPage(page)
            .SetWidgetRectangle(rect)
            .CreateSignature();

        var annotation = sig.GetFirstFormAnnotation();
        if (annotation is not null)
        {
            var normalizedRotation = ((field.Rotation % 360) + 360) % 360;
            var rotation = normalizedRotation switch
            {
                < 45 => 0,
                < 135 => 90,
                < 225 => 180,
                < 315 => 270,
                _ => 0,
            };
            annotation.SetVisibility(PdfFormAnnotation.VISIBLE);
            annotation.SetBorderWidth((float)job.BorderWidth);
            annotation.SetBorderColor(new DeviceGray((float)job.BorderGray));
            annotation.SetRotation(rotation);
            annotation.GetWidget().SetFlags(PdfAnnotation.PRINT);
        }

        form.AddField(sig, page);

        if (field.Rotation % 360 != 0)
        {
            var added = form.GetField(field.Name);
            if (added is null)
            {
                continue;
            }

            var dict = added.GetPdfObject();
            var mk = dict.GetAsDictionary(PdfName.MK) ?? new PdfDictionary();
            mk.Put(PdfName.R, new PdfNumber(((field.Rotation % 360) + 360) % 360));
            dict.Put(PdfName.MK, mk);
        }
    }
}

internal sealed class SignatureJob
{
    public string Input { get; set; } = string.Empty;
    public string Output { get; set; } = string.Empty;
    public bool ReplaceExisting { get; set; } = true;
    public double BorderWidth { get; set; } = 1.0;
    public double BorderGray { get; set; } = 0.65;
    public List<SignatureFieldSpec> Fields { get; set; } = new();
}

internal sealed class SignatureFieldSpec
{
    public string Name { get; set; } = string.Empty;
    public int PageIndex { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public int Rotation { get; set; }
}
