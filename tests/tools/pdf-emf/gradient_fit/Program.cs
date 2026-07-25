using System.Drawing;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using CadPeta.Acquire.Raster;
using CadPeta.Ir;

try
{
    return Run(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Gradient analysis failed ({ex.GetType().Name}): {ex.Message}");
    return 1;
}

static int Run(string[] args)
{
if (args.Length is < 1 or > 4 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: GradientFit <svg-directory> [profiles|production|shiftedfit]\n" +
                            "       GradientFit <svg-directory> golden <pdf-directory> <output.json>\n" +
                            "       GradientFit <existing-directory> pnghash");
    return 1;
}

string[] shapes = ["LINEAR", "CYLINDER", "INVCYLINDER", "SPHERICAL", "INVSPHERICAL",
                   "HEMISPHERICAL", "INVHEMISPHERICAL", "CURVED", "INVCURVED"];
if (args.Length == 4 && args[1] == "golden")
{
    try
    {
        WriteGoldenData(args[0], args[2], args[3], shapes);
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Golden data generation failed: {ex.Message}");
        return 1;
    }
}
if (args.Length == 2 && args[1] == "pnghash")
{
    GradientHatchResult result = GradientRasterizer.Rasterize(CrossTfmPngRequest());
    if (result.Status != GradientHatchStatus.Rasterized)
    {
        Console.Error.WriteLine($"PNG hash request did not rasterize: {result.Status} {result.FallbackReason}");
        return 1;
    }
    Console.WriteLine(Convert.ToHexString(SHA256.HashData(result.PngBytes)));
    return 0;
}
if (args.Length == 2 && args[1] == "profiles")
{
    foreach (string shape in new[] { "LINEAR", "CYLINDER" })
    foreach (string suffix in new[] { "a000_s000", "a000_s050" })
    {
        using var profileImage = LoadEmbeddedImage(Path.Combine(args[0], $"grad_{shape}_bw_{suffix}.svg"));
        const int profileNodeCount = 21;
        var values = new List<string>();
        for (int node = 0; node < profileNodeCount; node++)
            values.Add(SampleAt(profileImage, node / (double)(profileNodeCount - 1), .5)
                .ToString("0.000000", CultureInfo.InvariantCulture));
        Console.WriteLine($"{shape} {suffix} nodes={string.Join(',', values)}");
    }
    return 0;
}
if (args.Length == 2 && args[1] == "production")
{
    foreach (string shape in shapes)
    foreach ((string suffix, double angle, double shift) in new[]
    {
        ("a000_s000", 0.0, 0.0), ("a045_s000", Math.PI / 4, 0.0), ("a000_s050", 0.0, .5),
    })
    {
        using var productionImage = LoadEmbeddedImage(Path.Combine(args[0], $"grad_{shape}_bw_{suffix}.svg"));
        Model model = sample => GradientSemantics.Evaluate(shape,
            sample.U * 340, sample.V * 220, 0, 0, 340, 220, angle, shift);
        Stats stats = Measure(ReadSamples(productionImage), model, false);
        Console.WriteLine($"{shape,-20} {suffix} med={stats.Median:0.00} p95={stats.P95:0.00} p99={stats.P99:0.00} max={stats.Max:0.00} rmse={stats.Rmse:0.00}");
    }
    return 0;
}
if (args.Length == 2 && args[1] == "shiftedfit")
{
    string shifted45Directory = Path.Combine(Path.GetDirectoryName(args[0])!, "CADPeta_gradient_shifted_angles_decoded");
    string shifted90Directory = Path.Combine(Path.GetDirectoryName(args[0])!, "CADPeta_gradient_shifted_right_angles_decoded");
    foreach (string shape in new[] { "HEMISPHERICAL", "CURVED" })
    {
        using var image0 = LoadEmbeddedImage(Path.Combine(args[0], $"grad_{shape}_bw_a000_s050.svg"));
        using var image45 = LoadEmbeddedImage(Path.Combine(shifted45Directory, $"grad_{shape}_bw_a045_s100.svg"));
        using var image90 = LoadEmbeddedImage(Path.Combine(shifted90Directory, $"grad_{shape}_bw_a090_s100.svg"));
        List<Sample> validation = ReadSamples(image90);
        foreach (string scaleMode in new[] { "ProjectedSpans", "CommonProjectedDiameter", "OriginalLongSide" })
        foreach (int degree in new[] { 6, 8 })
        {
            List<Sample> training = ReadSamples(image0).Select(s => RotateToTemplate(s, 0, scaleMode)).ToList();
            training.AddRange(ReadSamples(image45).Select(s => RotateToTemplate(s, Math.PI / 4, scaleMode)));
            double[] coefficients = FitTensorCoefficients(training, degree);
            Model model = s => EvaluateTensor(coefficients, degree,
                RotateToTemplate(s, Math.PI / 2, scaleMode));
            Stats stats = Measure(validation, model, false);
            Console.WriteLine($"{shape} {scaleMode} Chebyshev{degree} validate a090 med={stats.Median:0.00} p95={stats.P95:0.00} p99={stats.P99:0.00} max={stats.Max:0.00} rmse={stats.Rmse:0.00}");
            if (degree == 6 && ((shape == "HEMISPHERICAL" && scaleMode == "CommonProjectedDiameter")
                             || (shape == "CURVED" && scaleMode == "ProjectedSpans")))
                Console.WriteLine($"{shape} selected coeff=" +
                    string.Join(",", coefficients.Select(v => v.ToString("R", CultureInfo.InvariantCulture))));
        }
    }
    return 0;
}
using var cylinderImage = LoadEmbeddedImage(Path.Combine(args[0], "grad_CYLINDER_bw_a000_s000.svg"));
double[] polynomial = FitPolynomial(ReadSamples(cylinderImage), 8, s => Math.Abs(2 * s.U - 1));
Console.WriteLine("F(r) polynomial probe: " + string.Join(", ", polynomial.Select(x => x.ToString("R", CultureInfo.InvariantCulture))));
double[] radial = BuildMeasuredSurface(cylinderImage, 65);
Console.WriteLine("F(r) measured nodes: " + string.Join(", ", radial.Select(x => x.ToString("0.000000", CultureInfo.InvariantCulture))));
FitRadialCandidates(ReadSamples(cylinderImage));
using (var shiftedSphereImage = LoadEmbeddedImage(Path.Combine(args[0], "grad_SPHERICAL_bw_a000_s050.svg")))
{
    List<Sample> shifted = ReadSamples(shiftedSphereImage);
    (double u0, double v0, double radius, Stats stats) = FitShiftedSphere(shifted, radial);
    Console.WriteLine($"Shift .5 sphere: center=({u0:0.000000},{v0:0.000000}) radiusFactor={radius:0.000000} " +
                      $"med={stats.Median:0.00} p95={stats.P95:0.00} max={stats.Max:0.00} rmse={stats.Rmse:0.00}");
}
Console.WriteLine("Variant probes (TL,TC,TR,CL,CC,CR,BL,BC,BR):");
foreach (string shape in shapes)
{
    foreach (string variant in new[] { "a000_s000", "a045_s000", "a000_s050" })
    {
        using var image = LoadEmbeddedImage(Path.Combine(args[0], $"grad_{shape}_bw_{variant}.svg"));
        double[] p = [.1, .5, .9];
        var values = from v in p from u in p select SampleAt(image, u, v).ToString("0.000", CultureInfo.InvariantCulture);
        Console.WriteLine($"{shape,-20} {variant}: {string.Join(' ', values)}");
    }
}
foreach (string shape in shapes)
{
    using var image = LoadEmbeddedImage(Path.Combine(args[0], $"grad_{shape}_bw_a000_s000.svg"));
    List<Sample> samples = ReadSamples(image);
    Model model;
    string detail;
    if (shape == "LINEAR")
    {
        model = s => s.U;
        detail = "t=u";
    }
    else if (shape is "CYLINDER" or "INVCYLINDER")
    {
        model = s => Radial(radial, Math.Abs(2 * s.U - 1));
        detail = "t=F(abs(2u-1))";
    }
    else if (shape is "SPHERICAL" or "INVSPHERICAL")
    {
        model = s => Radial(radial, SphereRadius(s));
        detail = "t=F(sqrt(x^2+y^2)), x=2u-1, y=(2v-1)*H/W";
    }
    else if (shape is "CURVED" or "INVCURVED")
    {
        (double k, Stats stats) = FitScalar(samples, k => s =>
            Radial(radial, Math.Sqrt(Math.Pow(1 - s.V, 2) + Math.Pow(k * (2 * s.U - 1), 2))),
            shape.StartsWith("INV", StringComparison.Ordinal), 0, 1);
        Print(shape, stats, $"k={k:0.0000}; cos(pi/2*sqrt((1-v)^2+(k*(2u-1))^2))");
        continue;
    }
    else
    {
        bool inverse = shape.StartsWith("INV", StringComparison.Ordinal);
        (double a, double b, double c) = FitPlane(samples, inverse, radial);
        model = s => a + b * (2 * s.V - 1) + c * Radial(radial, SphereRadius(s));
        detail = $"t={a:0.000000}+{b:0.000000}*(2v-1)+{c:0.000000}*sphere";
    }
    bool inv = shape.StartsWith("INV", StringComparison.Ordinal);
    Print(shape, Measure(samples, model, inv), detail);
}
foreach (string shape in new[] { "HEMISPHERICAL", "CURVED" })
{
    using var image = LoadEmbeddedImage(Path.Combine(args[0], $"grad_{shape}_bw_a000_s000.svg"));
    FitOffsetRadial(shape, ReadSamples(image), radial);
}
foreach (string shape in new[] { "HEMISPHERICAL", "CURVED" })
foreach (string variant in new[] { "a000_s000", "a000_s050" })
{
    using var image = LoadEmbeddedImage(Path.Combine(args[0], $"grad_{shape}_bw_{variant}.svg"));
    foreach (int degree in new[] { 4, 6, 8 }) FitTensor(shape + " " + variant, ReadSamples(image), degree);
    if (variant == "a000_s000")
    {
        const int validationDegree = 8;
        double[] coefficients = FitTensorCoefficients(ReadSamples(image), validationDegree);
        foreach ((string suffix, double angle) in new[]
        {
            ("a045_s000", Math.PI / 4), ("a090_s000", Math.PI / 2), ("a180_s000", Math.PI)
        })
        {
            string directory = suffix == "a045_s000" ? args[0] :
                Path.Combine(Path.GetDirectoryName(args[0])!, "CADPeta_gradient_extended_decoded");
            using var validationImage = LoadEmbeddedImage(Path.Combine(directory, $"grad_{shape}_bw_{suffix}.svg"));
            foreach (string scaleMode in new[] { "ProjectedSpans", "OriginalAxes", "OriginalLongSide" })
            {
                Model model = sample => EvaluateTensor(coefficients, validationDegree, RotateToTemplate(sample, angle, scaleMode));
                Stats stats = Measure(ReadSamples(validationImage), model, false);
                Console.WriteLine($"{shape} Chebyshev{validationDegree} validate {suffix} {scaleMode} med={stats.Median:0.00} p95={stats.P95:0.00} max={stats.Max:0.00} rmse={stats.Rmse:0.00}");
            }
        }
    }
    else
    {
        const int validationDegree = 8;
        double[] coefficients = FitTensorCoefficients(ReadSamples(image), validationDegree);
        string directory = Path.Combine(Path.GetDirectoryName(args[0])!, "CADPeta_gradient_shifted_angles_decoded");
        using var validationImage = LoadEmbeddedImage(Path.Combine(directory, $"grad_{shape}_bw_a045_s100.svg"));
        foreach (double sign in new[] { 1.0, -1.0 })
        {
            Model model = sample => EvaluateTensor(coefficients, validationDegree,
                RotateToTemplate(sample, sign * Math.PI / 4, "ProjectedSpans"));
            Stats stats = Measure(ReadSamples(validationImage), model, false);
            Console.WriteLine($"{shape} shifted Chebyshev{validationDegree} validate a045 sign={sign:+0;-0} med={stats.Median:0.00} p95={stats.P95:0.00} max={stats.Max:0.00} rmse={stats.Rmse:0.00}");
        }
    }
}
using (var hemiImage = LoadEmbeddedImage(Path.Combine(args[0], "grad_HEMISPHERICAL_bw_a000_s000.svg")))
{
    foreach (int degree in new[] { 4, 6, 8, 10 })
    {
        double[] coefficients = FitOneDimensionalChebyshev(ReadSamples(hemiImage), degree, s => HemisphericalRadius(s, 0));
        foreach ((string suffix, double angle) in new[]
        {
            ("a000_s000", 0.0), ("a045_s000", Math.PI / 4),
            ("a090_s000", Math.PI / 2), ("a180_s000", Math.PI)
        })
        {
            string directory = suffix is "a000_s000" or "a045_s000" ? args[0] :
                Path.Combine(Path.GetDirectoryName(args[0])!, "CADPeta_gradient_extended_decoded");
            using var validationImage = LoadEmbeddedImage(Path.Combine(directory, $"grad_HEMISPHERICAL_bw_{suffix}.svg"));
            Model model = s => EvaluateOneDimensionalChebyshev(coefficients, HemisphericalRadius(s, angle));
            Stats stats = Measure(ReadSamples(validationImage), model, false);
            Console.WriteLine($"HEMISPHERICAL radial Chebyshev{degree} {suffix} med={stats.Median:0.00} p95={stats.P95:0.00} max={stats.Max:0.00} rmse={stats.Rmse:0.00}");
        }
        if (degree == 8) Console.WriteLine("HEMISPHERICAL radial coeff=" + string.Join(",", coefficients.Select(v => v.ToString("R", CultureInfo.InvariantCulture))));
    }
}
return 0;
}

