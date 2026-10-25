using System.Diagnostics;
using System.Globalization;
using Saldo360.Application.CreditCards;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class LocalStatementPageReader(string? dockerImage = null)
{
    public static bool Usable(string text) => text.Length >= 40 && text.Count(char.IsLetterOrDigit) >= 25
        && text.Count(c => c == '\ufffd' || char.IsControl(c) && c is not ('\r' or '\n' or '\t')) * 100 <= text.Length;

    public async Task<CreditCardPdfExtractedPage> ReadAsync(byte[] unlockedPage, CreditCardPdfExtractedPage page, bool ocr, CancellationToken ct)
    {
        var root = Path.GetFullPath(Path.GetTempPath());
        var directory = Directory.CreateTempSubdirectory("saldo360-page-").FullName;
        var stage = "Render";
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var pdf = Path.Combine(directory, "input.pdf"); var prefix = Path.Combine(directory, "page");
            await File.WriteAllBytesAsync(pdf, unlockedPage, ct);
            await RunAsync("pdftoppm", ["-f", "1", "-singlefile", "-scale-to", "2200", "-png", pdf, prefix], directory, ct);
            stage = "ReadImage";
            var imagePath = prefix + ".png";
            if (new FileInfo(imagePath).Length is <= 8 or > 4 * 1024 * 1024) throw new InvalidDataException();
            var image = await File.ReadAllBytesAsync(imagePath, ct);
            if (!ocr)
            {
                var regions = new List<StatementVisualRegion>();
                var regionBudget = Stopwatch.StartNew();
                foreach (var region in page.VisualRegions)
                {
                    if (region.Status != "Pending") { regions.Add(region); continue; }
                    if (regionBudget.Elapsed > TimeSpan.FromSeconds(60)) { regions.Add(region with { Status = "RegionBudgetExceeded" }); continue; }
                    // Poppler crops the already-unlocked page; coordinates remain in original PDF points.
                    var scale = 2200d / Math.Max(page.PageWidth, page.PageHeight);
                    if (!double.IsFinite(scale) || scale <= 0 || region.Right <= region.Left || region.Bottom <= region.Top)
                    { regions.Add(region with { Status = "InvalidBounds" }); continue; }
                    var x = (int)Math.Floor(region.Left * scale); var y = (int)Math.Floor(region.Top * scale);
                    var width = (int)Math.Ceiling(region.Right * scale) - x; var height = (int)Math.Ceiling(region.Bottom * scale) - y;
                    var crop = Path.Combine(directory, "region-" + region.Index.ToString(CultureInfo.InvariantCulture));
                    stage = "RegionRender";
                    await RunAsync("pdftoppm", ["-f", "1", "-singlefile", "-scale-to", "2200", "-x", x.ToString(CultureInfo.InvariantCulture), "-y", y.ToString(CultureInfo.InvariantCulture),
                        "-W", width.ToString(CultureInfo.InvariantCulture), "-H", height.ToString(CultureInfo.InvariantCulture), "-png", pdf, crop], directory, ct);
                    if (new FileInfo(crop + ".png").Length is <= 8 or > 4 * 1024 * 1024) throw new InvalidDataException();
                    // Bounded orientation handling, not another recognition engine. Map OCR boxes back below.
                    var rotated = region.Bottom - region.Top > 3 * (region.Right - region.Left);
                    var ocrImage = crop + ".png";
                    if (rotated)
                    {
                        var rotatedPdf = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
                        var rotatedPage = rotatedPdf.AddPage(width, height);
                        rotatedPage.AddPng(await File.ReadAllBytesAsync(ocrImage, ct), new(0, 0, width, height));
                        rotatedPage.SetRotation(new(90));
                        await File.WriteAllBytesAsync(crop + "-rotated.pdf", rotatedPdf.Build(), ct);
                        stage = "RegionOrient";
                        await RunAsync("pdftoppm", ["-f", "1", "-singlefile", "-r", "72", "-png", crop + "-rotated.pdf", crop + "-rotated"], directory, ct);
                        ocrImage = crop + "-rotated.png";
                        if (new FileInfo(ocrImage).Length is <= 8 or > 4 * 1024 * 1024) throw new InvalidDataException();
                    }
                    stage = "RegionOcr";
                    await RunAsync("tesseract", [ocrImage, crop, "-l", "por", "--psm", rotated ? "11" : "3", "tsv"], directory, ct);
                    if (new FileInfo(crop + ".tsv").Length > 1024 * 1024) throw new InvalidDataException();
                    regions.Add(ReadRegionTsv(region, await File.ReadAllTextAsync(crop + ".tsv", ct), x / scale, y / scale, scale, rotated ? height : null));
                }
                return StatementVisualRegionText.Merge(page with { ImagePng = image, ProcessingStatus = "NativeWithImage", VisualRegions = regions });
            }
            var output = Path.Combine(directory, "ocr");
            stage = "Ocr";
            await RunAsync("tesseract", [imagePath, output, "-l", "por", "--psm", "3"], directory, ct);
            stage = "ReadText";
            if (new FileInfo(output + ".txt").Length > 1024 * 1024) throw new InvalidDataException();
            var text = await File.ReadAllTextAsync(output + ".txt", ct);
            return page with { Text = text, Words = null, TextSource = "OCR", ImagePng = image,
                ProcessingStatus = Usable(text) ? "OCRWithImage" : "Unreadable" };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            var code = error switch
            {
                FileNotFoundException => "MissingOutput", InvalidDataException => "OutputLimit",
                System.ComponentModel.Win32Exception => "ProcessStart", OperationCanceledException => "Timeout",
                IOException => "ToolIO", _ => "PreparationFailed"
            };
            return page with { ProcessingStatus = "Unavailable", PreparationFailureCode = stage + code };
        }
        finally
        {
            // Only the unique directory created above, beneath the resolved temporary root.
            if (Path.GetFullPath(directory).StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(directory, recursive: true);
        }
    }

    internal static StatementVisualRegion ReadRegionTsv(StatementVisualRegion region, string tsv, double left, double top, double scale, int? rotatedHeight = null)
    {
        var words = new List<CreditCardPdfExtractedWord>(); var confidence = new List<double>();
        foreach (var line in tsv.Split('\n').Skip(1))
        {
            var fields = line.TrimEnd('\r').Split('\t');
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (fields.Length != 12) return region with { Status = "Unreadable" };
            if (fields[0] != "5" || string.IsNullOrWhiteSpace(fields[11])) continue;
            if (!double.TryParse(fields[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var score)
                || !double.IsFinite(score) || score is < 0 or > 100
                || !int.TryParse(fields[6], out var x) || !int.TryParse(fields[7], out var y)
                || !int.TryParse(fields[8], out var width) || !int.TryParse(fields[9], out var height)
                || x < 0 || y < 0 || width <= 0 || height <= 0 || scale <= 0 || !double.IsFinite(scale))
                return region with { Status = "Unreadable" };
            var word = rotatedHeight is { } rotated
                ? new CreditCardPdfExtractedWord(fields[11], left + y / scale, top + (rotated - x - (double)width) / scale, left + (y + (double)height) / scale, top + (rotated - x) / scale)
                : new CreditCardPdfExtractedWord(fields[11], left + x / scale, top + y / scale, left + (x + (double)width) / scale, top + (y + (double)height) / scale);
            if (word.X < region.Left - 2 || word.Top < region.Top - 2 || word.Right > region.Right + 2 || word.Bottom > region.Bottom + 2 || words.Count >= 20000)
                return region with { Status = "Unreadable" };
            words.Add(word); confidence.Add(score);
        }
        var text = string.Join('\n', StatementVisualRegionText.Lines(words).Select(line => string.Join(' ', line.Select(w => w.Text))));
        var usable = text.Count(char.IsLetterOrDigit) >= 3 && !text.Any(c => c == '\ufffd' || char.IsControl(c) && c is not ('\r' or '\n' or '\t'))
            && text.Length <= 40000 && confidence.Count > 0 && confidence.Average() >= 70
            && confidence.Count(c => c >= 40) >= confidence.Count * .9;
        return region with { Words = words, Text = text, Status = usable ? "Readable" : "Unreadable" };
    }

    private async Task RunAsync(string executable, string[] args, string directory, CancellationToken ct)
    {
        var container = dockerImage is null ? null : "saldo360-page-" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(dockerImage is null ? executable : "docker") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        if (dockerImage is not null)
        {
            foreach (var arg in new[] { "run", "--rm", "--name", container!, "--pull", "never", "--network", "none", "--memory", "256m", "--cpus", "1", "--mount", $"type=bind,source={directory},target=/work", "--workdir", "/work", "--entrypoint", executable, dockerImage }) start.ArgumentList.Add(arg);
        }
        foreach (var arg in args) start.ArgumentList.Add(dockerImage is null ? arg : arg.Replace(directory, "/work", StringComparison.Ordinal).Replace('\\', '/')); // Never a password or CPF.
        using var process = Process.Start(start) ?? throw new IOException();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var drain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        var outputDrain = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        try
        {
            while (!process.HasExited)
            {
                timeout.Token.ThrowIfCancellationRequested();
                // Docker enforces the actual tool's memory limit. Its short-lived client is not the tool.
                if (dockerImage is null)
                {
                    try
                    {
                        process.Refresh();
                        if (!process.HasExited && process.WorkingSet64 > 256L * 1024 * 1024) throw new IOException();
                    }
                    catch (InvalidOperationException) when (process.HasExited) { }
                    catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
                }
                await Task.Delay(40, timeout.Token);
            }
            await Task.WhenAll(drain, outputDrain);
            if (process.ExitCode != 0) throw new IOException();
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None);
                if (container is not null) await RemoveOwnedContainerAsync(container);
            }
        }
    }

    private static async Task RemoveOwnedContainerAsync(string container)
    {
        // Exact random name generated by this invocation; never enumerate or remove other containers.
        var start = new ProcessStartInfo("docker") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "rm", "--force", container }) start.ArgumentList.Add(arg);
        using var cleanup = Process.Start(start);
        if (cleanup is null) return;
        var stdout = cleanup.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        var stderr = cleanup.StandardError.BaseStream.CopyToAsync(Stream.Null);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await cleanup.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { if (!cleanup.HasExited) cleanup.Kill(entireProcessTree: true); }
        await cleanup.WaitForExitAsync(CancellationToken.None);
        await Task.WhenAll(stdout, stderr);
    }
}
