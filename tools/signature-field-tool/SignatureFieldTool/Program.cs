using System.Text.Json;
using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Colors;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;

if (args.Length != 2 || !string.Equals(args[0], "--job", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Usage: SignatureFieldTool --job <job.json>");
    return 2;
}

var jobPath = args[1];
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

if (job is null || string.IsNullOrWhiteSpace(job.Input) || string.IsNullOrWhiteSpace(job.Output))
{
    Console.Error.WriteLine("Invalid job: input/output are required.");
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
    var outputDir = System.IO.Path.GetDirectoryName(job.Output);
    if (!string.IsNullOrWhiteSpace(outputDir))
    {
        Directory.CreateDirectory(outputDir);
    }

    using var reader = new PdfReader(job.Input);
    using var writer = new PdfWriter(job.Output);
    using var pdf = new PdfDocument(reader, writer);

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
            annotation.SetVisibility(PdfFormAnnotation.VISIBLE);
            annotation.SetBorderWidth((float)job.BorderWidth);
            annotation.SetBorderColor(new DeviceGray((float)job.BorderGray));
            annotation.GetWidget().SetFlags(PdfAnnotation.PRINT);
        }

        form.AddField(sig, page);
    }

    pdf.Close();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Signature field injection failed: {ex}");
    return 1;
}

return 0;

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
}