static Bitmap LoadEmbeddedImage(string svgPath)
{
    XDocument document = XDocument.Load(svgPath);
    XElement image = document.Descendants().First(e => e.Name.LocalName == "image");
    XAttribute href = image.Attributes().First(a => a.Name.LocalName == "href");
    int comma = href.Value.IndexOf(',');
    byte[] bytes = Convert.FromBase64String(href.Value[(comma + 1)..]);
    using var stream = new MemoryStream(bytes);
    using var decoded = new Bitmap(stream);
    return new Bitmap(decoded);
}

static void WriteGoldenData(string svgDirectory, string pdfDirectory, string outputPath, string[] shapes)
{
    const double hatchWidthMm = 340;
    const double hatchHeightMm = 220;
    const double excludedEdgeMm = 3;
    const int sampleColumns = 17;
    const int sampleRows = 11;
    // Limits include the measured AutoCAD JPEG/triangle-seam floor. Current production maxima are
    // fixed-grid maxima are median=2.48, p95=7.00 and rmse=4.45 levels across
    // the 27 black/white charts (the shifted CURVED pair contains isolated seam spikes).
    const double medianErrorLimitLevels = 2.5;
    const double p95ErrorLimitLevels = 7.5;
    const double rmseLimitLevels = 4.5;
    (string Suffix, double Angle, double Shift)[] variants =
    [
        ("a000_s000", 0, 0),
        ("a045_s000", Math.PI / 4, 0),
        ("a000_s050", 0, .5),
    ];
    var charts = new List<GoldenChart>();
    Stats maximum = new(0, 0, 0, 0, 0);
    foreach (string shape in shapes)
    foreach ((string suffix, double angle, double shift) in variants)
    {
        string stem = $"grad_{shape}_bw_{suffix}";
        string pdfPath = Path.Combine(pdfDirectory, stem + ".pdf");
        using Bitmap image = LoadEmbeddedImage(Path.Combine(svgDirectory, stem + ".svg"));
        var values = new byte[sampleColumns * sampleRows];
        for (int row = 0; row < sampleRows; row++)
        for (int column = 0; column < sampleColumns; column++)
        {
            double u = (excludedEdgeMm + column * (hatchWidthMm - 2 * excludedEdgeMm) / (sampleColumns - 1)) / hatchWidthMm;
            double v = (excludedEdgeMm + row * (hatchHeightMm - 2 * excludedEdgeMm) / (sampleRows - 1)) / hatchHeightMm;
            values[row * sampleColumns + column] = checked((byte)Math.Round(SampleAt(image, u, v) * 255));
        }
        var samples = new List<Sample>(values.Length);
        for (int row = 0; row < sampleRows; row++)
        for (int column = 0; column < sampleColumns; column++)
        {
            double u = (excludedEdgeMm + column * (hatchWidthMm - 2 * excludedEdgeMm) / (sampleColumns - 1)) / hatchWidthMm;
            double v = (excludedEdgeMm + row * (hatchHeightMm - 2 * excludedEdgeMm) / (sampleRows - 1)) / hatchHeightMm;
            samples.Add(new Sample(u, v, values[row * sampleColumns + column] / 255.0));
        }
        Stats stats = Measure(samples, sample => GradientSemantics.Evaluate(shape,
            sample.U * hatchWidthMm, sample.V * hatchHeightMm,
            0, 0, hatchWidthMm, hatchHeightMm, angle, shift), false);
        Console.WriteLine($"golden {stem,-42} med={stats.Median:0.00} p95={stats.P95:0.00} rmse={stats.Rmse:0.00}");
        maximum = new Stats(Math.Max(maximum.Median, stats.Median), Math.Max(maximum.P95, stats.P95),
            Math.Max(maximum.P99, stats.P99), Math.Max(maximum.Max, stats.Max), Math.Max(maximum.Rmse, stats.Rmse));
        charts.Add(new GoldenChart(shape, suffix, angle, shift, Path.GetFileName(pdfPath),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pdfPath))), values));
    }
    var corpus = new GoldenCorpus(
        "AutoCAD 2026 accoreconsole / AutoCAD PDF (High Quality Print).pc3",
        hatchWidthMm, hatchHeightMm, excludedEdgeMm, sampleColumns, sampleRows,
        medianErrorLimitLevels, p95ErrorLimitLevels, rmseLimitLevels, charts);
    string fullOutputPath = Path.GetFullPath(outputPath);
    Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
    File.WriteAllText(fullOutputPath, JsonSerializer.Serialize(corpus, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"golden charts={charts.Count} samples={charts.Sum(chart => chart.Values.Length)} output={fullOutputPath}");
    Console.WriteLine($"golden maxima med={maximum.Median:0.00} p95={maximum.P95:0.00} rmse={maximum.Rmse:0.00}");
}

static GradientHatchRequest CrossTfmPngRequest()
{
    const double oneInchMm = 25.4;
    const double requestedDpi = 10;
    const double minimumDpi = 1;
    const int maximumDimensionPx = 32767;
    const long availablePixels = 1_000_000;
    return new GradientHatchRequest
    {
        GradientName = "LINEAR",
        Stops = [new GradientStop(0, new IrRgb(0, 0, 0)), new GradientStop(1, new IrRgb(1, 1, 1))],
        Boundary =
        [
            new IrMove(0, 0), new IrLine(oneInchMm, 0), new IrLine(oneInchMm, oneInchMm),
            new IrLine(0, oneInchMm), new IrClose(),
        ],
        RequestedDpi = requestedDpi,
        MinimumDpi = minimumDpi,
        MaxDimensionPx = maximumDimensionPx,
        AvailablePixels = availablePixels,
    };
}

static List<Sample> ReadSamples(Bitmap image)
{
    var result = new List<Sample>();
    for (int py = (int)Math.Ceiling(3.0 / 220.0 * image.Height);
         py < (int)Math.Floor(217.0 / 220.0 * image.Height); py += 21)
    for (int px = (int)Math.Ceiling(3.0 / 340.0 * image.Width);
         px < (int)Math.Floor(337.0 / 340.0 * image.Width); px += 21)
    {
        if (image.GetPixel(px, py).A >= 250)
            result.Add(new Sample((px + .5) / image.Width, (py + .5) / image.Height,
                                  MedianPixel(image, px, py) / 255.0));
    }
    return result;
}

static int MedianPixel(Bitmap image, int cx, int cy)
{
    Span<int> values = stackalloc int[9];
    int n = 0;
    for (int dy = -1; dy <= 1; dy++)
    for (int dx = -1; dx <= 1; dx++)
    {
        Color c = image.GetPixel(cx + dx, cy + dy);
        values[n++] = (c.R + c.G + c.B) / 3;
    }
    values.Sort();
    return values[4];
}

static double SampleAt(Bitmap image, double u, double v)
{
    int x = Math.Clamp((int)(u * image.Width), 2, image.Width - 3);
    int y = Math.Clamp((int)(v * image.Height), 2, image.Height - 3);
    return MedianPixel(image, x, y) / 255.0;
}

static double SphereRadius(Sample s)
{
    double x = 2 * s.U - 1;
    double y = (2 * s.V - 1) * 220.0 / 340.0;
    return Math.Sqrt(x * x + y * y);
}

static double Radial(double[] coefficients, double radius)
{
    if (radius >= 1) return 0;
    if (radius <= 0) return coefficients[0];
    double position = radius * (coefficients.Length - 1);
    int index = (int)position;
    double fraction = position - index;
    return coefficients[index] + (coefficients[index + 1] - coefficients[index]) * fraction;
}

static double[] BuildMeasuredSurface(Bitmap image, int nodeCount)
{
    const double excludedEdgeMm = 3;
    const double hatchHeightMm = 220;
    var nodes = new double[nodeCount];
    int y0 = (int)Math.Ceiling(excludedEdgeMm / hatchHeightMm * image.Height);
    int y1 = (int)Math.Floor((hatchHeightMm - excludedEdgeMm) / hatchHeightMm * image.Height);
    for (int node = 0; node < nodeCount; node++)
    {
        double radius = node / (double)(nodeCount - 1);
        double leftU = (1 - radius) / 2;
        double rightU = (1 + radius) / 2;
        var values = new List<int>();
        foreach (double u in new[] { leftU, rightU })
        {
            int x = Math.Clamp((int)Math.Round(u * (image.Width - 1)), 2, image.Width - 3);
            for (int y = y0; y < y1; y += 17) values.Add(MedianPixel(image, x, y));
        }
        values.Sort();
        nodes[node] = values[values.Count / 2] / 255.0;
    }
    return nodes;
}

static double[] FitPolynomial(List<Sample> samples, int degree, Func<Sample, double> argument)
{
    int n = degree + 1;
    var matrix = new double[n, n + 1];
    foreach (Sample sample in samples)
    {
        double r = argument(sample);
        var powers = new double[2 * degree + 1];
        powers[0] = 1;
        for (int i = 1; i < powers.Length; i++) powers[i] = powers[i - 1] * r;
        for (int row = 0; row < n; row++)
        {
            for (int col = 0; col < n; col++) matrix[row, col] += powers[row + col];
            matrix[row, n] += powers[row] * sample.Value;
        }
    }
    return Solve(matrix);
}

static void FitRadialCandidates(List<Sample> samples)
{
    (double scale, double power, Stats stats) best = (0, 0, new Stats(0, 0, 0, 0, double.MaxValue));
    const double coarseStep = .01;
    const double fineStep = .001;
    Search(.95, 1.10, .80, 1.20, coarseStep);
    Search(best.scale - coarseStep, best.scale + coarseStep,
           best.power - coarseStep, best.power + coarseStep, fineStep);

    void Search(double scaleMin, double scaleMax, double powerMin, double powerMax, double step)
    {
        for (double scale = scaleMin; scale <= scaleMax + step / 2; scale += step)
        for (double power = powerMin; power <= powerMax + step / 2; power += step)
        {
            Stats stats = Measure(samples, s =>
            {
                double r = Math.Min(1, scale * Math.Abs(2 * s.U - 1));
                return Math.Pow(Math.Max(0, Math.Cos(Math.PI * r / 2)), power);
            }, false);
            if (stats.Rmse < best.stats.Rmse) best = (scale, power, stats);
        }
    }
    Console.WriteLine($"radial cosine scale={best.scale:0.000} power={best.power:0.000} med={best.stats.Median:0.00} p95={best.stats.P95:0.00} max={best.stats.Max:0.00} rmse={best.stats.Rmse:0.00}");
    Console.WriteLine("cylinder centerline r/value/cos:");
    using var _ = new StringWriter();
    // ReadSamples is intentionally sparse; report the median nearest each requested radius.
    for (int i = 0; i <= 20; i++)
    {
        double r = i / 20.0;
        Sample nearest = samples.MinBy(s => Math.Abs(Math.Abs(2 * s.U - 1) - r));
        double predicted = Math.Pow(Math.Max(0, Math.Cos(Math.PI * Math.Min(1, best.scale * r) / 2)), best.power);
        Console.WriteLine($"  {r:0.00} {nearest.Value:0.0000} {predicted:0.0000}");
    }
}

static void FitOffsetRadial(string shape, List<Sample> samples, double[] radial)
{
    double[] parameters = [1.0, .35, 1.0];
    double[] steps = [.2, .2, .2];
    double Error()
    {
        Model model = s => Radial(radial, Math.Sqrt(
            Math.Pow(parameters[1] * (2 * s.U - 1), 2) +
            Math.Pow(parameters[2] * (s.V - parameters[0]), 2)));
        return RootMeanSquareError(samples, model);
    }
    double bestError = Error();
    for (int iteration = 0; iteration < 60 && steps.Max() >= .0001; iteration++)
    {
        bool improved = false;
        for (int axis = 0; axis < parameters.Length; axis++)
        for (int direction = -1; direction <= 1; direction += 2)
        {
            double old = parameters[axis];
            parameters[axis] += direction * steps[axis];
            if (parameters[1] <= 0 || parameters[2] <= 0) { parameters[axis] = old; continue; }
            double error = Error();
            if (error < bestError) { bestError = error; improved = true; }
            else parameters[axis] = old;
        }
        if (!improved) for (int i = 0; i < steps.Length; i++) steps[i] /= 2;
    }
    double centerV = parameters[0], xScale = parameters[1], yScale = parameters[2];
    Model final = s => Radial(radial, Math.Sqrt(
        Math.Pow(xScale * (2 * s.U - 1), 2) + Math.Pow(yScale * (s.V - centerV), 2)));
    Stats stats = Measure(samples, final, false);
    Console.WriteLine($"{shape} offset radial centerV={centerV:0.000} xScale={xScale:0.000} yScale={yScale:0.000} " +
                      $"med={stats.Median:0.00} p95={stats.P95:0.00} max={stats.Max:0.00} rmse={stats.Rmse:0.00}");
}

static double RootMeanSquareError(List<Sample> samples, Model model)
{
    double sum = 0;
    foreach (Sample sample in samples)
    {
        double error = (sample.Value - Math.Clamp(model(sample), 0, 1)) * 255;
        sum += error * error;
    }
    return Math.Sqrt(sum / samples.Count);
}

static void FitTensor(string label, List<Sample> samples, int degree)
{
    double[] coefficients = FitTensorCoefficients(samples, degree);
    Model model = sample => EvaluateTensor(coefficients, degree, sample);
    Stats stats = Measure(samples, model, false);
    Console.WriteLine($"{label} Chebyshev{degree} med={stats.Median:0.00} p95={stats.P95:0.00} p99={stats.P99:0.00} max={stats.Max:0.00} rmse={stats.Rmse:0.00}");
    if (degree == 8) Console.WriteLine("  coeff=" + string.Join(",", coefficients.Select(v => v.ToString("R", CultureInfo.InvariantCulture))));
}

static double[] FitTensorCoefficients(List<Sample> samples, int degree)
{
    int side = degree + 1, count = side * side;
    var normal = new double[count, count + 1];
    foreach (Sample sample in samples)
    {
        double[] tx = Chebyshev(2 * sample.U - 1, degree);
        double[] ty = Chebyshev(2 * sample.V - 1, degree);
        var basis = new double[count];
        for (int y = 0; y < side; y++)
        for (int x = 0; x < side; x++) basis[y * side + x] = tx[x] * ty[y];
        for (int row = 0; row < count; row++)
        {
            for (int col = 0; col < count; col++) normal[row, col] += basis[row] * basis[col];
            normal[row, count] += basis[row] * sample.Value;
        }
    }
    const double ridge = 1e-8;
    for (int i = 0; i < count; i++) normal[i, i] += ridge;
    return Solve(normal);
}

static double EvaluateTensor(double[] coefficients, int degree, Sample sample)
{
    int side = degree + 1;
    double[] tx = Chebyshev(2 * sample.U - 1, degree);
    double[] ty = Chebyshev(2 * sample.V - 1, degree);
    double value = 0;
    for (int y = 0; y < side; y++)
    for (int x = 0; x < side; x++) value += coefficients[y * side + x] * tx[x] * ty[y];
    return value;
}

static Sample RotateToTemplate(Sample sample, double angle, string scaleMode)
{
    const double width = 340, height = 220;
    double c = Math.Cos(angle), s = Math.Sin(angle);
    double alongSpan = Math.Abs(c) * width + Math.Abs(s) * height;
    double crossSpan = Math.Abs(s) * width + Math.Abs(c) * height;
    double dx = (sample.U - .5) * width, dy = (sample.V - .5) * height;
    (double uSpan, double vSpan) = scaleMode switch
    {
        "OriginalAxes" => (width, height),
        "OriginalLongSide" => (width, width),
        "CommonProjectedDiameter" => (Math.Max(alongSpan, crossSpan), Math.Max(alongSpan, crossSpan)),
        _ => (alongSpan, crossSpan),
    };
    return sample with
    {
        U = .5 + (dx * c - dy * s) / uSpan,
        V = .5 + (dx * s + dy * c) / vSpan,
    };
}

static double[] Chebyshev(double value, int degree)
{
    var result = new double[degree + 1];
    result[0] = 1;
    if (degree > 0) result[1] = value;
    for (int i = 2; i <= degree; i++) result[i] = 2 * value * result[i - 1] - result[i - 2];
    return result;
}

static double HemisphericalRadius(Sample sample, double angle)
{
    const double width = 340, height = 220;
    double c = Math.Cos(angle), s = Math.Sin(angle);
    double diameter = Math.Max(Math.Abs(c) * width + Math.Abs(s) * height,
                               Math.Abs(s) * width + Math.Abs(c) * height);
    double dx = (sample.U - .5) * width - s * diameter / 2;
    double dy = (sample.V - .5) * height - c * diameter / 2;
    return Math.Sqrt(dx * dx + dy * dy) / diameter;
}

static double[] FitOneDimensionalChebyshev(List<Sample> samples, int degree, Func<Sample, double> argument)
{
    int count = degree + 1;
    var normal = new double[count, count + 1];
    foreach (Sample sample in samples)
    {
        double[] basis = Chebyshev(2 * argument(sample) - 1, degree);
        for (int row = 0; row < count; row++)
        {
            for (int col = 0; col < count; col++) normal[row, col] += basis[row] * basis[col];
            normal[row, count] += basis[row] * sample.Value;
        }
    }
    return Solve(normal);
}

static double EvaluateOneDimensionalChebyshev(double[] coefficients, double argument)
{
    argument = Math.Clamp(argument, 0, 1);
    double[] basis = Chebyshev(2 * argument - 1, coefficients.Length - 1);
    double value = 0;
    for (int i = 0; i < coefficients.Length; i++) value += coefficients[i] * basis[i];
    return value;
}

static double[] Solve(double[,] matrix)
{
    int n = matrix.GetLength(0);
    for (int pivot = 0; pivot < n; pivot++)
    {
        int best = pivot;
        for (int row = pivot + 1; row < n; row++)
            if (Math.Abs(matrix[row, pivot]) > Math.Abs(matrix[best, pivot])) best = row;
        for (int col = pivot; col <= n; col++)
            (matrix[pivot, col], matrix[best, col]) = (matrix[best, col], matrix[pivot, col]);
        double divisor = matrix[pivot, pivot];
        for (int col = pivot; col <= n; col++) matrix[pivot, col] /= divisor;
        for (int row = 0; row < n; row++) if (row != pivot)
        {
            double q = matrix[row, pivot];
            for (int col = pivot; col <= n; col++) matrix[row, col] -= q * matrix[pivot, col];
        }
    }
    return Enumerable.Range(0, n).Select(row => matrix[row, n]).ToArray();
}

static (double K, Stats Stats) FitScalar(List<Sample> samples, Func<double, Model> factory,
                                         bool inverse, double min, double max)
{
    double bestK = 0;
    Stats best = new(0, 0, 0, 0, double.MaxValue);
    for (int i = 0; i <= 1000; i++)
    {
        double k = min + (max - min) * i / 1000.0;
        Stats stats = Measure(samples, factory(k), inverse);
        if (stats.Rmse < best.Rmse) (bestK, best) = (k, stats);
    }
    return (bestK, best);
}

static (double U0, double V0, double Radius, Stats Stats) FitShiftedSphere(List<Sample> samples, double[] radial)
{
    double[] p = [.25, .25, 1.0];
    double[] step = [.1, .1, .1];
    Stats Eval() => Measure(samples, s =>
    {
        double dx = (s.U - p[0]) * 340.0;
        double dy = (s.V - p[1]) * 220.0;
        return Radial(radial, Math.Sqrt(dx * dx + dy * dy) / (170.0 * p[2]));
    }, false);
    Stats best = Eval();
    for (int iteration = 0; iteration < 80; iteration++)
    {
        bool improved = false;
        for (int axis = 0; axis < 3; axis++)
        for (int direction = -1; direction <= 1; direction += 2)
        {
            double old = p[axis];
            p[axis] = old + direction * step[axis];
            if (axis == 2 && p[axis] <= .05) { p[axis] = old; continue; }
            Stats candidate = Eval();
            if (candidate.Rmse < best.Rmse) { best = candidate; improved = true; }
            else p[axis] = old;
        }
        if (!improved)
        {
            for (int i = 0; i < step.Length; i++) step[i] *= .5;
            if (step.Max() < 1e-6) break;
        }
    }
    return (p[0], p[1], p[2], best);
}

static (double A, double B, double C) FitPlane(List<Sample> samples, bool inverse, double[] radial)
{
    double[,] m = new double[3, 4];
    foreach (Sample s in samples)
    {
        double value = inverse ? 1 - s.Value : s.Value;
        double[] f = [1, 2 * s.V - 1, Radial(radial, SphereRadius(s))];
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++) m[row, col] += f[row] * f[col];
            m[row, 3] += f[row] * value;
        }
    }
    for (int pivot = 0; pivot < 3; pivot++)
    {
        int best = pivot;
        for (int row = pivot + 1; row < 3; row++)
            if (Math.Abs(m[row, pivot]) > Math.Abs(m[best, pivot])) best = row;
        for (int col = pivot; col < 4; col++) (m[pivot, col], m[best, col]) = (m[best, col], m[pivot, col]);
        double d = m[pivot, pivot];
        for (int col = pivot; col < 4; col++) m[pivot, col] /= d;
        for (int row = 0; row < 3; row++) if (row != pivot)
        {
            double q = m[row, pivot];
            for (int col = pivot; col < 4; col++) m[row, col] -= q * m[pivot, col];
        }
    }
    return (m[0, 3], m[1, 3], m[2, 3]);
}

static Stats Measure(List<Sample> samples, Model model, bool inverse)
{
    var errors = new double[samples.Count];
    double squares = 0;
    for (int i = 0; i < samples.Count; i++)
    {
        double expected = Math.Clamp(model(samples[i]), 0, 1);
        if (inverse) expected = 1 - expected;
        double error = Math.Abs(samples[i].Value - expected) * 255;
        errors[i] = error;
        squares += error * error;
    }
    Array.Sort(errors);
    double P(double p) => errors[(int)Math.Min(errors.Length - 1, Math.Round((errors.Length - 1) * p))];
    return new Stats(P(.5), P(.95), P(.99), errors[^1], Math.Sqrt(squares / errors.Length));
}

static void Print(string shape, Stats s, string detail) => Console.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"{shape,-20} med={s.Median,6:0.00} p95={s.P95,6:0.00} p99={s.P99,6:0.00} max={s.Max,6:0.00} rmse={s.Rmse,6:0.00}  {detail}"));

delegate double Model(Sample sample);
readonly record struct Sample(double U, double V, double Value);
readonly record struct Stats(double Median, double P95, double P99, double Max, double Rmse);
sealed record GoldenCorpus(string SourceRenderer,
    double HatchWidthMm, double HatchHeightMm, double ExcludedEdgeMm,
    int SampleColumns, int SampleRows,
    double MedianErrorLimitLevels, double P95ErrorLimitLevels, double RmseLimitLevels,
    List<GoldenChart> Charts);
sealed record GoldenChart(string Shape, string Suffix, double AngleRadians, double Shift,
    string SourcePdf, string SourcePdfSha256, byte[] Values);
